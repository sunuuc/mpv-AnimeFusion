using AnimeJaNaiConfEditor.ViewModels;
using AnimeJaNaiConfEditor.Views;
using Avalonia;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Controls;
using ReactiveUI.Avalonia;
using System.Text.Json;
using AnimeJaNai.Localization;
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
Check(window.GetLogicalDescendants().OfType<TabItem>().Count()==3,"About is the third manager tab");
using(var catalog=JsonDocument.Parse(File.ReadAllText("tools/standalone/component-catalog.json")))
{
    InterfaceLanguage.SettingsPath=Path.Combine(fixture,"interface-language.json");
    foreach(var language in new[]{"zh-CN","en"})
    {
        InterfaceLanguage.Save(language);InterfaceLanguage.Reload();
        var items=catalog.RootElement.GetProperty("packs").EnumerateArray().Select(p=>new ComponentItem
        {
            Name=p.GetProperty("name").GetString()!,
            CatalogTitle=p.GetProperty("title").GetString(),
            CatalogDescription=p.GetProperty("description").GetString()
        }).ToArray();
        Check(items.Select(i=>i.Title).Distinct().Count()==items.Length && items.All(i=>i.Description.Length>0),"component titles are distinct and descriptions present: "+language);
        Check(items.Single(i=>i.Name=="trt-sm100").Title.Contains("B200") && items.Single(i=>i.Name=="trt-sm120").Title.Contains("RTX 50"),"Blackwell data-center and consumer kernels remain distinct: "+language);
    }
    InterfaceLanguage.Save("zh-CN");InterfaceLanguage.Reload();
}
var picker=vm.ComponentManager;
picker.Packs.Add(new ComponentItem{Name="optional",Recommended=true});
picker.Packs.Add(new ComponentItem{Name="installed",Installed=true,Selected=true});
picker.SelectRecommended();Check(picker.Packs.All(p=>p.Selected),"recommended selection preserves installed components");
picker.ClearSelection();Check(!picker.Packs[0].Selected && picker.Packs[1].Selected,"reset selection preserves installed components");
Directory.CreateDirectory(Path.Combine(fixture,"onnx"));
File.WriteAllText(Path.Combine(fixture,"onnx","new-model.onnx"),"fixture");
vm.RefreshComponentAwareness();
Check(vm.AnimeJaNaiConf.UpscaleSlots.SelectMany(s=>s.Chains).SelectMany(c=>c.Models).All(m=>m.AllModels.Contains("new-model")),"downloaded models refresh existing profile pickers");
Check(names.SequenceEqual(vm.AnimeJaNaiConf.UpscaleSlots.Select(s=>s.ProfileName)),"constructing the view preserves custom profile names");
vm.HandleShowCustomProfile("2");vm.SelectDefaultProfile();
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
vm.WriteAnimeJaNaiConf();
var saved=File.ReadAllText(Path.Combine(fixture,"animejanai.conf"));
Check(!saved.Contains("quality_preset") && !saved.Contains("balanced_preset") && !saved.Contains("performance_preset"),"manager writes only supported profile configuration");
Console.WriteLine("PASS manager profile checks without desktop interaction");
