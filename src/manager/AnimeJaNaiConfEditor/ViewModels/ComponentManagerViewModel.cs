using Avalonia.Collections;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.ViewModels
{
    // One installable component pack (TensorRT runtime, per-GPU-generation kernels, RIFE
    // models), as reported by `mpv-AnimeFusionUpdater.exe --components --json`.
    public class ComponentItem : ViewModelBase
    {
        public string Name { get; init; } = "";
        public long Bytes { get; init; }
        public string? CatalogTitle { get; init; }
        public string? CatalogDescription { get; init; }
        public bool Installed { get; init; }
        public bool Recommended { get; init; }

        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set => this.RaiseAndSetIfChanged(ref _selected, value);
        }

        public string SizeText => $"{Bytes / 1048576:N0} MB";

        public string StateText => Installed ? AnimeJaNai.Localization.UiText.T("已安装") : AnimeJaNai.Localization.UiText.T("可选");

        // accent-tagged in the UI instead of pre-checked: recommendations should
        // be visible, not pre-decided
        public bool HighlightRecommended => Recommended && !Installed;

        public string Title => AnimeJaNai.Localization.UiText.T(CatalogTitle ?? Name);

        public string Description => AnimeJaNai.Localization.UiText.T(CatalogDescription);
    }

    // Detects, installs, and removes component packs by shelling out to the updater, which
    // owns all pack logic (release lookup, NVML GPU detection, components.json bookkeeping).
    public class ComponentManagerViewModel : ViewModelBase
    {
        public static string UpdaterPath { get; } =
            Path.Combine(MainWindowViewModel.RootDir, "app", "mpv-AnimeFusionUpdater.exe");

        public bool UpdaterFound => File.Exists(UpdaterPath);

        public AvaloniaList<ComponentItem> Packs { get; } = [];

        // GPU identity from the engine's NVML detection; null until a refresh
        // has completed (callers fall back to disk-state-only behavior).
        public bool? GpuNvidia { get; private set; }

        public bool TrtPackAvailable { get; private set; }

        // Raised on the UI thread after every refresh (including after Apply),
        // so the Profiles tab can re-derive its component awareness.
        public event Action? Refreshed;

        private string _gpuText = AnimeJaNai.Localization.UiText.T("正在检测硬件……");
        public string GpuText
        {
            get => _gpuText;
            set => this.RaiseAndSetIfChanged(ref _gpuText, value);
        }

        private string _statusLine = "";
        public string StatusLine
        {
            get => _statusLine;
            set => this.RaiseAndSetIfChanged(ref _statusLine, value);
        }

        private bool _setupNeeded;
        public bool SetupNeeded
        {
            get => _setupNeeded;
            set => this.RaiseAndSetIfChanged(ref _setupNeeded, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                this.RaiseAndSetIfChanged(ref _isBusy, value);
                this.RaisePropertyChanged(nameof(NotBusy));
            }
        }

        public bool NotBusy => !IsBusy;
        private Process? _activeProcess;
        public void Cancel()
        {
            try
            {
                if (_activeProcess is { HasExited: false }) _activeProcess.StandardInput.WriteLine("cancel");
            }
            catch (IOException) { } // The child may finish between checking and writing.
            catch (InvalidOperationException) { }
        }
        public void SelectRecommended()
        {
            foreach (var item in Packs) item.Selected = item.Installed || item.Recommended;
        }
        public void ClearSelection()
        {
            foreach (var item in Packs) item.Selected = item.Installed;
        }

        private bool _loadFailed;
        public bool LoadFailed
        {
            get => _loadFailed;
            set => this.RaiseAndSetIfChanged(ref _loadFailed, value);
        }

        // Installed items are checked; hardware recommendations remain explicit choices.
        public async Task RefreshAsync()
        {
            if (!UpdaterFound)
            {
                GpuText = AnimeJaNai.Localization.UiText.T("安装目录中未找到 mpv-AnimeFusionUpdater.exe，无法管理组件。");
                LoadFailed = true;
                return;
            }

            if (IsBusy) return;
            IsBusy = true;
            StatusLine = AnimeJaNai.Localization.UiText.T("正在检查已安装组件……");
            try
            {
                var (exitCode, output) = await RunUpdaterAsync("--components --json", null);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(output.Trim());
                }

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                var gpu = root.GetProperty("gpu");
                bool nvidia = gpu.GetProperty("nvidia").GetBoolean();
                GpuText = nvidia
                    ? AnimeJaNai.Localization.UiText.F($"GPU：{gpu.GetProperty("name").GetString()}")
                    : AnimeJaNai.Localization.UiText.T("GPU：未检测到 NVIDIA 设备；内置 DirectML 后端可用于 AMD 和 Intel GPU");
                GpuNvidia = nvidia;

                Packs.Clear();
                foreach (var e in root.GetProperty("packs").EnumerateArray())
                {
                    bool installed = e.GetProperty("installed").GetBoolean();
                    bool recommended = e.GetProperty("recommended").GetBoolean();
                    var item = new ComponentItem
                    {
                        Name = e.GetProperty("name").GetString() ?? "",
                        Bytes = e.GetProperty("bytes").GetInt64(),
                        CatalogTitle = e.TryGetProperty("title", out var title) ? title.GetString() : null,
                        CatalogDescription = e.TryGetProperty("description", out var description) ? description.GetString() : null,
                        Installed = installed,
                        Recommended = recommended,
                    };
                    // checked = currently installed (the checkbox is desired state);
                    // recommended items are highlighted, never pre-checked
                    item.Selected = item.Installed;
                    Packs.Add(item);
                }

                TrtPackAvailable = Packs.Any(p => p.Name == "trt-runtime");
                SetupNeeded = Packs.Any(p => p.Recommended && !p.Installed);
                StatusLine = "";
                LoadFailed = false;
            }
            catch (Exception ex)
            {
                GpuText = AnimeJaNai.Localization.UiText.T("无法读取组件信息。");
                StatusLine = TranslateUpdaterText(ex.Message);
                LoadFailed = true;
            }
            finally
            {
                IsBusy = false;
                Refreshed?.Invoke();
            }
        }

        // Installs every checked-but-missing pack and removes every unchecked-but-installed
        // one, streaming the updater's progress output into the status line.
        public async void Apply()
        {
            var toInstall = Packs.Where(p => p.Selected && !p.Installed).Select(p => p.Name).ToList();
            var toRemove = Packs.Where(p => !p.Selected && p.Installed).Select(p => p.Name).ToList();
            await RunChangesAsync(toInstall, toRemove);
        }

        private async Task RunChangesAsync(List<string> toInstall, List<string> toRemove)
        {
            if (IsBusy)
            {
                return;
            }
            if (toInstall.Count == 0 && toRemove.Count == 0)
            {
                StatusLine = AnimeJaNai.Localization.UiText.T("没有需要更改的内容。");
                return;
            }

            IsBusy = true;
            try
            {
                foreach (var name in toInstall)
                {
                    var (exitCode, output) = await RunUpdaterAsync($"--install {name}",
                        line => StatusLine = $"{name}：{TranslateUpdaterText(line)}");
                    if (exitCode != 0)
                    {
                        StatusLine = AnimeJaNai.Localization.UiText.F($"安装 {name} 失败：{TranslateUpdaterText(LastLine(output))}");
                        return;
                    }
                }
                foreach (var name in toRemove.OrderBy(n => n == "trt-runtime" ? 1 : 0))
                {
                    StatusLine = AnimeJaNai.Localization.UiText.F($"正在移除 {name}……");
                    var (exitCode, output) = await RunUpdaterAsync($"--remove {name}", null);
                    if (exitCode != 0)
                    {
                        StatusLine = AnimeJaNai.Localization.UiText.F($"移除 {name} 失败：{TranslateUpdaterText(LastLine(output))}");
                        return;
                    }
                }
                StatusLine = AnimeJaNai.Localization.UiText.T("完成。");
            }
            catch (Exception ex)
            {
                StatusLine = TranslateUpdaterText(ex.Message);
            }
            finally
            {
                var result = StatusLine;
                IsBusy = false;
                await RefreshAsync();
                StatusLine = result;
            }
        }

        public async void Refresh()
        {
            await RefreshAsync();
        }

        private static string LastLine(string s) =>
            s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .LastOrDefault() ?? AnimeJaNai.Localization.UiText.T("未知错误");

        private static string TranslateUpdaterText(string message)
        {
            if (!AnimeJaNai.Localization.InterfaceLanguage.IsChinese) return message;
            if (string.IsNullOrWhiteSpace(message))
                return message;

            string translated = AnimeJaNaiConfEditor.ChineseLocalization.Translate(message);
            translated = translated.Replace("Download complete", "下载完成", StringComparison.Ordinal)
                                   .Replace("Downloading", "正在下载", StringComparison.Ordinal)
                                   .Replace("Installing", "正在安装", StringComparison.Ordinal)
                                   .Replace("Removing", "正在移除", StringComparison.Ordinal)
                                   .Replace("Done", "完成", StringComparison.Ordinal)
                                   .Replace("failed", "失败", StringComparison.OrdinalIgnoreCase)
                                   .Replace("error", "错误", StringComparison.OrdinalIgnoreCase);
            return translated;
        }

        // Runs the updater hidden; onLine (marshalled to the UI thread) sees each output
        // line live, the full output is returned for error reporting.
        private async Task<(int ExitCode, string Output)> RunUpdaterAsync(
            string arguments, Action<string>? onLine)
        {
            var psi = new ProcessStartInfo
            {
                FileName = UpdaterPath,
                Arguments = arguments,
                WorkingDirectory = MainWindowViewModel.RootDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            var output = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }
                lock (output) output.AppendLine(e.Data);
                if (onLine is not null && e.Data.Trim() is { Length: > 0 } line)
                {
                    Dispatcher.UIThread.Post(() => onLine(line));
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (output) output.AppendLine(e.Data);
                }
            };
            process.Start();
            _activeProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit(); // drain asynchronous output handlers
            _activeProcess = null;
            return (process.ExitCode, output.ToString());
        }
    }
}
