using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrafficOrb;

public sealed class OrbSettings
{
    public string? SelectedVmId { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public List<VmUsage> CachedUsage { get; set; } = [];
    public bool? StartWithWindows { get; set; }
}

public sealed class LocalStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrafficOrb");
    private readonly string folder;
    public LocalStore(string? directory = null) { folder = directory ?? DataDirectory; Directory.CreateDirectory(folder); }
    public OrbSettings ReadSettings()
    {
        try { return JsonSerializer.Deserialize<OrbSettings>(File.ReadAllText(Path.Combine(folder, "settings.json"))) ?? new(); }
        catch { return new(); }
    }
    public void SaveSettings(OrbSettings settings) => AtomicWrite("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }));
    public void SaveToken(string token)
    {
        byte[] plain = Encoding.UTF8.GetBytes(token);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            AtomicWrite("account.dpapi", protectedBytes);
            try { AtomicWrite("account.backup.dpapi", protectedBytes); } catch (IOException) { }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public string? ReadToken()
    {
        foreach (string name in new[] { "account.dpapi", "account.backup.dpapi" })
        {
            byte[]? plain = null;
            try
            {
                plain = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder, name)), null, DataProtectionScope.CurrentUser);
                string token = Encoding.UTF8.GetString(plain);
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
            finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
        }
        return null;
    }
    public void EnsureCredentialBackup()
    {
        string primary = Path.Combine(folder, "account.dpapi"), backup = Path.Combine(folder, "account.backup.dpapi");
        if (!File.Exists(backup) && File.Exists(primary)) AtomicWrite("account.backup.dpapi", File.ReadAllBytes(primary));
    }
    private void AtomicWrite(string filename, byte[] content)
    {
        string file = Path.Combine(folder, filename), temp = file + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, file, true);
    }
}
