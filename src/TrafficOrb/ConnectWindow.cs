using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrafficOrb;

public sealed class ConnectWindow : Window
{
    public const string ProfileUrl = "https://dash.fuckip.me/profile";
    private readonly PasswordBox key = new();
    private readonly Button submit = new();
    private readonly TextBlock status = new();
    private readonly Func<string, Task<(bool Ok, string Message)>> connect;
    private bool checking, closed;

    public ConnectWindow(Func<string, Task<(bool, string)>> callback)
    {
        connect = callback;
        Title = "连接账号 · 小鲸鱼流量悬浮球";
        Width = 510; Height = 470;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var panel = new StackPanel { Background = Brushes.White, Margin = new Thickness(30, 26, 30, 24) };
        panel.Children.Add(new TextBlock { Text = "连接流量账号", FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = Ink });
        panel.Children.Add(new TextBlock
        {
            Text = "用普通浏览器登录，避免 Google 内嵌登录限制。",
            FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 8, 0, 22)
        });
        var browser = new Button
        {
            Content = "1. 打开官网，使用 Google 登录",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 12, 14, 12),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#DDE3E7")!,
            Background = (Brush)new BrushConverter().ConvertFromString("#F7F9FA")!, Foreground = Ink,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        browser.Click += (_, _) => OpenOfficialProfile();
        panel.Children.Add(browser);
        panel.Children.Add(new TextBlock
        {
            Text = "2. 在官网「个人资料 → API Key」复制已有密钥，\n粘贴到下面。已有密钥直接复制，无需重新生成。",
            FontSize = 12, LineHeight = 21, TextWrapping = TextWrapping.Wrap,
            Foreground = Ink, Margin = new Thickness(0, 21, 0, 10)
        });
        key.FontSize = 15; key.Padding = new Thickness(10); key.Height = 44;
        key.ToolTip = "仅粘贴到本程序，不要发到聊天。支持 Ctrl+V。";
        System.Windows.Automation.AutomationProperties.SetName(key, "独角鲸云 API Key");
        panel.Children.Add(key);
        submit.Content = "连接并显示用量";
        submit.Margin = new Thickness(0, 15, 0, 0);
        submit.Padding = new Thickness(12, 11, 12, 11);
        submit.Background = (Brush)new BrushConverter().ConvertFromString("#247D68")!;
        submit.Foreground = Brushes.White;
        submit.BorderThickness = new Thickness(0);
        submit.IsDefault = true;
        submit.Cursor = System.Windows.Input.Cursors.Hand;
        submit.Click += async (_, _) => await ConnectAsync();
        panel.Children.Add(submit);
        status.Text = "只查询用量 · 凭据仅本机加密保存 · 不改 Clash";
        status.FontSize = 11;
        status.TextWrapping = TextWrapping.Wrap;
        status.Foreground = Muted;
        status.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(status);
        Content = panel;
        Closed += (_, _) => { closed = true; key.Clear(); };
    }
    private static Brush Ink => (Brush)new BrushConverter().ConvertFromString("#202B34")!;
    private static Brush Muted => (Brush)new BrushConverter().ConvertFromString("#6B7883")!;
    private void OpenOfficialProfile()
    {
        try
        {
            // OAuth stays in the real browser. Never spoof user agents or embed Google login.
            Process.Start(new ProcessStartInfo(ProfileUrl) { UseShellExecute = true });
            status.Foreground = Muted;
            status.Text = "官网已交给默认浏览器打开。复制 API Key 后回到这里粘贴。";
        }
        catch
        {
            status.Foreground = Brushes.Firebrick;
            status.Text = "浏览器未能打开，请手动访问 dash.fuckip.me/profile。";
        }
    }
    private async Task ConnectAsync()
    {
        if (checking || closed) return;
        string candidate = key.Password.Trim();
        if (candidate.Length == 0)
        {
            status.Foreground = Brushes.Firebrick;
            status.Text = "请先粘贴官网 API Key，不是账号密码或订阅链接。";
            key.Focus(); return;
        }
        checking = true;
        submit.IsEnabled = false;
        submit.Content = "正在读取实际用量…";
        status.Foreground = Muted;
        status.Text = "正在通过官方 HTTPS 接口验证连接。";
        try
        {
            var result = await connect(candidate);
            if (closed) return;
            status.Text = result.Message;
            status.Foreground = result.Ok ? Muted : Brushes.Firebrick;
            if (result.Ok) { key.Clear(); Close(); }
        }
        finally
        {
            checking = false;
            if (!closed) { submit.IsEnabled = true; submit.Content = "连接并显示用量"; }
        }
    }
}
