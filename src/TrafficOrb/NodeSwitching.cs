using System.IO;
using System.Text.Json;

namespace TrafficOrb;

public sealed record NodeBinding(string Group, string Node);
public sealed record ClashProxy(string Name, string Type, string Current, List<string> Members)
{
    public bool IsNode => Members.Count == 0 && Type is not ("Selector" or "URLTest" or "Fallback" or "LoadBalance" or "Relay") &&
        !new[] { "DIRECT", "REJECT", "REJECT-DROP", "COMPATIBLE", "PASS" }.Contains(Name, StringComparer.OrdinalIgnoreCase) &&
        Type.ToLowerInvariant() is not ("direct" or "reject" or "rejectdrop" or "compatible" or "pass" or "dns");
}
public sealed record ClashChoices(string Mode, Dictionary<string, ClashProxy> Proxies)
{
    public IEnumerable<ClashProxy> Groups => Proxies.Values.Where(x => x.Type == "Selector" &&
        (Mode == "global" ? x.Name == "GLOBAL" : x.Name != "GLOBAL"));
    public IEnumerable<ClashProxy> Nodes(string group) => Proxies.TryGetValue(group, out var entry)
        ? entry.Members.Where(Proxies.ContainsKey).Select(x => Proxies[x]).Where(x => x.IsNode)
        : [];
    public bool Contains(NodeBinding binding) => Groups.Any(x => x.Name == binding.Group) && Nodes(binding.Group).Any(x => x.Name == binding.Node);
}

// View selection never calls this class. Only an explicit context-menu switch may write to Clash.
public sealed class NodeSwitcher(LocalConnections controller)
{
    public async Task<ClashChoices> ReadAsync(CancellationToken cancel)
    {
        using var config = await controller.ReadJsonAsync("/configs", cancel);
        string mode = Text(config.RootElement, "mode").ToLowerInvariant();
        using var proxies = await controller.ReadJsonAsync("/proxies", cancel);
        return Parse(mode, proxies.RootElement);
    }
    internal static ClashChoices Parse(string mode, JsonElement root)
    {
        var result = new Dictionary<string, ClashProxy>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Object)
            throw new IOException("Clash 未返回节点列表");
        foreach (var property in proxies.EnumerateObject())
        {
            var row = property.Value;
            if (row.ValueKind != JsonValueKind.Object) continue;
            var members = row.TryGetProperty("all", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
            result[property.Name] = new(property.Name, Text(row, "type"), Text(row, "now"), members);
        }
        return new(mode.ToLowerInvariant(), result);
    }
    public async Task SwitchAsync(NodeBinding binding, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(binding.Group) || string.IsNullOrWhiteSpace(binding.Node)) throw new IOException("请先绑定这个实例的 Clash 节点");
        var before = await ReadAsync(cancel);
        Validate(before, binding);
        if (before.Proxies[binding.Group].Current == binding.Node) return;

        // Test the target through Mihomo without changing the active selection.
        string delayPath = "/proxies/" + Uri.EscapeDataString(binding.Node) +
            "/delay?timeout=5000&url=" + Uri.EscapeDataString("https://www.gstatic.com/generate_204");
        using var delay = await controller.ReadJsonAsync(delayPath, cancel);
        if (delay.RootElement.ValueKind != JsonValueKind.Object || !delay.RootElement.TryGetProperty("delay", out var measured) ||
            measured.ValueKind != JsonValueKind.Number || !measured.TryGetInt32(out var ms) || ms <= 0)
            throw new IOException("目标节点连通检查失败，已保留当前节点");

        // Recheck immediately before writing: a profile/mode may have changed during the test.
        var latest = await ReadAsync(cancel);
        Validate(latest, binding);
        await controller.SelectAsync(binding.Group, binding.Node, cancel);
        using var selected = await controller.ReadJsonAsync("/proxies/" + Uri.EscapeDataString(binding.Group), cancel);
        if (Text(selected.RootElement, "now") != binding.Node)
            throw new IOException("Clash 尚未确认切换结果，请在 Clash 中核对当前节点");
    }
    private static void Validate(ClashChoices choices, NodeBinding binding)
    {
        if (choices.Mode == "direct") throw new IOException("Clash 当前为直连模式，请先在 Clash 切到规则或全局模式");
        if (choices.Mode is not ("rule" or "global")) throw new IOException("Clash 当前模式不支持节点切换");
        if (!choices.Contains(binding)) throw new IOException("绑定节点不在当前 Clash 配置或代理组中，请重新绑定；其他订阅的节点需先加入当前配置");
    }
    private static string Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
