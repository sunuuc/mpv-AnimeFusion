// Component management adapted from the-database/mpv-AnimeJaNai.
// Source revision and changes: updater-upstream.json; upstream license: LICENSE.
// The manager delegates downloads, NVML detection and component ownership here.
using AnimeJaNai.Components;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using var installMutex = new Mutex(false, "mpv-AnimeFusion_Components");
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
    if (mode == "--components")
    {
        var gpus = ListNvidiaGpus();
        var gpu = GpuSupport.Summary(gpus);
        var recommended = GpuSupport.RecommendedPacks(index.packs.Select(p => p.name), gpus);
        Console.WriteLine(JsonSerializer.Serialize(new {
            package_version = index.package_version,
            gpu = new { name = gpu.GpuName, nvidia = gpu.HasNvidia, sm = gpu.Sm }, offline = true,
            gpus = GpuSupport.Capable(gpus).Where(g => g.Uuid.Length > 0).Select(g => new { name = g.Name, uuid = g.Uuid }),
            packs = index.packs.Select(p => new { name = p.name, bytes = p.bytes, title = p.title,
                description = p.description, installed = PackContentMatches(p),
                recommended = p.recommended || recommended.Contains(p.name), requires = p.requires })
        }));
        return 0;
    }
    Pack FindPack(string? name) => index.packs.SingleOrDefault(p => p.name == name)
        ?? throw new InvalidDataException("Unknown component.");
    // One writer protects both download staging and uninstall. Inspection never acquires it.
    using var lease = new FileStream(Path.Combine(installDir, "app", ".components.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    if (mode == "--install")
    {
        var resolved = new HashSet<string>();
        await InstallWithDependencies(FindPack(args.ElementAtOrDefault(1)));
        return 0;
        async Task InstallWithDependencies(Pack p)
        {
            if (!resolved.Add(p.name)) return;
            foreach (var dep in p.requires) await InstallWithDependencies(index.packs.Single(x => x.name == dep));
            await InstallComponentAsync(p, cancellation.Token);
        }
    }
    if (args.Length < 2) throw new InvalidDataException("Unknown component.");
    RemoveComponents(args.Skip(1).Select(FindPack).Distinct().ToArray(), index);
    return 0;
}
catch (OperationCanceledException) { Console.WriteLine("Download cancelled."); return 3; }
catch (Exception e) { Console.Error.WriteLine("Component operation failed: " + e.Message); return 1; }

bool PackContentMatches(Pack pack)
{
    var payload = pack.files.Where(f => !IsLicenseFile(f)).ToArray();
    return payload.All(f => File.Exists(Inside(installDir, f))) &&
        payload.Sum(f => new FileInfo(Inside(installDir, f)).Length) == pack.installed_bytes &&
        payload.All(f => Hash(Inside(installDir, f)) == pack.file_sha256[f]);
}

static bool IsStandaloneModel(Pack pack) => pack.asset.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);

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
        if (IsStandaloneModel(p))
        {
            const string modelSource = "https://raw.githubusercontent.com/the-database/mpv-AnimeJaNai/6b700f2954e80045e1d890a57a9cbab1327c49e2/BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/animejanai/onnx/";
            if (p.asset != Path.GetFileName(p.asset) || p.url != modelSource + p.asset ||
                p.files.Count != 1 || p.files[0] != "animejanai/onnx/" + p.asset ||
                p.bytes != p.installed_bytes || p.archive_root != null)
                throw new InvalidDataException("Invalid standalone model source or destination.");
        }
        else if (!Uri.TryCreate(p.url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/the-database/mpv-AnimeJaNai/releases/download/3.12.5/", StringComparison.Ordinal) &&
            !uri.AbsolutePath.StartsWith("/the-database/animejanai-inference/releases/download/runtime-2/", StringComparison.Ordinal) &&
            !uri.AbsolutePath.StartsWith("/sunuuc/mpv-AnimeFusion/releases/download/standalone-v1.3.4/", StringComparison.Ordinal))
            throw new InvalidDataException("Untrusted component source.");
        if (p.file_sha256 == null || p.file_sha256.Count != p.files.Count ||
            p.files.Any(f => !p.file_sha256.TryGetValue(f, out var hash) || !Regex.IsMatch(hash, "^[a-f0-9]{64}$")) ||
            p.archive_root != null && (p.archive_root != "animejanai/inference" ||
                p.files.Any(f => !f.StartsWith(p.archive_root + "/", StringComparison.Ordinal))))
            throw new InvalidDataException("Invalid component file hashes or archive location.");
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
        var extracted = Path.Combine(work, "extracted");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
        if (IsStandaloneModel(pack))
        {
            var model = Inside(extracted, pack.files.Single());
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            File.Copy(archive, model);
        }
        else
        {
            var listing = await SevenZip(["l", "-slt", "-sccUTF-8", archive]);
            var entries = listing.Split("----------", 2).Last();
            if (entries.Contains("Symbolic Link =") || entries.Contains("Hard Link =") ||
                Regex.IsMatch(entries, @"^Attributes = .*L", RegexOptions.Multiline))
                throw new InvalidDataException("Links in component archive.");
            foreach (Match m in Regex.Matches(entries, @"^Path = (.+)$", RegexOptions.Multiline)) Inside(work, m.Groups[1].Value.TrimEnd('\r'));
            await SevenZip(["x", "-y", "-bd", "-o" + (pack.archive_root == null ? extracted : Inside(extracted, pack.archive_root)), archive]);
        }
        var actual = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(extracted, f).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(pack.files) || actual.Any(f => Hash(Inside(extracted, f)) != pack.file_sha256[f]) || actual.Where(f => !IsLicenseFile(f))
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
        if (progress != last)
        {
            Console.WriteLine($"Downloading {pack.name}: {progress}% ({ComponentSize.FormatProgress(received, pack.bytes)})");
            last = progress;
        }
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

void RemoveComponents(Pack[] packs, PackIndex index)
{
    var names = packs.Select(p => p.name).ToHashSet();
    var files = packs.SelectMany(p => p.files).Where(f => !IsLicenseFile(f))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var remaining in index.packs.Where(p => !names.Contains(p.name)))
    {
        var required = remaining.requires.FirstOrDefault(names.Contains);
        if (required == null && !files.Overlaps(remaining.files)) continue;
        if (!PackContentMatches(remaining)) continue;
        if (required != null)
            throw new InvalidOperationException($"Remove {remaining.name} before {required}.");
        files.ExceptWith(remaining.files);
    }

    // Renames on the installation volume preserve the original bytes and ACLs.
    // Only discard them once every selected pack has been staged successfully.
    var staging = Inside(installDir, "app/.component-removal-" + Guid.NewGuid().ToString("N"));
    var moved = new List<(string Original, string Staged)>();
    Directory.CreateDirectory(staging);
    try
    {
        foreach (var file in files)
        {
            var original = Inside(installDir, file);
            if (!File.Exists(original)) continue;
            if ((File.GetAttributes(original) & FileAttributes.ReadOnly) != 0)
                throw new IOException("Component file is read-only: " + file);
            var staged = Inside(staging, file);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.Move(original, staged);
            moved.Add((original, staged));
        }
    }
    catch
    {
        foreach (var file in moved.AsEnumerable().Reverse())
            File.Move(file.Staged, file.Original);
        Directory.Delete(staging, true);
        throw;
    }
    Directory.Delete(staging, true);
    foreach (var pack in packs) Console.WriteLine(pack.name + " removed.");
}

