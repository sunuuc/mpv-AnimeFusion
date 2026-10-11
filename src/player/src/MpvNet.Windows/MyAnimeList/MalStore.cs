using System.Security.Cryptography;
using System.Text.Json;

namespace MpvNet.Windows.MyAnimeList;

public sealed record MalAccount(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Username)
{
    public string AvatarUrl { get; init; } = "";
}
public sealed record MalBinding(int Id, int Season, string Title, int EpisodeOffset);
public sealed class MalSettings
{
    public bool AutoSync { get; set; } = true;
    public int WatchedPercent { get; set; } = 90;
    public Dictionary<string, MalBinding> Bindings { get; set; } = new();
}
public sealed class MalStore
{
    readonly object _lock = new();
    readonly string _settingsPath;
    readonly string _folder;
    public MalSettings Settings { get; }
    public string LoadError { get; } = "";
    string AccountPath => Path.Combine(_folder, "account.bin");
    public MalStore(string configFolder, string accountFolder)
    {
        _settingsPath = Path.Combine(configFolder, "mal.json");
        _folder = accountFolder;
        Settings = new();
        try
        {
            if (File.Exists(_settingsPath)) Settings = JsonSerializer.Deserialize<MalSettings>(File.ReadAllText(_settingsPath)) ?? throw new JsonException();
            if (Settings.WatchedPercent is < 1 or > 100 || Settings.Bindings == null) throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { Settings = new(); LoadError = "MyAnimeList 设置无法读取。"; }
    }
    public MalAccount? ReadAccount()
    {
        lock (_lock)
        {
            if (!File.Exists(AccountPath)) return null;
            byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(AccountPath), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<MalAccount>(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public void SaveAccount(MalAccount account)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(account);
        try
        {
            byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            lock (_lock)
            {
                Directory.CreateDirectory(_folder);
                File.WriteAllBytes(AccountPath + ".tmp", encrypted);
                File.Move(AccountPath + ".tmp", AccountPath, true);
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void RemoveAccount() { lock (_lock) File.Delete(AccountPath); }
    public MalBinding? FindBinding(string key) { lock (_lock) return Settings.Bindings.GetValueOrDefault(key); }
    public void Bind(string key, MalBinding binding) { lock (_lock) { Settings.Bindings[key] = binding; Save(); } }
    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            switch (key)
            {
                case "auto-sync": Settings.AutoSync = value == "yes"; break;
                case "watched-percent":
                    int percent = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (percent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
                    Settings.WatchedPercent = percent; break;
                default: throw new ArgumentException("未知同步设置。");
            }
            Save();
        }
    }
    void Save()
    {
        File.WriteAllText(_settingsPath + ".tmp", JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_settingsPath + ".tmp", _settingsPath, true);
    }
    public string ImagePath(string url, bool avatar) => Path.Combine(_folder, (avatar ? "avatar-" : "cover-")
        + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".bgra");
}
