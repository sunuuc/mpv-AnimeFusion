using System.Text.Json;

namespace MpvNet.Windows.Sync;

public sealed class SyncPreferences
{
    public static SyncPreferences Current { get; private set; } = null!;
    readonly object _lock = new();
    readonly string _path;
    Dictionary<string, bool> _enabled = new() { ["bangumi"] = true, ["trakt"] = false, ["mal"] = false };
    public event Action? Changed;

    SyncPreferences(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _enabled = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path))
                    ?? throw new JsonException("同步设置无法读取。");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { _enabled = new() { ["bangumi"] = true, ["trakt"] = false, ["mal"] = false }; }
    }

    public static void Initialize()
    {
        Current = new(Path.Combine(Player.ConfigFolder, "sync.json"));
        Current.Changed += Current.Publish;
        Current.Publish();
    }

    public bool Enabled(string provider) { lock (_lock) return _enabled.GetValueOrDefault(provider); }
    public void SetEnabled(string provider, bool enabled)
    {
        if (provider is not ("bangumi" or "trakt" or "mal")) throw new ArgumentException("未知同步服务。");
        lock (_lock)
        {
            _enabled[provider] = enabled;
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_enabled));
            File.Move(_path + ".tmp", _path, true);
        }
        Changed?.Invoke();
    }
    void Publish()
    {
        lock (_lock) Player.SetPropertyString("user-data/player_ui/sync", JsonSerializer.Serialize(_enabled));
    }
}
