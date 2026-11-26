using AnimeJaNaiConfEditor.ViewModels;
using AnimeJaNaiConfEditor.Views;
using Avalonia;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Controls;
using ReactiveUI.Avalonia;
using System.Text.Json;
using AnimeJaNai.Localization;
using AnimeJaNai.Components;
if(args.Length!=1){Console.Error.WriteLine("Usage: ManagerProfiles <empty fixture directory>");Environment.ExitCode=2;return;}
var fixture=Path.GetFullPath(args[0]);Directory.CreateDirectory(fixture);
Directory.CreateDirectory(Path.Combine(fixture,"onnx"));Directory.CreateDirectory(Path.Combine(fixture,"rife"));
Environment.SetEnvironmentVariable("ANIMEJANAI_DATA_DIR",fixture);
Environment.SetEnvironmentVariable("ANIMEJANAI_ROOT",fixture);
File.Copy("animejanai/animejanai.conf",Path.Combine(fixture,"animejanai.conf"),true);
void Check(bool valid,string label){if(!valid)throw new Exception(label);Console.WriteLine("PASS "+label);}
AppBuilder.Configure<AnimeJaNaiConfEditor.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseReactiveUI(_=>{}).SetupWithoutStarting();
Directory.Delete(Path.Combine(fixture,"onnx"));
Directory.Delete(Path.Combine(fixture,"rife"));
var vm=new MainWindowViewModel();
Check(vm.GetAllModels().Count==0,"manager supports a fresh install without model directories");
Check(typeof(MainWindowViewModel).GetProperty("DefaultUpscaleSlots")==null && typeof(MainWindowViewModel).GetMethod("HandleShowDefaultProfile")==null,"builtin preset model and actions removed");
foreach(var key in new[]{"QualitySharp","BalancedSharp","PerformanceSharp"})Check(typeof(AnimeJaNaiConf).GetProperty(key)==null,"obsolete preset field removed: "+key);
Check(vm.AnimeJaNaiConf.UpscaleSlots.Count==9 && vm.CommonResolutions.Length>0,"custom profiles and resolution choices preserved");
var names=vm.AnimeJaNaiConf.UpscaleSlots.Select(s=>s.ProfileName).ToArray();
var window=new MainWindow {DataContext=vm};
window.Measure(new Size(1100,800));window.Arrange(new Rect(0,0,1100,800));
Check(typeof(MainWindow).BaseType==typeof(Window) && !window.ExtendClientAreaToDecorationsHint && window.WindowDecorations==WindowDecorations.Full,"manager uses the native movable and resizable window frame");
Check(window.GetLogicalDescendants().OfType<TabItem>().Count()==4,"Playback history is followed by About in the manager");
using(var catalog=JsonDocument.Parse(File.ReadAllText("tools/standalone/component-catalog.json")))
{
    InterfaceLanguage.SettingsPath=Path.Combine(fixture,"interface-language.json");
    foreach(var language in new[]{"zh-CN","en"})
    {
        InterfaceLanguage.Save(language);InterfaceLanguage.Reload();
        var items=catalog.RootElement.GetProperty("packs").EnumerateArray().Select(p=>new ComponentItem
        {
            Name=p.GetProperty("name").GetString()!,
            Bytes=p.GetProperty("bytes").GetInt64(),
            CatalogTitle=p.GetProperty("title").GetString(),
            CatalogDescription=p.GetProperty("description").GetString()
        }).ToArray();
        Check(items.Select(i=>i.Title).Distinct().Count()==items.Length && items.All(i=>i.Description.Length>0),"component titles are distinct and descriptions present: "+language);
        Check(items.All(item => item.Bytes > 0 && item.SizeText == (item.Bytes switch {
            < 1024 => $"{item.Bytes:N0} B",
            < 1048576 => $"{item.Bytes / 1024:N0} KB",
            _ => $"{item.Bytes / 1048576:N0} MB"
        })), "every catalog pack displays its download size: " + language);
        Check(items.Single(i=>i.Name=="trt-sm100").Title.Contains("B200") && items.Single(i=>i.Name=="trt-sm120").Title.Contains("RTX 50"),"Blackwell data-center and consumer kernels remain distinct: "+language);
    }
    InterfaceLanguage.Save("zh-CN");InterfaceLanguage.Reload();
}
var picker=vm.ComponentManager;
foreach (var (bytes, expected) in new (long, string)[]
{
    (0, "0 B"), (1, "1 B"), (1023, $"{1023:N0} B"), (1024, "1 KB"),
    (669466, "653 KB"), (668033, "652 KB"), (1048575, $"{1023:N0} KB"),
    (1048576, "1 MB"), (1048577, "1 MB"), (397410305, "379 MB")
})
{
    var pack = new ComponentItem { Bytes = bytes };
    Check(pack.SizeText == expected, $"component size {bytes} displays as {expected}");
}
foreach (var (received, total, expected) in new (long, long, string)[]
{
    (0, 10, "0 / 10 B"), (1, 10, "1 / 10 B"), (10, 10, "10 / 10 B"),
    (1, 1024, "1 B / 1 KB"), (1024, 1024, "1 / 1 KB"),
    (1, 669466, "1 B / 653 KB"), (1024, 669466, "1 / 653 KB"), (669466, 669466, "653 / 653 KB"),
    (1, 1048576, "1 B / 1 MB"), (1024, 1048576, "1 KB / 1 MB"), (1048576, 1048576, "1 / 1 MB"),
    (10485760, 397410305, "10 / 379 MB")
})
{
    Check(ComponentSize.FormatProgress(received, total) == expected, $"component progress {received}/{total} displays as {expected}");
}
picker.Packs.Add(new ComponentItem{Name="optional",Recommended=true});
picker.Packs.Add(new ComponentItem{Name="installed",Installed=true,Selected=true});
picker.SelectRecommended();Check(picker.Packs.All(p=>p.Selected),"recommended selection preserves installed components");
picker.ClearSelection();Check(!picker.Packs[0].Selected && picker.Packs[1].Selected,"reset selection preserves installed components");
var runtime = new ComponentItem { Name = "runtime", Installed = true, Selected = true };
var kernel = new ComponentItem { Name = "kernel", Requires = ["runtime"] };
var dependent = new ComponentItem { Name = "dependent", Requires = ["kernel"], Recommended = true };
picker.Packs.AddRange([dependent, kernel, runtime]);
picker.SelectRecommended();
Check(dependent.Selected && kernel.Selected && runtime.Selected, "recommended packs select transitive prerequisites regardless of catalog order");
runtime.Selected = false;
Check(!dependent.Selected && !kernel.Selected && !runtime.Selected && picker.Packs[1].Selected, "unchecking the runtime clears only its dependents");
dependent.Selected = true;
Check(kernel.Selected && runtime.Selected, "checking a dependent visibly selects all prerequisites");
kernel.Selected = false;
Check(runtime.Selected && !dependent.Selected, "unchecking a kernel preserves the runtime and clears its dependents");
picker.ClearSelection();
Check(runtime.Selected && !kernel.Selected && !dependent.Selected, "reset restores the installed set after dependency edits");
Directory.CreateDirectory(Path.Combine(fixture,"onnx"));
File.WriteAllText(Path.Combine(fixture,"onnx","new-model.onnx"),"fixture");
vm.HandleShowCustomProfile("2");vm.AddChain();
vm.RefreshComponentAwareness();
Check(vm.AnimeJaNaiConf.UpscaleSlots.SelectMany(s=>s.Chains).SelectMany(c=>c.Models).All(m=>m.AllModels.Contains("new-model")),"downloaded models refresh existing profile pickers");
Check(names.SequenceEqual(vm.AnimeJaNaiConf.UpscaleSlots.Select(s=>s.ProfileName)),"constructing the view preserves custom profile names");
vm.HandleShowCustomProfile("2");vm.SelectDefaultProfile();
vm.CurrentSlot.Chains[0].EnableRife=true;
window.Show();
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
var chain=vm.CurrentSlot.Chains[0];
var factorControl=window.GetLogicalDescendants().OfType<NumericUpDown>().First(c=>ReferenceEquals(c.DataContext,chain) && c.FormatString=="0" && c.Value==chain.RifeFactorNumerator && c.Minimum==chain.RifeFactorDenominator && c.Maximum==decimal.MaxValue);
factorControl.Value=2;
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
Check(chain.RifeFactorNumerator==2,"RIFE numerator control updates the selected chain");
Check(vm.ReadAnimeJaNaiConf(Path.Combine(fixture,"animejanai.conf")).UpscaleSlots.Single(s=>s.SlotNumber=="2").Chains[0].RifeFactorNumerator==2,"RIFE edit is immediately saved to the player configuration");
factorControl.Value=3;
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
factorControl.Text="2";
Avalonia.Threading.Dispatcher.UIThread.RunJobs();
Check(chain.RifeFactorNumerator==2,"typing a RIFE factor commits it without changing focus");
window.Close();
Check(typeof(MainWindowViewModel).GetMethod("LaunchBenchmark")==null,"builtin preset benchmark action removed");
Check(vm.AnimeJaNaiConf.DefaultSlot==2,"custom profile can be selected as startup default");
vm.AnimeJaNaiConf.GpuTensorRt = "test-device-uuid";
vm.AnimeJaNaiConf.GpuDirectMl = "pci:01:00.0";
vm.AnimeJaNaiConf.UseNativeKernels = false;
vm.AnimeJaNaiConf.ReuseHeldFrames = false;
// The manager must retain model and chain options in both complete profiles and slot exports.
File.WriteAllText(Path.Combine(fixture, "onnx", "temporalfix_s2_v1_op18_fp16.onnx"), "test model");
vm.RefreshComponentAwareness();
Check(!vm.GetAllModels().Any(name => name.StartsWith("temporalfix_")), "TemporalFix is excluded from regular upscalers");
vm.AnimeJaNaiConf.AutoDisplayRefresh = true;
chain.EnableUpscale = false;
chain.TemporalFixStrength = 2;
chain.EnableTemporalFix = true;
chain.TemporalFixBeforeUpscale = false;
chain.TemporalFixSceneThreshold = 0.27m;
chain.RifeEnhancedSceneDetection = true;
Check(!chain.CanInterpolateFirst, "temporal processing uses interpolation last");
vm.WriteAnimeJaNaiConf();
var reloaded = vm.ReadAnimeJaNaiConf(Path.Combine(fixture,"animejanai.conf"));
var savedChain = reloaded.UpscaleSlots.Single(x => x.SlotNumber == "2").Chains[0];
Check(reloaded.AutoDisplayRefresh && savedChain.EnableTemporalFix && !savedChain.EnableUpscale &&
      savedChain.TemporalFixStrength == 2 && !savedChain.TemporalFixBeforeUpscale &&
      savedChain.TemporalFixSceneThreshold == 0.27m && savedChain.RifeEnhancedSceneDetection, "display and temporal settings survive a full save");
