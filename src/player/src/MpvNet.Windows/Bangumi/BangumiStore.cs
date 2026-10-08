using System.Security.Cryptography;
using System.Text.Json;

namespace MpvNet.Windows.Bangumi;

public sealed record BangumiAccount(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Username)
{
    public string AvatarUrl { get; init; } = "";
}
public sealed record BangumiBinding(int SubjectId, string Title);
public sealed class BangumiSettings
{
    public bool AutoCollect { get; set; } = true;
    public int CollectPercent { get; set; } = 10;
    public bool AutoSync { get; set; } = true;
    public int WatchedPercent { get; set; } = 90;
    public Dictionary<string, BangumiBinding> Bindings { get; set; } = new();
}

public sealed class BangumiStore
{
    readonly string _configFolder;
    readonly string _accountFolder;
    readonly object _lock = new();
    public BangumiSettings Settings { get; }
    public string LoadError { get; } = "";
    string AccountPath => Path.Combine(_accountFolder, "account.bin");

    public BangumiStore(string configFolder, string accountFolder)
    {
        _configFolder = configFolder;
        _accountFolder = accountFolder;
        string settings = Path.Combine(configFolder, "bangumi.json");
        Settings = new();
        try
        {
            if (File.Exists(settings))
                Settings = JsonSerializer.Deserialize<BangumiSettings>(File.ReadAllText(settings))
                    ?? throw new JsonException();
            if (Settings.WatchedPercent is < 1 or > 100 || Settings.CollectPercent is < 1 or > 100 || Settings.Bindings == null) throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Settings = new();
            LoadError = "Bangumi 设置无法读取。";
        }
    }

    public BangumiAccount? ReadAccount()
    {
        if (!File.Exists(AccountPath)) return null;
        byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(AccountPath), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<BangumiAccount>(data); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public void SaveAccount(BangumiAccount account)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(account);
        try
        {
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            lock (_lock)
            {
                Directory.CreateDirectory(_accountFolder);
                File.WriteAllBytes(AccountPath + ".tmp", encrypted);
                File.Move(AccountPath + ".tmp", AccountPath, true);
            }
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    public void RemoveAccount()
    {
        lock (_lock) File.Delete(AccountPath);
    }

    public BangumiBinding? FindBinding(string key)
    {
        lock (_lock) return Settings.Bindings.GetValueOrDefault(key);
    }

    public void Bind(string key, BangumiBinding binding)
    {
        lock (_lock)
        {
            Settings.Bindings[key] = binding;
            WriteJson(Path.Combine(_configFolder, "bangumi.json"), Settings);
        }
    }

    public void SetAutoSync(bool enabled)
    {
        lock (_lock)
        {
            Settings.AutoSync = enabled;
            WriteJson(Path.Combine(_configFolder, "bangumi.json"), Settings);
        }
    }

    public void SetAutoCollect(bool enabled)
    {
        lock (_lock) { Settings.AutoCollect = enabled; WriteJson(Path.Combine(_configFolder, "bangumi.json"), Settings); }
    }

    public void SetCollectPercent(int percent)
    {
        if (percent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        lock (_lock) { Settings.CollectPercent = percent; WriteJson(Path.Combine(_configFolder, "bangumi.json"), Settings); }
    }

    public void SetWatchedPercent(int percent)
    {
        if (percent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        lock (_lock)
        {
            Settings.WatchedPercent = percent;
            WriteJson(Path.Combine(_configFolder, "bangumi.json"), Settings);
        }
    }

    public string AvatarPath(string url) => Path.Combine(_accountFolder,
        "avatar-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".bgra");

    public string CoverPath(string url) => Path.Combine(_accountFolder,
        "cover-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".bgra");

    static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
