using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrafficOrb;

internal static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [STAThread]
    public static int Main(string[] args)
    {
        SetCurrentProcessExplicitAppUserModelID("TrafficOrb.Desktop");
        if (args.Contains("--quit"))
        {
            try { using var quit = EventWaitHandle.OpenExisting("Local\\TrafficOrb.Quit"); quit.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
        if (args.Length >= 2 && args[0] == "--check-clash")
        {
            using var local = new LocalConnections();
            var choices = new NodeSwitcher(local).ReadAsync(default).GetAwaiter().GetResult();
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new
            {
                choices.Mode,
                Groups = choices.Groups.Select(x => new { x.Name, x.Current, Nodes = choices.Nodes(x.Name).Select(n => n.Name) }),
                ReadOnly = true
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--render-binding-preview")
        {
            var previewApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            using var sample = System.Text.Json.JsonDocument.Parse("""{"proxies":{"PROXY":{"type":"Selector","now":"US-demo","all":["US-demo","HK-demo","DIRECT"]},"US-demo":{"type":"VLESS"},"HK-demo":{"type":"VLESS"},"DIRECT":{"type":"Direct"}}}""");
            var binding = new NodeBindingWindow("美国 · 演示实例（非真实数据）", NodeSwitcher.Parse("rule", sample.RootElement), new("PROXY", "US-demo"), false);
            binding.Loaded += (_, _) => binding.Dispatcher.BeginInvoke(() =>
            {
                OrbWindow.RenderElement((FrameworkElement)binding.Content, Path.GetFullPath(args[1])); binding.Close();
            }, DispatcherPriority.ApplicationIdle);
            return previewApp.Run(binding);
        }
        if (args.Length >= 2 && args[0] == "--render-connect-preview")
        {
            var previewApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var connection = new ConnectWindow(_ => Task.FromResult((false, "仅界面预览")));
            connection.Loaded += (_, _) => connection.Dispatcher.BeginInvoke(() =>
            {
                var content = (FrameworkElement)connection.Content;
                connection.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 2), (int)Math.Ceiling(content.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.GetFullPath(args[1]))) png.Save(output);
                connection.Close();
            }, DispatcherPriority.ApplicationIdle);
            return previewApp.Run(connection);
        }
        bool preview = args.Contains("--preview");
        string? verifyFolder = args.Length >= 2 && args[0] == "--verify-live" ? Path.GetFullPath(args[1]) : null;
        string? renderFile = args.Length >= 2 && args[0] == "--render-preview" ? args[1] : null;
        string? menuFile = args.Length >= 2 && args[0] == "--render-switch-menu-preview" ? args[1] : null;
        preview |= renderFile != null || menuFile != null;
        using var mutex = new Mutex(true, preview ? "Local\\TrafficOrb-Preview" : "Local\\TrafficOrb", out bool first);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, preview ? "Local\\TrafficOrb-Preview.Wake" : "Local\\TrafficOrb.Wake");
        using var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, preview ? "Local\\TrafficOrb-Preview.Quit" : "Local\\TrafficOrb.Quit");
        if (!first)
        {
            if (!args.Contains("--background")) wake.Set();
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            MessageBox.Show("小鲸鱼流量悬浮球遇到问题。请退出后重新打开；账号凭据和用量缓存仍保存在本机。", "小鲸鱼流量悬浮球");
        };
        var window = new OrbWindow(preview);
        app.SessionEnding += (_, _) => window.AllowSessionExit();
        var wakeRegistration = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) =>
        {
            if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(window.ShowOrb);
        }, null, Timeout.Infinite, false);
        var quitRegistration = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) =>
        {
            if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(window.RequestExit);
        }, null, Timeout.Infinite, false);
        if (verifyFolder != null)
        {
            window.Loaded += async (_, _) =>
            {
                for (int retry = 0; retry < 50 && !window.HasLiveQuota; retry++) await Task.Delay(500);
                await Task.Delay(3000);
                await window.VerifyHiddenSamplingAsync();
                await window.CaptureLiveAsync(verifyFolder);
                window.RequestExit();
            };
        }
        if (renderFile != null)
        {
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() =>
            {
                window.RenderPreview(Path.GetFullPath(renderFile)); window.Close();
            }, DispatcherPriority.ApplicationIdle);
        }
        if (menuFile != null)
        {
            window.Loaded += async (_, _) =>
            {
                await window.RenderSwitchMenuPreviewAsync(Path.GetFullPath(menuFile)); window.Close();
            };
        }
        try { return app.Run(window); }
        finally { wakeRegistration.Unregister(null); quitRegistration.Unregister(null); }
    }
}
