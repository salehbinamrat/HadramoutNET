namespace HadramoutNET.Core.Cards;

/// <summary>
/// Immutable, value-based identity of the creation fields for a generated batch.
/// Changing artwork or paper layout is intentionally allowed: it does not change
/// the credentials, printed voucher metadata, or RouterOS quotas of the batch.
/// </summary>
public sealed record VoucherDraftSettings(
    CardGenerationOptions Generation,
    VoucherCardMetadataOptions Metadata)
{
    public static VoucherDraftSettings Capture(
        CardGenerationOptions generation, VoucherCardMetadataOptions metadata)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(metadata);
        CardGenerationPresetCodec.Validate(generation);
        VoucherCardMetadata.Validate(metadata);
        return new VoucherDraftSettings(generation, metadata);
    }

    public bool Matches(
        CardGenerationOptions generation, VoucherCardMetadataOptions metadata) =>
        Generation == generation && Metadata == metadata;
}
