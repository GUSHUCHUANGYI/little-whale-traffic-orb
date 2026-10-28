using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrafficOrb;

public sealed class NodeBindingWindow : Window
{
    public NodeBinding? Binding { get; private set; }
    private readonly ComboBox group = new(), node = new();
    private readonly Button save = new();

    public NodeBindingWindow(string instance, ClashChoices choices, NodeBinding? existing, bool switchAfter)
    {
        Title = "绑定 Clash 节点 · 小鲸鱼流量悬浮球";
        Width = 550; Height = 470; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei UI");
        var panel = new StackPanel { Background = Brushes.White, Margin = new Thickness(28, 24, 28, 22) };
        panel.Children.Add(new TextBlock { Text = switchAfter ? "为实例选择要切换的节点" : "实例与 Clash 节点绑定", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = instance, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 9, 0, 18), MaxHeight = 40 });
        panel.Children.Add(new TextBlock { Text = "代理组", FontSize = 12 });
        group.DisplayMemberPath = nameof(ClashProxy.Name); group.Height = 35; group.Margin = new Thickness(0, 6, 0, 14);
        group.ItemsSource = choices.Groups.ToList();
        panel.Children.Add(group);
        panel.Children.Add(new TextBlock { Text = "该实例对应的节点", FontSize = 12 });
        node.DisplayMemberPath = nameof(ClashProxy.Name); node.Height = 35; node.Margin = new Thickness(0, 6, 0, 14);
        panel.Children.Add(node);
        panel.Children.Add(new TextBlock
        {
            Text = "只列出当前 Clash 配置中的节点。请按实际对应关系选择，不会按地区猜测。不同订阅里的节点需先加入同一配置。\n双击悬浮球始终只查看用量。",
            FontSize = 11.5, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, LineHeight = 20
        });
        save.Content = switchAfter ? "绑定并切换" : "保存绑定（不切换）";
        save.IsEnabled = false; save.IsDefault = true; save.Padding = new Thickness(12, 11, 12, 11);
        save.Margin = new Thickness(0, 18, 0, 0); save.Background = new SolidColorBrush(Color.FromRgb(36, 125, 104)); save.Foreground = Brushes.White;
        save.Click += (_, _) =>
        {
            if (group.SelectedItem is not ClashProxy g || node.SelectedItem is not ClashProxy n) return;
            Binding = new(g.Name, n.Name); DialogResult = true;
        };
        panel.Children.Add(save); Content = panel;
        group.SelectionChanged += (_, _) =>
        {
            node.ItemsSource = group.SelectedItem is ClashProxy selected ? choices.Nodes(selected.Name).ToList() : [];
            node.SelectedItem = node.Items.Cast<ClashProxy>().FirstOrDefault(x => x.Name == existing?.Node);
        };
        node.SelectionChanged += (_, _) => save.IsEnabled = group.SelectedItem is ClashProxy && node.SelectedItem is ClashProxy;
        group.SelectedItem = group.Items.Cast<ClashProxy>().FirstOrDefault(x => x.Name == existing?.Group)
            ?? group.Items.Cast<ClashProxy>().FirstOrDefault(x => x.Name.Equals("PROXY", StringComparison.OrdinalIgnoreCase))
            ?? (group.Items.Count == 1 ? group.Items[0] : null);
    }
}
