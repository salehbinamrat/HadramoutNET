using System.Buffers.Binary;

namespace HadramoutNET.Core.Printing;

/// <summary>
/// Bounded, validated in-memory PNG background. WPF converts imported formats
/// to PNG with Windows' own decoder; PDFSharp Core accepts PNG natively.
/// No file paths, original Python DB or external image service is required.
/// </summary>
public sealed record CardBackgroundPng(byte[] Png, int Width, int Height);

public static class CardBackgroundAsset
{
    public const int MaxFileBytes = 15 * 1024 * 1024;
    public const long MaxPixels = 32_000_000;

    private static readonly byte[] PngMagic = [137,80,78,71,13,10,26,10];

    public static CardBackgroundPng? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        // Preflight encoded size BEFORE allocating potentially huge buffers.
        if (base64.Length > ((MaxFileBytes + 2) / 3) * 4 + 4)
            throw new PrintLayoutException("حجم صورة الخلفية يتجاوز 15 ميجابايت.");
        byte[] raw;
        try { raw = Convert.FromBase64String(base64); }
        catch (FormatException) {
            throw new PrintLayoutException("بيانات صورة خلفية الكرت غير صالحة.");
        }
        if (raw.Length < 33 || raw.Length > MaxFileBytes ||
            !raw.AsSpan(0, 8).SequenceEqual(PngMagic) ||
            !raw.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new PrintLayoutException("صورة الخلفية يجب أن تكون PNG سليمة.");

        uint width = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(20, 4));
        if (width == 0 || height == 0 || (long)width * height > MaxPixels)
            throw new PrintLayoutException("صورة الخلفية أكبر من 32 مليون بكسل.");

        // Require 8-bit RGB/RGBA or indexed PNG, known-good formats shared
        // by PDFsharp and WPF. CRC/zlib is checked by their image decoders.
        byte depth = raw[24], color = raw[25];
        if (depth != 8 || color is not (2 or 3 or 6))
            throw new PrintLayoutException("تنسيق PNG غير مناسب لخلفية الكرت.");
        return new CardBackgroundPng(raw, checked((int)width), checked((int)height));
    }
}
