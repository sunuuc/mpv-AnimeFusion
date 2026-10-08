using AnimeJaNai.Localization;
using AnimeJaNaiConfEditor;
using AnimeJaNaiConfEditor.Views;
using AnimeJaNaiConfEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using ReactiveUI.Avalonia;
using System.Diagnostics;
using System.Text.Json;
string evidence=Path.GetFullPath(args[0]);Directory.CreateDirectory(evidence);
if(args.Length==1)
{
    // Each launch has its own language and native window lifetime, just as a restart does.
    foreach(string language in new[]{"zh-CN","en","system"})
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(evidence);start.ArgumentList.Add(language);
        using var process=Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(120000)){process.Kill(true);throw new Exception("Language test timeout: "+language);}
        Console.Write(stdout.GetAwaiter().GetResult());Console.Write(stderr.GetAwaiter().GetResult());
        if(process.ExitCode!=0)throw new Exception("Language test failed: "+language+" / "+process.ExitCode);
    }
    Console.WriteLine("PASS Manager language suite");return;
}
string selected=args[1];
string dir=Path.Combine(evidence,"preference-test-"+selected);Directory.CreateDirectory(dir);
string fixture=Path.Combine(evidence,"isolated-manager-"+selected);Directory.CreateDirectory(fixture);
foreach(string folder in new[]{"onnx","rife","backups","portable_config","inference"})Directory.CreateDirectory(Path.Combine(fixture,folder));
File.Copy("manager/AnimeJaNaiConfEditor/animejanai.conf",Path.Combine(fixture,"animejanai.conf"),true);
Environment.SetEnvironmentVariable("ANIMEJANAI_DATA_DIR",fixture);
Environment.SetEnvironmentVariable("ANIMEJANAI_ROOT",fixture);
InterfaceLanguage.SettingsPath=Path.Combine(dir,"interface-language.json");
void Check(bool ok,string what){if(!ok)throw new Exception(what);Console.WriteLine("PASS "+what);}
Check(!File.Exists(Path.Combine(fixture,"AnimeVEUpdater.exe")),"test fixture cannot launch updater or network installer");
Check(InterfaceLanguage.Read()=="zh-CN","Chinese default without setting");
Check(InterfaceLanguage.Resolve("system","zh-TW")=="zh-CN"&&InterfaceLanguage.Resolve("system","en-US")=="en","system language resolution");
File.WriteAllText(InterfaceLanguage.SettingsPath,"{broken");
Check(InterfaceLanguage.Read()=="zh-CN","corrupt preference falls back to Chinese");
InterfaceLanguage.Save("en");Check(InterfaceLanguage.Read()=="en","persist English");
bool rejected=false;try{InterfaceLanguage.Save("bad");}catch(ArgumentException){rejected=true;}
Check(rejected&&InterfaceLanguage.Read()=="en","reject invalid selection without changing stored value");
using(var locked=new FileStream(InterfaceLanguage.SettingsPath,FileMode.Open,FileAccess.Read,FileShare.Read))
{rejected=false;try{InterfaceLanguage.Save("zh-CN");}catch(Exception e) when(e is IOException or UnauthorizedAccessException){rejected=true;}Check(rejected,"locked preference refuses replacement");}
Check(InterfaceLanguage.Read()=="en"&&!Directory.EnumerateFiles(dir,"*.tmp").Any(),"failed save retains old preference and removes temporary file");
AppBuilder.Configure<AnimeJaNaiConfEditor.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().UseReactiveUI(_=>{}).SetupWithoutStarting();
InterfaceLanguage.Save(selected);InterfaceLanguage.Reload();bool zh=InterfaceLanguage.IsChinese;
Check(UiText.T("Profiles")== (zh?"配置方案":"Profiles"),"embedded resource "+selected);
Check(UiText.F($"Remove Chain {7}")==(zh?"移除处理链 7":"Remove Chain 7"),"dynamic format "+selected);
var vm=new MainWindowViewModel();vm.HandleShowGlobalSettings();
string[] raw=vm.AnimeJaNaiConf.UpscaleSlots.Select(s=>s.ProfileName).ToArray();
Check(typeof(MainWindowViewModel).GetProperty("DefaultUpscaleSlots")==null,
      "builtin default presets removed from the manager model");
