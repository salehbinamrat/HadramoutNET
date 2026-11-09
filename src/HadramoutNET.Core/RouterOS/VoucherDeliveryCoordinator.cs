using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Data;

namespace HadramoutNET.Core.RouterOS;

/// <summary>
/// Write-ahead wrapper used by the WPF voucher upload flow. Every confirmed
/// attempt must be recorded durably BEFORE any RouterOS read or mutation.
/// Neither rejected nor uncertain calls are automatically retried.
/// </summary>
public sealed class VoucherDeliveryCoordinator(VoucherDeliveryJournal journal)
{
    private readonly VoucherDeliveryJournal _journal =
        journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly RouterVoucherPublisher _standard = new();
    private readonly RouterUserManagerPublisher _userManager = new();

    public async Task<VoucherPublishReport> PublishAsync(
        RouterAuthorizedSession session,
        IReadOnlyList<Voucher> vouchers,
        string service, string profile, string customer,
        bool confirmed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vouchers);

        if (!confirmed)
            return new VoucherPublishReport(false, "confirmation_required",
                "يجب تأكيد رفع الكروت قبل الاتصال بالراوتر.",
                vouchers.Count, 0, false, []);

        // If SQLite initialization or write fails, NO publisher is entered.
        long attempt = _journal.Begin(session.Identity, session.Endpoint,
            service, profile, vouchers.Count);
        // Conservative write-ahead: before even checking remote duplicates,
        // so process termination at any later point is never silently replayed.
        _journal.MarkDispatching(attempt);
        try
        {
            VoucherPublishReport result = service == "userman"
                ? await _userManager.PublishAsync(session, vouchers,
                    new UserManagerPublishOptions(profile, Confirmed: true,
                        Customer: customer), cancellationToken).ConfigureAwait(false)
                : await _standard.PublishAsync(session, vouchers,
                    new VoucherPublishOptions(service, profile, Confirmed: true),
                    cancellationToken).ConfigureAwait(false);

            string state = result.Success ? "accepted"
                : result.Uncertain ? "uncertain"
                : result.Added > 0 ? "partial" : "rejected";
            _journal.Complete(attempt, state, result.Reason, result.Added);
            return result;
        }
        catch
        {
            // Lost acknowledgment, crash or an inaccessible audit database
            // must NEVER cause automatic replay of remote mutations.
            // If this local update fails, "dispatching" remains as the
            // durable unresolved state for the operator to investigate.
            try
            {
                _journal.Complete(attempt, "uncertain", "unexpected_failure", 0);
            }
            catch (InvalidOperationException) { }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
            catch (IOException) { }
            throw;
        }
    }
}
