using System.IO;
using System.Text;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// RouterOS API length framing, ported from the pinned Python source.
/// This class does not connect to, authenticate to, or modify any router.
/// </summary>
public static class RouterOsWordCodec
{
    public const int MaxResponseWordBytes = 32 * 1024 * 1024;

    public static byte[] EncodeLength(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        if (length < 0x80)
            return [(byte)length];

        if (length < 0x4000)
            return [(byte)((length >> 8) | 0x80), (byte)length];

        if (length < 0x200000)
            return [(byte)((length >> 16) | 0xC0), (byte)(length >> 8), (byte)length];

        if (length < 0x10000000)
            return [(byte)((length >> 24) | 0xE0), (byte)(length >> 16),
                    (byte)(length >> 8), (byte)length];

        return [0xF0, (byte)(length >> 24), (byte)(length >> 16),
                (byte)(length >> 8), (byte)length];
    }

    public static int DecodeLength(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.IsEmpty) throw new EndOfStreamException("Missing RouterOS API word length.");

        byte first = buffer[0];
        int required;
        uint value;

        if ((first & 0x80) == 0)
        {
            required = 1;
            value = first;
        }
        else if ((first & 0xC0) == 0x80)
        {
            required = 2;
            EnsureAvailable(buffer, required);
            value = (uint)(((first & 0x3F) << 8) | buffer[1]);
        }
        else if ((first & 0xE0) == 0xC0)
        {
            required = 3;
            EnsureAvailable(buffer, required);
            value = (uint)(((first & 0x1F) << 16) | (buffer[1] << 8) | buffer[2]);
        }
        else if ((first & 0xF0) == 0xE0)
        {
            required = 4;
            EnsureAvailable(buffer, required);
            value = ((uint)(first & 0x0F) << 24) | ((uint)buffer[1] << 16)
                    | ((uint)buffer[2] << 8) | buffer[3];
        }
        else if (first == 0xF0)
        {
            required = 5;
            EnsureAvailable(buffer, required);
            value = ((uint)buffer[1] << 24) | ((uint)buffer[2] << 16)
                    | ((uint)buffer[3] << 8) | buffer[4];
        }
        else throw new InvalidDataException("Invalid RouterOS API length prefix.");

        if (value > MaxResponseWordBytes)
            throw new InvalidDataException("RouterOS API response word exceeds the safety limit.");

        consumed = required;
        return (int)value;
    }

    public static byte[] EncodeWord(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] payload = Encoding.UTF8.GetBytes(value);
        if (payload.Length > MaxResponseWordBytes)
            throw new ArgumentOutOfRangeException(nameof(value), "RouterOS API word is too long.");
        byte[] size = EncodeLength(payload.Length);
        byte[] result = new byte[size.Length + payload.Length];
        size.CopyTo(result, 0);
        payload.CopyTo(result, size.Length);
        return result;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> buffer, int required)
    {
        if (buffer.Length < required) throw new EndOfStreamException("Truncated RouterOS API length.");
    }
}
