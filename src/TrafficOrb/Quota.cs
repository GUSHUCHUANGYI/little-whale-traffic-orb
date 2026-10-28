using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TrafficOrb;

public sealed record VmUsage(
    string Id, string Name, string Region, decimal LimitGb,
    long? InBytes, long? OutBytes, long ExpiresAt, string Status)
{
    public const decimal BytesPerGb = 1024m * 1024m * 1024m;
    public bool HasTraffic => InBytes.HasValue && OutBytes.HasValue && LimitGb >= 0;
    public bool Unlimited => LimitGb == 0;
    public decimal UsedGb => ((decimal)(InBytes ?? 0) + (decimal)(OutBytes ?? 0)) / BytesPerGb;
    public decimal RemainingGb => Math.Max(0, LimitGb - UsedGb);
    public double UsedFraction => Unlimited ? 0 : (double)Math.Clamp(UsedGb / Math.Max(1, LimitGb), 0, 1);
    public bool Expired => ExpiresAt > 0 && ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public string ShortName
    {
        get
        {
            string region = Region.ToUpperInvariant() switch
            {
                "HK" => "香港", "US" => "美国", "JP" => "日本", "SG" => "新加坡",
                _ => string.IsNullOrWhiteSpace(Region) ? "服务器" : Region.ToUpperInvariant()
            };
            return $"{region} · {Id[..Math.Min(4, Id.Length)]}";
        }
    }
    public static string Number(decimal number) => number.ToString(number >= 1000 ? "0" : "0.0", CultureInfo.InvariantCulture);
}

public sealed class QuotaException(string message, bool loginRequired = false) : Exception(message)
{
    public bool LoginRequired { get; } = loginRequired;
}

// The client deliberately exposes GET requests only. It cannot renew, purchase or modify a VM.
public sealed class QuotaClient : IDisposable
{
    public const string ApiOrigin = "https://api.fuckip.me";
    private HttpClient http;
    private readonly bool injectedHandler;
    public QuotaClient(HttpMessageHandler? handler = null)
    {
        injectedHandler = handler != null;
        http = CreateHttp(handler);
    }
    private static HttpClient CreateHttp(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(7),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = false
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TrafficOrb/1.2");
        return http;
    }
    // Called between requests after a network change or failed attempt. Keeps the saved credential intact.
    public void ResetTransport()
    {
        if (injectedHandler) return;
        var old = http; http = CreateHttp(); old.Dispose();
    }
    public async Task<List<VmUsage>> GetUsageAsync(string token, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new QuotaException("请先登录", true);
        var result = new List<VmUsage>();
        for (int page = 1; page <= 50; page++)
        {
            using var json = await GetAsync($"/api/v1/vms?page_size=100&page={page}", token, cancel);
            var payload = Payload(json.RootElement);
            if (!payload.TryGetProperty("vms", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new QuotaException("平台返回格式发生变化");
            var parsed = rows.EnumerateArray().Select(ParseVm).ToList();
            result.AddRange(parsed);
            int total = payload.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) ? n : -1;
            if (parsed.Count == 0 || (total >= 0 && result.Count >= total) || (total < 0 && parsed.Count < 100))
                return result;
        }
        throw new QuotaException("实例数量过多，未取完数据");
    }
    public async Task<VmUsage> GetVmAsync(string id, string token, CancellationToken cancel = default)
    {
        if (!Guid.TryParse(id, out _)) throw new QuotaException("实例编号无效");
        using var json = await GetAsync($"/api/v1/vms/{id}", token, cancel);
        return ParseVm(Payload(json.RootElement));
    }
    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiOrigin + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new QuotaException("账号验证未通过，已保留凭据并自动重试", true);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new QuotaException("平台或当前线路拒绝访问，自动重试中");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new QuotaException("平台限流，稍后重试");
        if (!response.IsSuccessStatusCode) throw new QuotaException($"平台暂不可用 · HTTP {(int)response.StatusCode}");
        await using var content = await response.Content.ReadAsStreamAsync(cancel);
        try { return await JsonDocument.ParseAsync(content, cancellationToken: cancel); }
        catch (JsonException) { throw new QuotaException("平台未返回流量数据"); }
    }
    internal static JsonElement Payload(JsonElement root)
    {
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int value) && value != 0)
            throw new QuotaException("平台拒绝本次查询，已保留凭据", value == 401);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new QuotaException("平台返回格式发生变化");
        return data;
    }
    internal static VmUsage ParseVm(JsonElement row)
    {
        static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static long? Counter(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;
        decimal limit = row.TryGetProperty("monthly_traffic_gb", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetDecimal(out var amount) ? amount : -1;
        string id = Str(row, "id");
        if (!Guid.TryParse(id, out _)) throw new QuotaException("平台实例编号无效");
        string name = Str(row, "remark");
        if (string.IsNullOrWhiteSpace(name)) name = Str(row, "machine_name");
        return new VmUsage(id, name, Str(row, "machine_region"), limit,
            Counter(row, "monthly_traffic_in"), Counter(row, "monthly_traffic_out"),
            Counter(row, "expires_at") ?? 0, Str(row, "status"));
    }
    public void Dispose() => http.Dispose();
}
