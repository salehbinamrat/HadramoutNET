using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Win32;
using HadramoutNET.Core.Printing;

namespace HadramoutNET.App;

/// <summary>
/// Per-package card artwork editor. Source-compatible optional text fields,
/// legacy 280x130 layer positions, and configurable login QR. No writes to
/// the source Python installation. Draft edits are discarded on Cancel.
/// </summary>
internal static class CardArtworkEditorWindow
{
    private static readonly (string Key, string Title)[] Layers =
    [
        ("title", "اسم الشبكة"), ("sequence", "تسلسل الكرت"),
        ("username", "اسم المستخدم"), ("password", "كلمة المرور"),
        ("price", "السعر"), ("capacity", "السعة"),
        ("duration", "المدة"), ("validity", "الصلاحية"),
        ("date", "التاريخ"), ("phone", "الهاتف"),
        ("qr", "رمز QR"), ("other", "النص الإضافي"),
        ("other2", "النص الإضافي الثاني")
    ];

    public static CardArtOptions? Edit(Window owner, CardArtOptions current)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(current);
        CardArtOptions? chosen = null;

        var window = new Window
        {
            Title = "تخصيص تصميم كرت حضرموت",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FlowDirection = FlowDirection.RightToLeft,
            ShowInTaskbar = false,
            Width = 640, Height = 795, MinWidth = 500, MinHeight = 550
        };
        var content = new StackPanel { Margin = new Thickness(17) };

        var intro = new TextBlock
        {
            Text = "تخصيص العناصر ومواضعها بحسب شبكة إحداثيات حضرموت الأصلية " +
                   "(عرض 280 وارتفاع 130). التصميم يطبق على المعاينة وPDF والطباعة. " +
                   "للحفظ الدائم احفظ الباقة في شاشة الباقات.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        content.Children.Add(intro);

        static TextBox TextField(StackPanel panel, string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            var input = new TextBox { Text = value, Height = 33, Margin = new Thickness(0, 3, 0, 8) };
            panel.Children.Add(input);
            return input;
        }
        static CheckBox Toggle(Panel panel, string label, bool value)
        {
            var c = new CheckBox
            {
                Content = label, IsChecked = value, Height = 28,
                Margin = new Thickness(3)
            };
            panel.Children.Add(c);
            return c;
        }
        static bool Selected(CheckBox c) => c.IsChecked == true;

        var titleText = TextField(content, "اسم الشبكة الظاهر على الكرت", current.Title);
        var phoneText = TextField(content, "رقم الهاتف", current.Phone);
        var otherText = TextField(content, "النص الإضافي الأول", current.Other);
        var other2Text = TextField(content, "النص الإضافي الثاني", current.Other2);
        var priceText = TextField(content, "السعر (نص اختياري)", current.Price);
        var durationText = TextField(content, "المدة (نص اختياري)", current.Duration);
        var capacityText = TextField(content, "السعة (نص اختياري)", current.Capacity);
        var validityText = TextField(content, "الصلاحية (نص اختياري)", current.Validity);
        var dateText = TextField(content, "التاريخ (نص اختياري)", current.Date);

        var toggles = new WrapPanel { Margin = new Thickness(0, 7, 0, 11) };
        var title = Toggle(toggles, "عرض الاسم", current.ShowTitle);
        var sequence = Toggle(toggles, "عرض التسلسل", current.ShowSequence);
        var phone = Toggle(toggles, "عرض الهاتف", current.ShowPhone);
        var other = Toggle(toggles, "عرض إضافي 1", current.ShowOther);
        var other2 = Toggle(toggles, "عرض إضافي 2", current.ShowOther2);
        var price = Toggle(toggles, "عرض السعر", current.ShowPrice);
        var duration = Toggle(toggles, "عرض المدة", current.ShowDuration);
        var capacity = Toggle(toggles, "عرض السعة", current.ShowCapacity);
        var validity = Toggle(toggles, "عرض الصلاحية", current.ShowValidity);
        var date = Toggle(toggles, "عرض التاريخ", current.ShowDate);
        var qr = Toggle(toggles, "تفعيل QR", current.ShowQr);
        content.Children.Add(toggles);

        content.Children.Add(new TextBlock
        {
            Text = "نوع بيانات رمز QR", FontWeight = FontWeights.SemiBold
        });
        var qrMode = new ComboBox { Height = 33, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var (label, key) in new[]
        {
            ("الاسم وكلمة المرور", "credentials"),
            ("اسم المستخدم فقط", "username"),
            ("رابط الدخول", "login_url")
        })
        {
            var item = new ComboBoxItem { Content = label, Tag = key };
            qrMode.Items.Add(item);
            if (current.QrMode == key) qrMode.SelectedItem = item;
        }
        content.Children.Add(qrMode);
        var qrUrl = TextField(content, "رابط QR عند اختيار رابط الدخول", current.QrLoginUrl);
        var qrSize = TextField(content, "حجم QR بالمليمتر",
            current.QrSizeMm.ToString("G17", CultureInfo.InvariantCulture));

        content.Children.Add(new TextBlock {
            Text = "إعدادات خطوط وإطار الكرت", FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 9, 0, 7)
        });
        var fontSize = TextField(content, "حجم الخط المرجعي (1-300)",
            current.FontSize.ToString("G", CultureInfo.InvariantCulture));
        var fontBold = Toggle(content, "الخط الأساسي عريض", current.FontBold);
        var fontColor = TextField(content, "لون النصوص الأخرى (#RRGGBB)",
            current.FontColor);
        var credentialColor = TextField(content,
            "لون اسم المستخدم وكلمة المرور معا (#RRGGBB)",
            current.UsernameColor);
        var borderColor = TextField(content, "لون إطار الكرت (#RRGGBB)",
            current.BorderColor);
        var borderWidth = TextField(content, "سمك الإطار المرجعي (0-100)",
            current.BorderWidth.ToString("G", CultureInfo.InvariantCulture));
        var borderRadius = TextField(content, "استدارة زوايا الإطار (0-100)",
            current.BorderRadius.ToString("G", CultureInfo.InvariantCulture));

