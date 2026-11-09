using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HadramoutNET.Core.Cards;
using HadramoutNET.Core.Printing;
using HadramoutNET.Core.Data;
using HadramoutNET.Core.Exporting;
using HadramoutNET.Core.Licensing;
using HadramoutNET.Core.RouterOS;
using Microsoft.Win32;

namespace HadramoutNET.App;

/// <summary>
/// First functional .NET workspace; source Python application is untouched.
/// All data reads require the currently authenticated, authorized RouterOS
/// session. Draft voucher exports are intentionally offline and reviewed.
/// </summary>
public partial class WorkspaceWindow : Window
{
    private readonly RouterAuthorizedSession _session;
    private readonly PaidLicenseCoordinator _paid;
    private readonly TrialStateManager _trial;
    private readonly Stopwatch _uptime;
    private readonly RouterDashboardReader _dashboard = new();
    private readonly RouterAccountDirectory _directory = new();
    private readonly RouterNetworkInventory _networkInventory = new();
    private readonly UserManagerDatabaseStatus _umDatabaseStatus = new();
    private bool _readingUmDatabase;
    private SessionMaintenanceJournal? _maintenanceAudit;
    private RouterSessionDisconnect? _sessionDisconnect;
    private RouterSessionReconciler? _sessionReconciler;
    private bool _readingMaintenanceAudit;
    private bool _recheckingSession;
    private readonly DispatcherTimer _heartbeat = new();
    private readonly CancellationTokenSource _cancel = new();
    private IReadOnlyList<Voucher> _generated = [];
    private PrintLayoutOptions _printLayout = new();
    private bool _published;
    private bool _uploading;
    private bool _verifyingUmDraft;
    private readonly UserManagerVoucherVerifier _umDraftVerifier = new();
    private VoucherDeliveryJournal? _voucherAudit;
    private VoucherDeliveryCoordinator? _deliveryCoordinator;
    private bool _deliveryUncertain;
    private int _userManagerVersion;
    private IReadOnlyList<UserManagerProfileRow> _userManagerProfiles = [];
    private PackageSettingsStore? _packages;
    private bool _checking;
    private bool _refreshing;
    private bool _readingPpp;
    private bool _readingNetworkServers;
    private bool _readingNetworkNeighbors;
    private bool _readingNetworkSessions;
    private bool _disconnectingSession;
    private IReadOnlyList<NetworkNeighbor> _neighbors = [];
    private bool _legacyBusy;
    private bool _legacyReading;
    private string? _legacySource;
    private LegacyImportReport? _legacyPreview;
    private int _legacyOffset;
    // Keep the applied filter stable while users paginate or refresh the archive.
    private string _legacyAppliedSearch = string.Empty;
    private const int LegacyPageSize = 100;

    private sealed record HotspotRow(
        string Name, string Profile, string Disabled, string LimitUptime, string LimitBytes);

