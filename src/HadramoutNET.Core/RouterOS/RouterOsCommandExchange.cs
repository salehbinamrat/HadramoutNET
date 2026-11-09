using System.IO;
using System.Text;

namespace HadramoutNET.Core.RouterOS;

public sealed record RouterOsCommandResult(
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    IReadOnlyDictionary<string, string> Done);

public sealed class RouterOsTrapException(string message) : Exception(message);

public sealed class RouterOsProtocolException(string message) : IOException(message);

/// <summary>
/// Executes one RouterOS sentence against an already connected bidirectional
/// Stream. Does not own the stream, connect to a router, or retry commands.
/// The stream owner must close its connection after any protocol/transport error
/// other than a fully consumed !trap followed by !done.
/// </summary>
public static class RouterOsCommandExchange
{
    public const int MaxResponseBytes = 128 * 1024 * 1024;
    public const int MaxResponseRows = 250_000;

    public static async Task<RouterOsCommandResult> ExecuteAsync(
        Stream stream,
        string command,
        IReadOnlyDictionary<string, string>? attributes = null,
        IEnumerable<string>? queries = null,
        int maxRows = MaxResponseRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("RouterOS requires a readable and writable stream.", nameof(stream));
        if (maxRows < 1 || maxRows > MaxResponseRows)
            throw new ArgumentOutOfRangeException(nameof(maxRows));

        string[] queryArray = queries?.ToArray() ?? [];
        // Authorize before constructing or sending any bytes. Never allow the
        // GUI layer to bypass the safety guard even for one-off commands.
        RouterOsCommandGuard.Validate(command, attributes, queryArray);

        var request = new List<string> { command.Trim() };
        if (attributes is not null)
        {
            foreach (var entry in attributes)
            {
                // Python's original builder omits null-valued attributes.
                if (entry.Value is not null)
                    request.Add($"={entry.Key}={entry.Value}");
            }
        }
        request.AddRange(queryArray);
        byte[] encoded = RouterOsSentenceCodec.Encode(request);

        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<IReadOnlyDictionary<string, string>>();
        string? pendingTrap = null;
        long totalBytes = 0;
        int rowCount = 0;

        while (true)
        {
            IReadOnlyList<string> sentence = await RouterOsSentenceCodec
                .ReadAsync(stream, cancellationToken).ConfigureAwait(false);

            foreach (string word in sentence)
            {
                totalBytes += Encoding.UTF8.GetByteCount(word);
                if (totalBytes > MaxResponseBytes)
                    throw new RouterOsProtocolException("RouterOS API response exceeds its memory limit.");
            }

            if (sentence.Count == 0) continue;
            RouterOsReply reply = RouterOsSentenceCodec.Parse(sentence);

            switch (reply.Kind)
            {
                case "!re":
                    if (rowCount >= maxRows)
                        throw new RouterOsProtocolException("RouterOS API returned too many rows.");
                    rowCount++;
                    rows.Add(reply.Data);
                    break;
                case "!empty":
                    // RouterOS 7.18+ !empty is not a command terminator.
                    break;
                case "!trap":
                    // Keep consuming until !done; the socket remains aligned.
                    pendingTrap = reply.Data.TryGetValue("message", out string? message)
                        && !string.IsNullOrEmpty(message) ? message : "RouterOS API trap";
                    break;
                case "!fatal":
                    throw new RouterOsProtocolException(reply.Data.TryGetValue("message", out string? fatal)
                        && !string.IsNullOrEmpty(fatal) ? fatal : "RouterOS API fatal");
                case "!done":
                    if (pendingTrap is not null)
                        throw new RouterOsTrapException(pendingTrap);
                    return new RouterOsCommandResult(rows, reply.Data);
                default:
                    throw new RouterOsProtocolException("Unexpected RouterOS API reply type.");
            }
        }
    }
}
