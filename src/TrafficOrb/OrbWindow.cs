using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrafficOrb;

public sealed class OrbFace : FrameworkElement
{
    public string Node { get; set; } = "流量";
    public string Number { get; set; } = "—";
    public string Unit { get; set; } = "点击连接";
    public string Footer { get; set; } = "平台总用量";
    public double Fraction { get; set; }
    public bool Stale { get; set; }
    public bool Alert { get; set; }
    public OrbFace() { Width = 148; Height = 148; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        static SolidColorBrush Brush(string hex) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; brush.Freeze(); return brush; }
        var ink = Brush("#F4F7F8");
        var muted = Brush("#9AA6AF");
        var accent = Brush(Alert ? "#FF8F8B" : Fraction >= .8 ? "#F6C56F" : "#86E3C4");
        dc.DrawEllipse(Brush("#1821272E"), null, new Point(74, 77), 64.5, 64.5);
        dc.DrawEllipse(Brush("#21282F"), new Pen(Brush("#424B53"), .7), new Point(74, 74), 63, 63);
        dc.DrawEllipse(null, new Pen(Brush("#39434B"), 3.5), new Point(74, 74), 58.5, 58.5);
        if (Fraction >= .99999) dc.DrawEllipse(null, new Pen(accent, 3.5), new Point(74, 74), 58.5, 58.5);
        else if (Fraction > 0)
        {
            double angle = -Math.PI / 2 + Math.Clamp(Fraction, 0, 1) * 2 * Math.PI;
            var geometry = new StreamGeometry();
            using (var shape = geometry.Open())
            {
                shape.BeginFigure(new Point(74, 15.5), false, false);
                shape.ArcTo(new Point(74 + Math.Cos(angle) * 58.5, 74 + Math.Sin(angle) * 58.5),
                    new Size(58.5, 58.5), 0, Fraction > .5, SweepDirection.Clockwise, true, false);
            }
            var pen = new Pen(accent, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, geometry);
        }
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Text(string value, double y, double size, Brush color, FontWeight weight, string font = "Microsoft YaHei UI")
        {
            var text = new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal), size, color, dpi);
            dc.DrawText(text, new Point((148 - text.Width) / 2, y));
        }
        Text(Node, 32, 10.5, muted, FontWeights.Normal);
        Text(Number, 48, Number.Length >= 7 ? 23 : 29, ink, FontWeights.SemiBold, "Segoe UI");
        Text(Unit, 84, 10, Stale ? Brush("#F6C56F") : muted, FontWeights.Normal);
        Text(Footer, 103, 8.5, Stale ? Brush("#F6C56F") : muted, FontWeights.Normal);
    }
}

public sealed class OrbWindow : Window
{
    private readonly LocalStore store;
    private readonly QuotaClient client = new();
    private readonly LocalConnections nodeController = new();
    private readonly NodeSwitcher nodeSwitcher;
    private readonly OrbSettings settings;
    private readonly OrbFace face = new();
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer localTimer;
    private readonly TrafficRecorder recorder;
    private readonly CancellationTokenSource lifetime = new();
    private string? token;
    private bool busy, closing;
    private bool switching;
    private string? switchStatus;
    private NodeBindingWindow? bindingWindow;
    private string? error;
    private ConnectWindow? login;
    private DetailsWindow? detailsWindow;
    private ResidentHost? resident;
    private bool allowExit, transportDirty, retryWhenFree;
    private readonly bool preview;
    private readonly ContextMenu menu = new();

