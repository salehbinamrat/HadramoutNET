using System.Windows;
using System.Windows.Controls;
using HadramoutNET.Core.Data;

namespace HadramoutNET.App;

/// <summary>
/// A one-time operator prompt for verifying a previous RouterOS upload.
/// Passwords are neither requested nor stored. The input remains in memory
/// only for the immediate, read-only comparison to the live router.
/// </summary>
internal sealed record VoucherReconciliationRequest(
    long AttemptId, IReadOnlyList<string> Usernames);

internal static class VoucherReconciliationPrompt
{
    public static VoucherReconciliationRequest? Show(
        Window owner, IReadOnlyList<VoucherDeliveryAuditEntry> attempts)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(attempts);
        if (attempts.Count == 0) return null;

        VoucherReconciliationRequest? request = null;
        var window = new Window
        {
            Owner = owner,
            Title = "فحص محاولة رفع سابقة — للقراءة فقط",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FlowDirection = FlowDirection.RightToLeft,
            ShowInTaskbar = false,
            Width = 650,
            Height = 540,
            MinWidth = 520,
            MinHeight = 430,
            ResizeMode = ResizeMode.CanResize
        };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            Text = "اختر محاولة رفع غير محسومة (Hotspot أو PPP أو User Manager)، ثم ألصق أسماء المستخدمين " +
                   "الأصلية من ملف موثوق: اسم واحد في كل سطر دون كلمات مرور. " +
                   "التحقق للقراءة فقط ولا يحول النتيجة القديمة إلى نجاح. " +
                   "لا تُرسل ملفات CSV كاملة أو رموز تراخيص.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(intro, 0);
        grid.Children.Add(intro);

        var select = new ComboBox
        {
            MinHeight = 35,
            Margin = new Thickness(0, 0, 0, 12)
        };
        foreach (VoucherDeliveryAuditEntry item in attempts)
            select.Items.Add(new ComboBoxItem
            {
                Tag = item,
                Content = $"#{item.Id} | {item.CreatedAtUtc} | {item.Service} / {item.Profile} | " +
                          $"{item.Added}/{item.Attempted} | {item.StateLabel}"
            });
        select.SelectedIndex = 0;
        Grid.SetRow(select, 1);
        grid.Children.Add(select);

        var prompt = new TextBlock
        {
            Text = "أسماء الكروت فقط، كل اسم في سطر:",
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(prompt, 2);
        grid.Children.Add(prompt);

        var input = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            MaxLength = 400000,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(input, 3);
        grid.Children.Add(input);

        var notice = new TextBlock
        {
            Text = "بعد الفحص يُحفظ عدد الحالات فقط، وتبقى المحاولة القديمة دون تعديل.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(notice, 4);
        grid.Children.Add(notice);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var check = new Button
        {
            Content = "تحقق من الراوتر للقراءة فقط",
            MinWidth = 210,
            Height = 36,
            Margin = new Thickness(5, 0, 0, 0)
        };
        var cancel = new Button
        {
            Content = "إلغاء",
            MinWidth = 90,
            Height = 36,
            IsCancel = true,
            Margin = new Thickness(5, 0, 0, 0)
        };
        check.Click += (_, _) =>
        {
            if ((select.SelectedItem as ComboBoxItem)?.Tag is not
                VoucherDeliveryAuditEntry chosen)
                return;

            // Only newlines are separators. No password or CSV parsing.
            string[] names = input.Text.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length != 0)
                .ToArray();
            if (names.Length != chosen.Attempted ||
                names.Length > 5000 ||
                names.Any(name => name.Contains(',') || name.Contains(';') ||
                                  name.Contains('\t')))
            {
                notice.Text = $"يجب إدخال {chosen.Attempted} اسم مستخدم منفردا، " +
                    "دون أعمدة إضافية أو فواصل أو كلمات مرور. لم يبدأ الفحص.";
                return;
            }

            request = new VoucherReconciliationRequest(chosen.Id, names);
            input.Clear();
            window.DialogResult = true;
        };
        cancel.Click += (_, _) =>
        {
            input.Clear();
            window.DialogResult = false;
        };
        footer.Children.Add(check);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 5);
        grid.Children.Add(footer);
        window.Closed += (_, _) => input.Clear();
        window.Content = grid;
        return window.ShowDialog() == true ? request : null;
    }
}