var exportedSlot = Path.Combine(fixture, "exported.conf");
vm.WriteAnimeJaNaiCurrentProfileConf(exportedSlot);
vm.ReadAnimeJaNaiConfToCurrentSlot(exportedSlot);
Check(vm.CurrentSlot.Chains[0].EnableTemporalFix && vm.CurrentSlot.Chains[0].RifeEnhancedSceneDetection, "slot export and import retain temporal and RIFE settings");
Check(reloaded.GpuTensorRt == "test-device-uuid" && reloaded.GpuDirectMl == "pci:01:00.0", "GPU selection survives profile save per backend");
Check(!reloaded.UseNativeKernels && !reloaded.ReuseHeldFrames, "inference preferences survive profile save");
var saved=File.ReadAllText(Path.Combine(fixture,"animejanai.conf"));
Check(!saved.Contains("quality_preset") && !saved.Contains("balanced_preset") && !saved.Contains("performance_preset"),"manager writes only supported profile configuration");
var singlePath = Path.Combine(fixture, "single.pconf");
File.WriteAllText(singlePath, "[slot]\nprofile_name=Import\nchain_1_rife=yes\nchain_1_rife_factor_numerator=4\n");
vm.ReadAnimeJaNaiConfToCurrentSlot(singlePath);
var diskSlot = vm.ReadAnimeJaNaiConf(Path.Combine(fixture,"animejanai.conf")).UpscaleSlots.Single(s => s.SlotNumber == "2");
Check(diskSlot.ProfileName == "Import" && diskSlot.Chains.Single().RifeFactorNumerator == 4,
    "equal-count slot replacement is persisted");
