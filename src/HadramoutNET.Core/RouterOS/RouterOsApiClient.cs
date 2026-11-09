using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace HadramoutNET.Core.RouterOS;

public sealed record RouterOsConnectionOptions(
    string Host,
    int Port = 8728,
    string Username = "admin",
    string Password = "",
    bool UseSsl = false,
    bool VerifySsl = false,
    bool RemoteMode = false,
    int ConnectTimeoutSeconds = 8,
    int CommandTimeoutSeconds = 45);

public sealed class RouterOsConnectionException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>
/// Stateful, serialized RouterOS API v6/v7 TCP or API-SSL client.
///
/// This implements the protocol transport and authentication but does not
/// store passwords, modify any databases, or run any command by itself except
/// /login. Actual operations are all checked by RouterOsCommandGuard.
/// </summary>
public sealed class RouterOsApiClient : IAsyncDisposable
{
    private readonly RouterOsConnectionOptions _options;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private TcpClient? _tcp;
    private Stream? _stream;
    private bool _disposed;

    public RouterOsApiClient(RouterOsConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new ArgumentException("Router address is required.", nameof(options));
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid RouterOS API port.");
        if (options.ConnectTimeoutSeconds <= 0 || options.CommandTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Connection timeouts must be positive.");
        _options = options;
    }

    public bool Connected => !_disposed && _stream is not null && _tcp is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Connected) return;

        TcpClient? client = null;
        int maxAttempts = _options.RemoteMode ? 2 : 1;

        // Retry only establishing the initial remote TCP transport. Never
        // replay a rejected login, a TLS failure, or a mutation command.
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            client = new TcpClient();
            try
            {
                using var connectTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
                await client.ConnectAsync(_options.Host, _options.Port, connectTimeout.Token)
                    .ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (
                ex is SocketException or IOException ||
                ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                if (attempt == maxAttempts - 1)
                    throw new RouterOsConnectionException("تعذر الاتصال بالراوتر.", ex);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        if (client is null)
            throw new RouterOsConnectionException("تعذر الاتصال بالراوتر.");

        try
        {
            Stream stream = client.GetStream();
            if (_options.UseSsl)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                    (sender, certificate, chain, errors) =>
                        !_options.VerifySsl || errors == SslPolicyErrors.None);
                using var handshakeTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = _options.Host,
                        EnabledSslProtocols = SslProtocols.None
                    }, handshakeTimeout.Token).ConfigureAwait(false);
                stream = ssl;
            }
            _tcp = client;
            _stream = stream;

            // Modern RouterOS direct login; RouterOS 6.42 and older can
            // return a challenge requiring MD5(0x00 + password + challenge).
            var login = await ExecuteAsync("/login",
                new Dictionary<string, string>
                {
                    ["name"] = _options.Username,
                    ["password"] = _options.Password
                }, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (login.Done.TryGetValue("ret", out string? challenge) &&
                !string.IsNullOrWhiteSpace(challenge))
            {
                byte[] challengeBytes;
                try
                {
                    challengeBytes = Convert.FromHexString(challenge);
                }
                catch (FormatException ex)
                {
                    throw new RouterOsProtocolException("RouterOS returned an invalid login challenge: " + ex.GetType().Name);
                }

                byte[] password = Encoding.UTF8.GetBytes(_options.Password);
                byte[] toHash = new byte[1 + password.Length + challengeBytes.Length];
                password.CopyTo(toHash, 1);
                challengeBytes.CopyTo(toHash, 1 + password.Length);
                string response = "00" + Convert.ToHexString(MD5.HashData(toHash)).ToLowerInvariant();
                CryptographicOperations.ZeroMemory(toHash);
                CryptographicOperations.ZeroMemory(password);
                await ExecuteAsync("/login", new Dictionary<string, string>
                {
                    ["name"] = _options.Username,
                    ["response"] = response
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            CloseTransport();
            client.Dispose();
            throw;
        }
    }

    public async Task<RouterOsCommandResult> ExecuteAsync(
        string path,
        IReadOnlyDictionary<string, string>? attributes = null,
        IEnumerable<string>? queries = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Reject unsafe commands before waiting for I/O or accessing a socket.
        string[] queryArray = queries?.ToArray() ?? [];
        RouterOsCommandGuard.Validate(path, attributes, queryArray);

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is null)
                throw new RouterOsConnectionException("غير متصل بالراوتر.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));
            try
            {
                return await RouterOsCommandExchange.ExecuteAsync(
                    _stream, path, attributes, queryArray, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (RouterOsTrapException)
            {
                // !done was fully consumed; later requests are still aligned.
                throw;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                CloseTransport();
                throw new RouterOsConnectionException("انتهت مهلة استجابة الراوتر.", ex);
            }
            catch
            {
                // EOF, malformed frame, unknown reply, transport error and
                // cancelled streaming consumers all invalidate the session.
                CloseTransport();
                throw;
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private void CloseTransport()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            CloseTransport();
            _commandGate.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
