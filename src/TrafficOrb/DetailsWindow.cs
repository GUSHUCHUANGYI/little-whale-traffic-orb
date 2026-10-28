using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TrafficOrb;

public sealed class DetailsWindow : Window
{
    private readonly TrafficRecorder recorder;
    private readonly Func<(VmUsage? Vm, DateTimeOffset? Updated, string? Error)> getQuota;
    private readonly TextBlock quotaTitle = new(), quotaValues = new(), quotaTime = new();
    private readonly TextBlock total = new(), status = new();
    private readonly ComboBox period = new(), scope = new(), node = new();
    private readonly DataGrid grid = new();
    private bool ready;
    public DetailsWindow(TrafficRecorder recorder, Func<(VmUsage?, DateTimeOffset?, string?)> getQuota)
    {
        this.recorder = recorder; this.getQuota = getQuota;
        Title = "流量明细 · 悬浮球";
        Width = Math.Min(780, SystemParameters.WorkArea.Width - 30);
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 30);
        MinWidth = 620; MinHeight = Math.Min(600, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F5F7F9");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(26), Background = Brush("#F5F7F9") };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "流量明细", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brush("#202B34") });
        top.Children.Add(new TextBlock { Text = "套餐总量读取平台 · 分类明细来自这台电脑", Foreground = Brush("#72808C"), FontSize = 12, Margin = new Thickness(0, 7, 0, 18) });
        var quota = new StackPanel { Margin = new Thickness(17, 12, 17, 12) };
        quotaTitle.FontSize = 12; quotaTitle.Foreground = Brush("#667683");
        quotaValues.FontSize = 20; quotaValues.FontWeight = FontWeights.SemiBold; quotaValues.Foreground = Brush("#263A44"); quotaValues.Margin = new Thickness(0, 6, 0, 5);
        quotaTime.FontSize = 11; quotaTime.Foreground = Brush("#72808C"); quotaTime.TextWrapping = TextWrapping.Wrap;
        quota.Children.Add(quotaTitle); quota.Children.Add(quotaValues); quota.Children.Add(quotaTime);
        top.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Child = quota });
        var heading = new DockPanel { Margin = new Thickness(0, 22, 0, 12) };
        heading.Children.Add(new TextBlock { Text = "本机分类", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        total.FontSize = 13; total.Foreground = Brush("#247D68"); total.HorizontalAlignment = HorizontalAlignment.Right;
        heading.Children.Add(total); top.Children.Add(heading);
        var filters = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        period.ItemsSource = new[] { "今日", "本月", "累计" }; period.SelectedIndex = 0;
        scope.ItemsSource = new[] { "全部连接", "仅代理", "仅直连" }; scope.SelectedIndex = 0;
        foreach (var box in new[] { period, scope, node }) { box.Padding = new Thickness(8, 6, 8, 6); box.Margin = new Thickness(0, 0, 8, 0); }
        Grid.SetColumn(scope, 1); Grid.SetColumn(node, 2);
        filters.Children.Add(period); filters.Children.Add(scope); filters.Children.Add(node); top.Children.Add(filters);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        status.FontSize = 11; status.Foreground = Brush("#687785"); status.TextWrapping = TextWrapping.Wrap;
        bottom.Children.Add(status);
        bottom.Children.Add(new TextBlock
        {
            Text = "分类按域名 / 应用推断，仅累计程序运行期间经本机 Clash 的流量。短连接或无法识别的流量列为“未归类”；不含其他电脑和过去的分类记录，不能代替平台账单。",
            FontSize = 11, LineHeight = 18, Foreground = Brush("#87929C"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
        });
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        grid.AutoGenerateColumns = false; grid.IsReadOnly = true; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
        grid.HeadersVisibility = DataGridHeadersVisibility.Column; grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        grid.RowHeight = 33; grid.ColumnHeaderHeight = 31; grid.BorderThickness = new Thickness(0);
        grid.Background = Brushes.White; grid.RowBackground = Brushes.White; grid.AlternatingRowBackground = Brush("#F9FBFC");
        grid.SelectionMode = DataGridSelectionMode.Single; grid.FontSize = 12;
        grid.Columns.Add(Column("类型", "Name", 2)); grid.Columns.Add(Column("上行", "UpText", 1));
        grid.Columns.Add(Column("下行", "DownText", 1)); grid.Columns.Add(Column("合计", "TotalText", 1)); grid.Columns.Add(Column("占比", "PercentText", .75));
        root.Children.Add(grid); Content = root;
        period.SelectionChanged += (_, _) => Refresh(); scope.SelectionChanged += (_, _) => Refresh(); node.SelectionChanged += (_, _) => Refresh();
        recorder.Changed += Refresh;
        Closed += (_, _) => recorder.Changed -= Refresh;
        ready = true; Refresh();
    }
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static DataGridTextColumn Column(string title, string property, double width) => new()
    {
        Header = title, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star),
        ElementStyle = new Style(typeof(TextBlock))
        {
            Setters = { new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center), new Setter(FrameworkElement.MarginProperty, new Thickness(10, 0, 5, 0)) }
        }
    };
    public void Refresh()
    {
        if (!ready) return;
        var (vm, updated, error) = getQuota();
        quotaTitle.Text = vm == null ? "平台套餐 · 尚未连接" : $"{vm.ShortName} · 整个实例（所有设备共享）";
        quotaValues.Text = vm is { HasTraffic: true } ? $"已用 {VmUsage.Number(vm.UsedGb)} GB     剩余 {(vm.Unlimited ? "不限量" : VmUsage.Number(vm.RemainingGb) + " GB")}" : "暂无平台用量数据";
        quotaTime.Text = (updated == null ? "连接账号后自动同步额度。" : $"平台同步 {updated.Value.LocalDateTime:MM-dd HH:mm:ss} · 60 秒刷新") + (error != null ? " · " + error : "");
        var nodes = new[] { "所有本机节点" }.Concat(recorder.Ledger.History.Buckets.Select(x => x.Node).Distinct().OrderBy(x => x)).ToList();
        if (node.ItemsSource is not List<string> current || !current.SequenceEqual(nodes))
        {
            string? selected = node.SelectedItem as string;
            ready = false; node.ItemsSource = nodes; node.SelectedItem = nodes.Contains(selected ?? "") ? selected : nodes[0]; ready = true;
        }
        string filter = node.SelectedIndex > 0 ? node.SelectedItem as string ?? "" : "";
        var rows = recorder.Ledger.Summary(period.SelectedItem as string ?? "今日", scope.SelectedItem as string ?? "全部连接", filter.Length == 0 ? null : filter);
        grid.ItemsSource = rows;
        total.Text = "已记录 " + TrafficFormat.Bytes(rows.Sum(x => x.Total));
        var history = recorder.Ledger.History;
        status.Text = $"{recorder.Status}\n记录起点 {history.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm} · 关闭程序时不记录 · 明细约每秒更新";
        if (history.Gaps > 0) status.Text += $" · 发现 {history.Gaps} 次采样中断 / 内核重启";
    }
}
