using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace MobileTransferPro;

public partial class MainWindow : Window
{
    private readonly string _adbPath;
    public MainWindow()
    {
        InitializeComponent();
        _adbPath = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        Log("بدأ البرنامج. هذه نسخة أولية لنقل الملفات المشتركة فقط.");
        if (!File.Exists(_adbPath))
            Log("لم يتم العثور على platform-tools/adb.exe بجانب البرنامج. راجع README لتنزيل أدوات Android الرسمية ووضعها في المجلد المطلوب.");
        Loaded += (_, _) => RefreshDevices();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    private async Task RefreshDevices() => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        try
        {
            RefreshButton.IsEnabled = false;
            var result = await RunAdbAsync("devices -l");
            var devices = ParseDevices(result.StdOut);
            SourceDeviceBox.ItemsSource = devices.ToList();
            TargetDeviceBox.ItemsSource = devices.ToList();
            SourceDeviceBox.SelectedIndex = -1;
            TargetDeviceBox.SelectedIndex = -1;
            Log($"تم العثور على {devices.Count} جهاز/أجهزة بحالة اتصال صالحة.");
            if (devices.Count == 0) Log("لا توجد أجهزة جاهزة. تأكد من الكابل والتعريفات وموافقة الهاتف على USB debugging.");
            StatusText.Text = devices.Count >= 2 ? "اختر المصدر والوجهة يدوياً من القائمتين." : "يلزم ظهور جهازين جاهزين على الأقل لنقل الملفات بين هاتفين.";
        }
        catch (Exception ex) { Log("خطأ في اكتشاف الأجهزة: " + ex.Message); StatusText.Text = "تعذر اكتشاف الأجهزة."; }
        finally { RefreshButton.IsEnabled = true; }
    }

