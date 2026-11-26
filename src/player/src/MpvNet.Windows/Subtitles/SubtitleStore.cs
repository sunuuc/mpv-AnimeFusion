using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitleStore
{
    readonly string _folder;
    readonly string _mutexName;
    public string Root => _folder;
    public string LoadError { get; private set; } = "";
    public SubtitleCredentials Credentials { get; private set; } = new("");

    public SubtitleStore(string folder)
    {
        _folder = folder;
        _mutexName = @"Local\mpv-AnimeFusion-SubtitleStore-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
        Read();
    }

    void Read()
    {
        string path = Path.Combine(_folder, "account.bin");
        if (!File.Exists(path)) return;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            byte[] encrypted = new byte[checked((int)input.Length)];
            input.ReadExactly(encrypted);
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var credentials = JsonSerializer.Deserialize<SubtitleCredentials>(plain) ?? throw new JsonException();
                if (credentials.AssrtToken == null) throw new JsonException();
                Credentials = credentials;
                LoadError = "";
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { LoadError = "字幕服务凭据无法读取，请在来源设置中重新填写。"; }
    }

    public void SetAmenzbSession(AmenzbSession? session)
    {
        if (session != null && !session.IsValid) throw new SubtitleServiceException("ameNZB 登录已失效，请重新登录。");
        Update(current => current with { AmenzbSession = session });
    }

    internal void RejectAmenzbSession(AmenzbSession session) =>
        Update(current => current.AmenzbSession == session ? current with { AmenzbSession = null } : current);

    public void Update(Func<SubtitleCredentials, SubtitleCredentials> change)
    {
        // Other player instances can sign in while an old request is reporting an expired session.
        using var mutex = new Mutex(false, _mutexName);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            Read();
            var updated = change(Credentials);
            if (updated != Credentials) Write(updated);
        }
        finally { mutex.ReleaseMutex(); }
    }

    public void Save(SubtitleCredentials credentials) => Update(_ => credentials);

    void Write(SubtitleCredentials credentials)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, "account.bin");
            File.WriteAllBytes(path + ".tmp", encrypted);
            File.Move(path + ".tmp", path, true);
            Credentials = credentials;
            LoadError = "";
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
