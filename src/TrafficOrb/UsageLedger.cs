using System.IO;
using System.Text.Json;

namespace TrafficOrb;

public sealed class UsageBucket
{
    public string Date { get; set; } = "";
    public string Category { get; set; } = "";
    public string Node { get; set; } = "";
    public string Route { get; set; } = "";
    public long Upload { get; set; }
    public long Download { get; set; }
}
public sealed class UsageHistory
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastSample { get; set; }
    public int Sessions { get; set; }
    public int Gaps { get; set; }
    public List<UsageBucket> Buckets { get; set; } = [];
}
public sealed record CategoryUsage(string Name, long Upload, long Download)
{
    public decimal Total => (decimal)Upload + Download;
    public string UpText => TrafficFormat.Bytes(Upload);
    public string DownText => TrafficFormat.Bytes(Download);
    public string TotalText => TrafficFormat.Bytes(Total);
    public string PercentText { get; init; } = "—";
}
public static class TrafficFormat
{
    public static string Bytes(decimal bytes) => bytes switch
    {
        >= 1099511627776 => $"{bytes / 1099511627776:0.00} TB",
        >= 1073741824 => $"{bytes / 1073741824:0.00} GB",
        >= 1048576 => $"{bytes / 1048576:0.00} MB",
        >= 1024 => $"{bytes / 1024:0.0} KB",
        _ => $"{bytes:0} B"
    };
}

// Stores aggregate categories only, not domains, URLs, IPs, process paths or individual connections.
public sealed class UsageLedger
{
    public UsageHistory History { get; }
    private ConnectionSample? previous;
    public UsageLedger(UsageHistory? history = null)
    {
        History = history ?? new UsageHistory();
        History.Sessions++;
    }
    public void Apply(ConnectionSample sample)
    {
        var old = previous;
        previous = sample;
        History.LastSample = sample.At;
        if (old == null) return; // Never count traffic that happened before the app started.
        if (sample.UploadTotal < old.UploadTotal || sample.DownloadTotal < old.DownloadTotal)
        {
            History.Gaps++; return; // Core restarted: establish a fresh baseline.
        }
        if (sample.At - old.At > TimeSpan.FromSeconds(10)) History.Gaps++;
        long upBudget = sample.UploadTotal - old.UploadTotal, downBudget = sample.DownloadTotal - old.DownloadTotal;
        var oldConnections = old.Connections.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.Last());
        var observed = new List<(LocalConnection Connection, long Up, long Down)>();
        foreach (var connection in sample.Connections)
        {
            long up = 0, down = 0;
            if (oldConnections.TryGetValue(connection.Id, out var baseline) && baseline.Start == connection.Start)
            {
                up = Math.Max(0, connection.Upload - baseline.Upload);
                down = Math.Max(0, connection.Download - baseline.Download);
            }
            else if (connection.Start >= old.At && connection.Start <= sample.At)
            {
                up = connection.Upload; down = connection.Download;
            }
            if (up != 0 || down != 0) observed.Add((connection, up, down));
        }
        decimal seenUp = observed.Sum(x => (decimal)x.Up), seenDown = observed.Sum(x => (decimal)x.Down);
        decimal upFactor = seenUp > upBudget ? upBudget / seenUp : 1;
        decimal downFactor = seenDown > downBudget ? downBudget / seenDown : 1;
        long allocatedUp = 0, allocatedDown = 0;
        foreach (var entry in observed)
        {
            long up = (long)(entry.Up * upFactor), down = (long)(entry.Down * downFactor);
            Add(sample.At, TrafficCategories.Classify(entry.Connection.Host, entry.Connection.Process),
                entry.Connection.Node, entry.Connection.Route, up, down);
            allocatedUp += up; allocatedDown += down;
        }
        // Short-lived closed connections are absent from snapshots. Preserve their volume without guessing a type or route.
        Add(sample.At, TrafficCategories.Unknown, "未识别节点", "未知", upBudget - allocatedUp, downBudget - allocatedDown);
    }
    private void Add(DateTimeOffset at, string category, string node, string route, long up, long down)
    {
        if (up <= 0 && down <= 0) return;
        string date = at.LocalDateTime.ToString("yyyy-MM-dd");
        var bucket = History.Buckets.FirstOrDefault(x => x.Date == date && x.Category == category && x.Node == node && x.Route == route);
        if (bucket == null)
        {
            bucket = new UsageBucket { Date = date, Category = category, Node = node, Route = route };
            History.Buckets.Add(bucket);
        }
        bucket.Upload = checked(bucket.Upload + Math.Max(0, up));
        bucket.Download = checked(bucket.Download + Math.Max(0, down));
    }
    public List<CategoryUsage> Summary(string period, string scope, string? node = null, DateTime? today = null)
    {
        var date = today ?? DateTime.Now;
        string day = date.ToString("yyyy-MM-dd"), month = date.ToString("yyyy-MM");
        var rows = History.Buckets.Where(x => period == "今日" ? x.Date == day : period == "本月" ? x.Date.StartsWith(month, StringComparison.Ordinal) : true)
            .Where(x => scope == "仅代理" ? x.Route == "代理" : scope == "仅直连" ? x.Route == "直连" : true)
            .Where(x => node == null || x.Node == node).ToList();
        var categories = TrafficCategories.All.Select(category => new CategoryUsage(category,
            rows.Where(x => x.Category == category).Sum(x => x.Upload), rows.Where(x => x.Category == category).Sum(x => x.Download))).ToList();
        decimal total = categories.Sum(x => x.Total);
        return categories.OrderByDescending(x => x.Total).Select(x => x with { PercentText = total > 0 ? $"{x.Total / total * 100:0.0}%" : "—" }).ToList();
    }
}

public sealed class TrafficRecorder : IDisposable
{
    private readonly LocalConnections source = new();
    private readonly string file;
    private bool busy;
    private DateTimeOffset lastSave;
    public UsageLedger Ledger { get; }
    public string Status { get; private set; } = "正在连接本机 Clash…";
    public bool Connected { get; private set; }
    public event Action? Changed;

    public TrafficRecorder(string? folder = null)
    {
        file = Path.Combine(folder ?? LocalStore.DataDirectory, "traffic-history.json");
        UsageHistory? history = null;
        try { history = JsonSerializer.Deserialize<UsageHistory>(File.ReadAllText(file)); } catch { }
        Ledger = new UsageLedger(history);
    }
    public async Task PollAsync(CancellationToken cancel)
    {
        if (busy) return;
        busy = true;
        try
        {
            var sample = await source.ReadAsync(cancel);
            Ledger.Apply(sample);
            Connected = true;
            Status = $"本机 Clash 已连接 · {sample.At.LocalDateTime:HH:mm:ss}";
            if (DateTimeOffset.Now - lastSave > TimeSpan.FromSeconds(15)) Save();
        }
        catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) { Connected = false; Status = "Clash 读取超时，自动重试中"; } }
        catch { Connected = false; Status = "未连到本机 Clash · 已保留历史记录，自动重试中"; }
        finally { busy = false; if (!cancel.IsCancellationRequested) Changed?.Invoke(); }
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Ledger.History));
            File.Move(temp, file, true); lastSave = DateTimeOffset.Now;
        }
        catch { Status = "本机流量正在记录，但历史文件暂未保存"; }
    }
    public void Dispose() { Save(); source.Dispose(); }
}
