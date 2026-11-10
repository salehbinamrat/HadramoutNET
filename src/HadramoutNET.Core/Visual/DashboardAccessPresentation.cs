using System.Globalization;
using HadramoutNET.Core.Licensing;

namespace HadramoutNET.Core.Visual;

/// <summary>
/// Dashboard only presents current verified rights; it never starts a trial,
/// claims a code, or makes network requests.
/// </summary>
public sealed record DashboardAccessState(string Text, bool Authorized);

public static class DashboardAccessPresentation
{
    public static DashboardAccessState From(
        LicenseAccess paid, TrialAccess trial, bool sessionAuthorized)
    {
        ArgumentNullException.ThrowIfNull(paid);
        ArgumentNullException.ThrowIfNull(trial);
        if (!sessionAuthorized)
            return new("حالة الوصول: الجلسة غير مصرح بها؛ سجل الدخول مجددا.", false);
        if (paid.Permitted && paid.Reason == "online_valid")
        {
            string remain = paid.RemainingSeconds >= 0
                ? " — المتبقي: " + Duration(paid.RemainingSeconds) : "";
            return new("الترخيص المدفوع: مفعل" + remain, true);
        }
        if (trial.Mode == "trial" && trial.Permitted)
            return new("التجربة المجانية — المتبقي: " +
                Duration(trial.RemainingSeconds), true);
        return new("حالة الوصول: يلزم إعادة التحقق من الترخيص أو التجربة.", false);
    }
    private static string Duration(long seconds)
    {
        long d = Math.Max(0, seconds) / 86400;
        long h = Math.Max(0, seconds) % 86400 / 3600;
        long m = Math.Max(0, seconds) % 3600 / 60;
        return d.ToString(CultureInfo.InvariantCulture) + " يوم، " +
            h.ToString(CultureInfo.InvariantCulture) + " ساعة، " +
            m.ToString(CultureInfo.InvariantCulture) + " دقيقة";
    }
}