    public OrbWindow(bool preview = false)
    {
        nodeSwitcher = new NodeSwitcher(nodeController);
        this.preview = preview;
        store = new LocalStore(preview ? Path.Combine(Path.GetTempPath(), "TrafficOrb-Preview") : null);
        settings = preview ? new OrbSettings() : store.ReadSettings();
        token = preview ? null : store.ReadToken();
        if (!preview && token != null) { try { store.EnsureCredentialBackup(); } catch (IOException) { } }
        recorder = new TrafficRecorder(preview ? Path.Combine(Path.GetTempPath(), "TrafficOrb-Preview") : null);
        Title = "小鲸鱼流量悬浮球";
        Width = Height = 148;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = preview;
        Topmost = true;
        Content = face;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetClearTypeHint(face, ClearTypeHint.Enabled);
        System.Windows.Automation.AutomationProperties.SetName(face, "小鲸鱼流量悬浮球，双击只查看实例用量，右键切换节点，单击刷新，可拖动");
        ContextMenu = menu;
        menu.Opened += (_, _) => RebuildMenu();
        MouseLeftButtonDown += OnMouseDown;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += async (_, _) => await RefreshAsync();
        localTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        localTimer.Tick += async (_, _) => await recorder.PollAsync(lifetime.Token);
        Closing += (_, args) =>
        {
            if (!preview && !allowExit && !Dispatcher.HasShutdownStarted)
            {
                args.Cancel = true;
                Hide(); SavePosition();
            }
        };
        Closed += (_, _) =>
        {
            closing = true;
            if (!preview)
            {
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            lifetime.Cancel(); timer.Stop(); localTimer.Stop(); login?.Close(); detailsWindow?.Close(); bindingWindow?.Close();
            resident?.Dispose(); client.Dispose(); recorder.Dispose(); nodeController.Dispose(); lifetime.Dispose();
            if (!preview) SavePosition();
        };
        Loaded += async (_, _) =>
        {
            SetInitialPosition();
            if (preview)
            {
                settings.CachedUsage = [new("11111111-1111-4111-8111-111111111111", "香港节点", "HK", 150, 1073741824, 4123168604, 0, "running")];
                settings.LastSuccess = DateTimeOffset.UtcNow;
                Redraw(); return;
            }
            resident = new ResidentHost(ShowOrb, ToggleVisibility, () => IsVisible, () => OpenDetails(),
                () => _ = RefreshAsync(), ToggleStartup, RequestExit);
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            if (settings.StartWithWindows ?? true)
            {
                try { StartupRegistration.SetEnabled(true); settings.StartWithWindows = true; TrySave(); }
                catch { settings.StartWithWindows = StartupRegistration.Enabled; }
            }
            Redraw();
            timer.Start();
            localTimer.Start();
            await recorder.PollAsync(lifetime.Token);
            if (string.IsNullOrWhiteSpace(token)) OpenConnect();
            else await RefreshAsync();
        };
    }
    private VmUsage? Selected => settings.CachedUsage.FirstOrDefault(x => x.Id == settings.SelectedVmId);
    private void SelectDefault()
    {
        if (settings.CachedUsage.Any(x => x.Id == settings.SelectedVmId)) return;
        settings.SelectedVmId = settings.CachedUsage.FirstOrDefault(x => !x.Expired && x.Status == "running")?.Id
            ?? settings.CachedUsage.FirstOrDefault()?.Id;
    }
    private void SetInitialPosition()
    {
        var work = SystemParameters.WorkArea;
        Left = Math.Clamp(settings.Left ?? (work.Right - Width - 28), work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(settings.Top ?? (work.Top + 140), work.Top, Math.Max(work.Top, work.Bottom - Height));
    }
    private void SavePosition()
    {
        settings.Left = Left; settings.Top = Top;
        TrySave();
    }
    private void TrySave()
    {
        if (preview) return;
        try { store.SaveSettings(settings); }
        catch { error = "本机设置暂未保存"; }
    }
    private async void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            CycleVm();
            return;
        }
        double x = Left, y = Top;
        try { DragMove(); } catch (InvalidOperationException) { }
        bool moved = Math.Abs(Left - x) + Math.Abs(Top - y) > 3;
        if (moved) { SavePosition(); return; }
        if (preview) return;
        if (string.IsNullOrWhiteSpace(token)) token = store.ReadToken();
        if (string.IsNullOrWhiteSpace(token)) OpenConnect();
        else await RefreshAsync();
    }
    public void ShowOrb()
    {
        if (closing) return;
        if (!IsVisible) Show();
        WindowState = WindowState.Normal; Activate();
    }
    private void ToggleVisibility() { if (IsVisible) { Hide(); SavePosition(); } else ShowOrb(); }
    public void RequestExit() { allowExit = true; Close(); }
    public void AllowSessionExit() => allowExit = true;
    private void ToggleStartup()
    {
        try
        {
            bool enabled = !StartupRegistration.Enabled;
            StartupRegistration.SetEnabled(enabled); settings.StartWithWindows = enabled; TrySave();
        }
        catch { MessageBox.Show("无法保存当前用户的开机启动设置。后台常驻仍可继续使用。", "小鲸鱼流量悬浮球"); }
    }
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => ScheduleReconnect();
    private void OnNetworkAddressChanged(object? sender, EventArgs args) => ScheduleReconnect();
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
    {
        if (args.Mode == Microsoft.Win32.PowerModes.Resume) ScheduleReconnect();
    }
    private void ScheduleReconnect()
    {
        if (closing || preview || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            transportDirty = true;
            if (busy) retryWhenFree = true;
            else { timer.Stop(); timer.Interval = TimeSpan.FromSeconds(2); timer.Start(); }
        });
    }
    private IEnumerable<VmUsage> OrderedUsage => settings.CachedUsage.OrderBy(x => x.Expired)
        .ThenBy(x => x.Region).ThenBy(x => x.Name).ThenBy(x => x.Id);
    private void CycleVm()
    {
        settings.SelectedVmId = NextVmId(OrderedUsage.ToList(), settings.SelectedVmId);
        TrySave(); Redraw();
    }
    internal static string? NextVmId(IReadOnlyList<VmUsage> nodes, string? current)
    {
        if (nodes.Count == 0) return null;
        int index = nodes.ToList().FindIndex(x => x.Id == current);
        return nodes[(index + 1) % nodes.Count].Id;
    }
    private DetailsWindow OpenDetails()
    {
        if (detailsWindow != null) { detailsWindow.Activate(); return detailsWindow; }
        detailsWindow = new DetailsWindow(recorder, () => (Selected, settings.LastSuccess, error));
        detailsWindow.Closed += (_, _) => detailsWindow = null;
        detailsWindow.Show(); detailsWindow.Activate();
        return detailsWindow;
    }
    private async Task ChooseBindingAsync(VmUsage vm, bool switchAfter)
    {
        if (preview || closing || switching) return;
        if (bindingWindow != null) { bindingWindow.Activate(); return; }
        switching = true; switchStatus = "正在读取 Clash 节点…";
        try
        {
            var choices = await nodeSwitcher.ReadAsync(lifetime.Token);
            if (choices.Mode == "direct") throw new IOException("Clash 当前为直连模式，请先在 Clash 切到规则或全局模式");
            if (!choices.Groups.Any(x => choices.Nodes(x.Name).Any()))
                throw new IOException("当前 Clash 配置没有可绑定的手动代理组和节点。请先在 Clash 导入包含目标节点的配置");
            settings.NodeBindings.TryGetValue(vm.Id, out var existing);
            bindingWindow = new NodeBindingWindow(vm.Name, choices, existing, switchAfter);
            NodeBinding? chosen = bindingWindow.ShowDialog() == true ? bindingWindow.Binding : null;
            bindingWindow = null;
            if (chosen == null) { switchStatus = "已取消，当前节点未改变"; return; }
            settings.NodeBindings[vm.Id] = chosen; TrySave();
            switchStatus = "绑定已保存 · 双击仍仅查看用量";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            ShowSwitchFailure(ex); return;
        }
        finally { switching = false; if (!closing) Redraw(); }
        if (switchAfter) await SwitchVmAsync(vm);
    }
    private async Task SwitchVmAsync(VmUsage vm)
    {
        if (preview || closing || switching) return;
        if (vm.Expired || !vm.Status.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            switchStatus = "该实例已到期或未运行，未切换节点";
            MessageBox.Show(switchStatus, "小鲸鱼流量悬浮球"); return;
        }
        if (!settings.NodeBindings.TryGetValue(vm.Id, out var binding)) { await ChooseBindingAsync(vm, true); return; }
        switching = true; switchStatus = "正在检查目标节点并切换…";
        try
        {
            await nodeSwitcher.SwitchAsync(binding, lifetime.Token);
            settings.SelectedVmId = vm.Id; TrySave();
            switchStatus = $"Clash 已选：{binding.Node} · 新连接生效";
            transportDirty = true;
            _ = RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        { ShowSwitchFailure(ex); }
        finally { switching = false; if (!closing) Redraw(); }
    }
    private void ShowSwitchFailure(Exception exception)
    {
        if (closing) return;
        switchStatus = exception is OperationCanceledException ? "Clash 响应超时，请核对当前节点" : exception.Message;
        MessageBox.Show(switchStatus + "\n\n可从右键「绑定 Clash 节点」重新选择；双击查看用量不会切换节点。", "节点未确认切换 · 小鲸鱼流量悬浮球");
    }
    private void RebuildMenu()
    {
        menu.Items.Clear();
        var vm = Selected;
        var status = new MenuItem { Header = busy ? "正在同步…" : error ?? (settings.LastSuccess is { } time ? $"已同步 · {time.LocalDateTime:HH:mm:ss}" : "尚未连接"), IsEnabled = false };
        menu.Items.Add(status);
        if (switchStatus != null) menu.Items.Add(new MenuItem { Header = switchStatus, IsEnabled = false });
        if (vm != null)
        {
            menu.Items.Add(new MenuItem { Header = vm.HasTraffic ? $"本月已用 {VmUsage.Number(vm.UsedGb)} GB" : "平台未提供本月统计", IsEnabled = false });
            if (vm.ExpiresAt > 0) menu.Items.Add(new MenuItem { Header = $"套餐到期 {DateTimeOffset.FromUnixTimeSeconds(vm.ExpiresAt).LocalDateTime:yyyy-MM-dd HH:mm}", IsEnabled = false });
        }
        menu.Items.Add(new Separator());
        var details = new MenuItem { Header = "流量明细…", IsEnabled = !preview };
        details.Click += (_, _) => OpenDetails(); menu.Items.Add(details);
        if (settings.CachedUsage.Count > 0)
        {
            var nodes = new MenuItem { Header = "切换实例（同时切节点）", IsEnabled = !switching && !preview };
            var bindings = new MenuItem { Header = "绑定 Clash 节点…", IsEnabled = !switching && !preview };
            foreach (var item in OrderedUsage)
            {
                string title = string.IsNullOrWhiteSpace(item.Name) ? item.ShortName : item.Name;
                if (title.Length > 40) title = title[..40] + "…";
                var entry = new MenuItem
                {
                    Header = $"{title} · {(item.Unlimited ? "不限量" : $"{item.LimitGb:0} GB")}{(item.Expired ? " · 已到期" : "")}",
                    IsCheckable = false, IsEnabled = !item.Expired && item.Status.Equals("running", StringComparison.OrdinalIgnoreCase),
                    ToolTip = settings.NodeBindings.TryGetValue(item.Id, out var binding) ? $"切换 {binding.Group} → {binding.Node}" : "首次使用需选择这个实例对应的 Clash 节点"
                };
                entry.Click += async (_, _) => await SwitchVmAsync(item);
                nodes.Items.Add(entry);
                var link = new MenuItem { Header = title + (settings.NodeBindings.ContainsKey(item.Id) ? " · 已绑定" : " · 未绑定") };
                link.Click += async (_, _) => await ChooseBindingAsync(item, false); bindings.Items.Add(link);
            }
            menu.Items.Add(nodes);
            menu.Items.Add(bindings);
            var next = new MenuItem { Header = "查看下个实例用量（双击，不切节点）", IsEnabled = settings.CachedUsage.Count > 1 };
            next.Click += (_, _) => CycleVm(); menu.Items.Add(next);
        }
        var refresh = new MenuItem { Header = "立即刷新", IsEnabled = !busy && !preview && token != null };
        refresh.Click += async (_, _) => await RefreshAsync(); menu.Items.Add(refresh);
        var connect = new MenuItem { Header = token == null ? "连接账号…" : "重新连接…", IsEnabled = !preview };
        connect.Click += (_, _) => OpenConnect(); menu.Items.Add(connect);
        menu.Items.Add(new Separator());
        if (!preview)
        {
            menu.Items.Add(new MenuItem { Header = "后台常驻 · 断网自动重试", IsEnabled = false });
            var hide = new MenuItem { Header = "隐藏悬浮球（后台继续统计）" };
            hide.Click += (_, _) => ToggleVisibility(); menu.Items.Add(hide);
            var startup = new MenuItem { Header = "开机自动启动", IsCheckable = true, IsChecked = StartupRegistration.Enabled };
            startup.Click += (_, _) => ToggleStartup(); menu.Items.Add(startup);
        }
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => RequestExit(); menu.Items.Add(exit);
    }
    private async Task RefreshAsync()
    {
        if (busy || closing || preview || string.IsNullOrWhiteSpace(token)) return;
        busy = true;
        try
        {
            if (transportDirty) { client.ResetTransport(); transportDirty = false; }
            var rows = await client.GetUsageAsync(token, lifetime.Token);
            // A list response can omit counters. Fetch selected VM details without saving credentials.
            string? selected = settings.SelectedVmId;
            if (rows.FirstOrDefault(x => x.Id == selected) is { HasTraffic: false } missing)
            {
                var detail = await client.GetVmAsync(missing.Id, token, lifetime.Token);
                rows[rows.FindIndex(x => x.Id == missing.Id)] = detail;
            }
            settings.CachedUsage = rows; settings.LastSuccess = DateTimeOffset.UtcNow;
            error = null; SelectDefault(); TrySave();
        }
        catch (QuotaException ex) { error = ex.Message; }
        catch (OperationCanceledException) { if (!closing) error = "连接超时，等待重试"; }
        catch (HttpRequestException) { error = "网络不可达，等待重试"; }
        catch { error = "本次同步失败，等待重试"; }
        finally
        {
            busy = false;
            if (!closing)
            {
                transportDirty |= error != null;
                timer.Interval = TimeSpan.FromSeconds(error == null ? 60 : 10);
                Redraw();
                if (retryWhenFree) { retryWhenFree = false; _ = RefreshAsync(); }
            }
        }
    }
    private void OpenConnect()
    {
        if (preview || closing) return;
        if (login != null) { login.Activate(); return; }
        login = new ConnectWindow(async candidate =>
        {
            if (busy) return (false, "正在刷新，请稍后再试");
            busy = true;
            try
            {
                var rows = await client.GetUsageAsync(candidate, lifetime.Token);
                store.SaveToken(candidate);
                token = candidate; settings.CachedUsage = rows;
                settings.LastSuccess = DateTimeOffset.UtcNow;
                error = null; SelectDefault(); TrySave(); Redraw();
                return (true, $"已连接 · {rows.Count} 个实例");
            }
            catch (QuotaException ex) { return (false, ex.Message); }
            catch (OperationCanceledException) { return (false, "查询超时，请重试"); }
            catch { return (false, "暂时无法连接平台，请重试"); }
            finally { busy = false; }
        });
        login.Closed += (_, _) => { login = null; Activate(); };
        login.Show(); login.Activate();
    }
    public void Redraw()
    {
        SelectDefault();
        var vm = Selected;
        bool old = settings.LastSuccess is not { } time || DateTimeOffset.UtcNow - time > TimeSpan.FromMinutes(3);
        bool stale = error != null || old;
        face.Node = preview ? "香港 · 预览" : vm?.ShortName ?? "流量";
        face.Number = vm is { HasTraffic: true } ? (vm.Unlimited ? "∞" : VmUsage.Number(vm.RemainingGb)) : "—";
        face.Unit = vm is { HasTraffic: true } ? (vm.Unlimited ? "不限量" : "GB 剩余") : token == null ? "点击连接" : "暂无统计";
        face.Footer = vm is { HasTraffic: true } ? (vm.Expired ? "套餐已到期" : stale ? "数据未同步" : $"已用 {VmUsage.Number(vm.UsedGb)} GB") : (error != null ? "同步失败" : "平台总用量");
        face.Fraction = vm is { HasTraffic: true } ? vm.UsedFraction : 0;
        face.Stale = vm != null && stale;
        face.Alert = vm is { HasTraffic: true } && (vm.Expired || (!vm.Unlimited && vm.UsedFraction >= .95));
        string details = vm == null ? "点击连接独角鲸云，即可读取实际套餐流量。\n双击只查看实例用量，右键切换节点，拖动改变位置。" :
            $"{vm.ShortName} · {vm.Name}\n" +
            (vm.HasTraffic ? $"本月入站：{VmUsage.Number((vm.InBytes ?? 0) / VmUsage.BytesPerGb)} GB\n本月出站：{VmUsage.Number((vm.OutBytes ?? 0) / VmUsage.BytesPerGb)} GB\n已用合计：{VmUsage.Number(vm.UsedGb)} GB\n套餐额度：{(vm.Unlimited ? "不限量" : $"{vm.LimitGb:0.##} GB")}\n" : "平台没有返回完整的本月计数。\n") +
            (vm.ExpiresAt > 0 ? $"到期：{DateTimeOffset.FromUnixTimeSeconds(vm.ExpiresAt).LocalDateTime:yyyy-MM-dd HH:mm}\n" : "") +
            $"最后成功同步：{settings.LastSuccess?.LocalDateTime.ToString("MM-dd HH:mm:ss") ?? "无"}\n" +
            (error != null ? $"{error}\n" : "") +
            "每 60 秒读取平台；平台统计本身可能延迟。\n所有设备共享本实例额度。外圈表示已用比例。\nGB 按平台 1024³ 字节口径计算。\n双击仅查看实例用量；右键「切换实例」才切换 Clash 节点。";
        if (switchStatus != null) details += "\n" + switchStatus;
        ToolTip = details;
        System.Windows.Automation.AutomationProperties.SetName(face, $"{face.Node}，{face.Number} {face.Unit}，{face.Footer}");
        face.InvalidateVisual();
        resident?.SetText($"小鲸鱼流量悬浮球 · {face.Node} · {face.Number} {face.Unit}" + (stale ? " · 重试中" : ""));
        detailsWindow?.Refresh();
    }
    public bool HasLiveQuota => settings.LastSuccess is { } stamp && DateTimeOffset.UtcNow - stamp < TimeSpan.FromMinutes(1) && error == null;
    private bool hiddenSamplingVerified;
    public async Task VerifyHiddenSamplingAsync()
    {
        var before = recorder.Ledger.History.LastSample;
        Hide();
        await Task.Delay(2200);
        hiddenSamplingVerified = !closing && resident?.Visible == true && recorder.Ledger.History.LastSample > before;
        ShowOrb();
    }
    public async Task CaptureLiveAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await recorder.PollAsync(lifetime.Token);
        RenderPreview(Path.Combine(folder, "悬浮球-实际用量.png"));
        RebuildMenu();
        menu.PlacementTarget = face;
        menu.IsOpen = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        RenderElement(menu, Path.Combine(folder, "右键菜单-后台常驻.png"));
        menu.IsOpen = false;
        var details = OpenDetails();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        RenderElement((FrameworkElement)details.Content, Path.Combine(folder, "流量明细-实际数据.png"));
        var proof = new
        {
            Updated = settings.LastSuccess, Error = error, LocalConnected = recorder.Connected,
            LocalStatus = recorder.Status, Selected = settings.SelectedVmId,
            Instances = settings.CachedUsage.Select(x => new { x.Id, x.ShortName, x.Status, x.LimitGb, x.UsedGb, x.RemainingGb, x.HasTraffic }),
            CategoryRows = recorder.Ledger.Summary("今日", "全部连接"),
            OnlyLocalCategories = true,
            ResidentTray = resident?.Visible == true, StartupEnabled = StartupRegistration.Enabled,
            SavedCredentialLoaded = !string.IsNullOrWhiteSpace(token), LoginWindowOpen = login != null,
            HiddenSamplingContinues = hiddenSamplingVerified,
            AppVersion = typeof(OrbWindow).Assembly.GetName().Version?.ToString()
        };
        File.WriteAllText(Path.Combine(folder, "verification.json"), System.Text.Json.JsonSerializer.Serialize(proof, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    public static void RenderElement(FrameworkElement element, string file)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5), (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); png.Save(output);
    }
    public void RenderPreview(string file)
    {
        UpdateLayout();
        var image = new RenderTargetBitmap(296, 296, 192, 192, PixelFormats.Pbgra32);
        image.Render(face);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(file); png.Save(output);
    }
    internal async Task RenderSwitchMenuPreviewAsync(string file)
    {
        RebuildMenu(); menu.PlacementTarget = face; menu.IsOpen = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        RenderElement(menu, file); menu.IsOpen = false;
    }
}
