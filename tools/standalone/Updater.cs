// Component management adapted from the-database/mpv-AnimeJaNai.
// Source revision and changes: updater-upstream.json; upstream license: LICENSE.
// The manager delegates downloads, NVML detection and component ownership here.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Management;

string installDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
using var cancellation = new CancellationTokenSource();
if (Console.IsInputRedirected)
    _ = Task.Run(async () => { if (await Console.In.ReadLineAsync() == "cancel") cancellation.Cancel(); });
string mode = args.FirstOrDefault() ?? "--components";
try
{
    if (mode == "--verify")
    {
        var sums = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(installDir, "app", "build-info", "standalone", "SHA256.json")))!;
        var failed = sums.Where(p => !p.Key.EndsWith(".conf") && !p.Key.Contains("interface-language") &&
            (!File.Exists(Inside(installDir, p.Key)) || Hash(Inside(installDir, p.Key)) != p.Value)).Select(p => p.Key).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = failed.Length == 0, failed }));
        return failed.Length == 0 ? 0 : 1;
    }
    if (mode is not ("--components" or "--install" or "--remove"))
    {
        Console.Error.WriteLine("Unsupported command: " + mode);
        return 2;
    }
    var index = JsonSerializer.Deserialize<PackIndex>(File.ReadAllText(Path.Combine(installDir,
        "app", "build-info", "standalone", "components.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Missing component catalog.");
    ValidateIndex(index);
    var gpu = DetectGpu();
    var recommended = RecommendedPacks(index, gpu.Nvidia, gpu.Sm);
    if (mode == "--components")
    {
        Console.WriteLine(JsonSerializer.Serialize(new {
            package_version = index.package_version,
            gpu = new { name = gpu.Name, nvidia = gpu.Nvidia, sm = gpu.Sm }, offline = true,
            packs = index.packs.Select(p => new { name = p.name, bytes = p.bytes, title = p.title,
                description = p.description, installed = PackContentMatches(p),
                recommended = recommended.Contains(p.name), requires = p.requires })
        }));
        return 0;
    }
    var pack = index.packs.SingleOrDefault(p => p.name == args.ElementAtOrDefault(1))
        ?? throw new InvalidDataException("Unknown component.");
    // One writer protects both download staging and uninstall. Inspection never acquires it.
    using var lease = new FileStream(Path.Combine(installDir, "app", ".components.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    if (mode == "--install")
    {
        var resolved = new HashSet<string>();
        await InstallWithDependencies(pack);
        return 0;
        async Task InstallWithDependencies(Pack p)
        {
            if (!resolved.Add(p.name)) return;
            foreach (var dep in p.requires) await InstallWithDependencies(index.packs.Single(x => x.name == dep));
            await InstallComponentAsync(p, cancellation.Token);
        }
    }
    var dependent = index.packs.FirstOrDefault(p => p.requires.Contains(pack.name) && PackContentMatches(p));
    if (dependent != null) throw new InvalidOperationException($"Remove {dependent.name} before {pack.name}.");
    RemoveComponent(pack, index);
    return 0;
}
catch (OperationCanceledException) { Console.WriteLine("Download cancelled."); return 3; }
catch (Exception e) { Console.Error.WriteLine("Component operation failed: " + e.Message); return 1; }

bool PackContentMatches(Pack pack)
{
    var payload = pack.files.Where(f => !IsLicenseFile(f)).ToArray();
    return payload.Length > 0 && payload.All(f => File.Exists(Inside(installDir, f))) &&
        payload.Sum(f => new FileInfo(Inside(installDir, f)).Length) == pack.installed_bytes;
}

static bool IsLicenseFile(string path) => Path.GetFileName(path).Contains("LICENSE", StringComparison.OrdinalIgnoreCase);

static string Hash(string path)
{
    using var f = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
}

// Archive files may only affect the component's own manifest. Reject traversal and links.
static string Inside(string root, string relative)
{
    if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\0') ||
        Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(p => p is ".." or "."))
        throw new InvalidDataException("Unsafe component path: " + relative);
    var baseDir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(Path.Combine(baseDir, relative));
    if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path outside installation.");
    for (var p = Path.GetDirectoryName(full); p != null && p.Length >= baseDir.Length; p = Path.GetDirectoryName(p))
        if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Component path contains a link.");
    if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Component file is a link.");
    return full;
}

void ValidateIndex(PackIndex index)
{
    if (index.packs.Count == 0 || index.packs.Select(p => p.name).Distinct().Count() != index.packs.Count)
        throw new InvalidDataException("Invalid component catalog.");
    foreach (var p in index.packs)
    {
        if (!Regex.IsMatch(p.name, "^[a-z0-9-]+$") || p.bytes <= 0 || p.installed_bytes <= 0 ||
            !Regex.IsMatch(p.sha256, "^[a-f0-9]{64}$") || !p.files.Any(f => !IsLicenseFile(f)) ||
            p.files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.files.Count)
            throw new InvalidDataException("Invalid component: " + p.name);
        if (!Uri.TryCreate(p.url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/the-database/mpv-AnimeJaNai/releases/download/3.6.0/", StringComparison.Ordinal) &&
            !uri.AbsolutePath.StartsWith("/sunuuc/mpv-AnimeFusion/releases/latest/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Untrusted component source.");
        foreach (var f in p.files)
        {
            Inside(installDir, f);
            if (!f.Replace('\\', '/').StartsWith("animejanai/", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected component location.");
        }
        if (p.requires.Any(d => d == p.name || !index.packs.Any(x => x.name == d)))
            throw new InvalidDataException("Invalid component dependency.");
    }
    foreach (var p in index.packs) Visit(p, new HashSet<string>());
    void Visit(Pack p, HashSet<string> path)
    {
        if (!path.Add(p.name)) throw new InvalidDataException("Component dependency cycle.");
        foreach (var dep in p.requires) Visit(index.packs.Single(x => x.name == dep), new HashSet<string>(path));
    }
}

async Task InstallComponentAsync(Pack pack, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    if (PackContentMatches(pack)) { Console.WriteLine(pack.name + " already installed."); return; }
    var work = Path.Combine(installDir, "app", ".component-downloads", pack.name);
    Directory.CreateDirectory(work);
    var archive = Path.Combine(work, "package.7z");
    try
    {
        if (!File.Exists(archive) || new FileInfo(archive).Length != pack.bytes || Hash(archive) != pack.sha256)
            await DownloadFileAsync(pack, archive, token);
        if (new FileInfo(archive).Length != pack.bytes || Hash(archive) != pack.sha256)
            throw new InvalidDataException("SHA-256 verification failed: " + pack.name);
        token.ThrowIfCancellationRequested();
        var listing = await SevenZip(["l", "-slt", "-sccUTF-8", archive]);
        var entries = listing.Split("----------", 2).Last();
        if (entries.Contains("Symbolic Link =") || entries.Contains("Hard Link =") ||
            Regex.IsMatch(entries, @"^Attributes = .*L", RegexOptions.Multiline))
            throw new InvalidDataException("Links in component archive.");
        foreach (Match m in Regex.Matches(entries, @"^Path = (.+)$", RegexOptions.Multiline)) Inside(work, m.Groups[1].Value.TrimEnd('\r'));
        var extracted = Path.Combine(work, "extracted");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
        await SevenZip(["x", "-y", "-bd", "-o" + extracted, archive]);
        var actual = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(extracted, f).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(pack.files) || actual.Where(f => !IsLicenseFile(f))
                .Sum(f => new FileInfo(Inside(extracted, f)).Length) != pack.installed_bytes)
            throw new InvalidDataException("Extracted content does not match component catalog.");
        token.ThrowIfCancellationRequested();
        Console.WriteLine("Installing " + pack.name + "...");
        // Stage fully before writing; restore existing files on any failed commit.
        var committed = new List<(string Target, string Backup, bool Existed)>();
        try
        {
            foreach (var f in pack.files)
            {
                var target = Inside(installDir, f); var backup = Inside(Path.Combine(work, "backup"), f);
                var existed = File.Exists(target);
                // The core owns shared license notices. Component installation must not replace them.
                if (IsLicenseFile(f) && existed) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup, true); }
                committed.Add((target, backup, existed));
                File.Copy(Inside(extracted, f), target, true);
            }
            if (!PackContentMatches(pack)) throw new IOException("Installed component verification failed.");
        }
        catch
        {
            foreach (var f in committed.AsEnumerable().Reverse())
                if (f.Existed) File.Copy(f.Backup, f.Target, true); else File.Delete(f.Target);
            throw;
        }
        Console.WriteLine(pack.name + " installed.");
    }
    finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
}

// Streamed download with progress, following the upstream Downloader abstraction.
static async Task DownloadFileAsync(Pack pack, string destination, CancellationToken token)
{
    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("mpv-AnimeFusionUpdater");
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromMinutes(30));
    using var response = await client.GetAsync(pack.url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    response.EnsureSuccessStatusCode();
    await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
    await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
    var buffer = new byte[131072]; long received = 0; int last = -1, read;
    while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
    {
        received += read;
        if (received > pack.bytes) throw new InvalidDataException("Download exceeds catalog size.");
        await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        int progress = (int)(received * 100 / pack.bytes);
        if (progress != last) { Console.WriteLine($"Downloading {pack.name}: {progress}% ({received / 1048576} / {pack.bytes / 1048576} MB)"); last = progress; }
    }
}

async Task<string> SevenZip(string[] arguments)
{
    var psi = new ProcessStartInfo { FileName = Path.Combine(installDir, "app", "7za.exe"), WorkingDirectory = installDir,
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
    foreach (var arg in arguments) psi.ArgumentList.Add(arg);
    using var process = Process.Start(psi) ?? throw new IOException("Cannot start archive tool.");
    var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var result = await output; var error = await errors;
    if (process.ExitCode != 0) throw new IOException("Archive extraction failed: " + error);
    return result;
}

void RemoveComponent(Pack pack, PackIndex index)
{
    foreach (var f in pack.files)
    {
        // Shared notices and files owned by another installed component must survive.
        if (IsLicenseFile(f)) continue;
        if (index.packs.Any(p => p.name != pack.name && p.files.Contains(f) && PackContentMatches(p))) continue;
        File.Delete(Inside(installDir, f));
    }
    Console.WriteLine(pack.name + " removed. Custom models and configurations are preserved.");
}

static List<string> RecommendedPacks(PackIndex index, bool nvidia, string sm)
{
    var rec = index.packs.Where(p => p.recommended).Select(p => p.name).ToList();
    if (nvidia)
    {
        rec.Add("trt-runtime");
        rec.Add(index.packs.Any(p => p.name == "trt-" + sm) ? "trt-" + sm : "trt-ptx");
    }
    return rec;
}

static (bool Nvidia, string Sm, string Name) DetectGpu()
{
    // NVML reports the actual compute capability, rather than guessing from GPU names.
    try
    {
        if (Nvml.nvmlInit_v2() == 0)
        {
            try
            {
                if (Nvml.nvmlDeviceGetHandleByIndex_v2(0, out var dev) == 0 &&
                    Nvml.nvmlDeviceGetCudaComputeCapability(dev, out int major, out int minor) == 0)
                {
                    var name = new byte[96]; Nvml.nvmlDeviceGetName(dev, name, (uint)name.Length);
                    return (true, $"sm{major}{minor}", Encoding.ASCII.GetString(name).TrimEnd('\0'));
                }
            }
            finally { Nvml.nvmlShutdown(); }
        }
    }
    catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    using var search = new ManagementObjectSearcher("SELECT Name,PNPDeviceID FROM Win32_VideoController");
    using var devices = search.Get();
    var names = new List<string>(); bool nvidia = false;
    foreach (var item in devices)
    {
        using (item) { names.Add(item["Name"]?.ToString() ?? ""); nvidia |= (item["PNPDeviceID"]?.ToString() ?? "").Contains("VEN_10DE"); }
    }
    return (nvidia, "", string.Join(" / ", names));
}

static class Nvml
{
    [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] public static extern int nvmlShutdown();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
}
record PackIndex(string package_version, List<Pack> packs);
// installed_bytes counts binary/model payload only; shared license notices belong to the core.
record Pack(string name, string asset, string url, string sha256, long bytes, long installed_bytes, List<string> files,
    List<string> requires, bool recommended, string? title = null, string? description = null);