var w=new MainWindow {Width=1100,Height=800,DataContext=vm};w.Show();Dispatcher.UIThread.RunJobs();
Check(w.Title==(zh?"AnimeVE 管理器":"AnimeVE Manager"),"product window title "+selected);
Check(raw.SequenceEqual(vm.AnimeJaNaiConf.UpscaleSlots.Select(s=>s.ProfileName)),"custom profile names preserved");
var tabs=w.GetLogicalDescendants().OfType<TabItem>().Select(t=>t.Header?.ToString()).ToArray();
Check(tabs.Contains(zh?"配置方案":"Profiles")&&tabs.Contains(zh?"组件":"Components")&&tabs.Last()==(zh?"关于":"About"),"constructed tab headers "+selected);
var selector=w.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="InterfaceLanguageSelector");
Check(selector.SelectedIndex==Array.IndexOf(InterfaceLanguage.Choices,selected),"selector reflects persisted setting "+selected);
foreach(string page in new[]{"global","profile","components","about"})
{
    vm.SelectedTabIndex=page=="about"?2:page=="components"?1:0;
    if(page=="global")vm.HandleShowGlobalSettings();
    if(page=="profile")vm.HandleShowCustomProfile(vm.AnimeJaNaiConf.UpscaleSlots.First().SlotNumber);
    Dispatcher.UIThread.RunJobs();
    using(var bitmap=new RenderTargetBitmap(new PixelSize(1100,800))){bitmap.Render(w);bitmap.Save(Path.Combine(evidence,"manager-"+selected+"-"+page+".png"));}
    string text=string.Join("\n",w.GetVisualDescendants().OfType<Control>().Select(c=>c switch{TextBlock t=>t.Text??t.Inlines?.Text,ContentControl t=>t.Content is string s?s:null,_=>null}).Where(s=>s!=null));
    File.WriteAllText(Path.Combine(evidence,"manager-"+selected+"-"+page+".txt"),text);
    if(page=="about")
        Check(text.Contains(vm.ProductVersion) && text.Contains("sunuuc/mpv-AnimeFusion") &&
              text.Contains("the-database/mpv-AnimeJaNai") &&
              text.Contains(UiText.T("mpv-AnimeFusion: player interface, danmaku and component download management. Source code and issues:")) &&
              text.Contains(UiText.T("Based on mpv-AnimeJaNai: AI upscaling and RIFE processing chains, models and GPU components.")) &&
              w.GetVisualDescendants().OfType<Button>().Select(b=>b.Tag?.ToString()).Contains("docs/LICENSE") &&
              w.GetVisualDescendants().OfType<Button>().Select(b=>b.Tag?.ToString()).Contains("docs/OPEN_SOURCE_NOTICES.md") &&
              w.GetVisualDescendants().OfType<Button>().Select(b=>b.Tag?.ToString()).Contains("docs/THIRD_PARTY_LICENSES") &&
              !text.Contains("CC BY-NC-SA 4.0"),
              "About displays version, source repositories and license links without the removed summary "+selected);
    Check(!w.GetLogicalDescendants().OfType<ItemsControl>().Any(c=>c.ItemsSource?.GetType().Name=="DefaultUpscaleSlots"),"manager uses only custom profiles");
}
selector.SelectedIndex=zh?1:0;Dispatcher.UIThread.RunJobs();
Check(InterfaceLanguage.Read()==(zh?"en":"zh-CN"),"selector saves correct stable language ID "+selected);
Check(InterfaceLanguage.IsChinese==zh,"selection does not partially relocalize current window "+selected);
w.Close();Dispatcher.UIThread.RunJobs();
var targetVm=new MainWindowViewModel();targetVm.RefreshComponentAwareness();
Check(targetVm.DirectMlAvailable && targetVm.AnimeJaNaiConf.DirectMlSelected,
      "core offers DirectML without downloading TensorRT");
targetVm.AnimeJaNaiConf.UserSelectDirectMl();
Check(targetVm.AnimeJaNaiConf.DirectMlSelected, "DirectML remains selectable");
Console.WriteLine("PASS Manager language process "+selected);
