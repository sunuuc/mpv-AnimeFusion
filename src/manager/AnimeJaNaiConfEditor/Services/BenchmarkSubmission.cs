using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services
{
    // A single community benchmark result, ready to post to the AnimeJaNai
    // benchmark catalog. The user runs the benchmark (which writes
    // animejanai/benchmark.txt); this gathers the hardware/software context,
    // is shown back to the user verbatim for confirmation, and is POSTed to
    // our own endpoint - no GitHub account or token lives on the client. The
    // server stamps id + submitted_at, so those are not sent.
    public sealed class BenchmarkSubmission
    {
        // Maintainer-operated proxy (holds the GitHub credential, validates,
        // and files the submission as a PR). Catalog is the published site.
        public const string SubmitUrl = "https://animejan.ai/api/benchmarks";
        public const string CatalogUrl = "https://benchmarks.animejan.ai";

        [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
        [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("backend")] public string Backend { get; set; } = "";
        [JsonPropertyName("gpu")] public string Gpu { get; set; } = "";
        // Accurate total VRAM from DXGI for all vendors; NVIDIA systems also
        // get the same value from nvidia-smi as a fallback/confirmation.
        [JsonPropertyName("vram_mb")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? VramMb { get; set; }
        // Enforced GPU power limit / TGP in watts. NVIDIA reports the live
        // power limit through nvidia-smi; AMD falls back to nominal board power
        // for known desktop GPU names when no vendor API is available.
        [JsonPropertyName("gpu_power_w")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int GpuPowerW { get; set; }
        [JsonPropertyName("cpu")] public string Cpu { get; set; } = "";
        // CPU max clock (MHz) and core/thread counts (omitted when 0/unknown).
        [JsonPropertyName("cpu_mhz")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int CpuMhz { get; set; }
        [JsonPropertyName("cpu_cores")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int CpuCores { get; set; }
        [JsonPropertyName("cpu_threads")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int CpuThreads { get; set; }
        // Total installed RAM and its actual operating speed (omitted when 0/unknown).
        [JsonPropertyName("ram_mb")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long RamMb { get; set; }
        [JsonPropertyName("ram_speed_mhz")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int RamSpeedMhz { get; set; }
        [JsonPropertyName("os")] public string Os { get; set; } = "";
        [JsonPropertyName("driver")] public string Driver { get; set; } = "";
        // template name (Balanced/Performance) -> resolution -> fps
        [JsonPropertyName("results")] public Dictionary<string, Dictionary<string, double>> Results { get; set; } = new();
        // Optional credit: GitHub/Discord handle or any name; blank = anonymous.
        [JsonPropertyName("submitted_by")] public string SubmittedBy { get; set; } = "";
        [JsonPropertyName("note")] public string Note { get; set; } = "";

        static readonly JsonSerializerOptions PreviewOpts = new() { WriteIndented = true };

        public bool HasResults => Results.Values.Any(r => r.Count > 0);

        public string ToPreviewJson() => JsonSerializer.Serialize(this, PreviewOpts);

        // Parse animejanai/benchmark.txt exactly as benchmark.ps1 writes it:
        //
        //   AnimeJaNai inference benchmark - backend: TensorRT
        //   <blank>
        //   |fps|480x360|1280x720|1920x1080|
        //   |-|-|-|-|
        //   |Balanced|1204.5|210.3|78.99|
        //   |Performance|...|
        //
        // Resolution columns come from the header row, not a fixed list, so
        // this keeps working if the bundled seed resolutions change.
        public static BenchmarkSubmission FromBenchmarkFile(string path)
        {
            var sub = new BenchmarkSubmission();
            string[] cols = Array.Empty<string>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (!line.StartsWith("|"))
                {
                    var m = Regex.Match(line, @"backend:\s*(\S+)", RegexOptions.IgnoreCase);
                    if (m.Success) sub.Backend = m.Groups[1].Value;
                    continue;
                }

                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length == 0) continue;
                if (cells[0].Equals("fps", StringComparison.OrdinalIgnoreCase))
                {
                    cols = cells.Skip(1).ToArray();
                    continue;
                }
                if (cells.All(c => c.Length == 0 || c == "-")) continue; // separator

                var row = new Dictionary<string, double>();
                for (int i = 1; i < cells.Length && i - 1 < cols.Length; i++)
                {
                    if (TryParseFpsCell(cells[i], out var fps))
                        row[cols[i - 1]] = fps;
                }
                if (row.Count > 0) sub.Results[cells[0]] = row;
            }
            return sub;
        }

        // Fill in hardware/software context. Everything here is best-effort:
        // a missing field is left blank rather than blocking the submission.
        // animejanaiDir is the Manager's ExePath (the animejanai/ directory);
        // version.txt lives one level up at the install root.
        public void FillSystemInfo(string animejanaiDir)
        {
            AppVersion = ReadAppVersion(animejanaiDir);
            Os = RuntimeInformation.OSDescription;
            if (OperatingSystem.IsWindows())
            {
                try { GatherFromWmi(); } catch { /* WMI unavailable: leave blank */ }
            }
            else
            {
                try { GatherFromProc(); } catch { /* /proc unavailable: leave blank */ }
            }
            TryFillDxgiMemory();
            TryFillNvidia();
            TryFillKnownGpuSpecs();
        }

        // GPU name, total VRAM (MB), enforced power limit / TGP (W), and driver
        // from nvidia-smi (NVML under the hood). No-op on non-NVIDIA systems or
        // if nvidia-smi isn't on PATH. With multiple NVIDIA GPUs, the
        // largest-memory one wins, and all NVIDIA fields are taken from that row.
        // For TensorRT this is the authoritative GPU identity; WMI often lists an
        // AMD/Intel iGPU first on Ryzen/Intel hybrid systems.
        void TryFillNvidia()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,memory.total,power.limit,driver_version --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p == null) return;
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4000);
                string bestName = "";
                string bestDriver = "";
                long bestVram = 0;
                int bestPower = 0;
                foreach (var line in stdout.Split('\n'))
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2)
                        continue;
                    if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vram))
                        continue;
                    if (vram <= bestVram) continue;
                    bestName = parts[0].Trim();
                    bestVram = vram;
                    bestPower = parts.Length >= 3 &&
                                double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                        ? (int)Math.Round(w) : 0;
                    bestDriver = parts.Length >= 4 ? parts[3].Trim() : "";
                }
                var hadNoGpu = Gpu.Length == 0;
                var hadIntegratedGpu = IsIntegratedGpuName(Gpu);
                var hadNonNvidiaGpu = Gpu.Length > 0 && !IsNvidiaGpuName(Gpu);
                var shouldReplaceWithNvidia = Backend.Equals("TensorRT", StringComparison.OrdinalIgnoreCase) ||
                                              hadNoGpu ||
                                              hadIntegratedGpu;
                if (bestName.Length > 0 && shouldReplaceWithNvidia)
                    Gpu = bestName;
                if (bestVram > 0) VramMb = bestVram;
                if (bestPower > 0) GpuPowerW = bestPower;
                if (bestDriver.Length > 0 && (Driver.Length == 0 || hadNoGpu || hadNonNvidiaGpu))
                    Driver = bestDriver;
            }
            catch { /* leave blank */ }
        }

        void TryFillDxgiMemory()
        {
            try
            {
                var adapters = DxgiAdapterInfo.Enumerate();
                if (adapters.Count == 0) return;

                var tensorRt = Backend.Equals("TensorRT", StringComparison.OrdinalIgnoreCase);
                var selected = adapters
                    .Where(a => a.DedicatedVideoMemory > 0 && !a.Software)
                    .OrderByDescending(a => DxgiAdapterScore(a, tensorRt))
                    .ThenByDescending(a => a.DedicatedVideoMemory)
                    .FirstOrDefault();

                if (selected.Name.Length == 0 || selected.DedicatedVideoMemory <= 0)
                    return;

                if (Gpu.Length == 0 ||
                    IsIntegratedGpuName(Gpu) ||
                    (tensorRt && IsNvidiaGpuName(selected.Name)))
                    Gpu = selected.Name;

                VramMb = (long)Math.Round(selected.DedicatedVideoMemory / 1048576.0);
            }
            catch { /* leave blank */ }
        }

        void GatherFromWmi()
        {
            using (var s = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
            {
                var gpus = s.Get().Cast<ManagementObject>()
                    .Select(mo => (name: (mo["Name"] as string)?.Trim() ?? "",
                                   driver: (mo["DriverVersion"] as string)?.Trim() ?? ""))
                    .Where(g => g.name.Length > 0
                                && !g.name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                                && !g.name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // Prefer the adapter that can actually run the selected backend.
                // WMI order is not stable: Ryzen systems often list
                // "AMD Radeon(TM) Graphics" before the NVIDIA dGPU.
                var tensorRt = Backend.Equals("TensorRT", StringComparison.OrdinalIgnoreCase);
                var pick = tensorRt
                    ? gpus.FirstOrDefault(g => IsNvidiaGpuName(g.name))
                    : gpus.OrderByDescending(g => GpuPriority(g.name)).FirstOrDefault();
                if (pick.name == null || pick.name.Length == 0) pick = gpus.FirstOrDefault();
                if (pick.name != null && pick.name.Length > 0) { Gpu = pick.name; Driver = pick.driver; }
            }

            using (var s = new ManagementObjectSearcher("SELECT Name, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                var cpu = s.Get().Cast<ManagementObject>().FirstOrDefault();
                if (cpu != null)
                {
                    Cpu = (cpu["Name"] as string)?.Trim() ?? "";
                    CpuMhz = (int)ToLong(cpu["MaxClockSpeed"]);
                    CpuCores = (int)ToLong(cpu["NumberOfCores"]);
                    CpuThreads = (int)ToLong(cpu["NumberOfLogicalProcessors"]);
                }
            }

            using (var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                var bytes = s.Get().Cast<ManagementObject>()
                    .Select(mo => ToLong(mo["TotalPhysicalMemory"]))
                    .FirstOrDefault();
                if (bytes > 0) RamMb = (long)Math.Round(bytes / 1048576.0);
            }

            // ConfiguredClockSpeed is the actual operating speed (reflects XMP/EXPO);
            // fall back to the SMBIOS-rated Speed when it's unavailable.
            using (var s = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory"))
            {
                int best = 0;
                foreach (var mo in s.Get().Cast<ManagementObject>())
                {
                    int configured = (int)ToLong(mo["ConfiguredClockSpeed"]);
                    int rated = (int)ToLong(mo[AnimeJaNai.Localization.UiText.T("Speed")]);
                    best = Math.Max(best, configured > 0 ? configured : rated);
                }
                if (best > 0) RamSpeedMhz = best;
            }
        }

        // Linux equivalent of GatherFromWmi for CPU/RAM (the GPU comes from
        // nvidia-smi via TryFillNvidia, same as Windows). RAM speed needs
        // dmidecode/root, so it's left blank.
        void GatherFromProc()
        {
            var cpuinfo = File.ReadAllText("/proc/cpuinfo");
            var model = Regex.Match(cpuinfo, @"model name\s*:\s*(.+)");
            if (model.Success) Cpu = model.Groups[1].Value.Trim();
            CpuThreads = Regex.Matches(cpuinfo, @"(?m)^processor\s*:").Count;
            var cores = Regex.Match(cpuinfo, @"cpu cores\s*:\s*(\d+)");
            CpuCores = cores.Success ? int.Parse(cores.Groups[1].Value, CultureInfo.InvariantCulture)
                                     : CpuThreads;
            var mhz = Regex.Match(cpuinfo, @"cpu MHz\s*:\s*([\d.]+)");
            if (mhz.Success &&
                double.TryParse(mhz.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                CpuMhz = (int)Math.Round(m);

            var memTotal = Regex.Match(File.ReadAllText("/proc/meminfo"), @"MemTotal:\s*(\d+)\s*kB");
            if (memTotal.Success &&
                long.TryParse(memTotal.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                RamMb = (long)Math.Round(kb / 1024.0);
        }

        static long ToLong(object? value)
        {
            try { return value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0L; }
        }

        static bool IsNvidiaGpuName(string name) =>
            Regex.IsMatch(name, @"NVIDIA|GeForce|\bRTX\b|\bGTX\b|Quadro|Titan", RegexOptions.IgnoreCase);

        static bool TryParseFpsCell(string cell, out double fps)
        {
            if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out fps))
                return true;
            if (double.TryParse(cell, NumberStyles.Float, CultureInfo.CurrentCulture, out fps))
                return true;

            var normalized = cell.Trim().Replace(',', '.');
            return normalized.Contains('.') &&
                   double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);
        }

        static bool IsIntegratedGpuName(string name) =>
            Regex.IsMatch(name, @"Radeon\(TM\) Graphics|AMD Radeon Graphics|Intel\(R\).*Graphics|Intel.*UHD|Intel.*Iris", RegexOptions.IgnoreCase);

        static int GpuPriority(string name)
        {
            if (IsNvidiaGpuName(name)) return 500;
            if (Regex.IsMatch(name, @"Radeon\s+(RX|Pro|VII)|\bRX\s*\d|Arc\b", RegexOptions.IgnoreCase)) return 400;
            if (Regex.IsMatch(name, @"Radeon|\bAMD\b", RegexOptions.IgnoreCase) && !IsIntegratedGpuName(name)) return 300;
            if (Regex.IsMatch(name, @"Iris|UHD|Intel", RegexOptions.IgnoreCase)) return 100;
            return 0;
        }

        int DxgiAdapterScore(DxgiAdapterInfo adapter, bool tensorRt)
        {
            int score = GpuPriority(adapter.Name);
            if (tensorRt && IsNvidiaGpuName(adapter.Name)) score += 1000;
            if (GpuNameMatches(Gpu, adapter.Name)) score += 600;
            return score;
        }

        static bool GpuNameMatches(string a, string b)
        {
            a = NormalizeGpuName(a);
            b = NormalizeGpuName(b);
            return a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a));
        }

        static string NormalizeGpuName(string s) =>
            Regex.Replace(s ?? "", @"[^a-z0-9]+", "", RegexOptions.IgnoreCase).ToLowerInvariant();

        void TryFillKnownGpuSpecs()
        {
            var spec = KnownGpuSpec(Gpu);
            if (spec.vramMb > 0 && !VramMb.HasValue) VramMb = spec.vramMb;
            if (spec.powerW > 0 && GpuPowerW == 0) GpuPowerW = spec.powerW;
        }

        static (long vramMb, int powerW) KnownGpuSpec(string name)
        {
            foreach (var spec in KnownGpuSpecs)
                if (Regex.IsMatch(name ?? "", spec.pattern, RegexOptions.IgnoreCase))
                    return (spec.vramMb, spec.powerW);
            return (0, 0);
        }

        static readonly (string pattern, long vramMb, int powerW)[] KnownGpuSpecs =
        {
            (@"Radeon\s+RX\s+9070\s+XT\b", 16384, 304),
            (@"Radeon\s+RX\s+9070\s+GRE\b", 12288, 220),
            (@"Radeon\s+RX\s+9070\b", 16384, 220),
            (@"Radeon\s+RX\s+9060\s+XT\b", 0, 160),
            (@"Radeon\s+RX\s+7900\s+XTX\b", 24576, 355),
            (@"Radeon\s+RX\s+7900\s+XT\b", 20480, 315),
            (@"Radeon\s+RX\s+7900\s+GRE\b", 16384, 260),
            (@"Radeon\s+RX\s+7800\s+XT\b", 16384, 263),
            (@"Radeon\s+RX\s+7700\s+XT\b", 12288, 245),
            (@"Radeon\s+RX\s+7600\s+XT\b", 16384, 165),
            (@"Radeon\s+RX\s+7600\b", 8192, 165),
            (@"Radeon\s+RX\s+6950\s+XT\b", 16384, 335),
            (@"Radeon\s+RX\s+6900\s+XT\b", 16384, 300),
            (@"Radeon\s+RX\s+6800\s+XT\b", 16384, 300),
            (@"Radeon\s+RX\s+6800\b", 16384, 250),
            (@"Radeon\s+RX\s+6750\s+XT\b", 12288, 250),
            (@"Radeon\s+RX\s+6700\s+XT\b", 12288, 230),
            (@"Radeon\s+RX\s+6600\s+XT\b", 8192, 160),
            (@"Radeon\s+RX\s+6600\b", 8192, 132),
        };

        readonly struct DxgiAdapterInfo
        {
            const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
            readonly string _name;
            readonly ulong _dedicatedVideoMemory;
            readonly bool _software;

            public string Name => _name ?? "";
            public ulong DedicatedVideoMemory => _dedicatedVideoMemory;
            public bool Software => _software;

            DxgiAdapterInfo(string name, ulong dedicatedVideoMemory, bool software)
            {
                _name = name;
                _dedicatedVideoMemory = dedicatedVideoMemory;
                _software = software;
            }

            public static List<DxgiAdapterInfo> Enumerate()
            {
                var adapters = new List<DxgiAdapterInfo>();
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == null)
                    return adapters;

                try
                {
                    for (uint i = 0; ; i++)
                    {
                        IDXGIAdapter1 adapter;
                        int hr;
                        try { hr = factory.EnumAdapters1(i, out adapter); }
                        catch (COMException ex) when (ex.ErrorCode == DXGI_ERROR_NOT_FOUND) { break; }

                        if (hr == DXGI_ERROR_NOT_FOUND) break;
                        if (hr < 0 || adapter == null) break;

                        try
                        {
                            if (adapter.GetDesc1(out var desc) >= 0)
                            {
                                adapters.Add(new DxgiAdapterInfo(
                                    desc.Description?.Trim() ?? "",
                                    desc.DedicatedVideoMemory.ToUInt64(),
                                    (desc.Flags & 2) != 0));
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(adapter);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(factory);
                }

                return adapters;
            }

            [DllImport("dxgi.dll", SetLastError = false)]
            static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

            [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IDXGIFactory1
            {
                [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
                [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
                [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
                [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
                [PreserveSig] int EnumAdapters(uint adapter, out IntPtr dxgiAdapter);
                [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
                [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
                [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
                [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
                [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 dxgiAdapter);
                [PreserveSig] bool IsCurrent();
            }

            [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IDXGIAdapter1
            {
                [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
                [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
                [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
                [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
                [PreserveSig] int EnumOutputs(uint output, out IntPtr dxgiOutput);
                [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC desc);
                [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
                [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct DXGI_ADAPTER_DESC
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
                public uint VendorId, DeviceId, SubSysId, Revision;
                public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
                public LUID AdapterLuid;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct DXGI_ADAPTER_DESC1
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
                public uint VendorId, DeviceId, SubSysId, Revision;
                public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
                public LUID AdapterLuid;
                public uint Flags;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct LUID
            {
                public uint LowPart;
                public int HighPart;
            }
        }

        static string ReadAppVersion(string animejanaiDir)
        {
            try
            {
                var versionTxt = Path.GetFullPath(Path.Combine(animejanaiDir, "..", "version.txt"));
                if (File.Exists(versionTxt))
                    return File.ReadAllText(versionTxt).Trim();
            }
            catch { /* ignore */ }
            return "";
        }

        public async Task<(bool ok, string message)> SubmitAsync(CancellationToken ct = default)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("mpv-AnimeFusionManager");
            using var content = new StringContent(JsonSerializer.Serialize(this), Encoding.UTF8, "application/json");
            try
            {
                var resp = await client.PostAsync(SubmitUrl, content, ct);
                if (resp.IsSuccessStatusCode)
                    return (true, AnimeJaNai.Localization.UiText.F($"Thanks! Your benchmark was submitted.\n\nAfter a quick review it will appear in the community catalog at {CatalogUrl}."));

                var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
                return (false, AnimeJaNai.Localization.UiText.F($"The server rejected the submission (HTTP {(int)resp.StatusCode}).\n\n{Truncate(body, 300)}"));
            }
            catch (Exception ex)
            {
                return (false, AnimeJaNai.Localization.UiText.F($"Couldn't reach the benchmark server: {ex.Message}\n\n") +
                               AnimeJaNai.Localization.UiText.T("Please try again later, or share your benchmark.txt on the AnimeJaNai Discord."));
            }
        }

        static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? AnimeJaNai.Localization.UiText.T("(no details)") : (s.Length <= max ? s : s.Substring(0, max) + "...");
    }
}
