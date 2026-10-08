using Avalonia.Collections;
using ReactiveUI;
using Salaros.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.ViewModels
{
    [DataContract]
    public class MainWindowViewModel : ViewModelBase
    {
        private static readonly CultureInfo ENGLISH_CULTURE = CultureInfo.GetCultureInfo("en-US");

        public static MainWindowViewModel? Instance { get; private set; }

        public MainWindowViewModel()
        {
            Instance = this;
            AnimeJaNaiConf = ReadAnimeJaNaiConf(AnimeJaNaiConfPath, true);


            for (var i = 0; i < AnimeJaNaiConf.UpscaleSlots.Count; i++)
            {
                AnimeJaNaiConf.UpscaleSlots[i].MpvProfileName = $"upscale-on-{i + 1}";
            }

            CheckAndDoBackup();

            SelectedMpvProfile = ReadCurrentProfileFromMpvConf();

            InitializeSelectedSlot();

            RefreshComponentAwareness();
            ComponentManager.Refreshed += RefreshComponentAwareness;
            _ = InitializeComponentManagerAsync();
        }

        // ---- component awareness for the Profiles tab -------------------------------------
        // Install state comes from the disk (works offline); GPU identity comes from the
        // component engine once its refresh completes.

        public bool DirectMlAvailable => true;

        public static bool TrtOnDisk() =>
            File.Exists(Path.Combine(DataDir, "inference", "nvinfer_11.dll"));

        public static bool RifeOnDisk() =>
            Directory.Exists(Path.Combine(DataDir, "rife")) &&
            Directory.EnumerateFiles(Path.Combine(DataDir, "rife"), "*.onnx").Any();

        private bool _trtSelectable = true;
        public bool TrtSelectable
        {
            get => _trtSelectable;
            set => this.RaiseAndSetIfChanged(ref _trtSelectable, value);
        }

        private string _backendNotice = "";
        public string BackendNotice
        {
            get => _backendNotice;
            set
            {
                this.RaiseAndSetIfChanged(ref _backendNotice, value);
                this.RaisePropertyChanged(nameof(HasBackendNotice));
            }
        }

        public bool HasBackendNotice => !string.IsNullOrEmpty(BackendNotice);

        private bool _rifeMissing;
        public bool RifeMissing
        {
            get => _rifeMissing;
            set => this.RaiseAndSetIfChanged(ref _rifeMissing, value);
        }

        public void RefreshComponentAwareness()
        {
            _rifeModels = null;
            bool trtInstalled = TrtOnDisk();
            bool? nvidia = ComponentManager.GpuNvidia;
            bool trtUsable = trtInstalled && nvidia != false;
            TrtSelectable = trtUsable;

            string notice = "";
            if (trtUsable && AnimeJaNaiConf != null && AnimeJaNaiConf.DirectMlSelected &&
                AnimeJaNaiConf.BackendAutoFallback)
            {
                // TensorRT is the natural path on NVIDIA: the DirectML selection was
                // only our fallback, so installing TensorRT completes the setup
                AnimeJaNaiConf.BackendAutoFallback = false;
                AnimeJaNaiConf.SetTensorRtSelected();
                notice = AnimeJaNai.Localization.UiText.T("TensorRT is installed — the backend has switched back to TensorRT.");
            }
            else if (!trtUsable)
            {
                // a conf pointing at an unusable backend would only fail at playback;
                // flip it to the engine that ships in every install and say so
                string flipped = "";
                if (AnimeJaNaiConf != null && AnimeJaNaiConf.TensorRtSelected)
                {
                    AnimeJaNaiConf.BackendAutoFallback = true;
                    AnimeJaNaiConf.SetDirectMlSelected();
                    flipped = AnimeJaNai.Localization.UiText.T("Switched to DirectML: ");
                }
                notice = nvidia == false
                    ? flipped + AnimeJaNai.Localization.UiText.T("TensorRT requires an NVIDIA GPU.")
                    : ComponentManager.TrtPackAvailable
                        ? flipped + AnimeJaNai.Localization.UiText.T("TensorRT is not installed — install it from the Components tab (recommended for NVIDIA GPUs); the backend switches back automatically once installed.")
                        : flipped + AnimeJaNai.Localization.UiText.T("TensorRT is not installed.");
            }
            BackendNotice = notice;
            RifeMissing = !RifeOnDisk();

            // per-chain RIFE toggles derive from RifeMissing; let them re-evaluate
            foreach (var slot in AnimeJaNaiConf?.UpscaleSlots ?? [])
            {
                foreach (var chain in slot.Chains)
                {
                    chain.RifeModelList = new List<string>(RifeModels);
                    foreach (var model in chain.Models) model.AllModels = GetAllModels();
                    chain.RaisePropertyChanged(nameof(UpscaleChain.RifeToggleEnabled));
                }
            }
        }

        public ComponentManagerViewModel ComponentManager { get; } = new();
        public string ProductVersion => "mpv-AnimeFusion " +
            (typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "");

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
        }

        // Missing hardware components open the picker. Installation is always
        // an explicit selection followed by Apply; startup performs no downloads.
        private async Task InitializeComponentManagerAsync()
        {
            await ComponentManager.RefreshAsync();
            if (!ComponentManager.SetupNeeded)
            {
                return;
            }
            SelectedTabIndex = 1;


        }



        private string[] _commonResolutions = [

            "0x0",

            "640x360",

            "640x480",

            "720x480",

            "768x576",

            "960x540",

            "1024x576",

            "1280x720",

            "1440x1080",

            "1920x1080"];



        public string[] CommonResolutions

        {

            get => _commonResolutions;

            set => this.RaiseAndSetIfChanged(ref _commonResolutions, value);

        }



        public string[] BuilderOptimizationLevels { get; } = ["0", "1", "2", "3", "4", "5"];



        public string ExePath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

        public static string RootDir { get; } = Path.GetFullPath(
            Environment.GetEnvironmentVariable("ANIMEJANAI_ROOT") is { Length: > 0 } root ? root
            : Path.GetDirectoryName(Environment.ProcessPath)!);

        public static string DataDir { get; } = Path.GetFullPath(
            Environment.GetEnvironmentVariable("ANIMEJANAI_DATA_DIR") is { Length: > 0 } data ? data
            : Path.Combine(RootDir, "animejanai"));

        public string BackupPath => Path.Combine(DataDir, "backups");

        public string MpvConfPath => Path.Combine(RootDir, "portable_config", "mpv.conf");

        public string AnimeJaNaiConfPath => Path.Combine(DataDir, "animejanai.conf");

        public string OnnxPath => Path.Combine(DataDir, "onnx");

        private bool _showGlobalSettings = false; // TODO
        [DataMember]
        public bool ShowGlobalSettings
        {
            get => _showGlobalSettings;
            set => this.RaiseAndSetIfChanged(ref _showGlobalSettings, value);
        }

        private bool _showCustomProfiles = true; // TODO 
        [DataMember]
        public bool ShowCustomProfiles
        {
            get => _showCustomProfiles;
            set
            {
                this.RaiseAndSetIfChanged(ref _showCustomProfiles, value);
                this.RaisePropertyChanged(nameof(CurrentSlot));
            }
        }

        private string _selectedSlotNumber = "1"; // TODO
        [DataMember]
        public string SelectedSlotNumber
        {
            get => _selectedSlotNumber;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedSlotNumber, value);
                this.RaisePropertyChanged(nameof(CurrentSlot));
            }
        }

        private string? _selectedMpvProfile;
        [DataMember]
        public string? SelectedMpvProfile
        {
            get => _selectedMpvProfile;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedMpvProfile, value);
                this.RaisePropertyChanged(nameof(MpvConfDetected));
            }
        }

        public bool MpvConfDetected => File.Exists(MpvConfPath);


        public void HandleShowGlobalSettings()
        {
            ShowGlobalSettings = true;
            ShowCustomProfiles = false;
        }

        public void HandleShowCustomProfile(string slotNumber)
        {
            ShowGlobalSettings = false;
            ShowCustomProfiles = true;
            SelectedSlotNumber = slotNumber;
        }

        private AnimeJaNaiConf _animeJaNaiConf;
        public AnimeJaNaiConf AnimeJaNaiConf
        {
            get => _animeJaNaiConf;
            set => this.RaiseAndSetIfChanged(ref _animeJaNaiConf, value);
        }

        public UpscaleSlot CurrentSlot
        {
            get => ShowCustomProfiles ? AnimeJaNaiConf.UpscaleSlots.Where(slot => slot.SlotNumber == SelectedSlotNumber).FirstOrDefault() : null;
        }

        private UpscaleSlot? _selectedProfileToClone;
        public UpscaleSlot? SelectedProfileToClone
        {
            get => _selectedProfileToClone;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedProfileToClone, value);
                this.RaisePropertyChanged(nameof(CanCloneProfile));
            }
        }

        public bool CanCloneProfile => true; //SelectedProfileToClone != null;

        public void AddChain()
        {
            CurrentSlot.Chains.Add(new UpscaleChain(true) { Vm = this });
            UpdateChainHeaders();
        }

        public void DeleteChain(UpscaleChain chain)
        {
            try
            {
                CurrentSlot.Chains.Remove(chain);
            }
            catch (ArgumentOutOfRangeException)
            {

            }

            UpdateChainHeaders();
        }

        public void UpdateChainHeaders()
        {
            for (var i = 0; i < CurrentSlot.Chains.Count; i++)
            {
                CurrentSlot.Chains[i].ChainNumber = (i + 1).ToString();
            }
        }

        private string _validationText = string.Empty;
        public string ValidationText
        {
            get => _validationText;
            set
            {
                this.RaiseAndSetIfChanged(ref _validationText, value);
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
        }

        public AvaloniaList<UpscaleSlot> AllSlots => new(AnimeJaNaiConf.UpscaleSlots.Where(x => x.ShowSlot && !x.ActiveSlot));

        private bool _showAdvancedSettings = false;
        [DataMember]
        public bool ShowAdvancedSettings
        {
            get => _showAdvancedSettings;
            set => this.RaiseAndSetIfChanged(ref _showAdvancedSettings, value);
        }

        private bool _showConsole = false;
        public bool ShowConsole
        {
            get => _showConsole;
            set => this.RaiseAndSetIfChanged(ref _showConsole, value);
        }

        private string _inputStatusText = string.Empty;
        public string InputStatusText
        {
            get => _inputStatusText;
            set
            {
                this.RaiseAndSetIfChanged(ref _inputStatusText, value);
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
        }

        public string LeftStatus => !Valid ? ValidationText.Replace("\n", " ") : AnimeJaNai.Localization.UiText.F($"{InputStatusText} selected for upscaling.");

        private bool _valid = false;
        [IgnoreDataMember]
        public bool Valid
        {
            get => _valid;
            set
            {
                this.RaiseAndSetIfChanged(ref _valid, value);
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
        }

        public AvaloniaList<string> GetAllModels()
        {
            return new AvaloniaList<string>((Directory.Exists(OnnxPath) ? Directory.GetFiles(OnnxPath) : Array.Empty<string>()).Where(filename => Path.GetExtension(filename).Equals(".onnx", StringComparison.CurrentCultureIgnoreCase))
                .Select(filename => Path.GetFileNameWithoutExtension(filename))
                .Order().ToList());
        }


        public void Validate()
        {
            Console.WriteLine("OK");
        }

        private static List<string>? _rifeModels = null;

        public static List<string> RifeModels
        {
            get
            {
                if (_rifeModels == null)
                {
                    var models = new List<string>();
                    var modelsPath = Path.Combine(DataDir, "rife");

                    if (!Directory.Exists(modelsPath))
                    {
                        return [];
                    }

                    var files = Directory.GetFiles(modelsPath, searchPattern: "*.onnx");

                    foreach (var file in files)
                    {
                        Debug.WriteLine(file);
                        var m = Regex.Match(Path.GetFileName(file), @"rife_v(\d+)\.(\d+)(_lite)?(_ensemble)?.onnx");
                        if (m.Success)
                        {
                            var model = m.Groups[1].Value + m.Groups[2].Value;
                            if (file.Contains("_lite"))
                            {
                                model += "1";
                            }

                            if (!models.Contains(model))
                            {
                                models.Add(model);
                            }
                        }
                    }

                    models.Sort(delegate (string m1, string m2)
                    {
                        var m1i = decimal.Parse(m1[..Math.Min(3, m1.Length)]);
                        var m2i = decimal.Parse(m2[..Math.Min(3, m2.Length)]);

                        if (m1.Length > 3)
                        {
                            m1i += .1m;
                        }

                        if (m2.Length > 3)
                        {
                            m2i += .1m;
                        }

                        return m2i.CompareTo(m1i);
                    });

                    _rifeModels = [.. models.Select(m => RifeValueToLabel(m))];
                }

                return _rifeModels;
            }
        }

        public static string RifeLabelToValue(string rifeLabel)
        {
            if (string.IsNullOrEmpty(rifeLabel))
            {
                return rifeLabel;
            }

            var m = Regex.Match(rifeLabel, @"RIFE (\d+)\.(\d+)( Lite)?");
            if (m.Success)
            {
                var value = $"{m.Groups[1].Value}{m.Groups[2].Value}";
                if (m.Groups[3].Success)
                {
                    value += "1";
                }
                return value;
            }

            throw new ArgumentException(rifeLabel);
        }

        public static string RifeValueToLabel(string rifeValue)
        {
            string dec;

            if (string.IsNullOrEmpty(rifeValue))
            {
                return rifeValue;
            }

            if (rifeValue.Length == 2)
            {
                dec = rifeValue[1].ToString();
            }
            else
            {
                dec = rifeValue.Substring(1, 2);
            }

            var modelName = $"RIFE {rifeValue[0]}.{dec}";

            if (rifeValue.Length >= 4 && rifeValue.EndsWith('1'))
            {
                modelName += " Lite";
            }

            return modelName;
        }

        public AnimeJaNaiConf ReadAnimeJaNaiConf(string fullPath, bool autoSave = false)
        {
            return ReadAnimeJaNaiConf(new ConfigParser(fullPath), autoSave);
        }

        // Keep CONFIG_VERSION, the current default, and the historical-defaults set in sync with
        // aji_conf.cpp in the animejanai-inference repo (the native filter's conf loader).
        private const int CONFIG_VERSION = 3;
        // [global] sub_render_mode value that selects the experimental GPU subtitle path. The
        // settings themselves live in the [subs-gpu] profile of the managed mpv-animejanai.conf,
        // so they can change with the player without a Manager release.
        private const string SUB_RENDER_MODE_GPU = "gpu";
        // TRT 11: strongly-typed is the default (--stronglyTyped is a no-op), and the native filter's
        // sanitizer strips --inputIOFormats/--outputIOFormats/--tacticSources anyway (types come from
        // the network; the cuDNN/cuBLAS tactic sources are gone). Only the builder optimization level,
        // build shape, and skip-inference are worth carrying.
        private const string DEFAULT_TRT_ENGINE_SETTINGS =
            "--builderOptimizationLevel=5 --optShapes=input:%video_resolution% --skipInference";
        private static readonly HashSet<string> LEGACY_DEFAULT_TRT_ENGINE_SETTINGS = new()
        {
            DEFAULT_TRT_ENGINE_SETTINGS,
            // pre-trim default (TRT 8/10-era flags); drop it from old confs so the current default applies
            "--stronglyTyped --optShapes=input:%video_resolution% --inputIOFormats=fp16:chw --outputIOFormats=fp16:chw --builderOptimizationLevel=5 --tacticSources=-CUDNN,-CUBLAS,-CUBLAS_LT --skipInference",
        };

        // Hand-edited confs can leave numeric keys empty; the native filter reads
        // empty as 0, but a non-numeric string must never reach a NumberBox binding
        // (InvalidCastException converting '' to double).
        private static string NumericOr(ConfigParser parser, string section, string key, string fallback = "0")
        {
            var value = parser.GetValue(section, key);
            return string.IsNullOrWhiteSpace(value) ||
                   !double.TryParse(value, System.Globalization.NumberStyles.Float, ENGLISH_CULTURE, out _)
                ? fallback : value;
        }

        public AnimeJaNaiConf ReadAnimeJaNaiConf(ConfigParser parser, bool autoSave = false)
        {
            var animeJaNaiConf = new AnimeJaNaiConf(autoSave) { Vm = this };
            var slots = new Dictionary<string, UpscaleSlot>();

            animeJaNaiConf.EnableLogging = ParseBool(parser.GetValue("global", "logging", "no"));
            animeJaNaiConf.BackendAutoFallback = ParseBool(parser.GetValue("global", "backend_auto_fallback", "no"));
            if (Enum.TryParse(parser.GetValue("global", "backend", "TensorRT"), out Backend backend))
            {
                switch (backend)
                {
                    case Backend.DirectML:
                    case Backend.NCNN: // retired; the inference shim treats NCNN as DirectML
                        animeJaNaiConf.SetDirectMlSelected();
                        break;
                    case Backend.TensorRT:
                    default:
                        animeJaNaiConf.SetTensorRtSelected();
                        break;
                }
            }

            if (int.TryParse(parser.GetValue("global", "default_slot", ""), out var defaultSlot))
            {
                animeJaNaiConf.DefaultSlot = defaultSlot;
            }

            // Subtitle rendering mode: "gpu" opts into the experimental GPU subtitle path
            // (the [subs-gpu] profile in mpv-animejanai.conf, applied at startup by
            // scripts/animejanai_backend.lua). Anything else (including the absent key)
            // means the stable defaults baked into the [animejanai] profile.
            animeJaNaiConf.GpuSubtitles = SUB_RENDER_MODE_GPU.Equals(
                parser.GetValue("global", "sub_render_mode", "").Trim(),
                StringComparison.OrdinalIgnoreCase);

            // config_version drives migrations (absent => 1, the pre-versioning schema).
            int.TryParse(parser.GetValue("global", "config_version", "1"), out var configVersion);
            var trtEngineSettings = parser.GetValue("global", "trt_engine_settings", "");
            // v1 -> v2: drop a trt_engine_settings value that is just an old shipped default so the
            // current default applies; a genuinely customized value is kept.
            if (configVersion < 2 && LEGACY_DEFAULT_TRT_ENGINE_SETTINGS.Contains(trtEngineSettings))
            {
                trtEngineSettings = "";
            }
            // v2 -> v3: TRT 11 strongly-typed ONNX dropped --fp16/--bf16 (the backend strips them and
            // the precision selector was removed); rewrite either to --stronglyTyped so a migrated
            // custom value stays valid, collapsing a duplicate if both were present.
            if (configVersion < 3 && Regex.IsMatch(trtEngineSettings, @"--(fp16|bf16)\b"))
            {
                trtEngineSettings = Regex.Replace(trtEngineSettings, @"--(fp16|bf16)\b", "--stronglyTyped");
                trtEngineSettings = Regex.Replace(trtEngineSettings, @"--stronglyTyped(\s+--stronglyTyped)+", "--stronglyTyped");
            }
            animeJaNaiConf.TrtEngineSettings = string.IsNullOrEmpty(trtEngineSettings)
                ? DEFAULT_TRT_ENGINE_SETTINGS
                : trtEngineSettings;

            foreach (var section in parser.Sections)
            {
                if (section.SectionName != "global")
                {
                    var currentSlotNumber = Regex.Match(section.SectionName, @"slot_(\d+)").Groups[1].Value;

                    var chains = new Dictionary<string, UpscaleChain>();
                    var models = new Dictionary<string, Dictionary<string, UpscaleModel>>();

                    foreach (var key in section.Keys)
                    {
                        var matchCurrentChainNumber = Regex.Match(key.Name, @"chain_(\d+)_");

                        if (matchCurrentChainNumber.Success)
                        {
                            var currentChainNumber = matchCurrentChainNumber.Groups[1].Value;

                            chains[currentChainNumber] = new UpscaleChain(autoSave)
                            {
                                Vm = this,
                                ChainNumber = currentChainNumber,
                                MinResolution = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_min_resolution"),
                                MaxResolution = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_max_resolution"),
                                MinFps = NumericOr(parser, section.SectionName, $"chain_{currentChainNumber}_min_fps"),
                                MaxFps = NumericOr(parser, section.SectionName, $"chain_{currentChainNumber}_max_fps"),
                                EnableRife = ParseBool(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife")),
                                //RifeModel = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_model"),
                                RifeEnsemble = ParseBool(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_ensemble")),
                                RifeBeforeUpscale = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_before_upscale") is { } rbu ? ParseBool(rbu) : true,
                            };

                            if (int.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_factor_numerator"), ENGLISH_CULTURE, out var numerator))
                            {
                                chains[currentChainNumber].RifeFactorNumerator = numerator;
                            }

                            if (int.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_factor_denominator"), ENGLISH_CULTURE, out var denominator))
                            {
                                chains[currentChainNumber].RifeFactorDenominator = denominator;
                            }

                            if (decimal.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_scene_detect_threshold"), ENGLISH_CULTURE, out var scene_detect_threshold))
                            {
                                chains[currentChainNumber].RifeSceneDetectThreshold = scene_detect_threshold;
                            }

                            var rifeModel = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_model");

                            if (rifeModel != null)
                            {
                                chains[currentChainNumber].RifeModel = RifeValueToLabel(rifeModel);
                            }

                            var matchCurrentModelNumber = Regex.Match(key.Name, @"_model_(\d+)_");

                            if (matchCurrentModelNumber.Success)
                            {
                                var currentModelNumber = matchCurrentModelNumber.Groups[1].Value;

                                if (!models.ContainsKey(currentChainNumber))
                                {
                                    models[currentChainNumber] = [];
                                }

                                if (models[currentChainNumber].ContainsKey(currentModelNumber))
                                {
                                    continue;
                                }

                                models[currentChainNumber][currentModelNumber] = new UpscaleModel(autoSave)
                                {
                                    Vm = this,
                                    AllModels = GetAllModels(),
                                    ModelNumber = currentModelNumber,
                                    Name = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_model_{currentModelNumber}_name", "0x0"),
                                    ResizeFactorBeforeUpscale = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_model_{currentModelNumber}_resize_factor_before_upscale", 100.ToString()),
                                    ResizeHeightBeforeUpscale = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_model_{currentModelNumber}_resize_height_before_upscale", 0.ToString())
                                };
                            }
                        }
                    }

                    foreach (var currentChainNumber in models.Keys)
                    {
                        chains[currentChainNumber].Models = new AvaloniaList<UpscaleModel>(models[currentChainNumber].Values.ToList());
                    }

                    slots[currentSlotNumber] = new UpscaleSlot(autoSave)
                    {
                        Vm = this,
                        SlotNumber = currentSlotNumber,
                        ProfileName = parser.GetValue(section.SectionName, "profile_name", AnimeJaNai.Localization.UiText.T("New Profile")),
                        Chains = new AvaloniaList<UpscaleChain>(chains.Values.ToList())
                    };
                }
            }

            animeJaNaiConf.UpscaleSlots = new AvaloniaList<UpscaleSlot>(slots.Values);

            return animeJaNaiConf;
        }

        public void ReadAnimeJaNaiConfToCurrentSlot(string fullPath, bool autoSave = false)
        {
            ReadAnimeJaNaiConfToCurrentSlot(new ConfigParser(fullPath), autoSave);
        }

        public void ReadAnimeJaNaiConfToCurrentSlot(ConfigParser parser, bool autoSave = false)
        {
            var sectionKey = "slot";

            foreach (var section in parser.Sections)
            {
                if (section.SectionName != sectionKey)
                {
                    return; // Invalid
                }

                var currentSlotNumber = Regex.Match(section.SectionName, @"slot_(\d+)").Groups[1].Value;

                var chains = new Dictionary<string, UpscaleChain>();
                var models = new Dictionary<string, Dictionary<string, UpscaleModel>>();

                foreach (var key in section.Keys)
                {
                    var matchCurrentChainNumber = Regex.Match(key.Name, @"chain_(\d+)_");

                    if (matchCurrentChainNumber.Success)
                    {
                        var currentChainNumber = matchCurrentChainNumber.Groups[1].Value;

                        chains[currentChainNumber] = new UpscaleChain(autoSave)
                        {
                            Vm = this,
                            ChainNumber = currentChainNumber,
                            MinResolution = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_min_resolution"),
                            MaxResolution = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_max_resolution"),
                            MinFps = NumericOr(parser, section.SectionName, $"chain_{currentChainNumber}_min_fps"),
                            MaxFps = NumericOr(parser, section.SectionName, $"chain_{currentChainNumber}_max_fps"),
                            EnableRife = ParseBool(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife")),
                            RifeEnsemble = ParseBool(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_ensemble")),
                            RifeBeforeUpscale = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_before_upscale") is { } rbu ? ParseBool(rbu) : true,
                        };

                        if (int.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_factor_numerator"), ENGLISH_CULTURE, out var numerator))
                        {
                            chains[currentChainNumber].RifeFactorNumerator = numerator;
                        }

                        if (int.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_factor_denominator"), ENGLISH_CULTURE, out var denominator))
                        {
                            chains[currentChainNumber].RifeFactorDenominator = denominator;
                        }

                        if (decimal.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_scene_detect_threshold"), ENGLISH_CULTURE, out var scene_detect_threshold))
                        {
                            chains[currentChainNumber].RifeSceneDetectThreshold = scene_detect_threshold;
                        }

                        var rifeModel = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_model");

                        if (rifeModel != null)
                        {
                            chains[currentChainNumber].RifeModel = RifeValueToLabel(rifeModel);
                        }

                        var matchCurrentModelNumber = Regex.Match(key.Name, @"_model_(\d+)_");

                        if (matchCurrentModelNumber.Success)
                        {
                            var currentModelNumber = matchCurrentModelNumber.Groups[1].Value;

                            if (!models.ContainsKey(currentChainNumber))
                            {
                                models[currentChainNumber] = [];
                            }

                            if (models[currentChainNumber].ContainsKey(currentModelNumber))
                            {
                                continue;
                            }

                            models[currentChainNumber][currentModelNumber] = new UpscaleModel(true)
                            {
                                Vm = this,
                                AllModels = GetAllModels(),
                                ModelNumber = currentModelNumber,
                                Name = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_model_{currentModelNumber}_name", "0x0"),
                                ResizeFactorBeforeUpscale = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_model_{currentModelNumber}_resize_factor_before_upscale", 100.ToString()),
                                ResizeHeightBeforeUpscale = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_model_{currentModelNumber}_resize_height_before_upscale", 0.ToString())
                            };
                        }
                    }
                }

                foreach (var currentChainNumber in models.Keys)
                {
                    chains[currentChainNumber].Models = new AvaloniaList<UpscaleModel>(models[currentChainNumber].Values.ToList());
                }

                CurrentSlot.Vm = this;
                CurrentSlot.ProfileName = parser.GetValue(sectionKey, "profile_name", AnimeJaNai.Localization.UiText.T("New Profile"));
                CurrentSlot.Chains = new AvaloniaList<UpscaleChain>(chains.Values.ToList());
            }
        }

        static bool ParseBool(string value)
        {
            return value?.ToLower() == "yes";
        }

        public void WriteAnimeJaNaiConf()
        {
            WriteAnimeJaNaiConf(AnimeJaNaiConfPath);
        }

        public void WriteAnimeJaNaiConf(string fullPath)
        {
            var parser = ParsedAnimeJaNaiConf(AnimeJaNaiConf);
            parser?.Save(fullPath);
        }

        public static ConfigParser? ParsedAnimeJaNaiConf(AnimeJaNaiConf conf)
        {
            if (conf is null)
            {
                return null;
            }

            var parser = new ConfigParser();

            parser.SetValue("global", "config_version", CONFIG_VERSION.ToString());
            parser.SetValue("global", "backend", conf.SelectedBackend.ToString());
            parser.SetValue("global", "backend_auto_fallback", conf.BackendAutoFallback ? "yes" : "no");
            parser.SetValue("global", "logging", conf.EnableLogging ? "yes" : "no");
            // Write-minimal: only persist default_slot when the user picked one (absent => the vf
            // line's configured startup slot applies).
            if (conf.DefaultSlot is int defaultSlot)
            {
                parser.SetValue("global", "default_slot", defaultSlot.ToString(ENGLISH_CULTURE));
            }
            // Write-minimal: only persist the subtitle mode when the experimental GPU path is
            // enabled (absent => the stable [animejanai] subtitle defaults apply).
            if (conf.GpuSubtitles)
            {
                parser.SetValue("global", "sub_render_mode", SUB_RENDER_MODE_GPU);
            }
            // Write-minimal: only persist trt_engine_settings when it differs from the current
            // default, so future default changes apply automatically to users who didn't customize.
            if (conf.TrtEngineSettings != DEFAULT_TRT_ENGINE_SETTINGS)
            {
                parser.SetValue("global", "trt_engine_settings", conf.TrtEngineSettings);
            }
            foreach (var profile in conf.UpscaleSlots)
            {
                var section = $"slot_{profile.SlotNumber}";
                parser.SetValue(section, "profile_name", profile.ProfileName);

                foreach (var chain in profile.Chains)
                {
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_min_resolution", string.Create(ENGLISH_CULTURE, $"{chain.MinResolution}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_max_resolution", string.Create(ENGLISH_CULTURE, $"{chain.MaxResolution}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_min_fps", string.Create(ENGLISH_CULTURE, $"{chain.MinFps}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_max_fps", string.Create(ENGLISH_CULTURE, $"{chain.MaxFps}"));

                    foreach (var model in chain.Models)
                    {
                        parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_resize_height_before_upscale", string.Create(ENGLISH_CULTURE, $"{model.ResizeHeightBeforeUpscale}"));
                        parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_resize_factor_before_upscale", string.Create(ENGLISH_CULTURE, $"{model.ResizeFactorBeforeUpscale}"));
                        parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_name", string.Create(ENGLISH_CULTURE, $"{model.Name}"));
                    }

                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife", chain.EnableRife ? "yes" : "no");
                    chain.RifeModel ??= chain.RifeModelList.FirstOrDefault("");

                    var rifeModel = RifeLabelToValue(chain.RifeModel);

                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_model", rifeModel);
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_factor_numerator", string.Create(ENGLISH_CULTURE, $"{chain.RifeFactorNumerator ?? 1}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_factor_denominator", string.Create(ENGLISH_CULTURE, $"{chain.RifeFactorDenominator ?? 1}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_scene_detect_threshold", string.Create(ENGLISH_CULTURE, $"{chain.RifeSceneDetectThreshold ?? 0.015M}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_ensemble", chain.RifeEnsemble ? "yes" : "no");
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_before_upscale", chain.RifeBeforeUpscale ? "yes" : "no");
                }
            }

            return parser;
        }

        public void WriteAnimeJaNaiCurrentProfileConf(string fullPath)
        {
            var parser = ParsedAnimeJaNaiProfileConf(CurrentSlot);
            parser.Save(fullPath);
        }

        public ConfigParser ParsedAnimeJaNaiProfileConf(UpscaleSlot slot)
        {
            var parser = new ConfigParser();
            var section = "slot";

            parser.SetValue(section, "profile_name", slot.ProfileName);
            foreach (var chain in slot.Chains)
            {
                parser.SetValue(section, $"chain_{chain.ChainNumber}_min_resolution", string.Create(ENGLISH_CULTURE, $"{chain.MinResolution}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_max_resolution", string.Create(ENGLISH_CULTURE, $"{chain.MaxResolution}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_min_fps", string.Create(ENGLISH_CULTURE, $"{chain.MinFps}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_max_fps", string.Create(ENGLISH_CULTURE, $"{chain.MaxFps}"));

                foreach (var model in chain.Models)
                {
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_resize_height_before_upscale", string.Create(ENGLISH_CULTURE, $"{model.ResizeHeightBeforeUpscale}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_resize_factor_before_upscale", string.Create(ENGLISH_CULTURE, $"{model.ResizeFactorBeforeUpscale}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_name", string.Create(ENGLISH_CULTURE, $"{model.Name}"));
                }

                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife", chain.EnableRife ? "yes" : "no");
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_factor_numerator", string.Create(ENGLISH_CULTURE, $"{chain.RifeFactorNumerator ?? 0}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_factor_denominator", string.Create(ENGLISH_CULTURE, $"{chain.RifeFactorDenominator ?? 1}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_model", string.Create(ENGLISH_CULTURE, $"{RifeLabelToValue(chain.RifeModel)}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_ensemble", chain.RifeEnsemble ? "yes" : "no");
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_scene_detect_threshold", string.Create(ENGLISH_CULTURE, $"{chain.RifeSceneDetectThreshold ?? 0.015M}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_before_upscale", chain.RifeBeforeUpscale ? "yes" : "no");
            }

            return parser;
        }

        public void CheckAndDoBackup()
        {
            Task.Run(() =>
            {
                if (!Path.Exists(BackupPath))
                {
                    Directory.CreateDirectory(BackupPath);
                }

                var files = Directory.EnumerateFiles(BackupPath)
                            .Where(f => Path.GetFileName(f).StartsWith("autobackup_"))
                            .OrderByDescending(f => f)
                            .ToList();

                var currentConfStr = ParsedAnimeJaNaiConf(AnimeJaNaiConf)?.ToString();

                if (files.Count >= 10)
                {
                    // Delete oldest backup
                    File.Delete(files.Last());
                }

                if (files.Count > 0)
                {
                    var backupConf = ReadAnimeJaNaiConf(files.First());
                    if (currentConfStr == ParsedAnimeJaNaiConf(backupConf)?.ToString())
                    {
                        // Backup already exists - no need to create another backup
                        return;
                    }
                }

                WriteAnimeJaNaiConf(Path.Join(BackupPath, $"autobackup_{DateTime.Now:yyyyMMdd-HHmmss}.conf"));
            });
        }

#pragma warning disable CA1822 // Mark members as static
        public async void OpenModelsDirectory()
#pragma warning restore CA1822 // Mark members as static
        {
            await Task.Run(() =>
            {
                Process.Start("explorer.exe", OnnxPath);
            });
        }

        public void SelectDefaultProfile()
        {
            if (SelectedMpvProfile == CurrentSlot.MpvProfileName)
            {
                // Toggling off the current default leaves the player upscaling-off by default.
                SelectedMpvProfile = null;
                WriteDefaultProfile(true);
            }
            else
            {
                SelectedMpvProfile = CurrentSlot.MpvProfileName;
                WriteDefaultProfile();
            }
        }

        // Map custom profile names to the native filter slot; Off is slot 0.
        private static int? MpvProfileToSlot(string? mpvProfile)
        {
            if (string.IsNullOrEmpty(mpvProfile))
            {
                return null;
            }
            switch (mpvProfile)
            {
                case "upscale-off": return 0;
            }
            var m = Regex.Match(mpvProfile, @"^upscale-on-(\d+)$");
            return m.Success ? int.Parse(m.Groups[1].Value, ENGLISH_CULTURE) : (int?)null;
        }

        private static string? SlotToMpvProfile(int? slot)
        {
            switch (slot)
            {
                case null: return null;
                case 0: return "upscale-off";
            }
            return slot.Value is >= 1 and <= 9 ? $"upscale-on-{slot.Value}" : null;
        }

        private string? ReadCurrentProfileFromMpvConf() => SlotToMpvProfile(AnimeJaNaiConf?.DefaultSlot);

        private void WriteDefaultProfile(bool upscaleOff = false)
        {
            var profile = upscaleOff ? "upscale-off" : CurrentSlot.MpvProfileName;
            AnimeJaNaiConf.DefaultSlot = MpvProfileToSlot(profile);
            WriteAnimeJaNaiConf();
        }

        private void InitializeSelectedSlot()
        {
            if (SelectedMpvProfile == null || SelectedMpvProfile == "upscale-off")
            {
                return;
            }

            var customSlot = AnimeJaNaiConf.UpscaleSlots.Where(x => x.MpvProfileName == SelectedMpvProfile).FirstOrDefault();

            if (customSlot != null)
            {
                HandleShowCustomProfile(customSlot.SlotNumber);
            }
        }
    }

    [DataContract]
    public class AnimeJaNaiConf : ReactiveObject
    {
        public AnimeJaNaiConf(bool autoSave = false)
        {
            Debug.WriteLine($"autoSave? {autoSave}");
            if (autoSave)
            {
                this.WhenAnyValue(
                    x => x.EnableLogging,
                    x => x.TensorRtSelected,
                    x => x.DirectMlSelected,
                    x => x.BackendAutoFallback,
                    x => x.TrtEngineSettings).Subscribe(x =>
                    {
                        Vm?.WriteAnimeJaNaiConf();
                    });

                // Separate subscription: WhenAnyValue's tuple overload doesn't extend to this many
                // properties in one call.
                this.WhenAnyValue(
                    x => x.GpuSubtitles).Subscribe(x =>
                    {
                        Vm?.WriteAnimeJaNaiConf();
                    });

            }
        }

        private MainWindowViewModel? _vm;
        public MainWindowViewModel? Vm
        {
            get => _vm;
            set => this.RaiseAndSetIfChanged(ref _vm, value);
        }

        private bool _enableLogging = false;
        [DataMember]
        public bool EnableLogging
        {
            get => _enableLogging;
            set => this.RaiseAndSetIfChanged(ref _enableLogging, value);
        }

        // Startup slot: custom profiles 1-9 or Off (0).
        private int? _defaultSlot;
        public int? DefaultSlot
        {
            get => _defaultSlot;
            set => this.RaiseAndSetIfChanged(ref _defaultSlot, value);
        }

        private bool _tensorRtSelected = true;
        [DataMember]
        public bool TensorRtSelected
        {
            get => _tensorRtSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _tensorRtSelected, value);
            }
        }

        private bool _directMlSelected = false;
        [DataMember]
        public bool DirectMlSelected
        {
            get => _directMlSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _directMlSelected, value);
            }
        }

        private string _trtEngineSettings = "--builderOptimizationLevel=5 --optShapes=input:%video_resolution% --skipInference";
        [DataMember]
        public string TrtEngineSettings
        {
            get => _trtEngineSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _trtEngineSettings, value);
                this.RaisePropertyChanged(nameof(TrtStaticOnnxSelected));
                this.RaisePropertyChanged(nameof(TrtStaticSelected));
                this.RaisePropertyChanged(nameof(TrtDynamicSelected));
                this.RaisePropertyChanged(nameof(TrtDynamicMinResolution));
                this.RaisePropertyChanged(nameof(TrtDynamicOptResolution));
                this.RaisePropertyChanged(nameof(TrtDynamicMaxResolution));
                this.RaisePropertyChanged(nameof(TrtBuilderOptimizationLevel));
            }
        }

        public bool TrtDynamicSelected => TrtEngineSettings.Contains("--minShapes=");
        public bool TrtStaticSelected => TrtEngineSettings.Contains("--optShapes=") && !TrtDynamicSelected;
        public bool TrtStaticOnnxSelected => !TrtEngineSettings.Contains("--optShapes=") && !TrtDynamicSelected;

        private static string ShapeToResolution(string shapeArg)
        {
            var match = Regex.Match(shapeArg, @"input:1x3x(\d+)x(\d+)");
            if (match.Success)
                return $"{match.Groups[2].Value}x{match.Groups[1].Value}";
            return "0x0";
        }

        private static string ResolutionToShape(string resolution)
        {
            var match = Regex.Match(resolution, @"(\d+)x(\d+)");
            if (match.Success)
                return $"1x3x{match.Groups[2].Value}x{match.Groups[1].Value}";
            return "1x3x0x0";
        }

        private static string? ExtractShapeValue(string settings, string prefix)
        {
            var match = Regex.Match(settings, prefix + @"=(\S+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        public string TrtDynamicMinResolution
        {
            get
            {
                var val = ExtractShapeValue(TrtEngineSettings, "--minShapes");
                return val != null ? ShapeToResolution(val) : "8x8";
            }
            set
            {
                var shape = ResolutionToShape(value);
                TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--minShapes=\S+", $"--minShapes=input:{shape}");
            }
        }

        public string TrtDynamicOptResolution
        {
            get
            {
                if (!TrtDynamicSelected) return "1920x1080";
                var val = ExtractShapeValue(TrtEngineSettings, "--optShapes");
                return val != null ? ShapeToResolution(val) : "1920x1080";
            }
            set
            {
                var shape = ResolutionToShape(value);
                if (TrtDynamicSelected)
                    TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--optShapes=\S+", $"--optShapes=input:{shape}");
            }
        }

        public string TrtDynamicMaxResolution
        {
            get
            {
                var val = ExtractShapeValue(TrtEngineSettings, "--maxShapes");
                return val != null ? ShapeToResolution(val) : "1920x1080";
            }
            set
            {
                var shape = ResolutionToShape(value);
                TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--maxShapes=\S+", $"--maxShapes=input:{shape}");
            }
        }

        public string TrtBuilderOptimizationLevel
        {
            get
            {
                var match = Regex.Match(TrtEngineSettings, @"--builderOptimizationLevel=(\d+)");
                return match.Success ? match.Groups[1].Value : "5";
            }
            set
            {
                var match = Regex.Match(value ?? "", @"\d+");
                var level = match.Success ? match.Value : "5";
                if (Regex.IsMatch(TrtEngineSettings, @"--builderOptimizationLevel=\d+"))
                    TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--builderOptimizationLevel=\d+", $"--builderOptimizationLevel={level}");
                else
                    TrtEngineSettings = InsertBeforeTactics(TrtEngineSettings, $"--builderOptimizationLevel={level}");
            }
        }

        private static string RemoveShapeArgs(string settings)
        {
            var result = Regex.Replace(settings, @"\s*--(?:min|opt|max)Shapes=\S+", "");
            return Regex.Replace(result, @"\s+", " ").Trim();
        }

        private static string InsertBeforeTactics(string settings, string toInsert)
        {
            var idx = settings.IndexOf("--tacticSources");
            if (idx >= 0)
                return settings.Insert(idx, toInsert + " ");
            return settings + " " + toInsert;
        }

        public void SetTrtStaticOnnx()
        {
            TrtEngineSettings = RemoveShapeArgs(TrtEngineSettings);
        }

        public void SetTrtStatic()
        {
            var settings = RemoveShapeArgs(TrtEngineSettings);
            TrtEngineSettings = InsertBeforeTactics(settings, "--optShapes=input:%video_resolution%");
        }

        public void SetTrtDynamic()
        {
            var settings = RemoveShapeArgs(TrtEngineSettings);
            TrtEngineSettings = InsertBeforeTactics(settings, "--minShapes=input:1x3x8x8 --optShapes=input:1x3x1080x1920 --maxShapes=input:1x3x1080x1920");
        }

        private AvaloniaList<UpscaleSlot> _upscaleSlots = [];
        [DataMember]
        public AvaloniaList<UpscaleSlot> UpscaleSlots
        {
            get => _upscaleSlots;
            set
            {
                this.RaiseAndSetIfChanged(ref _upscaleSlots, value);
                Vm?.RaisePropertyChanged("AllSlots");
            }
        }

        public void SetTensorRtSelected()
        {
            TensorRtSelected = true;
            DirectMlSelected = false;
        }

        public void SetDirectMlSelected()
        {
            DirectMlSelected = true;
            TensorRtSelected = false;
        }

        public void UserSelectTensorRt()
        {
            BackendAutoFallback = false;
            SetTensorRtSelected();
        }

        public void UserSelectDirectMl()
        {
            BackendAutoFallback = false;
            SetDirectMlSelected();
        }

        private bool _backendAutoFallback;
        [DataMember]
        public bool BackendAutoFallback
        {
            get => _backendAutoFallback;
            set => this.RaiseAndSetIfChanged(ref _backendAutoFallback, value);
        }

        // Experimental GPU subtitle rendering ([global] sub_render_mode=gpu). Off => the player
        // uses the stable subtitle defaults from the managed [animejanai] profile.
        private bool _gpuSubtitles;
        [DataMember]
        public bool GpuSubtitles
        {
            get => _gpuSubtitles;
            set => this.RaiseAndSetIfChanged(ref _gpuSubtitles, value);
        }

        public Backend SelectedBackend => DirectMlSelected ? Backend.DirectML : Backend.TensorRT;
    }

    [DataContract]
    public class UpscaleSlot : ReactiveObject
    {
        public UpscaleSlot(bool autoSave = false)
        {
            if (autoSave)
            {
                this.WhenAnyValue(
                    x => x.SlotNumber,
                    x => x.ProfileName,
                    x => x.Chains.Count
                ).Subscribe(x =>
                {
                    Vm?.WriteAnimeJaNaiConf();
                });
            }

            this.WhenAnyValue(x => x.Vm).Subscribe(x =>
            {
                sub?.Dispose();
                sub = Vm.WhenAnyValue(
                    x => x.SelectedSlotNumber,
                    x => x.ShowCustomProfiles
                    ).Subscribe(x =>
                {
                    this.RaisePropertyChanged(nameof(ActiveSlot));
                    Vm?.RaisePropertyChanged("AllSlots");
                });

                sub2?.Dispose();
                sub2 = Vm.WhenAnyValue(x => x.SelectedMpvProfile).Subscribe(x =>
                {
                    this.RaisePropertyChanged(nameof(IsSelectedMpvProfile));
                });
            });
        }

        private IDisposable? sub;
        private IDisposable? sub2;

        private MainWindowViewModel? _vm;
        public MainWindowViewModel? Vm
        {
            get => _vm;
            set => this.RaiseAndSetIfChanged(ref _vm, value);
        }

        private string _slotNumber = string.Empty;
        [DataMember]
        public string SlotNumber
        {
            get => _slotNumber;
            set => this.RaiseAndSetIfChanged(ref _slotNumber, value);
        }

        public bool ActiveSlot => (Vm?.ShowCustomProfiles ?? false) && SlotNumber == Vm?.SelectedSlotNumber;

        public bool IsCustomSlot => MpvProfileName?.Any(char.IsDigit) ?? false;

        public string SlotIcon => $"Number{SlotNumber}Circle";

        public bool ShowSlot => int.Parse(_slotNumber) < 10;

        private string _profileName = string.Empty;
        [DataMember]
        public string ProfileName
        {
            get => _profileName;
            set => this.RaiseAndSetIfChanged(ref _profileName, value);
        }

        private string _descriptionText = string.Empty;
        [DataMember]
        public string DescriptionText
        {
            get => _descriptionText;
            set => this.RaiseAndSetIfChanged(ref _descriptionText, value);
        }

        private string _mpvProfileName;
        [DataMember]
        public string MpvProfileName
        {
            get => _mpvProfileName;
            set => this.RaiseAndSetIfChanged(ref _mpvProfileName, value);
        }

        public bool IsSelectedMpvProfile => MpvProfileName == Vm?.SelectedMpvProfile;

        private AvaloniaList<UpscaleChain> _chains = [];
        [DataMember]
        public AvaloniaList<UpscaleChain> Chains
        {
            get => _chains;
            set => this.RaiseAndSetIfChanged(ref _chains, value);
        }
    }

    [DataContract]
    public class UpscaleChain : ReactiveObject
    {
        public UpscaleChain(bool autoSave = false)
        {
            AutoSave = autoSave;

            if (autoSave)
            {
                var g1 = this.WhenAnyValue(
                    x => x.ChainNumber,
                    x => x.MinResolution,
                    x => x.MaxResolution,
                    x => x.MinFps,
                    x => x.MaxFps,
                    x => x.Models.Count,
                    x => x.EnableRife
                );

                var g2 = this.WhenAnyValue(
                    x => x.RifeFactorNumerator,
                    x => x.RifeFactorDenominator,
                    x => x.RifeModel,
                    x => x.RifeSceneDetectThreshold,
                    x => x.RifeEnsemble,
                    x => x.RifeBeforeUpscale
                );

                g1.CombineLatest(g2).Subscribe(x =>
                {
                    Vm?.WriteAnimeJaNaiConf();
                });
            }
        }

        public bool AutoSave { get; set; }

        public MainWindowViewModel? Vm { get; set; }

        private string _chainNumber = string.Empty;
        [DataMember]
        public string ChainNumber
        {
            get => _chainNumber;
            set => this.RaiseAndSetIfChanged(ref _chainNumber, value);
        }

        private string _minResolution = "0x0";
        [DataMember]
        public string MinResolution
        {
            get => _minResolution;
            set => this.RaiseAndSetIfChanged(ref _minResolution, value);
        }

        private string _maxResolution = "0x0";
        [DataMember]
        public string MaxResolution
        {
            get => _maxResolution;
            set => this.RaiseAndSetIfChanged(ref _maxResolution, value);
        }

        private string _minFps = 0.ToString();
        [DataMember]
        public string MinFps
        {
            get => _minFps;
            set => this.RaiseAndSetIfChanged(ref _minFps, value);
        }

        private string _maxFps = 0.ToString();
        [DataMember]
        public string MaxFps
        {
            get => _maxFps;
            set => this.RaiseAndSetIfChanged(ref _maxFps, value);
        }

        private AvaloniaList<UpscaleModel> _models = [];
        [DataMember]
        public AvaloniaList<UpscaleModel> Models
        {
            get => _models;
            set => this.RaiseAndSetIfChanged(ref _models, value);
        }

        private bool _enableRife = false;
        [DataMember]
        public bool EnableRife
        {
            get => _enableRife;
            set
            {
                this.RaiseAndSetIfChanged(ref _enableRife, value);
                this.RaisePropertyChanged(nameof(RifeToggleEnabled));
            }
        }

        // Enabling RIFE needs the models; disabling it must always be possible.
        // So the toggle locks only in the unchecked-and-not-installed state.
        public bool RifeToggleEnabled =>
            EnableRife || !(MainWindowViewModel.Instance?.RifeMissing ?? false);

        private List<string> _rifeModelList = new(MainWindowViewModel.RifeModels);

        public List<string> RifeModelList
        {
            get => _rifeModelList;
            set => this.RaiseAndSetIfChanged(ref _rifeModelList, value);
        }

        private string _rifeModel = MainWindowViewModel.RifeModels.FirstOrDefault("");
        [DataMember]
        public string RifeModel
        {
            get => _rifeModel;
            set => this.RaiseAndSetIfChanged(ref _rifeModel, value);
        }

        private bool _rifeEnsemble = false;
        [DataMember]
        public bool RifeEnsemble
        {
            get => _rifeEnsemble;
            set => this.RaiseAndSetIfChanged(ref _rifeEnsemble, value);
        }

        private int? _rifeFactorNumerator = 2;
        [DataMember]
        public int? RifeFactorNumerator
        {
            get => _rifeFactorNumerator ?? 2;
            set => this.RaiseAndSetIfChanged(ref _rifeFactorNumerator, value ?? 2);
        }

        private int? _rifeFactorDenominator = 1;
        [DataMember]
        public int? RifeFactorDenominator
        {
            get => _rifeFactorDenominator ?? 1;
            set => this.RaiseAndSetIfChanged(ref _rifeFactorDenominator, value ?? 1);
        }

        private decimal? _rifeSceneDetectThreshold = 0.150M;
        [DataMember]
        public decimal? RifeSceneDetectThreshold
        {
            get => _rifeSceneDetectThreshold;
            set => this.RaiseAndSetIfChanged(ref _rifeSceneDetectThreshold, value ?? 0.150M);
        }

        // Interpolate at the source resolution, then upscale (faster); default
        // on. False = interpolate the upscaled frames (the old order).
        private bool _rifeBeforeUpscale = true;
        [DataMember]
        public bool RifeBeforeUpscale
        {
            get => _rifeBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _rifeBeforeUpscale, value);
        }

        public void AddModel()
        {
            Models.Add(new UpscaleModel(AutoSave)
            {
                Vm = Vm,
                AllModels = Vm?.GetAllModels(),
            });

            UpdateModelHeaders();
        }

        public void DeleteModel(UpscaleModel model)
        {
            try
            {
                Models.Remove(model);
            }
            catch (ArgumentOutOfRangeException)
            {

            }

            UpdateModelHeaders();
        }

        public void UpdateModelHeaders()
        {
            for (var i = 0; i < Models.Count; i++)
            {
                Models[i].ModelNumber = (i + 1).ToString();
            }
        }

    }

    [DataContract]
    public class UpscaleModel : ReactiveObject
    {
        public UpscaleModel(bool autoSave = false)
        {
            if (autoSave)
            {
                this.WhenAnyValue(
                    x => x.ModelNumber,
                    x => x.ResizeHeightBeforeUpscale,
                    x => x.ResizeFactorBeforeUpscale,
                    x => x.Name
                ).Subscribe(x =>
                {
                    Vm?.WriteAnimeJaNaiConf();
                });
            }
        }

        public MainWindowViewModel? Vm { get; set; }

        private AvaloniaList<string> _allModels = [];
        [DataMember]
        public AvaloniaList<string> AllModels
        {
            get => _allModels;
            set => this.RaiseAndSetIfChanged(ref _allModels, value);
        }

        private string _modelNumber = string.Empty;
        [DataMember]
        public string ModelNumber
        {
            get => _modelNumber;
            set => this.RaiseAndSetIfChanged(ref _modelNumber, value);
        }

        private string _resizeHeightBeforeUpscale = 0.ToString();
        [DataMember]
        public string ResizeHeightBeforeUpscale
        {
            get => _resizeHeightBeforeUpscale;
            set
            {
                this.RaiseAndSetIfChanged(ref _resizeHeightBeforeUpscale, value);
                this.RaisePropertyChanged(nameof(ResizeFactorBeforeUpscaleIsEnabled));
            }
        }

        private string _resizeFactorBeforeUpscale = 100.ToString();
        [DataMember]
        public string ResizeFactorBeforeUpscale
        {
            get => _resizeFactorBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _resizeFactorBeforeUpscale, value);
        }

        public bool ResizeFactorBeforeUpscaleIsEnabled => ResizeHeightBeforeUpscale == 0.ToString();

        private string _name = string.Empty;
        [DataMember]
        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }
    }

    public enum Backend
    {
        TensorRT,
        DirectML,
        NCNN
    }
}