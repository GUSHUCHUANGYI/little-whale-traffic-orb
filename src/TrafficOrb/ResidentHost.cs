using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TrafficOrb;

public static class StartupRegistration
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TrafficOrb";
    public static string BuildCommand(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || exe.IndexOfAny(['"', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("程序路径无效");
        return $"\"{exe}\" --background";
    }
    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunPath);
                return key?.GetValue(ValueName) is string command && command == BuildCommand(Environment.ProcessPath!);
            }
            catch { return false; }
        }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath, true);
        if (enabled) key.SetValue(ValueName, BuildCommand(Environment.ProcessPath!), RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}

public sealed class ResidentHost : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Drawing.Icon ownedIcon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem visibility = new();
    private readonly Forms.ToolStripMenuItem startup = new("开机自动启动");
    private readonly Func<bool> visible;
    private readonly Action toggleVisible, toggleStartup;
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public ResidentHost(Action show, Action toggleVisible, Func<bool> visible, Action details, Action refresh,
        Action toggleStartup, Action exit)
    {
        this.visible = visible; this.toggleVisible = toggleVisible; this.toggleStartup = toggleStartup;
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(33, 40, 47));
            using var ring = new Drawing.Pen(Drawing.Color.FromArgb(134, 227, 196), 2.8f);
            graphics.FillEllipse(fill, 2, 2, 28, 28); graphics.DrawEllipse(ring, 3, 3, 26, 26);
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(244, 247, 248));
            graphics.FillEllipse(dot, 12, 12, 8, 8);
        }
        IntPtr handle = bitmap.GetHicon();
        try { ownedIcon = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
        icon = new Forms.NotifyIcon { Icon = ownedIcon, Text = "小鲸鱼流量悬浮球 · 后台常驻", ContextMenuStrip = menu, Visible = true };
        icon.MouseDoubleClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) show(); };
        visibility.Click += (_, _) => this.toggleVisible();
        menu.Items.Add(visibility);
        menu.Items.Add("流量明细…", null, (_, _) => details());
        menu.Items.Add("立即刷新", null, (_, _) => refresh());
        startup.Click += (_, _) => this.toggleStartup();
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出程序", null, (_, _) => exit());
        menu.Opening += (_, _) =>
        {
            visibility.Text = this.visible() ? "隐藏悬浮球（后台继续统计）" : "显示悬浮球";
            startup.Checked = StartupRegistration.Enabled;
        };
    }
    public void SetText(string text) => icon.Text = text.Length > 63 ? text[..63] : text;
    public bool Visible => icon.Visible;
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); ownedIcon.Dispose(); }
}