    public WorkspaceWindow(RouterAuthorizedSession session,
        PaidLicenseCoordinator paid, TrialStateManager trial, Stopwatch uptime)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _paid = paid ?? throw new ArgumentNullException(nameof(paid));
        _trial = trial ?? throw new ArgumentNullException(nameof(trial));
        _uptime = uptime ?? throw new ArgumentNullException(nameof(uptime));
        InitializeComponent();
        RouterCaption.Text = $"{session.Identity} — {session.Endpoint}";
        _heartbeat.Interval = TimeSpan.FromSeconds(20);
        _heartbeat.Tick += Heartbeat_Tick;
    }

    private async void WorkspaceLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string databasePath = PackageSettingsStore.DefaultPath(basePath);
            _packages = new PackageSettingsStore(databasePath);
            // A durable SQLite journal is mandatory before enabling any
            // remote disconnect. Failure leaves its UI button disabled.
            _maintenanceAudit = new SessionMaintenanceJournal(databasePath);
            _sessionDisconnect = new RouterSessionDisconnect(_maintenanceAudit);
            _sessionReconciler = new RouterSessionReconciler(_maintenanceAudit);
            DisconnectSessionButton.IsEnabled = true;
            RecheckSessionButton.IsEnabled = true;
            try
            {
                _voucherAudit = new VoucherDeliveryJournal(databasePath);
                _deliveryCoordinator = new VoucherDeliveryCoordinator(_voucherAudit);
                UploadCardsButton.IsEnabled = true;
                ReconcileVoucherButton.IsEnabled = true;
                var unresolved = _voucherAudit.ReadUnresolved(_session.Identity, _session.Endpoint, 20);
                if (unresolved.Count != 0)
                    CardsNotice.Text = $"تنبيه: توجد {unresolved.Count} عملية رفع غير محسومة لهذا الراوتر. راجع سجل رفع الكروت قبل أي محاولة جديدة.";
            }
            catch (Exception)
            {
                _voucherAudit = null;
                _deliveryCoordinator = null;
                UploadCardsButton.IsEnabled = false;
                ReconcileVoucherButton.IsEnabled = false;
                CardsNotice.Text = "تعذر تهيئة سجل رفع الكروت؛ أوقف البرنامج الرفع حماية من التكرار.";
            }
            ReloadPackages();
        }
        catch (Exception)
        {
            UploadCardsButton.IsEnabled = false;
            ReconcileVoucherButton.IsEnabled = false;
            WorkspaceStatus.Text = "تعذر تهيئة قاعدة بيانات HadramoutNET أو سجل التدقيق؛ أُوقفت عمليات الفصل ورفع الكروت.";
        }

        _heartbeat.Start();
        await LoadDashboardAsync();
    }

    private void ShowDashboard_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 0;
    private void ShowCards_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 1;
    private void ShowHotspot_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 2;
    private void ShowPackages_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 3;
    private void ShowPpp_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 5;
        _ = LoadPppAsync();
    }
    private void ShowUserManager_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 4;
    private void ShowLegacy_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 6;
        _ = LoadLegacyHistoryAsync();
    }

    private void ShowNetwork_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 7;
        _ = LoadNetworkServersAsync();
        _ = LoadMaintenanceAuditAsync();
    }
    private void RefreshUserManager_Click(object sender, RoutedEventArgs e) =>
        _ = LoadUserManagerAsync();
    private void Logout_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshDashboard_Click(object sender, RoutedEventArgs e) =>
        _ = LoadDashboardAsync();
    private void RefreshHotspot_Click(object sender, RoutedEventArgs e) =>
        _ = LoadHotspotAsync();
    private void RefreshPpp_Click(object sender, RoutedEventArgs e) =>
        _ = LoadPppAsync();

    private bool _loadingUserManager;

    /// <summary>Explicit read only: a permission failure is NOT an empty list.</summary>
    private async Task LoadUserManagerAsync()
    {
        if (_loadingUserManager || !IsLoaded) return;
        _loadingUserManager = true;
        RefreshUserManagerButton.IsEnabled = false;
        WorkspaceStatus.Text = "جارٍ اكتشاف User Manager وقراءة المستخدمين والباقات...";
        UmProfilesGrid.ItemsSource = null;
        UmUsersGrid.ItemsSource = null;
        try
        {
            UserManagerCatalogResult result =
                await new UserManagerCatalog().ReadAsync(_session, _cancel.Token);
            if (!IsLoaded) return;
            _userManagerVersion = result.UsersComplete ? result.Version : 0;
            _userManagerProfiles = result.UsersComplete ?
                result.Profiles : [];

            if (result.Version != 0)
            {
                UmProfilesGrid.ItemsSource = result.Profiles;
                if (result.UsersComplete)
                    UmUsersGrid.ItemsSource = result.Users;
            }

            WorkspaceStatus.Text = result.Status switch
            {
                "ready" => "User Manager v" + result.Version +
                    " — " + result.Profiles.Count + " باقة و" +
                    result.Users.Count + " مستخدم (قراءة فقط).",
                "not_installed" => "User Manager غير مثبت على هذا الراوتر.",
                "permission_denied" => "لا تتوفر صلاحية قراءة User Manager.",
                "users_unavailable" => "تمت قراءة الباقات من User Manager v" +
                    result.Version + " لكن تعذرت قراءة المستخدمين؛ لا تعني القائمة الفارغة صفرا.",
                _ => "تعذرت قراءة User Manager."
            };
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
                WorkspaceStatus.Text = "فشل قراءة User Manager؛ لا تعتبر الأعداد غير المعروفة صفرا.";
        }
        finally
        {
            _loadingUserManager = false;
            if (IsLoaded) RefreshUserManagerButton.IsEnabled = true;
        }
    }

    private async Task LoadDashboardAsync()
    {
        if (_refreshing || !IsLoaded) return;
        _refreshing = true;
        DashboardNotice.Text = "جارٍ تحديث البيانات من الراوتر...";
        try
        {
            RouterDashboardSnapshot data = await _dashboard.ReadAsync(
                _session, _cancel.Token);
            if (!IsLoaded) return;
            CpuValue.Text = FormatPercent(data.CpuPercent);
            RamValue.Text = FormatPercent(data.MemoryPercent);
            HotspotTotalValue.Text = FormatCount(data.HotspotUsers);
            HotspotActiveValue.Text = FormatCount(data.HotspotActive);
            PppActiveValue.Text = FormatCount(data.PppActive);
            UserManagerValue.Text = FormatCount(data.UserManagerUsers);
            RouterVersionText.Text = "إصدار RouterOS: " + data.RouterOsVersion;
            RouterUptimeText.Text = "وقت تشغيل الراوتر: " + data.Uptime;
            DashboardNotice.Text = data.UnavailableServices.Count == 0 ?
                "تم تحديث بيانات الشبكة بنجاح." :
                "خدمات غير متاحة: " + string.Join("، ", data.UnavailableServices) +
                " (ظهرت بشرطة، لا بصفر).";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
                DashboardNotice.Text = "تعذر قراءة البيانات؛ لم يتم تغيير أي إعدادات في MikroTik.";
        }
        finally { _refreshing = false; }
    }

    private async Task LoadHotspotAsync()
    {
        if (!IsLoaded) return;
        WorkspaceStatus.Text = "جارٍ قراءة مستخدمي Hotspot...";
        try
        {
            var read = await _session.ReadAsync("/ip/hotspot/user/print",
                new Dictionary<string, string>
                {
                    [".proplist"] = "name,profile,disabled,limit-uptime,limit-bytes-total"
                }, cancellationToken: _cancel.Token);
            if (!IsLoaded) return;
            UsersGrid.ItemsSource = read.Rows.Select(row => new HotspotRow(
                Field(row, "name"), Field(row, "profile"), Field(row, "disabled"),
                Field(row, "limit-uptime"), Field(row, "limit-bytes-total"))).ToArray();
            WorkspaceStatus.Text = "تم تحميل " +
                read.Rows.Count.ToString(CultureInfo.InvariantCulture) + " مستخدم Hotspot.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
                WorkspaceStatus.Text = "خدمة Hotspot غير متاحة، أو لا تملك صلاحية قراءة المستخدمين.";
        }
    }

    private async Task LoadPppAsync()
    {
        if (_readingPpp || !IsLoaded || !ConfirmCurrentAccess()) return;
        _readingPpp = true;
        PppNotice.Text = "جارٍ قراءة حسابات PPPoE...";
        try
        {
            RouterDirectoryResult result = await _directory.ReadPppAsync(_session, _cancel.Token);
            if (!IsLoaded) return;
            PppGrid.ItemsSource = result.Available ? result.Accounts : null;
            PppNotice.Text = result.Available
                ? $"تم تحميل {result.Accounts.Count} حساب PPPoE بدون كلمات مرور."
                : "تعذر الوصول لحسابات PPPoE أو لا توجد صلاحية قراءة.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded) PppNotice.Text = "تعذر قراءة حسابات PPPoE؛ لا تغييرات على الراوتر.";
        }
        finally { _readingPpp = false; }
    }

    private static string Field(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value) ? value : "";

    private async void Heartbeat_Tick(object? sender, EventArgs e)
    {
        if (_checking || !IsLoaded) return;
        _checking = true;
        try
        {
            if (_paid.NeedsCheck(_uptime.Elapsed))
            {
                var result = await _paid.CheckStoredAsync(DateTimeOffset.UtcNow,
                    _uptime.Elapsed, _cancel.Token);
                if (!result.Permitted)
                    WorkspaceStatus.Text = result.Message;
            }

            _ = _trial.AdvanceVerifiedClock(DateTimeOffset.UtcNow);
            if (!_session.HasCurrentAccess && IsLoaded)
                LockWorkspace();
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_session.HasCurrentAccess && IsLoaded)
                LockWorkspace();
        }
        finally { _checking = false; }
    }

    private void LockWorkspace()
    {
        WorkspaceStatus.Text = "انتهت صلاحية الجلسة. يجب إعادة التحقق وتسجيل الدخول.";
        _heartbeat.Stop();
        Close();
    }

    private void GenerateCards_Click(object sender, RoutedEventArgs e)
    {
        if (_uploading || _verifyingUmDraft)
        {
            CardsNotice.Text = "لا يمكن استبدال المسودة أثناء رفعها أو فحصها على الراوتر.";
            return;
        }
        if (!ConfirmCurrentAccess()) return;
        try
        {
            if (!int.TryParse(QtyBox.Text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int quantity) ||
                !int.TryParse(CardLengthBox.Text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int length))
                throw new ArgumentException("الكمية والطول يجب أن يكونا أرقاما إنجليزية صحيحة.");
            string mode = Choice(CardFormatCombo);
            var options = new CardGenerationOptions(
                quantity, length, CardAlphabetBox.Text, mode,
                PasswordLength: 6, PasswordCharacters: "abcdefghijklmnopqrstuvwxyz0123456789");

            // This is a LOCAL draft. A remote uniqueness and profile check is
            // mandatory before actually importing it to the router.
            _generated = VoucherGenerator.Generate(options, _ => false);
            _published = false;
            _deliveryUncertain = false;
            CardGrid.ItemsSource = _generated;
            CardsNotice.Text = $"تم إنشاء {_generated.Count} مسودة؛ لم ترفع إلى MikroTik.";
            WorkspaceStatus.Text = "راجع الأرقام وكلمات المرور قبل تصدير أي ملف.";
        }
        catch (Exception ex) when (ex is CardGenerationException or ArgumentException)
        {
            CardsNotice.Text = ex.Message;
        }
    }

    private async void UploadCards_Click(object sender, RoutedEventArgs e)
    {
        if (_uploading || _verifyingUmDraft || !ConfirmCurrentAccess()) return;
        if (_generated.Count == 0 || _published || _deliveryUncertain)
        {
            CardsNotice.Text = _deliveryUncertain ?
                "نتيجة رفع سابق غير مؤكدة: راجع المستخدمين على الراوتر أولا، ثم أنشئ مسودة جديدة." :
                (_published ? "هذه المسودة تم رفعها بالفعل، لا تعِد إرسالها." :
                "أنشئ مسودة كروت قبل الرفع.");
            return;
        }
        if (_deliveryCoordinator is null)
        {
            CardsNotice.Text = "سجل رفع الكروت غير متاح؛ لم يُرسل أي أمر للراوتر.";
            return;
        }
        string service = Choice(CardServiceCombo);
        string profile = CardProfileBox.Text.Trim();
        MessageBoxResult confirmed = MessageBox.Show(this,
            $"سيُرسل {_generated.Count} كرت إلى {service} بالباقة «{profile}».\n" +
            "سيُفحص عدم التكرار وصلاحية الباقة قبل البدء.\n" +
            "قد يحتاج أي فشل جزئي إلى مراجعة الكروت الموجودة يدويا.\n" +
            "هل تؤكد رفع هذه المسودة؟",
            "تأكيد رفع الكروت إلى MikroTik",
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes) return;

        _uploading = true;
        UploadCardsButton.IsEnabled = false;
        CardsNotice.Text = "جارٍ فحص المستخدمين والباقة ثم الرفع بدون تكرار تلقائي...";
        try
        {
            VoucherPublishReport result = await _deliveryCoordinator.PublishAsync(
                _session, _generated, service, profile, UmCustomerBox.Text.Trim(),
                confirmed: true, _cancel.Token);
            if (!IsLoaded) return;
            if (result.Success) _published = true;
            if (result.Uncertain || result.Added > 0) _deliveryUncertain = true;
            CardsNotice.Text = result.Message +
                $" — تمت إضافة {result.Added} من {result.Attempted}.";
            if (!result.Success && (result.Added > 0 || result.Uncertain))
                MessageBox.Show(this,
                    "الرفع غير مكتمل أو لا يمكن الجزم بنتيجة إنشاء أحد المستخدمين.\n" +
                    "الأسماء المؤكدة: " + string.Join("، ", result.AddedUsernames.Take(12)) +
                    (result.Added > 12 ? "..." : "") +
                    "\nراجع المستخدمين على MikroTik قبل إعادة الرفع.",
                    "رفع جزئي — يلزم المصالحة",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            if (IsLoaded)
            {
                _deliveryUncertain = true;
                CardsNotice.Text = "تعذر الرفع وقد تكون بعض الأوامر وصلت؛ راجع الراوتر وأنشئ مسودة جديدة.";
            }
        }
        finally
        {
            _uploading = false;
            if (IsLoaded) UploadCardsButton.IsEnabled = true;
        }
    }

    private async void VerifyUmDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_verifyingUmDraft || _uploading || !ConfirmCurrentAccess()) return;
        if (Choice(CardServiceCombo) != "userman" || _generated.Count == 0)
        {
            CardsNotice.Text = "اختر User Manager وأنشئ مسودة كروت قبل التحقق.";
            return;
        }

        var draft = _generated;
        string profile = CardProfileBox.Text.Trim();
        _verifyingUmDraft = true;
        VerifyUmDraftButton.IsEnabled = false;
        CardsNotice.Text = "جارٍ التحقق للقراءة فقط من أسماء المسودة وروابط الباقات على User Manager...";
        try
        {
            UserManagerVoucherVerificationResult result =
                await _umDraftVerifier.VerifyAsync(_session,
                    draft.Select(card => card.Username).ToArray(),
                    profile, _cancel.Token);
            if (!IsLoaded) return;
            if (!ReferenceEquals(draft, _generated) ||
                Choice(CardServiceCombo) != "userman" ||
                !string.Equals(profile, CardProfileBox.Text.Trim(), StringComparison.Ordinal))
            {
                CardsNotice.Text = "تغيرت خيارات المسودة أثناء الفحص؛ النتائج ملغاة.";
                return;
            }
            if (!result.Available)
            {
                CardsNotice.Text = result.Status switch
                {
                    "limit_exceeded" => "عدد سجلات User Manager يتجاوز الحد الآمن للفحص.",
                    "incomplete" => "قراءة User Manager غير مكتملة؛ لم تُعتبر أي باقة مفقودة.",
                    "not_installed" => "User Manager غير مثبت أو لا يمكن العثور عليه.",
                    _ => "تعذر التحقق من User Manager؛ لا يمكن الجزم بحالة الكروت."
                };
                return;
            }

            var grouped = result.Cards.GroupBy(card => card.State)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => DescribeUmObservation(group.Key) +
                    ": " + group.Count().ToString(CultureInfo.InvariantCulture));
            CardsNotice.Text = $"اكتمل فحص {result.Cards.Count} اسما (User Manager {result.Version}) — للقراءة فقط.";
            MessageBox.Show(this,
                string.Join("\n", grouped) +
                "\n\nوجود الحساب أو ربط الباقة لا يثبت أن الرفع السابق هو سبب ظهورهما، " +
                "ولا يؤكد قبول تسجيل الدخول أو استهلاك الباقة. " +
                "لن تُرسل أي إضافات أو إعادة محاولات. النتائج تخص المسودة الموجودة حاليا فقط.",
                "نتيجة التحقق من كروت User Manager",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsLoaded)
                CardsNotice.Text = "فشل التحقق من المسودة؛ لم يُرسل أي أمر تعديل للراوتر.";
        }
        finally
        {
            _verifyingUmDraft = false;
            if (IsLoaded) VerifyUmDraftButton.IsEnabled = true;
        }
    }

    private static string DescribeUmObservation(string state) => state switch
    {
        "profile_active" => "رابط الباقة فعال الآن (UM7)",
        "profile_ready" => "الباقة مرتبطة ولم تثبت فعاليتها (UM7)",
        "profile_used" => "رابط الباقة مستخدم أو منتهي (UM7)",
        "profile_reported" => "الباقة الحالية معلنة دون إثبات صلاحية (UM6)",
        "profile_other" => "الملف المرتبط لا يطابق الباقة المطلوبة",
        "profile_missing" => "المستخدم موجود دون سجل ربط باقة",
        "profile_unverified" => "حالة الربط غير معروفة",
        "disabled" => "حساب موجود لكنه معطل",
        "not_found" => "اسم غير موجود في القائمة المقروءة",
        _ => "حالة غير معروفة"
    };

    private async void ReconcileVoucher_Click(object sender, RoutedEventArgs e)
    {
        if (_uploading || _verifyingUmDraft || !ConfirmCurrentAccess()) return;
        if (_voucherAudit is null)
        {
            CardsNotice.Text = "سجل رفع الكروت غير متاح؛ لم يبدأ الفحص.";
            return;
        }

        _verifyingUmDraft = true;
        ReconcileVoucherButton.IsEnabled = false;
        try
        {
            var attempts = _voucherAudit.ReadUnresolved(
                    _session.Identity, _session.Endpoint, 200)
                .ToArray();
            if (attempts.Length == 0)
            {
                CardsNotice.Text = "لا توجد محاولات رفع غير محسومة لهذا الراوتر.";
                return;
            }

            VoucherReconciliationRequest? request =
                VoucherReconciliationPrompt.Show(this, attempts);
            if (request is null) return;

            var chosen = attempts.Single(item => item.Id == request.AttemptId);
            CardsNotice.Text = "جارٍ فحص محاولة الرفع السابقة للقراءة فقط...";
            var verifier = new VoucherDeliveryReconciler(_voucherAudit);
            string status;
            long checkId;
            IReadOnlyDictionary<string, int>? counts;
            if (chosen.Service == "userman")
            {
                var outcome = await verifier.ReconcileAsync(
                    _session, request.AttemptId, request.Usernames, _cancel.Token);
                status = outcome.Status;
                counts = outcome.Check?.Counts;
                checkId = outcome.Check?.Id ?? 0;
            }
            else
            {
                var outcome = await verifier.ReconcileStandardAsync(
                    _session, request.AttemptId, request.Usernames, _cancel.Token);
                status = outcome.Status;
                counts = outcome.Check?.Counts;
                checkId = outcome.Check?.Id ?? 0;
            }

            if (!IsLoaded) return;
            if (counts is null)
            {
                CardsNotice.Text = status switch
                {
                    "limit_exceeded" => "يحتوي الراوتر على سجلات أكثر من الحد الآمن للفحص.",
                    "incomplete" => "قراءة حسابات الراوتر غير مكتملة، ولم يُحفظ ملخص مضلل.",
                    "not_installed" => "User Manager غير مثبت أو غير متاح.",
                    _ => "تعذر تأكيد القراءة من الراوتر، ولم تتغير حالة المحاولة السابقة."
                };
                return;
            }

            string details = string.Join("\n", counts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (chosen.Service == "userman" ?
                        DescribeUmObservation(pair.Key) :
                        DescribeStandardObservation(pair.Key)) +
                    ": " + pair.Value.ToString(CultureInfo.InvariantCulture)));
            CardsNotice.Text =
                $"حُفظ الفحص #{checkId} للمحاولة #{request.AttemptId} — بدون أسماء كروت أو كلمات مرور.";
            MessageBox.Show(this,
                details + "\n\nهذه ملاحظات حالية فقط ولا تثبت نجاح الرفع القديم. " +
                "تبقى محاولة الرفع الأصلية غير محسومة، دون إعادة إرسال أو تعديل تلقائي.",
                "مراجعة محاولة رفع سابقة",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsLoaded)
                CardsNotice.Text = "تعذر مراجعة المحاولة؛ لم تُرسل أوامر تعديل إلى الراوتر.";
        }
        finally
        {
            _verifyingUmDraft = false;
            if (IsLoaded) ReconcileVoucherButton.IsEnabled = true;
        }
    }

    private static string DescribeStandardObservation(string state) => state switch
    {
        "profile_matches" => "الحساب والباقة مطابقان حاليا",
        "profile_other" => "الحساب موجود بباقة مختلفة",
        "profile_unknown" => "الحساب موجود لكن الباقة غير معلومة",
        "service_other" => "خدمة PPP لا تطابق PPPoE",
        "disabled" => "الحساب موجود لكنه معطل",
        "disabled_unknown" => "حالة تفعيل الحساب غير معروفة",
        "not_found" => "الاسم غير موجود في القائمة المقروءة",
        _ => "حالة غير معروفة"
    };

    private void ShowDeliveryAudit_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess()) return;
        if (_voucherAudit is null)
        {
            CardsNotice.Text = "سجل رفع الكروت غير متاح.";
            return;
        }
        try
        {
            var history = _voucherAudit.ReadRecent(100)
                .Where(item => item.RouterIdentity == _session.Identity &&
                    item.RouterEndpoint == _session.Endpoint)
                .Take(12).ToList();
            string details = history.Count == 0 ? "لم تسجل أي محاولات لهذا الراوتر." :
                string.Join("\n", history.Select(item =>
                {
                    string checkedAt;
                    if (item.Service == "userman")
                    {
                        VoucherDeliveryCheck? latest =
                            _voucherAudit.ReadChecks(item.Id, 1).FirstOrDefault();
                        checkedAt = latest is null ? "" :
                            $" | آخر فحص: {latest.ObservedUtc} ({latest.Counts.Values.Sum()} اسم)";
                        VoucherDeliveryUserManagerStage? lastStage =
                            _voucherAudit.ReadUserManagerStages(item.Id, 1).FirstOrDefault();
                        if (lastStage is not null)
                        {
                            string phase = lastStage.Stage == "create_user" ?
                                "إنشاء حساب" : "ربط باقة";
                            string receipt = lastStage.State == "acknowledged" ?
                                "إقرار API مسجل" : "نتيجة الأمر غير مؤكدة";
                            checkedAt += $" | كرت #{lastStage.Ordinal}: {phase} ({receipt})";
                        }
                    }
                    else
                    {
                        VoucherDeliveryStandardCheck? latest =
                            _voucherAudit.ReadStandardChecks(item.Id, 1).FirstOrDefault();
                        checkedAt = latest is null ? "" :
                            $" | آخر فحص: {latest.ObservedUtc} ({latest.Counts.Values.Sum()} اسم)";
                    }
                    return $"#{item.Id} | {item.CreatedAtUtc} | {item.Service} / {item.Profile} | " +
                        $"{item.Added}/{item.Attempted} | {item.StateLabel}{checkedAt}";
                }));
            MessageBox.Show(this, details +
                "\n\nالعمليات غير المؤكدة تتطلب فحص الراوتر يدويا. لا يعيد البرنامج أي كروت تلقائيا.",
                "سجل رفع الكروت (دون كلمات مرور)", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception)
        {
            CardsNotice.Text = "تعذر قراءة سجل رفع الكروت؛ لا تغييرات على الراوتر.";
        }
    }

    private static string Choice(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> CardRows()
    {
        if (_generated.Count == 0)
            throw new InvalidOperationException("أنشئ مسودة كروت قبل التصدير.");
        string service = Choice(CardServiceCombo);
        string profile = CardProfileBox.Text.Trim();
        string customer = UmCustomerBox.Text.Trim();
        if (service == "userman" && customer.Length == 0 && _userManagerVersion == 6)
            customer = _userManagerProfiles.FirstOrDefault(
                row => string.Equals(row.Name, profile, StringComparison.Ordinal))?.Owner ?? "";
        return _generated.Select(voucher => (IReadOnlyDictionary<string, string?>)
            new Dictionary<string, string?>
            {
                ["sequence"] = voucher.Sequence,
                ["username"] = voucher.Username,
                ["password"] = voucher.Password,
                ["profile"] = profile,
                ["service"] = service,
                ["um_version"] = service == "userman" && _userManagerVersion != 0 ?
                    _userManagerVersion.ToString(CultureInfo.InvariantCulture) : "",
                ["customer"] = service == "userman" ? customer : "",
                ["server"] = "all",
                ["status"] = _published ? "published" : "draft",
                ["created_at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz",
                    CultureInfo.InvariantCulture)
            }).ToList();
    }

    private void ExportExcel_Click(object sender, RoutedEventArgs e) =>
        ExportFile("Excel|*.xlsx", ".xlsx", (data, path) =>
            ExcelExporter.SaveExcel(data, path, "كروت حضرموت"));
    private void EditPrintLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess()) return;
        PrintLayoutOptions? updated = PrintLayoutEditorWindow.Edit(this, _printLayout);
        if (updated is null) return;
        _printLayout = updated;
        PrintLayoutInfo.Text = DescribePrintLayout(updated);
        CardsNotice.Text = "اعتُمد تخطيط الورقة لهذه الجلسة. احفظ الباقة للإبقاء عليه بعد إعادة التشغيل.";
    }

    private void PreviewPrint_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess()) return;
        try
        {
            WindowsVoucherPrinter.Preview(this, _generated, _printLayout);
            CardsNotice.Text = "اكتملت معاينة الورقة الأولى، ولم يُرسل أي أمر للطابعة.";
        }
        catch (Exception ex) when (ex is PrintLayoutException or InvalidOperationException)
        {
            CardsNotice.Text = "تعذرت معاينة التخطيط: " + ex.Message;
        }
    }

    private static string DescribePrintLayout(PrintLayoutOptions options) =>
        $"{options.Paper.ToUpperInvariant()} | " +
        (options.Orientation == "portrait" ? "عمودي" : "أفقي") +
        $" | {options.Rows}×{options.Columns} كرت/ورقة | " +
        $"{options.CardWidthMm:G}×{options.CardHeightMm:G} مم";

    private void PrintCards_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess()) return;
        if (_generated.Count == 0)
        {
            CardsNotice.Text = "أنشئ كروتا قبل الطباعة.";
            return;
        }

        if (MessageBox.Show(this,
            $"سيتم عرض اختيار الطابعة لإرسال {_generated.Count} كرت في نسخة واحدة.\n" +
            "سيرفض البرنامج أي تعريف لا يقبل أبعاد الورق والهوامش المحددة.\n" +
            "هل تريد متابعة الطباعة؟",
            "تأكيد الطباعة المباشرة",
            MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            var result = WindowsVoucherPrinter.Print(this, _generated, _printLayout);
            CardsNotice.Text = result.Message;
        }
        catch (Exception)
        {
            // A driver may already have accepted an uncertain job.
            // Never issue an automatic second print.
            CardsNotice.Text = "تعذرت الطباعة أو رفض التعريف المقاسات. راجع قائمة انتظار Windows قبل إعادة المحاولة.";
        }
    }

    private void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess()) return;
        if (_generated.Count == 0)
        {
            CardsNotice.Text = "أنشئ مسودة كروت قبل تصدير PDF.";
            return;
        }
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "حفظ كروت حضرموت PDF",
                FileName = "كروت حضرموت",
                Filter = "PDF|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            VoucherPdfExporter.SavePdf(_generated, dialog.FileName, _printLayout);
            CardsNotice.Text = "تم حفظ PDF بأبعاد A4 فعلية وأرقام قابلة للنسخ.";
        }
        catch (Exception ex) when (ex is IOException or PrintLayoutException or
            InvalidOperationException or UnauthorizedAccessException)
        {
            CardsNotice.Text = "تعذر تصدير PDF: " + ex.Message;
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) =>
        ExportFile("CSV|*.csv", ".csv", CardExports.SaveCsv);
    private void ExportScript_Click(object sender, RoutedEventArgs e) =>
        ExportFile("RouterOS|*.rsc", ".rsc", CardExports.SaveScript);

    private void ExportFile(string filter, string extension,
        Func<IEnumerable<IReadOnlyDictionary<string, string?>>, string, string> writer)
    {
        if (!ConfirmCurrentAccess()) return;
        try
        {
            var cards = CardRows();
            var chooser = new SaveFileDialog
            {
                Title = "حفظ كروت حضرموت",
                FileName = "كروت حضرموت",
                Filter = filter,
                DefaultExt = extension,
                AddExtension = true,
                OverwritePrompt = true
            };
            if (chooser.ShowDialog(this) != true) return;
            writer(cards, chooser.FileName);
            CardsNotice.Text = "تم حفظ الملف بنجاح — لم يتم رفع الكروت إلى الراوتر.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or
            InvalidOperationException or UnauthorizedAccessException)
        {
            CardsNotice.Text = "تعذر التصدير: " + ex.Message;
        }
    }

    private void SavePackage_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess() || _packages is null) return;
        try
        {
            var settings = new Dictionary<string, JsonElement>
            {
                ["create.quantity"] = JsonSerializer.SerializeToElement(QtyBox.Text),
                ["create.code_length"] = JsonSerializer.SerializeToElement(CardLengthBox.Text),
                ["create.characters"] = JsonSerializer.SerializeToElement(CardAlphabetBox.Text),
                ["create.format"] = JsonSerializer.SerializeToElement(Choice(CardFormatCombo)),
                ["create.service"] = JsonSerializer.SerializeToElement(Choice(CardServiceCombo)),
                ["create.profile"] = JsonSerializer.SerializeToElement(CardProfileBox.Text),
                ["create.customer"] = JsonSerializer.SerializeToElement(UmCustomerBox.Text)
            };
            foreach (var pair in PrintLayoutPresetCodec.ToSettings(_printLayout))
                settings[pair.Key] = pair.Value;
            _packages.SavePackage(PackageNameBox.Text, settings);
            ReloadPackages();
            WorkspaceStatus.Text = "حُفظت إعدادات هذه الباقة في HadramoutNET فقط.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
            InvalidOperationException)
        {
            WorkspaceStatus.Text = "تعذر حفظ الباقة: " + ex.Message;
        }
    }

    private void ReloadPackages_Click(object sender, RoutedEventArgs e) => ReloadPackages();

    private void ReloadPackages()
    {
        if (_packages is null) return;
        try { PackagesGrid.ItemsSource = _packages.ListPackages(); }
        catch (Exception) { WorkspaceStatus.Text = "تعذر قراءة الباقات المحلية."; }
    }

    private void PackagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackagesGrid.SelectedItem is not PackagePreset selected) return;
        PrintLayoutOptions layout;
        try
        {
            layout = PrintLayoutPresetCodec.FromSettings(selected.Settings);
        }
        catch (PrintLayoutException)
        {
            WorkspaceStatus.Text = "تعذر تحميل تخطيط الطباعة من الباقة: إعدادات غير صالحة؛ لم تُطبق أي خيارات.";
            return;
        }
        _printLayout = layout;
        PrintLayoutInfo.Text = DescribePrintLayout(layout);
        PackageNameBox.Text = selected.Name;
        var values = selected.Settings;
        if (values.TryGetValue("create.quantity", out var q) &&
            q.ValueKind == JsonValueKind.String) QtyBox.Text = q.GetString();
        if (values.TryGetValue("create.code_length", out var len) &&
            len.ValueKind == JsonValueKind.String) CardLengthBox.Text = len.GetString();
        if (values.TryGetValue("create.characters", out var chars) &&
            chars.ValueKind == JsonValueKind.String) CardAlphabetBox.Text = chars.GetString();
        if (values.TryGetValue("create.profile", out var profile) &&
            profile.ValueKind == JsonValueKind.String) CardProfileBox.Text = profile.GetString();
        if (values.TryGetValue("create.customer", out var owner) &&
            owner.ValueKind == JsonValueKind.String) UmCustomerBox.Text = owner.GetString();
        if (values.TryGetValue("create.format", out var format) &&
            format.ValueKind == JsonValueKind.String) SelectChoice(CardFormatCombo, format.GetString());
        if (values.TryGetValue("create.service", out var service) &&
            service.ValueKind == JsonValueKind.String) SelectChoice(CardServiceCombo, service.GetString());
        WorkspaceStatus.Text = "تم استعادة خيارات إنشاء الكروت وتخطيط الطباعة الخاص بهذه الباقة.";
    }

    private static void SelectChoice(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
    }


    private string LegacyNetDatabasePath() => PackageSettingsStore.DefaultPath(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private async void PreviewLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyBusy || !ConfirmCurrentAccess()) return;
        var chooser = new OpenFileDialog
        {
            Title = "اختر قاعدة بيانات حضرموت القديمة (SQLite v8)",
            Filter = "SQLite (*.db;*.sqlite)|*.db;*.sqlite|جميع الملفات (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (chooser.ShowDialog(this) != true) return;

        string source = chooser.FileName;
        _legacySource = null;
        _legacyPreview = null;
        _legacyBusy = true;
        PreviewLegacyButton.IsEnabled = false;
        ImportLegacyButton.IsEnabled = false;
        LegacyPreviewText.Text = "جارٍ التحقق من بنية قاعدة البيانات وسلامتها دون تعديل الملف الأصلي...";
        try
        {
            LegacyImportReport preview = await Task.Run(
                () => new LegacyDatabaseImporter(LegacyNetDatabasePath()).Preview(source),
                _cancel.Token);
            if (!IsLoaded || !ConfirmCurrentAccess()) return;
            _legacySource = source;
            _legacyPreview = preview;
            ImportLegacyButton.IsEnabled = true;
            LegacyPreviewText.Text = "تم فحص " + Path.GetFileName(source) +
                " بنجاح. راوترات: " + preview.Routers +
                "، باقات: " + preview.Packages +
                "، دفعات: " + preview.Batches +
                "، كروت: " + preview.Cards +
                "، إعدادات مسموحة: " + preview.Settings +
                ". اضغط تأكيد الاستيراد فقط إذا كانت هذه النسخة تخصك.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsLoaded)
                LegacyPreviewText.Text = "رفض فحص المصدر: ملف غير صالح أو مخطط غير مدعوم أو لا تتوفر صلاحية قراءته. لا تغييرات على قاعدة HadramoutNET.";
        }
        finally
        {
            _legacyBusy = false;
            if (IsLoaded) PreviewLegacyButton.IsEnabled = true;
        }
    }

    private async void ImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyBusy || !ConfirmCurrentAccess() ||
            _legacySource is null || _legacyPreview is null) return;

        string source = _legacySource;
        LegacyImportReport expected = _legacyPreview;
        var confirmed = MessageBox.Show(this,
            "سيتم استيراد أرشيف حضرموت إلى قاعدة HadramoutNET المستقلة لمرة واحدة فقط.\\n" +
            "المصدر للقراءة فقط، ولا تُنقل تراخيص البرنامج أو كلمات مرور الراوتر.\\n" +
            "يُحفظ سجل كلمات مرور الكروت القديمة داخل أرشيف .NET دون إظهارها في هذه الشاشة.\\n" +
            "تأكد من إغلاق برنامج حضرموت القديم أو اختيار نسخة احتياطية موثوقة.\\n" +
            "عدد الكروت في المعاينة: " + expected.Cards +
            ". ستعاد جميع الفحوص عند الاستيراد. هل توافق؟",
            "تأكيد استيراد أرشيف حضرموت", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes) return;

        _legacyBusy = true;
        PreviewLegacyButton.IsEnabled = false;
        ImportLegacyButton.IsEnabled = false;
        LegacyPreviewText.Text = "جارٍ الاستيراد بمعاملة SQLite واحدة إلى قاعدة منفصلة...";
        bool imported = false;
        try
        {
            // A confirmed transaction must finish or roll back safely even if
            // the user closes the window; it must not be silently retried.
            LegacyImportReport report = await Task.Run(
                () => new LegacyDatabaseImporter(LegacyNetDatabasePath()).Import(source));
            if (!IsLoaded) return;
            _legacySource = null;
            _legacyPreview = null;
            imported = true;
            LegacyPreviewText.Text = "تم استيراد " + report.Cards +
                " كرت و" + report.Batches + " دفعة و" +
                report.Packages + " باقة بنجاح إلى أرشيف .NET المستقل. لم يتغير الملف الأصلي.";
        }
        catch (Exception)
        {
            if (IsLoaded)
                LegacyPreviewText.Text = "تعذر الاستيراد، ربما استُورد الأرشيف سابقا أو تغير ملف المصدر أو تعذر فتحه. لم يتم استبدال السجل السابق؛ افحص السبب قبل أي إعادة.";
        }
        finally
        {
            _legacyBusy = false;
            if (IsLoaded)
            {
                PreviewLegacyButton.IsEnabled = true;
                ImportLegacyButton.IsEnabled = _legacyPreview is not null;
            }
        }
        if (imported) await LoadLegacyHistoryAsync(0);
    }

    private void SearchLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyBusy || _legacyReading || !IsLoaded || !ConfirmCurrentAccess()) return;
        _legacyAppliedSearch = LegacySearchBox.Text.Trim();
        _ = LoadLegacyHistoryAsync(0);
    }

    private void RefreshLegacy_Click(object sender, RoutedEventArgs e) =>
        _ = LoadLegacyHistoryAsync(0);
    private void PreviousLegacy_Click(object sender, RoutedEventArgs e) =>
        _ = LoadLegacyHistoryAsync(Math.Max(0, _legacyOffset - LegacyPageSize));
    private void NextLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyOffset <= int.MaxValue - LegacyPageSize)
            _ = LoadLegacyHistoryAsync(_legacyOffset + LegacyPageSize);
    }

    private async Task LoadLegacyHistoryAsync(int offset = 0)
    {
        if (_legacyBusy || _legacyReading || !IsLoaded || !ConfirmCurrentAccess()) return;
        _legacyReading = true;
        LegacyPreviousButton.IsEnabled = false;
        LegacyNextButton.IsEnabled = false;
        LegacyPageText.Text = "جارٍ قراءة الأرشيف المستقل...";
        string usernameSearch = _legacyAppliedSearch;
        try
        {
            LegacyHistoryPage page = await Task.Run(
                () => new LegacyHistoryReader(LegacyNetDatabasePath()).ReadPage(offset, LegacyPageSize, usernameSearch),
                _cancel.Token);
            if (!IsLoaded) return;
            _legacyOffset = offset;
            LegacyCardsGrid.ItemsSource = page.Cards;
            LegacyPageText.Text = page.Imported
                ? "السجلات: " + page.Total + "، المعروض: " + page.Cards.Count +
                  "، من " + (page.Cards.Count == 0 ? 0 : (long)offset + 1)
                  + " إلى " + ((long)offset + page.Cards.Count)
                : "لم يستورد أي أرشيف قديم بعد.";
            LegacyPreviousButton.IsEnabled = page.Imported && offset > 0;
            LegacyNextButton.IsEnabled = page.Imported &&
                (long)offset + LegacyPageSize < page.Total &&
                offset <= int.MaxValue - LegacyPageSize;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsLoaded) LegacyPageText.Text = "تعذر فتح أرشيف .NET؛ لم يُعدَّل أي ملف.";
        }
        finally { _legacyReading = false; }
    }


    private void RefreshUmDatabase_Click(object sender, RoutedEventArgs e) =>
        _ = LoadUmDatabaseAsync();

    private async Task LoadUmDatabaseAsync()
    {
        if (_readingUmDatabase || !IsLoaded || !ConfirmCurrentAccess()) return;
        _readingUmDatabase = true;
        RefreshUmDatabaseButton.IsEnabled = false;
        UmDatabaseVersionValue.Text = "—";
        UmDatabaseSizeValue.Text = "—";
        UmDatabaseFreeValue.Text = "—";
        UmDatabaseNotice.Text = "جارٍ قراءة حالة قاعدة User Manager دون تنفيذ أي صيانة...";
        try
        {
            UserManagerDatabaseSnapshot result = await _umDatabaseStatus.ReadAsync(
                _session, _cancel.Token);
            if (!IsLoaded) return;
            if (result.Version is 6 or 7)
                UmDatabaseVersionValue.Text = "RouterOS " + result.Version;
            if (result.Available)
            {
                UmDatabaseSizeValue.Text = result.DatabaseSize ?? "—";
                UmDatabaseFreeValue.Text = result.FreeDiskSpace ?? "—";
            }
            UmDatabaseNotice.Text = result.Status switch
            {
                "ready" => "حالة قاعدة User Manager متاحة للقراءة فقط. القيم معروضة كما أرسلها الراوتر؛ لا تُفترض وحدة إضافية.",
                "not_installed" => "User Manager غير مثبت أو لا يوجد أمر مدعوم لكشفه.",
                "permission_denied" => "الراوتر رفض صلاحية قراءة User Manager؛ لا تعني هذه الحالة أن قاعدة البيانات فارغة.",
                "empty_response" => "لم يُرجع الراوتر بيانات عن قاعدة User Manager؛ لا تتوفر قيم مؤكدة.",
                "fields_unavailable" => "الراوتر لم يُرجع حجم القاعدة والمساحة الحرة بشكل صالح وكامل.",
                "database_unavailable" => "تم تحديد إصدار User Manager لكن تعذرت قراءة حالة قاعدة البيانات.",
                _ => "تعذرت قراءة حالة قاعدة User Manager؛ لم تُغيّر أي إعدادات."
            };
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
                UmDatabaseNotice.Text =
                    "تعذر الوصول لحالة قاعدة User Manager؛ لم تُجرَ أي تغييرات على الراوتر.";
        }
        finally
        {
            _readingUmDatabase = false;
            if (IsLoaded) RefreshUmDatabaseButton.IsEnabled = true;
        }
    }

    private void RefreshServers_Click(object sender, RoutedEventArgs e) =>
        _ = LoadNetworkServersAsync();

    private void RefreshNeighbors_Click(object sender, RoutedEventArgs e) =>
        _ = LoadNetworkNeighborsAsync();

    private void RefreshSessions_Click(object sender, RoutedEventArgs e) =>
        _ = LoadNetworkSessionsAsync();

    private async void DisconnectSession_Click(object sender, RoutedEventArgs e)
    {
        if (_disconnectingSession || _readingNetworkSessions ||
            !IsLoaded || !ConfirmCurrentAccess()) return;
        if (_sessionDisconnect is null)
        {
            SessionsNotice.Text = "سجل التدقيق غير جاهز؛ لا يمكن فصل الجلسات دون حفظ سجل دائم.";
            return;
        }
        if (SessionsGrid.SelectedItem is not NetworkActiveSession selected)
        {
            SessionsNotice.Text = "اختر جلسة واحدة من القائمة أولا.";
            return;
        }

        // Two explicit, manual confirmations: the action affects a live
        // customer connection and cannot be undone by a database rollback.
        if (MessageBox.Show(this,
            "سيُقطع اتصال جلسة واحدة فقط:\n" +
            "الخدمة: " + selected.Service + "\n" +
            "المستخدم: " + selected.Username + "\n" +
            "عنوان IP: " + selected.Address + "\n" +
            "لا يتم فصل جلسات أخرى أو تنفيذ أي إجراء تلقائي. هل تريد المتابعة؟",
            "التحقق من الجلسة المحددة",
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        if (MessageBox.Show(this,
            "تأكيد نهائي: فصل جلسة " + selected.Username +
            " بالمعرف " + selected.Id + "؟\n" +
            "سيتحقق البرنامج من هوية الجلسة على الراوتر قبل الإرسال. " +
            "إذا انقطع الاتصال أثناء التنفيذ فقد تكون النتيجة غير مؤكدة ولن يعيد الأمر تلقائيا.",
            "تأكيد فصل اتصال المشترك",
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _disconnectingSession = true;
        DisconnectSessionButton.IsEnabled = false;
        SessionsGrid.IsEnabled = false;
        SessionsNotice.Text = "جارٍ التحقق من الجلسة وإرسال أمر فصل واحد إذا تطابقت...";
        try
        {
            SessionDisconnectResult result = await _sessionDisconnect.DisconnectAsync(
                _session, selected, confirmed: true, _cancel.Token);
            if (!IsLoaded) return;
            // The view is now potentially stale even if the acknowledgment
            // was lost. Require an explicit refresh before another action.
            if (result.Success || result.Uncertain)
                SessionsGrid.ItemsSource = null;
            SessionsNotice.Text = result.Message;
            if (!ConfirmCurrentAccess()) return;
            await LoadMaintenanceAuditAsync();
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            if (IsLoaded)
            {
                SessionsGrid.ItemsSource = null;
                SessionsNotice.Text = "أُلغيت العملية أثناء التنفيذ؛ قد تكون وصلت للراوتر. " +
                    "تحقق من الجلسات قبل أي إعادة.";
            }
        }
        catch (Exception)
        {
            if (IsLoaded)
            {
                SessionsGrid.ItemsSource = null;
                SessionsNotice.Text = "تعذر التحقق من نتيجة العملية؛ لا تعِد الفصل " +
                    "حتى تحدّث القائمة وتتحقق من الراوتر.";
            }
        }
        finally
        {
            _disconnectingSession = false;
            if (IsLoaded)
            {
                DisconnectSessionButton.IsEnabled = _sessionDisconnect is not null;
                SessionsGrid.IsEnabled = true;
            }
        }
    }



    private async void RecheckSession_Click(object sender, RoutedEventArgs e)
    {
        if (_recheckingSession || _readingMaintenanceAudit || _disconnectingSession ||
            !IsLoaded || !ConfirmCurrentAccess()) return;
        if (_sessionReconciler is null)
        {
            MaintenanceAuditNotice.Text =
                "تعذر تهيئة سجل التدقيق؛ لا يمكن تنفيذ التحقق.";
            return;
        }
        if (MaintenanceAuditGrid.SelectedItem is not SessionMaintenanceAuditEntry selected)
        {
            MaintenanceAuditNotice.Text =
                "اختر عملية واحدة من سجل التدقيق للتحقق من حالة جلستها.";
            return;
        }

        _recheckingSession = true;
        RecheckSessionButton.IsEnabled = false;
        MaintenanceAuditGrid.IsEnabled = false;
        MaintenanceAuditNotice.Text =
            "جارٍ إعادة فحص الجلسة المحددة بالقراءة فقط، دون فصل أو تعديل...";
        try
        {
            var result = await _sessionReconciler.CheckAsync(
                _session, selected.Id, _cancel.Token);
            if (!IsLoaded) return;
            if (!ConfirmCurrentAccess()) return;
            if (result.Checked)
                await LoadMaintenanceAuditAsync();
            MaintenanceAuditNotice.Text = result.Message +
                " لا يثبت هذا الفحص أن أمر الفصل القديم كان سبب اختفاء الجلسة.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            if (IsLoaded)
                MaintenanceAuditNotice.Text =
                    "انقطع فحص الحالة؛ قد لا تتوفر ملاحظة جديدة. لم يُرسل أمر فصل.";
        }
        catch (Exception)
        {
            if (IsLoaded)
                MaintenanceAuditNotice.Text =
                    "تعذر فحص السجل؛ لم يُرسل أي أمر تعديل إلى الراوتر.";
        }
        finally
        {
            _recheckingSession = false;
            if (IsLoaded)
            {
                RecheckSessionButton.IsEnabled = _sessionReconciler is not null;
                MaintenanceAuditGrid.IsEnabled = true;
            }
        }
    }

    private void RefreshMaintenanceAudit_Click(object sender, RoutedEventArgs e) =>
        _ = LoadMaintenanceAuditAsync();

    private async Task LoadMaintenanceAuditAsync()
    {
        if (_readingMaintenanceAudit || !IsLoaded || !ConfirmCurrentAccess()) return;
        if (_maintenanceAudit is null)
        {
            MaintenanceAuditNotice.Text = "سجل التدقيق غير متاح؛ عمليات الفصل معطلة.";
            MaintenanceAuditGrid.ItemsSource = null;
            return;
        }

        _readingMaintenanceAudit = true;
        MaintenanceAuditNotice.Text = "جارٍ تحميل سجل العمليات المحلي...";
        try
        {
            IReadOnlyList<SessionMaintenanceAuditEntry> items =
                await Task.Run(() => _maintenanceAudit.ReadRecent(200), _cancel.Token);
            if (!IsLoaded) return;
            MaintenanceAuditGrid.ItemsSource = items;
            int unresolved = items.Count(x => x.RequiresManualReconciliation);
            MaintenanceAuditNotice.Text =
                "آخر " + items.Count + " عملية محفوظة على هذا الجهاز. " +
                "عمليات تحتاج مراجعة يدوية: " + unresolved + ". " +
                "القبول يعني قبول الأمر من الراوتر، وليس تأكيد فصل العميل.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsLoaded)
            {
                MaintenanceAuditGrid.ItemsSource = null;
                MaintenanceAuditNotice.Text =
                    "تعذر قراءة سجل التدقيق المحلي؛ لا تعتبر سجلات العمليات المفقودة ناجحة.";
            }
        }
        finally { _readingMaintenanceAudit = false; }
    }

    private async Task LoadNetworkServersAsync()
    {
        if (_readingNetworkServers || !IsLoaded || !ConfirmCurrentAccess()) return;
        _readingNetworkServers = true;
        ServersNotice.Text = "جارٍ قراءة سيرفرات Hotspot...";
        try
        {
            var result = await _networkInventory.ReadServersAsync(_session, _cancel.Token);
            if (!IsLoaded) return;
            ServersGrid.ItemsSource = result.Available ? result.Items : null;
            ServersNotice.Text = result.Available
                ? "تمت قراءة " + result.Items.Count + " سيرفر Hotspot — عرض فقط."
                : "خدمة السيرفرات غير متاحة أو لا تتوفر صلاحية القراءة.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
            {
                ServersGrid.ItemsSource = null;
                ServersNotice.Text = "تعذرت قراءة السيرفرات؛ لم تُغيّر أي إعدادات.";
            }
        }
        finally { _readingNetworkServers = false; }
    }

    private async Task LoadNetworkNeighborsAsync()
    {
        if (_readingNetworkNeighbors || !IsLoaded || !ConfirmCurrentAccess()) return;
        _readingNetworkNeighbors = true;
        NeighborsNotice.Text = "جارٍ قراءة جدول اكتشاف الأجهزة من MikroTik دون مسح للشبكة...";
        try
        {
            var result = await _networkInventory.ReadNeighborsAsync(_session, _cancel.Token);
            if (!IsLoaded) return;
            _neighbors = result.Available ? result.Items : [];
            FilterNetworkNeighbors();
            NeighborsNotice.Text = result.Available
                ? "تم اكتشاف " + result.Items.Count + " جهاز — بيانات للقراءة فقط."
                : result.Reason == "limit_exceeded"
                    ? "تجاوز عدد الأجهزة حد القراءة الآمن (5000)؛ لم تُجلب القائمة."
                    : "تعذر حساب عدد الأجهزة أو قراءة القائمة؛ لا تتوفر بيانات مؤكدة.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
            {
                _neighbors = [];
                NeighborsGrid.ItemsSource = null;
                NeighborsNotice.Text = "تعذرت قراءة جدول الأجهزة؛ لم يُنفذ أي مسح أو تعديل.";
            }
        }
        finally { _readingNetworkNeighbors = false; }
    }

    private void NeighborSearch_Changed(object sender, TextChangedEventArgs e) =>
        FilterNetworkNeighbors();

    private void FilterNetworkNeighbors()
    {
        if (NeighborsGrid is null || NeighborSearchBox is null) return;
        string filter = NeighborSearchBox.Text.Trim();
        NeighborsGrid.ItemsSource = filter.Length == 0
            ? _neighbors
            : _neighbors.Where(row =>
                row.Interface.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Address.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Mac.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Identity.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Platform.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                row.Version.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void CopyNeighbor_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmCurrentAccess()) return;
        if (NeighborsGrid.SelectedItem is not NetworkNeighbor selected)
        {
            NeighborsNotice.Text = "اختر جهازا لنسخ عنوانه.";
            return;
        }
        try
        {
            Clipboard.SetText("IP: " + selected.Address + " | MAC: " + selected.Mac);
            NeighborsNotice.Text = "تم نسخ عنوان الجهاز المحدد إلى الحافظة.";
        }
        catch (Exception)
        {
            NeighborsNotice.Text = "تعذر النسخ إلى الحافظة، دون تغيير بيانات الراوتر.";
        }
    }

    private async Task LoadNetworkSessionsAsync()
    {
        if (_readingNetworkSessions || !IsLoaded || !ConfirmCurrentAccess()) return;
        _readingNetworkSessions = true;
        SessionsNotice.Text = "جارٍ فحص أعداد جلسات Hotspot وPPPoE قبل القراءة...";
        try
        {
            var result = await _networkInventory.ReadSessionsAsync(_session, _cancel.Token);
            if (!IsLoaded) return;
            SessionsGrid.ItemsSource = result.Available ? result.Items : null;
            SessionsNotice.Text = result.Available
                ? "تمت قراءة " + result.Items.Count + " جلسة نشطة — دون فصل."
                : result.Reason == "limit_exceeded"
                    ? "تجاوزت إحدى الخدمتين حد 2500 جلسة؛ أُلغيت قراءة القائمة."
                    : "بيانات الجلسات غير متاحة أو صلاحيات القراءة غير كافية؛ لا يُعرض صفر زائف.";
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            if (IsLoaded) LockWorkspace();
        }
        catch (Exception)
        {
            if (IsLoaded)
            {
                SessionsGrid.ItemsSource = null;
                SessionsNotice.Text = "تعذرت قراءة الجلسات؛ لم تُفصل أي جلسة.";
            }
        }
        finally { _readingNetworkSessions = false; }
    }

    private bool ConfirmCurrentAccess()
    {
        if (_session.HasCurrentAccess) return true;
        LockWorkspace();
        return false;
    }

    private static string FormatPercent(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + "%" : "—";
    private static string FormatCount(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "—";

    protected override void OnClosed(EventArgs e)
    {
        _heartbeat.Stop();
        _cancel.Cancel();
        base.OnClosed(e);
    }
}