static List<NvidiaGpu> ListNvidiaGpus()
{
    var gpus = new List<NvidiaGpu>();
    try
    {
        if (Nvml.nvmlInit_v2() != 0)
        {
            return gpus;
        }
        try
        {
            if (Nvml.nvmlDeviceGetCount_v2(out uint count) != 0)
            {
                return gpus;
            }
            for (uint i = 0; i < count; i++)
            {
                if (Nvml.nvmlDeviceGetHandleByIndex_v2(i, out var dev) != 0)
                {
                    continue;
                }
                bool known = Nvml.nvmlDeviceGetCudaComputeCapability(dev, out int major, out int minor) == 0;
                var name = new byte[96];
                Nvml.nvmlDeviceGetName(dev, name, (uint)name.Length);
                var uuid = new byte[96];
                var u = Nvml.nvmlDeviceGetUUID(dev, uuid, (uint)uuid.Length) == 0
                    ? System.Text.Encoding.ASCII.GetString(uuid).TrimEnd('\0') : "";
                if (u.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase))
                {
                    u = u[4..];
                }
                gpus.Add(new NvidiaGpu(System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0'),
                    known ? major : 0, known ? minor : 0, u.ToLowerInvariant(), known));
            }
        }
        finally { Nvml.nvmlShutdown(); }
    }
    catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
    {
    }
    return gpus;
}

static class Nvml
{
    [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetUUID(IntPtr device, byte[] uuid, uint length);
    [DllImport("nvml.dll")] public static extern int nvmlShutdown();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
}
record PackIndex(string package_version, List<Pack> packs);
// installed_bytes counts binary/model payload only; shared license notices belong to the core.
record Pack(string name, string asset, string url, string sha256, long bytes, long installed_bytes, List<string> files,
    List<string> requires, bool recommended, Dictionary<string, string> file_sha256, string? archive_root = null, string? title = null, string? description = null);
