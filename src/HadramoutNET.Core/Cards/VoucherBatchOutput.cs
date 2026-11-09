namespace HadramoutNET.Core.Cards;

/// <summary>
/// After an attempted remote upload, only acknowledged cards may be printed.
/// Any unknown result must be reconciled manually; never retry automatically.
/// </summary>
public static class VoucherBatchOutput
{
    public static IReadOnlyList<Voucher> Select(
        IReadOnlyList<Voucher> cards, bool uploadAttempted,
        bool published, IReadOnlyCollection<string> confirmedUsernames)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(confirmedUsernames);
        if (cards.Count == 0)
            throw new CardGenerationException("أنشئ كروتا قبل المعاينة أو الطباعة.");
        if (!uploadAttempted || published) return cards.ToArray();
        var names = confirmedUsernames.ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0)
            throw new CardGenerationException(
                "لم يتأكد إنشاء أي كرت؛ لا تطبع أو تصدّر قبل مراجعة الراوتر.");
        var confirmed = cards.Where(c => names.Contains(c.Username)).ToArray();
        if (confirmed.Length == 0 || confirmed.Length != names.Count)
            throw new CardGenerationException(
                "الأسماء المؤكدة لا تطابق هذه الدفعة؛ يلزم مراجعة الراوتر.");
        return confirmed;
    }
}

/// <summary>
/// Offline preview-only sample. Does not mutate the current batch, save a
/// package, reserve names, create an audit event or access MikroTik.
/// </summary>
public static class VoucherPreviewSample
{
    public static IReadOnlyList<Voucher> Create(
        CardGenerationOptions options, VoucherCardMetadataOptions metadata,
        DateTimeOffset timestamp, Func<int, int>? randomIndex = null) =>
        VoucherCardMetadata.Attach(
            VoucherGenerator.Generate(options, _ => false, randomIndex),
            metadata, timestamp);
}