var beforeImport = File.ReadAllBytes(vm.AnimeJaNaiConfPath);
try { vm.ImportFullConfiguration(new Salaros.Configuration.ConfigParser(singlePath)); throw new Exception("accepted a single profile"); }
catch (InvalidDataException) { }
Check(beforeImport.SequenceEqual(File.ReadAllBytes(vm.AnimeJaNaiConfPath)), "invalid full import preserves the original bytes");
var completePath = Path.Combine(fixture, "full.conf");
File.WriteAllText(completePath, "[global]\nbackend = vulkan\n[slot_2]\nchain_1_rife=yes\nchain_1_rife_ensemble=yes\n");
vm.ImportFullConfiguration(new Salaros.Configuration.ConfigParser(completePath));
var full = vm.ReadAnimeJaNaiConf(vm.AnimeJaNaiConfPath);
Check(full.VulkanSelected && full.UpscaleSlots.Single().MpvProfileName == "upscale-on-2" &&
      full.UpscaleSlots.Single().Chains.Single().RifeFactorNumerator == 1 &&
      full.UpscaleSlots.Single().Chains.Single().RifeModel == "RIFE 4.14", "Vulkan, numbered slot and native RIFE defaults round-trip");
Check(File.ReadAllText(vm.AnimeJaNaiConfPath).Contains("chain_1_rife_ensemble=yes"), "missing RIFE files preserve the configured variant");
var reopened = new MainWindowViewModel();
Check(reopened.CurrentSlot?.SlotNumber == "2", "reopening a sparse configuration selects its available slot");
reopened.AddChain();
Check(reopened.CurrentSlot!.Chains.Count == 2, "a sparse configuration remains editable after reopening");
File.WriteAllText(completePath, "[slot_1]\nchain_10_rife=yes\nchain_10_rife_factor_numerator=3\n" +
    "chain_2_rife=yes\nchain_2_rife_factor_numerator=2\nchain_2_model_10_name=Last\nchain_2_model_2_name=First\n");
vm.ImportFullConfiguration(new Salaros.Configuration.ConfigParser(completePath));
Check(vm.CurrentSlot.Chains.Select(c => c.ChainNumber).SequenceEqual(new[] { "2", "10" }) &&
    vm.CurrentSlot.Chains[0].Models.Select(m => m.Name).SequenceEqual(new[] { "First", "Last" }),
    "import follows native numeric priority for chains and models");
vm.AddChain();
var edited = vm.ReadAnimeJaNaiConf(vm.AnimeJaNaiConfPath).UpscaleSlots.Single().Chains;
Check(edited[0].RifeFactorNumerator == 2 && edited[1].RifeFactorNumerator == 3,
    "adding a chain preserves priority after importing out-of-order IDs");
Console.WriteLine("PASS manager profile checks without desktop interaction");