        string? backgroundData = current.BackgroundData;
        content.Children.Add(new TextBlock {
            Text = "صورة خلفية الكرت (تحفظ داخل إعدادات الباقة)",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4)
        });
        var backgroundStatus = new TextBlock {
            Text = string.IsNullOrEmpty(backgroundData) ?
                "لا توجد خلفية محددة" : "تم تحميل خلفية محفوظة",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 7)
        };
        var bgRow = new StackPanel { Orientation = Orientation.Horizontal };
        var chooseBackground = new Button {
            Content = "استيراد خلفية", Height = 34, Width = 125,
            Margin = new Thickness(3)
        };
        var clearBackground = new Button {
            Content = "إزالة الخلفية", Height = 34, Width = 125,
            Margin = new Thickness(3)
        };
        chooseBackground.Click += (_, _) =>
        {
            var picker = new OpenFileDialog {
                Title = "اختر خلفية كرت حضرموت",
                Filter = "صور|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
                CheckFileExists = true
            };
            if (picker.ShowDialog(window) != true) return;
            try
            {
                using var input = File.OpenRead(picker.FileName);
                if (input.Length > CardBackgroundAsset.MaxFileBytes)
                    throw new PrintLayoutException("صورة الخلفية أكبر من 15 ميجابايت.");
                BitmapDecoder decoder = BitmapDecoder.Create(input,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapFrame frame = decoder.Frames[0];
                if ((long)frame.PixelWidth * frame.PixelHeight >
                    CardBackgroundAsset.MaxPixels)
                    throw new PrintLayoutException("الخلفية أكبر من 32 مليون بكسل.");
                var normalized = new FormatConvertedBitmap(
                    frame, PixelFormats.Rgb24, null, 0);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(normalized));
                using var output = new MemoryStream();
                encoder.Save(output);
                string packed = Convert.ToBase64String(output.ToArray());
                _ = CardBackgroundAsset.Decode(packed);
                backgroundData = packed;
                backgroundStatus.Text = "خلفية صالحة جاهزة للحفظ مع الباقة.";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                or NotSupportedException or PrintLayoutException or
                System.Runtime.InteropServices.COMException)
            {
                backgroundStatus.Text = "فشل استيراد الصورة: " + ex.Message;
            }
        };
        clearBackground.Click += (_, _) =>
        {
            backgroundData = null;
            backgroundStatus.Text = "تم حذف الخلفية من تصميم هذه الجلسة.";
        };
        bgRow.Children.Add(chooseBackground);
        bgRow.Children.Add(clearBackground);
        content.Children.Add(bgRow);
        content.Children.Add(backgroundStatus);

        content.Children.Add(new TextBlock {
            Text = "توزيع الخلفية", FontWeight = FontWeights.SemiBold
        });
        var backgroundMode = new ComboBox {
            Height = 33, Margin = new Thickness(0, 4, 0, 8)
        };
        foreach (var (label, mode) in new[] {
            ("تمديد", "stretch"), ("ملء مع القص", "fill"),
            ("احتواء", "fit"), ("تكرار", "tile")
        })
        {
            var item = new ComboBoxItem { Content = label, Tag = mode };
            backgroundMode.Items.Add(item);
            if (current.BackgroundMode == mode) backgroundMode.SelectedItem = item;
        }
        content.Children.Add(backgroundMode);

        content.Children.Add(new TextBlock
        {
            Text = "مواضع العناصر الأصلية (X وY بالنقاط المرجعية 280×130)",
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 9, 0, 6)
        });
        var positions = new Dictionary<string, (TextBox X, TextBox Y)>(StringComparer.Ordinal);
        foreach (var (key, label) in Layers)
        {
            CardArtPoint point = current.Positions is not null &&
                current.Positions.TryGetValue(key, out var p)
                    ? p : CardArtwork.DefaultPositions[key];
            var row = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, Width = 170,
                VerticalAlignment = VerticalAlignment.Center });
            var x = new TextBox { Width = 90, Height = 28, Margin = new Thickness(4),
                Text = point.X.ToString("G", CultureInfo.InvariantCulture) };
            var y = new TextBox { Width = 90, Height = 28, Margin = new Thickness(4),
                Text = point.Y.ToString("G", CultureInfo.InvariantCulture) };
            row.Children.Add(new TextBlock { Text = "X", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(x);
            row.Children.Add(new TextBlock { Text = "Y", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(y);
            positions[key] = (x, y);
            content.Children.Add(row);
        }

        var feedback = new TextBlock { Foreground = Brushes.DarkRed,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 12, 3, 5) };
        content.Children.Add(feedback);
        var footer = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = "اعتماد التصميم", Width = 140,
            Height = 38, Margin = new Thickness(4) };
        var cancel = new Button { Content = "إلغاء", Width = 90, Height = 38,
            IsCancel = true, Margin = new Thickness(4) };
        accept.Click += (_, _) =>
        {
            try
            {
                double Read(TextBox box, double lower, double upper)
                {
                    if (!double.TryParse(box.Text.Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double v) ||
                        !double.IsFinite(v) || v < lower || v > upper)
                        throw new PrintLayoutException("إحداثيات أو مقاسات غير صالحة.");
                    return v;
                }
                var points = positions.ToDictionary(
                    item => item.Key,
                    item => new CardArtPoint(
                        Read(item.Value.X, 0, 280), Read(item.Value.Y, 0, 130)),
                    StringComparer.Ordinal);
                var result = current with
                {
                    Title = titleText.Text, Phone = phoneText.Text,
                    Other = otherText.Text, Other2 = other2Text.Text,
                    Price = priceText.Text, Duration = durationText.Text,
                    Capacity = capacityText.Text, Validity = validityText.Text,
                    Date = dateText.Text,
                    ShowTitle = Selected(title), ShowSequence = Selected(sequence),
                    ShowPhone = Selected(phone), ShowOther = Selected(other),
                    ShowOther2 = Selected(other2), ShowPrice = Selected(price),
                    ShowDuration = Selected(duration), ShowCapacity = Selected(capacity),
                    ShowValidity = Selected(validity), ShowDate = Selected(date),
                    ShowQr = Selected(qr),
                    QrMode = (qrMode.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                        ?? "credentials",
                    QrLoginUrl = qrUrl.Text,
                    QrSizeMm = Read(qrSize, 1, 300),
                    FontSize = Read(fontSize, 1, 300),
                    FontBold = Selected(fontBold),
                    FontColor = fontColor.Text,
                    UsernameColor = credentialColor.Text,
                    BorderColor = borderColor.Text,
                    BorderWidth = Read(borderWidth, 0, 100),
                    BorderRadius = Read(borderRadius, 0, 100),
                    BackgroundData = backgroundData,
                    BackgroundMode =
                        (backgroundMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                        "stretch",
                    Positions = points
                };
                CardArtPresetCodec.Validate(result);
                _ = CardArtwork.Create(new HadramoutNET.Core.Cards.Voucher(
                    "0001", "12345678", "abc123"), 50, 19, result);
                chosen = result;
                window.DialogResult = true;
            }
            catch (PrintLayoutException error)
            {
                feedback.Text = error.Message;
            }
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        footer.Children.Add(accept);
        footer.Children.Add(cancel);
        content.Children.Add(footer);
        window.Content = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        return window.ShowDialog() == true ? chosen : null;
    }
}