    private static List<DeviceInfo> ParseDevices(string output)
    {
        var list = new List<DeviceInfo>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith("*")) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != "device") continue;
            string serial = parts[0];
            string model = parts.FirstOrDefault(p => p.StartsWith("model:"))?.Substring(6).Replace('_', ' ') ?? "Android";
            string product = parts.FirstOrDefault(p => p.StartsWith("product:"))?.Substring(8) ?? "";
            list.Add(new DeviceInfo(serial, $"{model} — {serial}" + (string.IsNullOrWhiteSpace(product) ? "" : $" ({product})")));
        }
        return list;
    }

    private void SwapButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceDeviceBox.SelectedItem as DeviceInfo;
        var target = TargetDeviceBox.SelectedItem as DeviceInfo;
        SourceDeviceBox.SelectedItem = target;
        TargetDeviceBox.SelectedItem = source;
        Log("تم تبديل اختيار المصدر والوجهة.");
    }

    private async void StartTransferButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceDeviceBox.SelectedItem as DeviceInfo;
        var target = TargetDeviceBox.SelectedItem as DeviceInfo;
        if (source is null || target is null) { MessageBox.Show("اختر الهاتف المصدر والهاتف المستهدف يدوياً.", "اختيار الأجهزة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (source.Serial == target.Serial) { MessageBox.Show("لا يمكن اختيار الجهاز نفسه كمصدر ووجهة.", "خطأ", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var folders = GetSelectedFolders();
        if (folders.Count == 0) { MessageBox.Show("حدد نوع بيانات واحداً على الأقل.", "لا توجد بيانات محددة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        string summary = $"المصدر: {source.DisplayName}\nالوجهة: {target.DisplayName}\n\nالمجلدات المحددة:\n{string.Join("\n", folders.Select(f => "• " + f.Label))}\n\nسيتم نقل الملفات إلى مجلد MobileTransferPro_Import على الهاتف المستهدف، ولن تُحذف بيانات المصدر. هل تريد المتابعة؟";
        if (MessageBox.Show(summary, "تأكيد النقل", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        StartTransferButton.IsEnabled = false; RefreshButton.IsEnabled = false; TransferProgress.Value = 0;
        string sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string stageRoot = Path.Combine(Path.GetTempPath(), "MobileTransferPro", sessionId);
        bool keepTemporaryCopy = MessageBox.Show("هل تريد الاحتفاظ بنسخة مؤقتة من الملفات على الكمبيوتر بعد انتهاء النقل؟\n\nاختيار «لا» يحذف مجلد العمل المؤقت بعد انتهاء المحاولة.", "النسخة المؤقتة", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        int successful = 0, failed = 0, done = 0;
        try
        {
            Directory.CreateDirectory(stageRoot);
            foreach (var folder in folders)
            {
                await EnsureDevicesStillConnectedAsync(source, target);
                StatusText.Text = $"جاري نسخ {folder.Label} من الهاتف المصدر إلى الكمبيوتر...";
                Log($"سحب: {folder.RemotePath}");
                string local = Path.Combine(stageRoot, folder.LocalName);
                Directory.CreateDirectory(local);
                var pull = await RunAdbAsync(new[] { "-s", source.Serial, "pull", folder.RemotePath, local });
                LogOutput(pull);
                if (pull.ExitCode != 0)
                {
                    failed++; Log($"تخطّي {folder.Label}: المجلد غير موجود أو غير متاح على المصدر.");
                    done++; TransferProgress.Value = done * 100.0 / folders.Count; continue;
                }
                if (!Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).Any())
                {
                    failed++; Log($"تخطّي {folder.Label}: لم يُعثر على ملفات قابلة للنقل داخل المجلد.");
                    done++; TransferProgress.Value = done * 100.0 / folders.Count; continue;
                }
                await EnsureDevicesStillConnectedAsync(source, target);
                StatusText.Text = $"جاري إرسال {folder.Label} إلى الهاتف المستهدف...";
                string remoteTarget = $"/sdcard/MobileTransferPro_Import_{sessionId}/{folder.LocalName}";
                var push = await RunAdbAsync(new[] { "-s", target.Serial, "push", local, remoteTarget });
                LogOutput(push);
                if (push.ExitCode == 0) { successful++; Log($"نجح إرسال {folder.Label} إلى {remoteTarget}"); }
                else { failed++; Log($"فشل إرسال {folder.Label}؛ راجع السجل."); }
                done++; TransferProgress.Value = done * 100.0 / folders.Count;
            }
            StatusText.Text = $"انتهت المحاولة: نجح {successful}، تعذر {failed}. تحقق من الملفات على الهاتف الهدف.";
            Log($"ملخص الجلسة: نجاح={successful}، تعذّر/تم التخطي={failed}.");
            MessageBox.Show($"انتهت محاولة النقل.\nالمجلدات التي نُقلت: {successful}\nالمجلدات التي تعذّر نقلها أو كانت فارغة: {failed}\n\nتحقق من الملفات على الهاتف المستهدف قبل تسليم الجهاز. لا تشمل هذه العملية الرسائل أو جهات الاتصال أو بيانات التطبيقات الخاصة.", "ملخص النقل", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { StatusText.Text = "توقفت العملية بسبب خطأ."; Log("خطأ: " + ex.Message); MessageBox.Show(ex.Message, "خطأ في النقل", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            if (!keepTemporaryCopy && Directory.Exists(stageRoot))
            {
                try { Directory.Delete(stageRoot, true); Log("حُذف مجلد العمل المؤقت من الكمبيوتر."); }
                catch (Exception ex) { Log("تنبيه: تعذر حذف النسخة المؤقتة تلقائياً: " + ex.Message); }
            }
            StartTransferButton.IsEnabled = true; RefreshButton.IsEnabled = true;
        }
    }

    private List<FolderChoice> GetSelectedFolders()
    {
        var result = new List<FolderChoice>();
        if (DcimCheck.IsChecked == true) result.Add(new("الكاميرا (DCIM)", "/sdcard/DCIM", "DCIM"));
        if (PicturesCheck.IsChecked == true) result.Add(new("الصور (Pictures)", "/sdcard/Pictures", "Pictures"));
        if (MoviesCheck.IsChecked == true) result.Add(new("الفيديوهات (Movies)", "/sdcard/Movies", "Movies"));
        if (DownloadCheck.IsChecked == true) result.Add(new("التنزيلات (Download)", "/sdcard/Download", "Download"));
        if (DocumentsCheck.IsChecked == true) result.Add(new("المستندات (Documents)", "/sdcard/Documents", "Documents"));
        if (MusicCheck.IsChecked == true) result.Add(new("الموسيقى (Music)", "/sdcard/Music", "Music"));
        if (WhatsAppMediaCheck.IsChecked == true) result.Add(new("وسائط واتساب المتاحة", "/sdcard/Android/media/com.whatsapp/WhatsApp/Media", "WhatsAppMedia"));
        return result;
    }

    private async Task EnsureDevicesStillConnectedAsync(DeviceInfo source, DeviceInfo target)
    {
        var result = await RunAdbAsync(new[] { "devices" });
        var connected = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[1] == "device")
            .Select(parts => parts[0]).ToHashSet(StringComparer.Ordinal);
        if (!connected.Contains(source.Serial) || !connected.Contains(target.Serial))
            throw new InvalidOperationException("انقطع أحد الهاتفين أو لم يعد بحالة اتصال صالحة. أوقف البرنامج النقل لحماية البيانات. افحص الكابل ثم حدّث قائمة الأجهزة وابدأ من جديد.");
    }

    private Task<CommandResult> RunAdbAsync(string arguments) => RunAdbAsync(SplitArguments(arguments));

    private async Task<CommandResult> RunAdbAsync(IEnumerable<string> arguments)
    {
        if (!File.Exists(_adbPath)) throw new FileNotFoundException("adb.exe غير موجود. نزّل Android SDK Platform-Tools من الرابط الرسمي ثم ضع ملفاتها داخل مجلد platform-tools بجانب البرنامج.", _adbPath);
        var psi = new ProcessStartInfo { FileName = _adbPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static IEnumerable<string> SplitArguments(string arguments)
    {
        // Used only for fixed, simple ADB commands; transfer paths use ArgumentList directly.
        return arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
    private void Log(string message) { LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n"); LogBox.ScrollToEnd(); }
    private void LogOutput(CommandResult r) { if (!string.IsNullOrWhiteSpace(r.StdOut)) Log(r.StdOut.Trim()); if (!string.IsNullOrWhiteSpace(r.StdErr)) Log(r.StdErr.Trim()); }
    private sealed record DeviceInfo(string Serial, string DisplayName);
    private sealed record FolderChoice(string Label, string RemotePath, string LocalName);
    private sealed record CommandResult(int ExitCode, string StdOut, string StdErr);
}
