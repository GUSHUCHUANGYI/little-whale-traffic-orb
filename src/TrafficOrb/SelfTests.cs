using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TrafficOrb;

internal static class SelfTests
{
    private const string Id = "11111111-1111-4111-8111-111111111111";
    public static int Run(string report)
    {
        var passed = new List<string>();
        string? failure = null;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            passed.Add(name);
        }
        try
        {
            var vm = new VmUsage(Id, "香港", "HK", 150, 10L * 1073741824, 20L * 1073741824, 0, "running");
            Check(vm.UsedGb == 30 && vm.RemainingGb == 120 && Math.Abs(vm.UsedFraction - .2) < .00001, "inbound plus outbound use platform binary GB");
            var exhausted = vm with { LimitGb = 20 };
            Check(exhausted.RemainingGb == 0 && exhausted.UsedFraction == 1, "exhausted quota clamps to zero");
            var unlimited = vm with { LimitGb = 0 };
            Check(unlimited.Unlimited && unlimited.UsedFraction == 0, "zero quota means unlimited");
            var overflow = vm with { InBytes = long.MaxValue, OutBytes = long.MaxValue };
            Check(overflow.UsedGb > 0 && overflow.UsedFraction == 1, "large byte counters do not overflow");
            using var json = JsonDocument.Parse($$$"""{"code":0,"data":{"vms":[{"id":"{{{Id}}}","machine_region":"HK","monthly_traffic_gb":150,"monthly_traffic_in":1073741824,"monthly_traffic_out":2147483648,"credentials":{"password":"never-cache-this"}}],"total":1}}""");
            var row = QuotaClient.ParseVm(QuotaClient.Payload(json.RootElement).GetProperty("vms")[0]);
            Check(row.UsedGb == 3 && row.RemainingGb == 147, "API envelope and monthly fields parse");
            Check(!JsonSerializer.Serialize(row).Contains("never-cache-this"), "VM password cannot enter cache model");
            using var missing = JsonDocument.Parse($$"""{"id":"{{Id}}","monthly_traffic_gb":150}""");
            Check(!QuotaClient.ParseVm(missing.RootElement).HasTraffic, "missing counters never become a false zero");
            using var http = new QuotaClient(new FakeHandler(HttpStatusCode.OK, json.RootElement.GetRawText()));
            var response = http.GetUsageAsync("test-token").GetAwaiter().GetResult();
            Check(response.Count == 1 && response[0].RemainingGb == 147, "read-only API request returns correct quota");
            using var unauthorized = new QuotaClient(new FakeHandler(HttpStatusCode.Unauthorized, "{}"));
            bool login = false;
            try { unauthorized.GetUsageAsync("bad-token").GetAwaiter().GetResult(); }
            catch (QuotaException e) { login = e.LoginRequired; }
            Check(login, "expired authorization requests a new login");
            using var badGateway = new QuotaClient(new FakeHandler(HttpStatusCode.BadGateway, "bad gateway"));
            bool rejected = false;
            try { badGateway.GetUsageAsync("test-token").GetAwaiter().GetResult(); }
            catch (QuotaException e) { rejected = e.Message.Contains("502"); }
            Check(rejected, "server errors are not shown as fresh traffic");
            using var forbidden = new QuotaClient(new FakeHandler(HttpStatusCode.Forbidden, "blocked"));
            bool preservesAuth = false;
            try { forbidden.GetUsageAsync("test-token").GetAwaiter().GetResult(); }
            catch (QuotaException e) { preservesAuth = !e.LoginRequired; }
            Check(preservesAuth, "HTTP 403 does not misclassify a route block as expired credentials");
            using var recovering = new QuotaClient(new RecoveringHandler(json.RootElement.GetRawText()));
            bool offline = false;
            try { recovering.GetUsageAsync("unchanged-token").GetAwaiter().GetResult(); }
            catch (HttpRequestException) { offline = true; }
            Check(offline && recovering.GetUsageAsync("unchanged-token").GetAwaiter().GetResult().Count == 1,
                "same saved credential succeeds after a simulated disconnect");
            Check(StartupRegistration.BuildCommand(@"C:\Users\test\My Apps\TrafficOrb.exe") == "\"C:\\Users\\test\\My Apps\\TrafficOrb.exe\" --background",
                "startup command preserves executable paths containing spaces");
            Check(TrafficCategories.Classify("cdn.googlevideo.com", "") == "视频 / 直播" &&
                TrafficCategories.Classify("api.openai.com", "") == "AI 工具", "domain categories cover AI and video");
            Check(TrafficCategories.Classify("openai.com.evil.invalid", "") == "其他网站 / 服务", "classification respects domain boundaries");
            Check(LocalConnections.TryLoopback("127.0.0.1:9097", out _) && !LocalConnections.TryLoopback("example.com:9097", out _) &&
                !LocalConnections.TryLoopback("127.0.0.1:9097/secret", out _), "local API secret cannot go to a remote controller");
            Check(LocalConnections.Scalar("external-controller: ''\nsecret: 'test'\nexternal-controller-pipe: \\\\.\\pipe\\verge-mihomo\n", "external-controller-pipe") == @"\\.\pipe\verge-mihomo", "existing named pipe parses without changing Clash");
            using var sampleJson = JsonDocument.Parse("""{"uploadTotal":100,"downloadTotal":200,"connections":[{"id":"one","upload":10,"download":20,"start":"2026-10-27T00:00:00Z","metadata":{"host":"api.openai.com","process":"codex.exe"},"chains":["US-DEMO-REALITY","PROXY"]}]}""");
            var parsedSample = LocalConnections.Parse(sampleJson.RootElement, DateTimeOffset.Now);
            Check(parsedSample.Connections.Single().Route == "代理" && parsedSample.Connections.Single().Node == "US-DEMO-REALITY", "read-only connection snapshot identifies actual proxy chain");
            var at = DateTimeOffset.Parse("2026-10-27T10:00:00+08:00");
            var ledger = new UsageLedger();
            var connection = new LocalConnection("one", 100, 200, "api.openai.com", "", "US-node", "代理", at.AddMinutes(-1));
            ledger.Apply(new(at, 1000, 2000, [connection]));
            Check(ledger.History.Buckets.Count == 0, "first snapshot never backfills past browsing");
            var next = new ConnectionSample(at.AddSeconds(1), 1300, 2800,
                [connection with { Upload = 140, Download = 500 }, new("two", 60, 200, "cdn.googlevideo.com", "", "HK-node", "代理", at.AddMilliseconds(500))]);
            ledger.Apply(next);
            var categories = ledger.Summary("今日", "全部连接", today: at.LocalDateTime);
            Check(categories.Sum(x => x.Total) == 1100 && categories.Single(x => x.Name == TrafficCategories.Unknown).Total == 500,
                "closed short-connection volume stays unknown and totals reconcile");
            Check(ledger.Summary("今日", "仅代理", today: at.LocalDateTime).Sum(x => x.Total) == 600 &&
                ledger.Summary("今日", "全部连接", "US-node", at.LocalDateTime).Sum(x => x.Total) == 340,
                "scope filters do not attribute unknown traffic to a node");
            ledger.Apply(next with { At = at.AddSeconds(2) });
            Check(ledger.Summary("累计", "全部连接").Sum(x => x.Total) == 1100, "repeated snapshots do not double count");
            ledger.Apply(new(at.AddSeconds(3), 10, 20, []));
            Check(ledger.History.Gaps == 1 && ledger.Summary("累计", "全部连接").Sum(x => x.Total) == 1100,
                "Clash restart preserves history and resets baseline");
            var noisy = new UsageLedger();
            noisy.Apply(new(at, 0, 0, []));
            noisy.Apply(new(at.AddSeconds(1), 10, 10, [new("three", 500, 1000, "x.com", "", "node", "代理", at.AddMilliseconds(1))]));
            Check(noisy.Summary("累计", "全部连接").Sum(x => x.Total) == 20, "racing per-connection counters cannot inflate global volume");
            Check(OrbWindow.NextVmId([vm, vm with { Id = "22222222-2222-4222-8222-222222222222" }], "22222222-2222-4222-8222-222222222222") == vm.Id &&
                OrbWindow.NextVmId([], vm.Id) == null, "double-click selection cycles and handles an empty account");
            string ledgerJson = JsonSerializer.Serialize(ledger.History);
            Check(!ledgerJson.Contains("api.openai.com") && !ledgerJson.Contains("cdn.googlevideo.com") && !ledgerJson.Contains("codex.exe"), "category history excludes browsing targets and process names");
            string folder = Path.Combine(Path.GetTempPath(), "TrafficOrb-Test-" + Guid.NewGuid().ToString("N"));
            var store = new LocalStore(folder);
            store.SaveToken("synthetic-test-token");
            Check(store.ReadToken() == "synthetic-test-token" && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "account.dpapi"))).Contains("synthetic-test-token"), "Windows account encryption roundtrip");
            Check(new LocalStore(folder).ReadToken() == "synthetic-test-token", "new app instance reuses encrypted credential without another login");
            File.WriteAllText(Path.Combine(folder, "account.dpapi"), "damaged encrypted file");
            Check(new LocalStore(folder).ReadToken() == "synthetic-test-token", "encrypted backup recovers a damaged primary credential");
            store.SaveSettings(new OrbSettings { SelectedVmId = Id, CachedUsage = [row], LastSuccess = DateTimeOffset.UtcNow });
            var settings = store.ReadSettings();
            Check(settings.CachedUsage.Count == 1 && settings.CachedUsage[0].RemainingGb == 147, "nonsecret quota cache roundtrip");
            foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
        catch (Exception e) { failure = e.Message; }
        File.WriteAllText(report, JsonSerializer.Serialize(new { ok = failure == null, passed, failure }, new JsonSerializerOptions { WriteIndented = true }));
        return failure == null ? 0 : 1;
    }
    private sealed class FakeHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "api.fuckip.me" || request.Headers.Authorization?.Scheme != "Bearer")
                throw new Exception("unexpected API request");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class RecoveringHandler(string body) : HttpMessageHandler
    {
        private int attempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != "unchanged-token") throw new Exception("credential changed during retry");
            if (++attempts == 1) throw new HttpRequestException("synthetic network outage");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
