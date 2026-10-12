using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnimeJaNai.Localization;
using MpvNet.Windows;
using MpvNet.Windows.WPF;
using MpvNet.Windows.UI;
class Test
{
 [STAThread] static void Main(string[] args)
 {
    var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
    var config=Path.Combine(root,"isolated-player");Directory.CreateDirectory(config);
    Environment.SetEnvironmentVariable("ANIMEVE_HOME",config);
    InterfaceLanguage.SettingsPath=Path.Combine(config,"interface-language.json");
    MpvNet.Translator.Current=new WpfTranslator();
    WpfApplication.Init();
    foreach(var lang in new[]{"zh-CN","en","system"})
    foreach(var mode in new[]{"always","never"})
    {
       InterfaceLanguage.Save(lang);InterfaceLanguage.Reload();bool zh=InterfaceLanguage.IsChinese;
       MpvNet.Global.App.Language=InterfaceLanguage.PlayerValue;
       MpvNet.Global.App.DarkMode=mode;Theme.Init();Theme.UpdateWpfColors();
       var tr=new WpfTranslator();
       Check(tr.Gettext("Settings")== (zh?"设置":"Settings"),"embedded player resource "+lang);
       Check(tr.Gettext("About mpv-AnimeFusion")==(zh?"关于 mpv-AnimeFusion":"About mpv-AnimeFusion"),"product about title "+lang);
       var window=new ConfWindow{Width=1040,Height=760};
       window.Show();window.UpdateLayout();window.Dispatcher.Invoke(()=>{},DispatcherPriority.Render);
       Check(window.Title==(zh?"设置":"Config Editor"),"actual settings window title "+lang);
       var dock=(DockPanel)window.Content;var top=(StackPanel)dock.Children[0];var row=(StackPanel)top.Children[0];var selector=(ComboBox)row.Children[1];
       Check(selector.SelectedIndex==Array.IndexOf(InterfaceLanguage.Choices,lang),"player selector "+lang);
       Check(((SolidColorBrush)selector.Foreground).Color!=((SolidColorBrush)selector.Background).Color,"readable selector contrast "+mode);
       string expected=new[]{"简体中文","English","跟随系统 / System"}[selector.SelectedIndex];
       Check((string)selector.SelectionBoxItem==expected,"selected language has visible content "+lang);
       var all=(List<Setting>)typeof(ConfWindow).GetField("_settings",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
       var vo=(OptionSetting)all.Single(s=>s.Name=="vo");
       Check(vo.Options.All(o=>o.Name!="info")&&vo.Default=="gpu","only valid video driver values; localization leaves raw values intact");
       var bitmap=new RenderTargetBitmap(1040,760,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
       var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
       using(var stream=File.Create(Path.Combine(root,"player-"+lang+"-"+mode+".png")))encoder.Save(stream);
       selector.SelectedIndex=zh?1:0;
       Check(InterfaceLanguage.Read()==(zh?"en":"zh-CN"),"player saves shared preference "+lang);
       window.Close();
       Check(!File.Exists(Path.Combine(config,"mpv.conf"))&&!File.Exists(Path.Combine(config,"mpv-AnimeFusion.conf")),"closing language settings leaves playback configs untouched "+lang);
    }
    Console.WriteLine("PASS Player language suite");
 }
 static void Check(bool ok,string what){if(!ok)throw new Exception(what);Console.WriteLine("PASS "+what);}
}
