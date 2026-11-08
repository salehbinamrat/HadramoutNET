using System.IO;
using System.Text;

namespace HadramoutNET.Core.RouterOS;

public sealed record RouterOsReply(string Kind, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Byte-oriented RouterOS API sentence codec. No network connections or router
/// writes occur here. Limits match the pinned original Python implementation.
/// </summary>
public static class RouterOsSentenceCodec
{
    public const int MaxSentenceBytes = 64 * 1024 * 1024;
    public const int MaxWords = 4096;

    public static byte[] Encode(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        using var stream = new MemoryStream();
        int count = 0;
        foreach (string word in words)
        {
            if (++count > MaxWords)
                throw new InvalidDataException("RouterOS API sentence contains too many words.");
            byte[] utf8 = Encoding.UTF8.GetBytes(word);
            if (utf8.Length > RouterOsWordCodec.MaxResponseWordBytes)
                throw new InvalidDataException("RouterOS API word is too long.");
            byte[] length = RouterOsWordCodec.EncodeLength(utf8.Length);
            if ((long)stream.Length + length.Length + utf8.Length + 1 > MaxSentenceBytes)
                throw new InvalidDataException("RouterOS API sentence is too large.");
            stream.Write(length);
            stream.Write(utf8);
        }
        stream.WriteByte(0);
        return stream.ToArray();
    }

    public static async Task<IReadOnlyList<string>> ReadAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var result = new List<string>();
        int total = 0;
        var lengthBuffer = new byte[5];

        while (true)
        {
            await stream.ReadExactlyAsync(lengthBuffer.AsMemory(0, 1), cancellationToken);
            byte first = lengthBuffer[0];
            int prefixSize;

            if ((first & 0x80) == 0) prefixSize = 1;
            else if ((first & 0xC0) == 0x80) prefixSize = 2;
            else if ((first & 0xE0) == 0xC0) prefixSize = 3;
            else if ((first & 0xF0) == 0xE0) prefixSize = 4;
            else if (first == 0xF0) prefixSize = 5;
            else throw new InvalidDataException("Invalid RouterOS API length prefix.");

            if (prefixSize > 1)
                await stream.ReadExactlyAsync(lengthBuffer.AsMemory(1, prefixSize - 1), cancellationToken);

            int length = RouterOsWordCodec.DecodeLength(
                lengthBuffer.AsSpan(0, prefixSize), out _);

            if (length == 0) return result;
            if (result.Count >= MaxWords || (long)total + length > MaxSentenceBytes)
                throw new InvalidDataException("RouterOS API response sentence is too large.");

            byte[] data = new byte[length];
            await stream.ReadExactlyAsync(data.AsMemory(), cancellationToken);
            result.Add(Encoding.UTF8.GetString(data));
            total += length;
        }
    }

    public static RouterOsReply Parse(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        using var iter = words.GetEnumerator();
        if (!iter.MoveNext())
            return new RouterOsReply("", new Dictionary<string, string>());

        string kind = iter.Current ?? "";
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        while (iter.MoveNext())
        {
            string word = iter.Current ?? "";
            string pair = word.StartsWith("=", StringComparison.Ordinal) ? word[1..] : word;
            int separator = pair.IndexOf('=');
            if (separator >= 0)
            {
                string key = pair[..separator].TrimStart('=');
                string value = pair[(separator + 1)..];
                data[key] = value;
            }
            else if (word.StartsWith("=", StringComparison.Ordinal))
            {
                data[pair] = "";
            }
        }
        return new RouterOsReply(kind, data);
    }
}
