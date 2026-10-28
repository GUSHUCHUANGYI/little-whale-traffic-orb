using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrafficOrb;

public sealed record LocalConnection(string Id, long Upload, long Download, string Host,
    string Process, string Node, string Route, DateTimeOffset Start);
public sealed record ConnectionSample(DateTimeOffset At, long UploadTotal, long DownloadTotal, List<LocalConnection> Connections);

// Uses only the existing local controller. Never enables HTTP control, edits Clash or sends its secret off-device.
public sealed class LocalConnections : IDisposable
{
    private HttpClient? http;
    private string fingerprint = "";
    private string? secret;
    private readonly string configFile;
    public LocalConnections(string? configFile = null) => this.configFile = configFile ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "io.github.clash-verge-rev.clash-verge-rev", "clash-verge.yaml");

    public async Task<ConnectionSample> ReadAsync(CancellationToken cancel)
    {
        Configure();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connections");
        if (!string.IsNullOrEmpty(secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var response = await http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!response.IsSuccessStatusCode) throw new IOException("Clash 本地接口暂不可读");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
        return Parse(json.RootElement, DateTimeOffset.Now);
    }
    private void Configure()
    {
        if (!File.Exists(configFile)) throw new IOException("未找到正在使用的 Clash Verge");
        string raw = File.ReadAllText(configFile);
        string pipe = Scalar(raw, "external-controller-pipe"), endpoint = Scalar(raw, "external-controller");
        string newSecret = Scalar(raw, "secret");
        string identity = pipe + "|" + endpoint + "|" + newSecret;
        if (http != null && identity == fingerprint) return;
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
        string url;
        if (pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            string pipeName = pipe[9..];
            if (!Regex.IsMatch(pipeName, @"\A[A-Za-z0-9_.-]{1,100}\z")) throw new IOException("Clash 管道名称不支持");
            handler.ConnectCallback = async (_, token) =>
            {
                var connection = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await connection.ConnectAsync(token); return connection; }
                catch { connection.Dispose(); throw; }
            };
            url = "http://localhost";
        }
        else if (TryLoopback(endpoint, out var local)) url = local;
        else { handler.Dispose(); throw new IOException("Clash 未提供可读取的本地接口"); }
        http?.Dispose();
        http = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TrafficOrb/1.1");
        secret = newSecret; fingerprint = identity;
    }
    internal static string Scalar(string yaml, string name)
    {
        var match = Regex.Match(yaml, @"(?m)^" + Regex.Escape(name) + @":[ \t]*(.*)$");
        if (!match.Success) return "";
        string value = match.Groups[1].Value.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'");
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(value) ?? ""; } catch { return ""; }
        }
        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment < 0 ? value : value[..comment]).Trim();
    }
    internal static bool TryLoopback(string text, out string url)
    {
        url = "";
        if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) || uri.Port <= 0 ||
            uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.Host is "localhost" or "0.0.0.0") { url = $"http://127.0.0.1:{uri.Port}"; return true; }
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))
        { url = $"http://{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1")}:{uri.Port}"; return true; }
        return false;
    }
    internal static ConnectionSample Parse(JsonElement root, DateTimeOffset at)
    {
        static long Bytes(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n) || n < 0)
                throw new IOException("Clash 计数格式不完整");
            return n;
        }
        static string Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long upload = Bytes(root, "uploadTotal"), download = Bytes(root, "downloadTotal");
        var result = new List<LocalConnection>();
        if (!root.TryGetProperty("connections", out var rows)) throw new IOException("Clash 未返回连接列表");
        if (rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                string id = Text(row, "id");
                if (id.Length == 0 || !row.TryGetProperty("metadata", out var metadata)) continue;
                string[] chains = row.TryGetProperty("chains", out var chain) && chain.ValueKind == JsonValueKind.Array
                    ? chain.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray() : [];
                string leaf = chains.FirstOrDefault() ?? "未识别节点";
                bool direct = leaf.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) || leaf.Equals("COMPATIBLE", StringComparison.OrdinalIgnoreCase);
                bool rejected = leaf.Equals("REJECT", StringComparison.OrdinalIgnoreCase) || leaf.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase);
                string route = direct ? "直连" : chains.Length == 0 || rejected ? "未知" : "代理";
                string process = Text(metadata, "process");
                if (process.Length == 0) process = Path.GetFileName(Text(metadata, "processPath"));
                DateTimeOffset.TryParse(Text(row, "start"), out var start);
                result.Add(new LocalConnection(id, Bytes(row, "upload"), Bytes(row, "download"), Text(metadata, "host"), process,
                    leaf.Length > 120 ? leaf[..120] : leaf, route, start));
            }
        }
        else if (rows.ValueKind != JsonValueKind.Null) throw new IOException("Clash 连接列表格式不支持");
        return new ConnectionSample(at, upload, download, result);
    }
    public void Dispose() { http?.Dispose(); secret = null; fingerprint = ""; }
}
