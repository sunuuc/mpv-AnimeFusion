using System.Security.Cryptography;
using System.Text.Json;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitleStore
{
    readonly string _folder;
    readonly object _lock = new();
    public string Root => _folder;
    public string LoadError { get; private set; } = "";
    public SubtitleCredentials Credentials { get; private set; } = new("");

    public SubtitleStore(string folder)
    {
        _folder = folder;
        string path = Path.Combine(folder, "account.bin");
        if (!File.Exists(path)) return;
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                var credentials = JsonSerializer.Deserialize<SubtitleCredentials>(plain) ?? throw new JsonException();
                if (credentials.AssrtToken == null) throw new JsonException();
                Credentials = credentials;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { LoadError = "字幕服务凭据无法读取，请在来源设置中重新填写。"; }
    }

    public void Save(SubtitleCredentials credentials)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            lock (_lock)
            {
                Directory.CreateDirectory(_folder);
                string path = Path.Combine(_folder, "account.bin");
                File.WriteAllBytes(path + ".tmp", encrypted);
                File.Move(path + ".tmp", path, true);
                Credentials = credentials;
                LoadError = "";
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
