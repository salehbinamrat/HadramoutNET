namespace HadramoutNET.Core.Cards;

/// <summary>
/// Display the actual voucher of an unchanged generated batch. Otherwise
/// create exactly one throwaway example without mutating the batch, a database,
/// the RouterOS account inventory or the upload journal.
/// </summary>
public static class VoucherInlinePreview
{
    public static Voucher Choose(CardGenerationOptions current,
        VoucherCardMetadataOptions metadata, IReadOnlyList<Voucher> generated,
        bool generatedStillCurrent, DateTimeOffset now,
        Func<int, int>? randomIndex = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(generated);
        if (generatedStillCurrent && generated.Count > 0)
            return generated[0];

        CardGenerationOptions one = current with { Quantity = 1 };
        return VoucherPreviewSample.Create(one, metadata, now, randomIndex)[0];
    }
}
