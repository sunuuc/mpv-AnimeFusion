using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using MpvNet;
using MpvNet.Native;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;

string native=Environment.GetEnvironmentVariable("ANIMEFUSION_NATIVE_DIRECTORY")??throw new Exception("Native runtime path required");
string video=Environment.GetEnvironmentVariable("ANIMEFUSION_TEST_VIDEO")??throw new Exception("Generated test video required");
if(!Path.IsPathFullyQualified(video)||!File.Exists(video))throw new Exception("The generated test video must be an existing local file");
NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly,(name,_,_)=>File.Exists(Path.Combine(native,name))?NativeLibrary.Load(Path.Combine(native,name)):IntPtr.Zero);
string temp=Path.Combine(Path.GetTempPath(),"AnimeFusion-NativeSubtitles-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var library=new SubtitleLibrary(Path.Combine(temp,"Subtitles"));
int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
var result=new SubtitleResult(SubtitleSource.Assrt,"1","测试作品 第二季","简繁","",[]){Season=2,LanguageCode="zh"};
string sc="",tc="";
foreach(int episode in new[]{1,2})
foreach(string variant in new[]{"sc","tc"})
{
 var query=new SubtitleQuery("测试作品",2,episode,"zh");
 string name=$"[Group] Fixture [{episode:00}].{variant}.srt";
 string path=library.FilePath(query,result,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
 File.WriteAllText(path,"1\n00:00:00,000 --> 00:00:50,000\n"+variant+episode+"\n",Encoding.UTF8);
 library.Save(query,result,new(SubtitleSource.Assrt,"1",0,name,""),path,true);
 if(episode==1){if(variant=="sc")sc=path;else tc=path;}
}
library.Remember("测试作品",2,sc);
string supplied=Path.Combine(temp,"specified.srt");File.WriteAllText(supplied,"1\n00:00:00,000 --> 00:00:50,000\nspecified\n",Encoding.UTF8);
var player=Global.Player;Global.App.AutoLoadFolder=false;
nint core=LibMpv.mpv_create();
byte[] Bytes(string value)=>Encoding.UTF8.GetBytes(value+"\0");
foreach(var option in new Dictionary<string,string>{{"config","no"},{"load-scripts","no"},{"vo","null"},{"ao","null"},{"idle","yes"},{"force-window","no"},{"keep-open","yes"},{"sub-auto","no"},{"resume-playback","no"},{"watch-later-directory",Path.Combine(temp,"watch-later")}})
 Check(LibMpv.mpv_set_option_string(core,Bytes(option.Key),Bytes(option.Value))>=0,"Native option accepted");
if(CommandLine.Contains("sid")){player.Handle=core;CommandLine.ProcessCommandLineArgsPreInit();}
Check((int)LibMpv.mpv_initialize(core)>=0,"Native initialized without a desktop window");
player.MainHandle=core;player.Handle=LibMpv.mpv_create_client(core,"subtitle-library-check");
SubtitlePlayback.Initialize(library);
TaskCompletionSource loaded=null;
player.FileLoaded+=()=>loaded?.TrySetResult();
int fileStarts=0;
player.StartFile+=()=>Interlocked.Increment(ref fileStarts);
var events=Task.Run(()=>player.EventLoop());
var mainEvents=Task.Run(()=>player.MainEventLoop());
async Task Open(string title,string options="",string path=null)
{
 loaded=new(TaskCreationOptions.RunContinuationsAsynchronously);
 player.CommandV("loadfile",path??video,"replace","-1","force-media-title="+title+(options.Length>0?","+options:""));
 await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
}
async Task Drop(string path)
{
 loaded=new(TaskCreationOptions.RunContinuationsAsynchronously);
 player.LoadFiles([path],false,false);
 await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
}
string Selected()=>player.GetPropertyString("current-tracks/sub/external-filename");
SubtitleTitles CurrentTitles()=>new(TraktMedia.Parse(player.GetPropertyString("media-title"),player.GetPropertyString("path"),new Dictionary<string,string>()),default);
int SubtitleCount()=>Enumerable.Range(0,player.GetPropertyInt("track-list/count")).Count(i=>player.GetPropertyString($"track-list/{i}/type")=="sub");
string TrackId(string path)=>Enumerable.Range(0,player.GetPropertyInt("track-list/count"))
 .Where(i=>player.GetPropertyString($"track-list/{i}/type")=="sub"
  &&player.GetPropertyString($"track-list/{i}/external-filename").Equals(path,StringComparison.OrdinalIgnoreCase))
 .Select(i=>player.GetPropertyString($"track-list/{i}/id")).Single();
bool HasTrack(string path)=>Enumerable.Range(0,player.GetPropertyInt("track-list/count"))
 .Any(i=>player.GetPropertyString($"track-list/{i}/type")=="sub"
  &&player.GetPropertyString($"track-list/{i}/external-filename").Equals(path,StringComparison.OrdinalIgnoreCase));
async Task WaitFor(Func<bool> ready,string message)
{
 var until=DateTime.UtcNow.AddSeconds(5);
 while(!ready()&&DateTime.UtcNow<until)await Task.Delay(15);
 Check(ready(),message);
}
async Task DownloadedSubtitleChecks()
{
 string manifest=Environment.GetEnvironmentVariable("ANIMEFUSION_TEST_SUBTITLE_MANIFEST");
 if(string.IsNullOrWhiteSpace(manifest))return;
 var fixture=JsonSerializer.Deserialize<DownloadedSubtitleFixture>(File.ReadAllText(manifest),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
  ??throw new Exception("Downloaded subtitle manifest is empty");
 Check(Path.IsPathFullyQualified(fixture.File)&&File.Exists(fixture.File),"The downloaded subtitle fixture is an existing local file");
 var media=TraktMedia.Parse(fixture.MediaTitle,video,new Dictionary<string,string>());
 Check(media.Kind=="show"&&media.Episode is >0&&fixture.Result.Individual,"Downloaded playback regression uses an identified single episode subtitle");
 var work=new SubtitleWork(media,fixture.Names,fixture.MalId);
 var matched=work.ForDownload(new(media.Title,media.Season,media.Episode,fixture.Result.LanguageCode),fixture.Result);
 Check(matched.Current&&matched.Query.Title==media.Title,"Real provider metadata belongs to the confirmed current work before saving");
 byte[] bytes=File.ReadAllBytes(fixture.File);
 using var handler=new SubtitleBytesHandler(bytes);
 using var http=new HttpClient(handler);
 var downloader=new SubtitleDownload(http,library);
 var downloadable=fixture.Result with{Local=false,DownloadUrl="https://subtitle-fixture.invalid/track"};
 string saved=await downloader.DownloadAsync(matched.Query,downloadable,null,downloadable.Files,null,default)
  ??throw new Exception("The real downloaded subtitle did not match the current episode");
 Check(handler.Requests==1&&File.ReadAllBytes(saved).SequenceEqual(bytes),
  "The real downloaded subtitle bytes enter the library unchanged through SubtitleDownload");
 Check(saved.StartsWith(library.SeasonDirectory(media.Title,media.Season)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),
  "The actual release is stored beneath the current canonical work and source season");
 var local=library.Results(media.Title).Single(item=>item.Source==fixture.Result.Source&&item.Id==fixture.Result.Id);
 await Open(fixture.MediaTitle);
 player.SetPropertyBool("pause",true);
 int starts=Volatile.Read(ref fileStarts);
 string opened=player.GetPropertyString("path");
 double position=player.GetPropertyDouble("time-pos");
 var store=new SubtitleStore(Path.Combine(temp,"downloaded-fixture-account"));
 await SubtitlePlayback.Current.DownloadAsync(matched.Query,local,store,CurrentTitles());
 Check(Selected().Equals(saved,StringComparison.OrdinalIgnoreCase)&&player.GetPropertyBool("sub-visibility")
  &&SubtitlePlayback.Current.DownloadStatus=="",
  "Playback selects the actual downloaded subtitle immediately and clears its loading notice");
 Check(player.GetPropertyString("current-tracks/sub/codec").Length>0&&HasTrack(saved),
  "The native player recognizes the real subtitle as a loaded subtitle track");
 Check(Volatile.Read(ref fileStarts)==starts&&player.GetPropertyString("path")==opened
  &&Math.Abs(player.GetPropertyDouble("time-pos")-position)<.15,
  "Loading the real subtitle neither reloads the local sample nor changes its playback position");
 Check(await downloader.DownloadAsync(matched.Query,downloadable,null,downloadable.Files,null,default)==saved&&handler.Requests==1,
  "Reusing the real subtitle performs no additional download request");
 int tracks=SubtitleCount();
 await SubtitlePlayback.Current.DownloadAsync(matched.Query,local,store,CurrentTitles());
 Check(SubtitleCount()==tracks&&Selected().Equals(saved,StringComparison.OrdinalIgnoreCase)&&handler.Requests==1,
  "Repeated playback loading reuses the same native subtitle track and stored bytes");
 await Open(media.Title+$" S{media.Season}E{media.Episode+1}");
 Check(!HasTrack(saved)&&SubtitleCount()==0,"The real downloaded episode is not restored for the next episode");
 await SubtitlePlayback.Current.DownloadAsync(matched.Query with{Episode=media.Episode+1},local,store,CurrentTitles());
 Check(!HasTrack(saved)&&SubtitleCount()==0&&SubtitlePlayback.Current.DownloadStatus=="字幕加载失败",
  "Explicitly choosing the previous episode's local result cannot load it into the next episode");
 await Open(fixture.MediaTitle);
 Check(Selected().Equals(saved,StringComparison.OrdinalIgnoreCase)&&SubtitlePlayback.Current.DownloadStatus==""&&handler.Requests==1,
  "Reopening the matching episode restores the downloaded version without another download");
 Console.WriteLine($"Downloaded subtitle native chain passed; bytes={bytes.Length}; fixture HTTP reads={handler.Requests}; no network or desktop window.");
}
try
{
 if(CommandLine.Contains("sid"))
 {
  string startupVideo=Path.Combine(temp,"测试作品 S2E1.mp4");File.Copy(video,startupVideo);
  string startupSubtitle=Path.ChangeExtension(startupVideo,".srt");File.Copy(supplied,startupSubtitle);
  player.SetPropertyString("sub-auto","exact");
  await Drop(startupVideo);
  string expected=CommandLine.GetValue("sid");
  Check(expected=="no"?player.GetPropertyString("sid")=="no":Selected().Equals(startupSubtitle,StringComparison.OrdinalIgnoreCase),
   "Frontend startup --sid retains priority over the remembered library choice");
  Console.WriteLine($"Native startup subtitle checks passed: {checks}; sid={expected}; no desktop window or service calls.");
  return;
 }
 await Open("测试作品 S2E1");
 Check(SubtitleCount()==2,"All downloaded current-episode alternatives enter the real subtitle menu");
 Check(Selected().Equals(sc,StringComparison.OrdinalIgnoreCase),"Previous local choice selected on file load");
 Check(player.GetPropertyString("current-tracks/sub/title")==Path.GetFileName(sc),"Registered subtitle titles contain only the original filename");
 player.CommandV("sub-add",supplied,"auto","secondary fixture","danmaku");
 int secondary=Enumerable.Range(0,player.GetPropertyInt("track-list/count")).Where(i=>player.GetPropertyString($"track-list/{i}/external-filename")==supplied).Select(i=>player.GetPropertyInt($"track-list/{i}/id")).Single();
 player.SetPropertyInt("secondary-sid",secondary);
 SubtitlePlayback.Current.Select(sc);
 Check(player.GetPropertyInt("secondary-sid")==secondary,"Loading cached main subtitles preserves the real secondary subtitle/danmaku track");
 var progressStore=new SubtitleStore(Path.Combine(temp,"account"));
 var localResult=library.Results("测试作品").Single();
 Check(player.GetPropertyString("osd-ass-cc/0").Length>0,"Native ASS styled notice control is available");
 await SubtitlePlayback.Current.DownloadAsync(new("测试作品",2,1,"zh"),localResult,progressStore,CurrentTitles());
 Check(SubtitlePlayback.Current.DownloadStatus==""&&Selected().Equals(sc,StringComparison.OrdinalIgnoreCase),"Playback-owned loading clears progress and selects the real subtitles without a success notice");
 Check(player.GetPropertyInt("secondary-sid")==secondary,"Download progress leaves the secondary/danmaku track selected");
 await SubtitlePlayback.Current.DownloadAsync(new("测试作品",2,1,"zh"),localResult with{Files=[]},progressStore,CurrentTitles());
 Check(SubtitlePlayback.Current.DownloadStatus=="字幕加载失败"&&Selected().Equals(sc,StringComparison.OrdinalIgnoreCase),
  "A missing local subtitle shows the short playback error without changing the selected subtitle");
 await SubtitlePlayback.Current.DownloadAsync(new("测试作品",2,1,"zh"),new(SubtitleSource.Amenzb,"invalid","测试作品 S2E1","中文","",[]),progressStore,CurrentTitles());
 Check(SubtitlePlayback.Current.DownloadStatus=="字幕下载失败"&&Selected().Equals(sc,StringComparison.OrdinalIgnoreCase),
  "A rejected download shows the short playback error without contacting a provider or changing the selected subtitle");
 int tcId=Enumerable.Range(0,player.GetPropertyInt("track-list/count")).Where(i=>player.GetPropertyString($"track-list/{i}/external-filename")==tc).Select(i=>player.GetPropertyInt($"track-list/{i}/id")).Single();
 player.SetPropertyInt("sid",tcId);
 var deadline=DateTime.UtcNow.AddSeconds(3);
 while(!library.Tracks(TraktMedia.Parse("测试作品 S2E2","",new Dictionary<string,string>())).Any(t=>t.Preferred&&t.Path.EndsWith(".tc.srt"))&&DateTime.UtcNow<deadline)await Task.Delay(20);
 Check(library.Tracks(TraktMedia.Parse("测试作品 S2E2","",new Dictionary<string,string>())).Single(t=>t.Preferred).Path.EndsWith(".tc.srt"),"Real sid change persists version preference");
 await Open("测试作品 S2E2");
 Check(SubtitlePlayback.Current.DownloadStatus=="","Switching media clears the previous download notice and generation");
 Check(typeof(SubtitleResult).Assembly.GetType("MpvNet.Windows.Subtitles.OpenSubtitlesClient")==null,"Removed service implementation is absent from the loaded player assembly");
 Check(Enum.GetNames<SubtitleSource>().SequenceEqual(new[]{"Assrt","Amenzb","Tsukihime"}),"All three configured subtitle providers exist in the loaded assembly");
 Exception resourceError=null;
 var resourceThread=new Thread(()=>
 {
  try
  {
   MpvNet.Windows.UI.Theme.Current=new()
   {
    Foreground=System.Windows.Media.Brushes.White,Foreground2=System.Windows.Media.Brushes.LightGray,
    Background=System.Windows.Media.Brushes.DarkSlateGray,MenuBackground=System.Windows.Media.Brushes.DarkSlateGray,
    MenuHighlight=System.Windows.Media.Brushes.DimGray,Heading=System.Windows.Media.Brushes.LightPink
   };
   var resources=(System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/mpv-AnimeFusion;component/WPF/Resources.xaml",UriKind.Relative));
   var application=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
   application.Resources=resources;
   var list=new System.Windows.Controls.ListBox{Style=(System.Windows.Style)resources["MatchList"],Width=500,Height=280};
   Check(System.Windows.Controls.VirtualizingPanel.GetScrollUnit(list)==System.Windows.Controls.ScrollUnit.Pixel
    &&System.Windows.Controls.VirtualizingPanel.GetIsVirtualizingWhenGrouping(list),"Compiled player resources contain pixel scrolling for grouped lists");
   list.ApplyTemplate();list.Measure(new System.Windows.Size(500,280));list.Arrange(new System.Windows.Rect(0,0,500,280));list.UpdateLayout();
   var border=(System.Windows.Controls.Border)list.Template.FindName("Bd",list);
   Check(border.Child is HandyControl.Controls.ScrollViewer{IsPixelScrollingEnabled:true},"Actual compiled list template enables direct pixel input without per-event animations");
   var confirmation=new MpvNet.Windows.WPF.SubtitleDeleteWindow("[原始文件名] 测试字幕.sc.ass");
   try
   {
    var deleteFile=(System.Windows.Controls.CheckBox)confirmation.FindName("DeleteFile");
    Check(!confirmation.DeleteLocal&&deleteFile.IsChecked==false,"Subtitle deletion confirmation preserves local files by default");
    Check(confirmation.FileName=="[原始文件名] 测试字幕.sc.ass","Subtitle deletion confirmation shows the complete original filename");
    var panel=(System.Windows.Controls.StackPanel)confirmation.Content;
    confirmation.Measure(new System.Windows.Size(500,400));
    Check(ReferenceEquals(deleteFile.Foreground,confirmation.Foreground)&&ReferenceEquals(deleteFile.Foreground,System.Windows.Media.Brushes.White),
     "Local deletion checkbox text follows the dark theme foreground instead of system black");
    Check(!panel.Children.OfType<System.Windows.Controls.TextBlock>().Any(text=>text.Text.Contains("保留")||text.Text.StartsWith("勾选后")),
     "Deletion confirmation has no retention explanation below the checkbox");
    var buttons=panel.Children.OfType<System.Windows.Controls.StackPanel>().Single().Children.OfType<System.Windows.Controls.Button>().ToArray();
    Check(buttons.Length==2&&buttons.Single(button=>button.IsCancel).IsDefault
     &&buttons.Single(button=>!button.IsCancel).Content?.ToString()=="删除","The confirmation defaults to Cancel and provides a separate Delete action");
    foreach(var button in buttons)
    {
     button.ApplyTemplate();
     Check(ReferenceEquals(button.Style,resources[typeof(System.Windows.Controls.Button)])
      &&button.Template.FindName("Surface",button) is System.Windows.Controls.Border{CornerRadius.TopLeft:5},
      "Deletion confirmation uses the compiled shared rounded action-button style");
    }
    deleteFile.IsChecked=true;
    Check(confirmation.DeleteLocal,"The local-file option is passed only when explicitly checked");
    Check(new System.Windows.Interop.WindowInteropHelper(confirmation).Handle==IntPtr.Zero,"Confirmation resource checks never create a desktop window");
   }
   finally{confirmation.Close();}
   var window=new MpvNet.Windows.WPF.SubtitleSearchWindow();
   var previousContext=SynchronizationContext.Current;
   SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
   try
   {
    var fields=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    var type=window.GetType();
    var fixtureStore=new SubtitleStore(Path.Combine(temp,"window-account"));fixtureStore.Save(new(new string('a',32)));
    type.GetField("_store",fields)!.SetValue(window,fixtureStore);
    var items=(System.Collections.ObjectModel.ObservableCollection<SubtitleResult>)type.GetField("_results",fields)!.GetValue(window)!;
    var searchBox=(System.Windows.Controls.TextBox)window.FindName("SearchBox");
    var searchButton=(System.Windows.Controls.Button)window.FindName("SearchButton");
    var languages=(System.Windows.Controls.StackPanel)window.FindName("LanguageChips");
    var seasons=(System.Windows.Controls.WrapPanel)window.FindName("SeasonChips");
    var episodes=(System.Windows.Controls.StackPanel)window.FindName("EpisodeChips");
    var client=new WindowSearchClient();
    ((SubtitleSearch)type.GetField("_search",fields)!.GetValue(window)!).Dispose();
    type.GetField("_search",fields)!.SetValue(window,new SubtitleSearch(client));
    void Refresh()=>type.GetMethod("RefreshResults",fields)!.Invoke(window,null);
    string[] LanguageTags()=>languages.Children.Cast<System.Windows.Controls.Button>().Select(button=>(string)button.Tag).ToArray();
    string[] EpisodeTags()=>episodes.Children.Cast<System.Windows.Controls.Button>().Select(button=>(string)button.Tag).ToArray();
    string[] SeasonTags()=>seasons.Children.Cast<System.Windows.Controls.Button>().Select(button=>(string)button.Tag).ToArray();
    void ChooseSeason(string code)=>type.GetMethod("Season_Click",fields)!.Invoke(window,[seasons.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag==code),new System.Windows.RoutedEventArgs()]);
    void ChooseSource(string code)=>type.GetMethod("Source_Click",fields)!.Invoke(window,[new System.Windows.Controls.Button{Tag=code},new System.Windows.RoutedEventArgs()]);
    void ChooseLanguage(string code)=>type.GetMethod("Language_Click",fields)!.Invoke(window,[languages.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag==code),new System.Windows.RoutedEventArgs()]);
    void ChooseEpisode(string code)=>type.GetMethod("Episode_Click",fields)!.Invoke(window,[episodes.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag==code),new System.Windows.RoutedEventArgs()]);
    void WaitUi(Func<bool> ready)
    {
     var until=DateTime.UtcNow.AddSeconds(10);
     while(DateTime.UtcNow<until)
     {
      var frame=new System.Windows.Threading.DispatcherFrame();
      System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));
      System.Windows.Threading.Dispatcher.PushFrame(frame);
      if(ready())return;
      Thread.Sleep(1);
     }
     throw new Exception("Mock subtitle window action did not complete");
    }
    void WaitForSearch()=>WaitUi(()=>type.GetField("_searchResult",fields)!.GetValue(window) is SubtitleSearchResult{Complete:true});
    void ClickSearch()=>type.GetMethod("Search_Click",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);
    void EnterSearch()
    {
     var key=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,new MemoryPresentationSource(),Environment.TickCount,System.Windows.Input.Key.Enter)
      {RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
     type.GetMethod("SearchBox_KeyDown",fields)!.Invoke(window,[searchBox,key]);
     Check(key.Handled,"Enter is handled by the manual subtitle search action");
    }
    type.GetMethod("Window_Loaded",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);WaitUi(()=>true);
    Check(client.Requests==0&&type.GetField("_searchResult",fields)!.GetValue(window)==null&&searchButton.Content?.ToString()=="搜索",
     "First opening initializes the window without issuing a subtitle request");
    Check(LanguageTags().Take(5).SequenceEqual(new[]{"","zh-Hans","zh-Hant","en","ja"})
     &&(string)type.GetField("_language",fields)!.GetValue(window)! =="",
     "All is selected as the leftmost language option, followed by the four fixed languages before any search");
    Check((string)type.GetField("_source",fields)!.GetValue(window)! =="all"
     &&(string)type.GetField("_season",fields)!.GetValue(window)! =="all"
     &&(string)type.GetField("_episode",fields)!.GetValue(window)! =="all"
     &&SeasonTags().Take(2).SequenceEqual(new[]{"all","2"})&&EpisodeTags().Take(2).SequenceEqual(new[]{"all","2"}),
     "Source, season and episode default to All while the current season and episode are immediately available after All");
    var initialItems=items.ToArray();string initialText=searchBox.Text;
    items.Add(result with{Id="prior-season",Season=1,Episode=4,LanguageCode="en"});
    items.Add(result with{Id="later-season",Season=3,Episode=5,LanguageCode="en"});
    items.Add(result with{Id="earlier-episode",Season=2,Episode=1,LanguageCode="en"});
    type.GetMethod("RefreshSeasons",fields)!.Invoke(window,null);Refresh();
    Check(SeasonTags().Take(4).SequenceEqual(new[]{"all","2","1","3"})
     &&EpisodeTags().Take(2).SequenceEqual(new[]{"all","2"}),
     "The current season and episode precede numerically earlier available choices without changing All selection");
    ChooseEpisode("2");
    Check((string)type.GetField("_season",fields)!.GetValue(window)! =="all"
     &&(string)type.GetField("_episode",fields)!.GetValue(window)! =="2"&&searchBox.Text==initialText+" E02"&&client.Requests==0,
     "The current episode appends its visible suffix under All seasons without sending a request");
    ChooseEpisode("all");ChooseSeason("3");
    Check(EpisodeTags().SequenceEqual(new[]{"all","5"})&&searchBox.Text==initialText+" S03"&&client.Requests==0,
     "A different season exposes its own episodes and synchronizes the visible suffix without sending a request");
    ChooseLanguage("ja");
    Check(EpisodeTags().SequenceEqual(new[]{"all"})&&searchBox.Text==initialText+" S03"&&client.Requests==0,
     "An empty language in another season has no invented current episode and leaves the search text unchanged");
    ChooseSource("tsukihime");ChooseSource("all");ChooseLanguage("");ChooseSeason("all");
    Check(searchBox.Text==initialText&&client.Requests==0
     &&EpisodeTags().Take(2).SequenceEqual(new[]{"all","2"}),
     "Changing source, language and season is local, preserves the keyword, and restores the current-episode shortcut under All seasons");
    items.Clear();foreach(var item in initialItems)items.Add(item);
    type.GetMethod("RefreshSeasons",fields)!.Invoke(window,null);Refresh();
    var defaultWindow=new MpvNet.Windows.WPF.SubtitleSearchWindow();
    try
    {
     var defaultClient=new WindowSearchClient();
     type.GetField("_store",fields)!.SetValue(defaultWindow,fixtureStore);
     ((SubtitleSearch)type.GetField("_search",fields)!.GetValue(defaultWindow)!).Dispose();
     type.GetField("_search",fields)!.SetValue(defaultWindow,new SubtitleSearch(defaultClient));
     type.GetMethod("Window_Loaded",fields)!.Invoke(defaultWindow,[defaultWindow,new System.Windows.RoutedEventArgs()]);
     type.GetMethod("Search_Click",fields)!.Invoke(defaultWindow,[defaultWindow,new System.Windows.RoutedEventArgs()]);
     WaitUi(()=>type.GetField("_searchResult",fields)!.GetValue(defaultWindow) is SubtitleSearchResult{Complete:true});
     Check(defaultClient.Requests==1&&defaultClient.LastQuery.RequestSeason==null&&defaultClient.LastQuery.RequestEpisode==null
      &&defaultClient.LastQuery.Language==""&&defaultClient.LastQuery.Episode==2,
      "A first manual search submits All seasons, episodes and languages while retaining separate current-playback identity");
     Check(new System.Windows.Interop.WindowInteropHelper(defaultWindow).Handle==IntPtr.Zero,"Default-filter checks never create a desktop window");
    }
    finally{type.GetField("_canClose",fields)!.SetValue(defaultWindow,true);defaultWindow.Close();}
    ChooseSeason("2");ChooseLanguage("zh-Hans");
    ChooseEpisode("all");Refresh();
    Check((string)type.GetField("_episode",fields)!.GetValue(window)! =="all"&&EpisodeTags().Contains("2")&&client.Requests==0,
     "Choosing All before the first search retains the current-episode option and does not reset the manual choice");
    ChooseEpisode("2");
    Check((string)type.GetField("_episode",fields)!.GetValue(window)! =="2"&&client.Requests==0,
     "The current episode can be reselected after All before the first search without a request");
    ChooseEpisode("all");
    type.GetMethod("OnClosing",fields)!.Invoke(window,[new System.ComponentModel.CancelEventArgs()]);
    type.GetMethod("Window_Loaded",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);WaitUi(()=>true);
    Check(client.Requests==0&&type.GetField("_searchResult",fields)!.GetValue(window)==null,
     "Reopening before the first search still issues no request");
    items.Clear();
    var pending=new SubtitleSearchResult(new("测试作品",2,2,""));
    type.GetField("_searchResult",fields)!.SetValue(window,pending);
    type.GetField("_displayedKeyword",fields)!.SetValue(window,"测试作品");
    type.GetField("_episode",fields)!.SetValue(window,"all");
    items.Add(result with{LanguageCode="zh-Hans",Episode=1});items.Add(result with{Id="2",LanguageCode="en",Episode=2});
    var empty=(System.Windows.Controls.StackPanel)window.FindName("EmptyState");
    var status=(System.Windows.Controls.TextBlock)window.FindName("Status");
    var count=(System.Windows.Controls.TextBlock)window.FindName("ResultCount");
    Refresh();
    Check(LanguageTags().SequenceEqual(new[]{"","zh-Hans","zh-Hant","en","ja"})&&EpisodeTags().SequenceEqual(new[]{"all","2","1"}),
     "The current-episode shortcut stays after All while actual simplified-language episode choices remain separately available");
    ChooseLanguage("en");
    Check(EpisodeTags().SequenceEqual(new[]{"all","2"})&&client.Requests==0,
     "Selecting English changes the available episodes using cached results without a request");
    ChooseLanguage("ja");
    Check(EpisodeTags().SequenceEqual(new[]{"all","2"})&&System.Windows.Data.CollectionViewSource.GetDefaultView(items).IsEmpty
     &&(string)type.GetField("_language",fields)!.GetValue(window)! =="ja",
     "An unavailable fixed language keeps only All and the current-episode shortcut without inventing matching results");
    ChooseLanguage("zh-Hans");
    items.Add(result with{Id="fr",LanguageCode="fr"});items.Add(result with{Id="ko",LanguageCode="ko"});
    items.Add(result with{Id="other",LanguageCode=""});Refresh();
    Check(LanguageTags().Take(5).SequenceEqual(new[]{"","zh-Hans","zh-Hant","en","ja"})
     &&LanguageTags().Skip(5).ToHashSet().SetEquals(new[]{"fr","ko","other"}),
     "Additional result languages appear only after the fixed languages and All");
    type.GetMethod("Language_Click",fields)!.Invoke(window,[languages.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag=="ko"),new System.Windows.RoutedEventArgs()]);
    Check(System.Windows.Data.CollectionViewSource.GetDefaultView(items).Cast<SubtitleResult>().Single().Id=="ko",
     "Dynamic language button filters cached results without a search");
    items.RemoveAt(4);items.RemoveAt(3);items.RemoveAt(2);Refresh();
    Check(LanguageTags().SequenceEqual(new[]{"","zh-Hans","zh-Hant","en","ja","ko"})
     &&(string)type.GetField("_language",fields)!.GetValue(window)! =="ko",
     "The selected dynamic language remains visible when its results disappear, while absent unselected languages are removed");
    var remaining=items.ToArray();items.Clear();Refresh();
    Check(LanguageTags().SequenceEqual(new[]{"","zh-Hans","zh-Hant","en","ja","ko"})
     &&(string)type.GetField("_language",fields)!.GetValue(window)! =="ko"&&client.Requests==0,
     "Clearing all results retains the selected dynamic language without issuing a request");
    foreach(var item in remaining)items.Add(item);Refresh();
    ChooseLanguage("zh-Hans");
    Check(empty.Visibility==System.Windows.Visibility.Collapsed&&status.Text=="正在搜索字幕…"&&count.Text=="共 2 条搜索结果，显示 1 条",
     "Actual compiled window immediately shows local count and only footer progress while network search is pending");
    items.Clear();Refresh();
    Check(empty.Visibility==System.Windows.Visibility.Collapsed&&status.Text=="正在搜索字幕…"&&count.Text=="共 0 条搜索结果",
     "Pending empty search also has no central progress overlay");
    Check(LanguageTags().SequenceEqual(new[]{"","zh-Hans","zh-Hant","en","ja"})
     &&(string)type.GetField("_language",fields)!.GetValue(window)! =="zh-Hans",
     "An empty search never drops the fixed languages or falls back from simplified Chinese to All");
    typeof(SubtitleSearchResult).GetProperty("Complete")!.SetValue(pending,true);Refresh();
    Check(empty.Visibility==System.Windows.Visibility.Visible&&status.Text=="","Finished empty search displays one central empty state without duplicate footer text");
    items.Add(result with{LanguageCode="zh-Hans"});Refresh();
    Check(empty.Visibility==System.Windows.Visibility.Collapsed&&status.Text==""&&count.Text=="共 1 条搜索结果","Finished results hide all loading placeholders");
    type.GetField("_displayedKeyword",fields)!.SetValue(window,searchBox.Text);
    window.RestoreResults();
    var chosenLanguage=languages.Children.Cast<System.Windows.Controls.Button>().First(button=>((string)button.Tag).StartsWith("zh",StringComparison.Ordinal));
    type.GetMethod("Language_Click",fields)!.Invoke(window,[chosenLanguage,new System.Windows.RoutedEventArgs()]);
    string keptLanguage=(string)chosenLanguage.Tag;
    var cachedSearch=type.GetField("_search",fields)!.GetValue(window);
    var lifetime=(System.Threading.CancellationTokenSource)type.GetField("_lifetime",fields)!.GetValue(window)!;
    var closing=new System.ComponentModel.CancelEventArgs();
    type.GetMethod("OnClosing",fields)!.Invoke(window,[closing]);
    Check(closing.Cancel&&!lifetime.IsCancellationRequested&&items.Count==1&&window.IsCurrent,
     "Returning from subtitle search retains results and the request lifetime for this video");
    Check(new System.Windows.Interop.WindowInteropHelper(window).Handle==IntPtr.Zero,
     "Cached-window checks never create a desktop window");
    int generation=(int)type.GetField("_searchGeneration",fields)!.GetValue(window)!;
    window.RestoreResults();
    type.GetMethod("Window_Loaded",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);
    Check(ReferenceEquals(cachedSearch,type.GetField("_search",fields)!.GetValue(window))
     &&(int)type.GetField("_searchGeneration",fields)!.GetValue(window)! ==generation
     &&type.GetField("_searchResult",fields)!.GetValue(window)==pending,
     "Returning to the same video reuses the existing search and result without requesting again");
    Check((string)type.GetField("_season",fields)!.GetValue(window)! =="2"
     &&(string)type.GetField("_language",fields)!.GetValue(window)! ==keptLanguage,
     "Reopening keeps the season and language filters");
    ChooseLanguage("en");ClickSearch();WaitForSearch();
    int requests=client.Requests;
    Check(requests==1&&client.LastQuery.Language=="en", "The first explicit click submits exactly one request with the currently selected language");
    Check(client.LastQuery.RequestSeason==2&&client.LastQuery.RequestEpisode==null&&client.LastQuery.Episode==2,
     "A manually selected All on the first search clears the requested episode while preserving the independent current-playback episode");
    items.Add(result with{Id="manual-other-episode",LanguageCode="en",Episode=1});Refresh();ChooseEpisode("1");
    client.Block=true;ClickSearch();
    WaitUi(()=>client.LastQuery.RequestEpisode==1&&type.GetField("_searchResult",fields)!.GetValue(window) is SubtitleSearchResult{Complete:false});
    requests=client.Requests;
    Check(client.LastQuery.RequestSeason==2&&client.LastQuery.Episode==2&&EpisodeTags().Contains("1")
     &&(string)type.GetField("_episode",fields)!.GetValue(window)! =="1"&&searchButton.Content?.ToString()=="暂停",
     "A manually chosen other episode survives clearing results and the first empty progress update as a distinct request target");
    ClickSearch();WaitUi(()=>type.GetField("_searchResult",fields)!.GetValue(window) is SubtitleSearchResult{Paused:true});
    ChooseEpisode("all");
    Check(searchButton.Content?.ToString()=="搜索"&&EpisodeTags().Contains("1")&&client.Requests==requests,
     "Changing the episode during a paused request only filters locally and leaves its original target available");
    ChooseEpisode("1");
    Check(searchButton.Content?.ToString()=="继续"&&client.Requests==requests,
     "Returning to the paused episode restores Continue without beginning another search");
    ChooseLanguage("ja");
    Check(EpisodeTags().Contains("1")&&(string)type.GetField("_episode",fields)!.GetValue(window)! =="1"
     &&System.Windows.Data.CollectionViewSource.GetDefaultView(items).IsEmpty&&client.Requests==requests,
     "Switching to an unavailable language retains the selected episode as a search condition without inventing a matching result or sending a request");
    ChooseLanguage("en");
    Check(EpisodeTags().Contains("1")&&(string)type.GetField("_episode",fields)!.GetValue(window)! =="1"
     &&searchButton.Content?.ToString()=="继续"&&client.Requests==requests,
     "Returning to the same pending language restores the explicit episode choice and paused request");
    ClickSearch();WaitUi(()=>client.Requests>requests);client.Finish();WaitForSearch();
    Check(client.LastQuery.RequestEpisode==1&&(string)type.GetField("_episode",fields)!.GetValue(window)! =="1",
     "Resuming retains the originally selected episode instead of replacing it with the playing episode");
    var titleContext=(SubtitleTitles)type.GetField("_titles",fields)!.GetValue(window)!;
    var confirmedNames=(HashSet<string>)typeof(SubtitleTitles).GetField("_currentNames",fields)!.GetValue(titleContext)!;
    confirmedNames.Add("Test Work Alias");
    requests=client.Requests;
    type.GetMethod("SelectAlias",fields)!.Invoke(window,["Test Work Alias"]);
    Check(client.Requests==requests,"Selecting an already confirmed alias only changes the text field");
    ClickSearch();WaitForSearch();
    Check(client.Requests==requests+1&&client.LastQuery.Title=="Test Work Alias S02E01"&&client.LastQuery.RequestSeason==2
     &&client.LastQuery.RequestEpisode==1&&client.LastQuery.Episode==2,
     "Searching an alias of the same work preserves the manually selected season and episode");
    items.Add(result with{Id="chosen-season",Season=3,Episode=7,LanguageCode="en"});
    items.Add(result with{Id="unknown-language-episode",Season=3,Episode=8,LanguageCode=""});
    type.GetMethod("RefreshSeasons",fields)!.Invoke(window,null);ChooseSeason("3");ChooseEpisode("7");
    void CheckSelection(string message)
    {
     Check((string)type.GetField("_season",fields)!.GetValue(window)! =="3"
      &&(string)type.GetField("_episode",fields)!.GetValue(window)! =="7"
      &&SeasonTags().Contains("3")&&EpisodeTags().Contains("7")
      &&((System.Windows.FrameworkElement)window.FindName("SeasonRow")).Visibility==System.Windows.Visibility.Visible
      &&((System.Windows.FrameworkElement)window.FindName("EpisodeRow")).Visibility==System.Windows.Visibility.Visible,message);
    }
    CheckSelection("A selected non-current season and episode remain visible as independent search conditions");
    Check(!EpisodeTags().Contains("8"),"An unselected episode with unknown language is not advertised under the English filter");
    client.Empty=true;client.Block=true;requests=client.Requests;ClickSearch();
    WaitUi(()=>client.Requests>requests&&type.GetField("_searchResult",fields)!.GetValue(window) is SubtitleSearchResult{Complete:false});
    CheckSelection("Clearing results for a fresh search preserves the selected non-current season and episode chips");
    client.Finish();WaitForSearch();
    CheckSelection("An empty completed response preserves both selected season and episode instead of falling back to All");
    Check(client.LastQuery.RequestSeason==3&&client.LastQuery.RequestEpisode==7
     &&System.Windows.Data.CollectionViewSource.GetDefaultView(items).IsEmpty,
     "The retained selection is the actual submitted search condition and does not fabricate an available subtitle");
    requests=client.Requests;ChooseLanguage("ja");
    CheckSelection("Changing to a language with no matching results preserves the chosen season and episode");
    ChooseSource("tsukihime");
    CheckSelection("Changing to an empty source preserves the chosen season and episode");
    Check(client.Requests==requests,"Changing source and language does not request or reset the selected season and episode");
    ChooseSource("all");ChooseLanguage("en");
    type.GetMethod("OnClosing",fields)!.Invoke(window,[new System.ComponentModel.CancelEventArgs()]);
    window.RestoreResults();type.GetMethod("Window_Loaded",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);
    CheckSelection("Closing and restoring an empty completed search retains the actual selected season and episode");
    Check(client.Requests==requests,"Restoring the retained empty selection does not issue a search");
    client.Fail=true;ClickSearch();WaitForSearch();
    CheckSelection("A failed search preserves its selected season and episode for retry");
    Check(((SubtitleSearchResult)type.GetField("_searchResult",fields)!.GetValue(window)!).Errors.Count==1,
     "The failure regression receives an actual provider error rather than merely an empty page");
    client.Fail=false;requests=client.Requests;searchBox.Text="";ClickSearch();
    searchBox.Text="Test Work Alias";Refresh();
    CheckSelection("Temporarily clearing the search text does not erase the selected season and episode");
    Check(client.Requests==requests,"Submitting empty search text does not contact a provider");
    ClickSearch();WaitForSearch();
    CheckSelection("An explicit retry with the same work retains the previously selected season and episode");
    Check(client.LastQuery.RequestSeason==3&&client.LastQuery.RequestEpisode==7&&client.Requests==requests+1,
     "Retry after an empty or failed result submits the retained coordinates exactly once");
    searchBox.Text="另一部作品 S04E02";requests=client.Requests;ClickSearch();WaitForSearch();
    CheckSelection("Searching a different work with episode numbers in its text preserves the manually chosen season and episode");
    Check(client.Requests==requests+1&&client.LastQuery.Title=="另一部作品 S03E07"&&searchBox.Text==client.LastQuery.Title
     &&client.LastQuery.RequestSeason==3&&client.LastQuery.RequestEpisode==7&&client.LastQuery.Episode==null,
     "A different work submits the visible keyword with the selected request coordinates but without current-playback ownership");
    ChooseSeason("all");ChooseEpisode("all");ClickSearch();WaitForSearch();
    window.RestoreResults();
    Check((string)type.GetField("_season",fields)!.GetValue(window)! =="all"
     &&(string)type.GetField("_episode",fields)!.GetValue(window)! =="all"
     &&SeasonTags().First()=="all"&&EpisodeTags().SequenceEqual(new[]{"all","2"})
     &&((System.Windows.FrameworkElement)window.FindName("SeasonRow")).Visibility==System.Windows.Visibility.Visible
     &&((System.Windows.FrameworkElement)window.FindName("EpisodeRow")).Visibility==System.Windows.Visibility.Visible
     &&client.LastQuery.RequestSeason==null&&client.LastQuery.RequestEpisode==null,
     "Manually selected All remains visible after empty completion and restoration and is never replaced by the playing episode");
    searchBox.Text="Test Work Alias";ClickSearch();WaitForSearch();
    Check((string)type.GetField("_season",fields)!.GetValue(window)! =="all"
     &&(string)type.GetField("_episode",fields)!.GetValue(window)! =="all"
     &&client.LastQuery.RequestSeason==null&&client.LastQuery.RequestEpisode==null&&client.LastQuery.Episode==2,
     "Returning to a known current-work alias preserves manually selected All while restoring only playback identity");
    client.Empty=false;
    items.Add(result with{Id="return-episode",Season=2,Episode=1,LanguageCode="en"});
    type.GetMethod("RefreshSeasons",fields)!.Invoke(window,null);ChooseSeason("2");ChooseEpisode("1");
    ChooseEpisode("all");requests=client.Requests;
    var completed=type.GetField("_searchResult",fields)!.GetValue(window);
    generation=(int)type.GetField("_searchGeneration",fields)!.GetValue(window)!;
    var displayed=items.ToArray();var originalQuery=type.GetField("_query",fields)!.GetValue(window);
    foreach(string text in new[]{"测试作品 新", "测试作品", "测试作", ""})searchBox.Text=text;
    searchBox.Text="测试作";
    var editFrame=new System.Windows.Threading.DispatcherFrame();
    var editTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(800)};
    editTimer.Tick+=(_,_)=>{editTimer.Stop();editFrame.Continue=false;};editTimer.Start();
    System.Windows.Threading.Dispatcher.PushFrame(editFrame);
    Check(client.Requests==requests&&(int)type.GetField("_searchGeneration",fields)!.GetValue(window)! ==generation,
     "Typing, deleting and clearing text issue zero extra requests after the former debounce interval");
    Check(items.SequenceEqual(displayed)&&Equals(originalQuery,type.GetField("_query",fields)!.GetValue(window)),
     "Editing preserves the searched results and their query for filtering and downloading");
    type.GetMethod("OnClosing",fields)!.Invoke(window,[new System.ComponentModel.CancelEventArgs()]);
    window.RestoreResults();
    type.GetMethod("Window_Loaded",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);
    Check(client.Requests==requests&&ReferenceEquals(completed,type.GetField("_searchResult",fields)!.GetValue(window)),
     "A real window close/restore sequence adds zero HTTP requests and retains the same complete search snapshot");
    Check(searchBox.Text=="测试作"&&items.Select(item=>(item.Id,item.Title)).SequenceEqual(displayed.Select(item=>(item.Id,item.Title))),
     "Reopening retains the edited draft and existing results without searching the draft");
    var oldRequest=(System.Threading.CancellationTokenSource)type.GetField("_searchLifetime",fields)!.GetValue(window)!;
    ChooseLanguage("zh-Hant");EnterSearch();
    Check(oldRequest.IsCancellationRequested,"An explicit search cancels the previous request before submitting the new keyword");
    WaitForSearch();
    Check(client.Requests==requests+1&&client.LastQuery.Language=="zh-Hant"&&!ReferenceEquals(completed,type.GetField("_searchResult",fields)!.GetValue(window)),
     "Enter issues a fresh request using the newly selected language after restoring the cached window");
    Check(((SubtitleSearchResult)type.GetField("_searchResult",fields)!.GetValue(window)!).Query.Title=="测试作 S02"
     &&searchBox.Text==client.LastQuery.Title,
     "Explicit search submits the edited keyword with the selected season visible in the search box");
    Check(client.LastQuery.RequestSeason==2&&client.LastQuery.RequestEpisode==null&&client.LastQuery.Episode==null,
     "A genuinely different keyword preserves the manually selected request coordinates while clearing current-playback episode ownership");
    items.Add(result with{Id="ko",LanguageCode="ko"});Refresh();
    client.Block=true;
    ChooseLanguage("ko");ClickSearch();
    WaitUi(()=>client.LastQuery.Language=="ko"&&type.GetField("_searchResult",fields)!.GetValue(window) is SubtitleSearchResult{Complete:false});
    requests=client.Requests;
    Check(items.Count==0&&LanguageTags().Contains("ko")
     &&(string)type.GetField("_language",fields)!.GetValue(window)! =="ko",
     "Starting a fresh search keeps the selected dynamic language visible while the results are reset and its response is pending");
    ChooseLanguage("zh-Hans");
    Check(!LanguageTags().Contains("ko")&&client.Requests==requests&&searchButton.Content?.ToString()=="搜索",
     "An unavailable dynamic language disappears after selecting another language without submitting a request");
    ClickSearch();
    WaitUi(()=>client.LastQuery.Language=="zh-Hans"&&type.GetField("_searchResult",fields)!.GetValue(window) is SubtitleSearchResult{Complete:false});
    requests=client.Requests;
    Check(searchButton.Content?.ToString()=="暂停","A running request uses the shared button to pause when its selected language is unchanged");
    ChooseLanguage("en");
    Check(searchButton.Content?.ToString()=="搜索"&&client.Requests==requests,
     "Changing language during a search only filters locally and makes the button start a new search");
    ChooseLanguage("zh-Hans");
    Check(searchButton.Content?.ToString()=="暂停"&&client.Requests==requests,"Returning to the active request's language restores its pause action");
    ClickSearch();WaitUi(()=>type.GetField("_searchResult",fields)!.GetValue(window) is SubtitleSearchResult{Paused:true});
    ChooseLanguage("en");
    Check(searchButton.Content?.ToString()=="搜索"&&client.Requests==requests,"A changed language does not accidentally resume the paused request");
    ChooseLanguage("zh-Hans");
    Check(searchButton.Content?.ToString()=="继续","Returning to the paused request's language restores Continue");
    type.GetMethod("OnClosing",fields)!.Invoke(window,[new System.ComponentModel.CancelEventArgs()]);
    type.GetMethod("Window_Loaded",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);WaitUi(()=>true);
    Check(searchButton.Content?.ToString()=="继续"&&client.Requests==requests,"Reopening a paused search preserves its page state without issuing a request");
    ClickSearch();WaitUi(()=>client.Requests>requests);
    Check(searchButton.Content?.ToString()=="暂停"&&client.LastQuery.Language=="zh-Hans","Continuing retries the unfinished page with its original language");
    requests=client.Requests;
    oldRequest=(System.Threading.CancellationTokenSource)type.GetField("_searchLifetime",fields)!.GetValue(window)!;
    ChooseLanguage("en");ClickSearch();
    WaitUi(()=>client.Requests>requests&&client.LastQuery.Language=="en");
    Check(oldRequest.IsCancellationRequested&&client.Requests==requests+1,
     "Searching another language cancels the old request and submits exactly one fresh request for that language");
    client.Finish();WaitForSearch();
    Check(searchButton.Content?.ToString()=="搜索"&&((SubtitleSearchResult)type.GetField("_searchResult",fields)!.GetValue(window)!).Query.Language=="en",
     "A completed language-specific search returns to Search and cannot be overwritten by the canceled request");
    requests=client.Requests;EnterSearch();WaitForSearch();
    Check(client.Requests==requests+1&&client.LastQuery.Language=="en","Enter after completion refreshes the chosen language instead of silently reusing its cache");
    string previousMediaTitle=player.GetPropertyString("force-media-title");
    string previousDisplayedTitle=player.GetPropertyString("media-title");
    void SetFixtureMediaTitle(string value,string expectedTitle)
    {
     var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
     void OnTitle(string title){if(title==expectedTitle)observed.TrySetResult();}
     // Registered after SubtitlePlayback.Load: completing this callback means the
     // simulated title has finished applying before another scenario or file opens.
     player.ObservePropertyString("media-title",OnTitle);
     try
     {
      player.SetPropertyString("force-media-title",value);
      if(!observed.Task.Wait(TimeSpan.FromSeconds(10)))throw new Exception("The fixture title change did not finish on the native event loop");
     }
     finally{lock(player.StringPropChangeActions)player.StringPropChangeActions["media-title"].Remove(OnTitle);}
    }
    try
    {
     foreach(var scenario in new[]{(Title:"特别篇回归 S00E03",Season:(int?)0,Episode:(int?)3),
      (Title:"无集数电影 (2020)",Season:(int?)null,Episode:(int?)null),(Title:"多集不确定 S01E01-E02",Season:(int?)null,Episode:(int?)null)})
     {
      SetFixtureMediaTitle(scenario.Title,scenario.Title);
      var targetWindow=new MpvNet.Windows.WPF.SubtitleSearchWindow();
      try
      {
       var targetClient=new WindowSearchClient();
       type.GetField("_store",fields)!.SetValue(targetWindow,fixtureStore);
       ((SubtitleSearch)type.GetField("_search",fields)!.GetValue(targetWindow)!).Dispose();
       type.GetField("_search",fields)!.SetValue(targetWindow,new SubtitleSearch(targetClient));
       type.GetMethod("Window_Loaded",fields)!.Invoke(targetWindow,[targetWindow,new System.Windows.RoutedEventArgs()]);WaitUi(()=>true);
       Check(targetClient.Requests==0&&(string)type.GetField("_season",fields)!.GetValue(targetWindow)! =="all"
        &&(string)type.GetField("_episode",fields)!.GetValue(targetWindow)! =="all"
        &&(string)type.GetField("_language",fields)!.GetValue(targetWindow)! =="",
        "Specials, movies and ambiguous playback all start with All filters without automatically choosing coordinates");
       if(scenario.Season==0)
       {
        targetClient.Empty=true;
        var seasonPanel=(System.Windows.Controls.WrapPanel)targetWindow.FindName("SeasonChips");
        var episodePanel=(System.Windows.Controls.StackPanel)targetWindow.FindName("EpisodeChips");
        Check(seasonPanel.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag=="0").Content?.ToString()=="特别篇",
         "A confirmed season zero has its own Specials chip rather than sharing Other Works");
        Check(seasonPanel.Children.Cast<System.Windows.Controls.Button>().Select(button=>(string)button.Tag).Take(2).SequenceEqual(new[]{"all","0"})
         &&episodePanel.Children.Cast<System.Windows.Controls.Button>().Select(button=>(string)button.Tag).Take(2).SequenceEqual(new[]{"all","3"}),
         "Current special season and episode appear directly after All without requiring a selected season");
        type.GetMethod("Episode_Click",fields)!.Invoke(targetWindow,[episodePanel.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag=="3"),new System.Windows.RoutedEventArgs()]);
        var targetItems=(System.Collections.ObjectModel.ObservableCollection<SubtitleResult>)type.GetField("_results",fields)!.GetValue(targetWindow)!;
        targetItems.Add(result with{Id="special",Season=0,Episode=3,LanguageCode="zh-Hans"});
        targetItems.Add(result with{Id="unclassified",Season=-1,Episode=null,LanguageCode="zh-Hans"});
        targetItems.Add(result with{Id="film",Season=null,Episode=null,LanguageCode="zh-Hans"});
        type.GetMethod("RefreshSeasons",fields)!.Invoke(targetWindow,null);
        type.GetMethod("Season_Click",fields)!.Invoke(targetWindow,[seasonPanel.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag=="other"),new System.Windows.RoutedEventArgs()]);
        Check((string)type.GetField("_episode",fields)!.GetValue(targetWindow)! =="3"
         &&episodePanel.Children.Cast<System.Windows.Controls.Button>().Any(button=>(string)button.Tag=="3"),
         "Switching to Other Works retains the selected episode instead of silently broadening the filter");
        type.GetMethod("Episode_Click",fields)!.Invoke(targetWindow,[episodePanel.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag=="all"),new System.Windows.RoutedEventArgs()]);
        Check(System.Windows.Data.CollectionViewSource.GetDefaultView(targetItems).Cast<SubtitleResult>().Select(item=>item.Id).ToHashSet().SetEquals(new[]{"unclassified","film"}),
         "Other Works includes unclassified results and films without duplicating the separate season-zero special");
        type.GetMethod("Season_Click",fields)!.Invoke(targetWindow,[seasonPanel.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag=="0"),new System.Windows.RoutedEventArgs()]);
        Check((string)type.GetField("_episode",fields)!.GetValue(targetWindow)! =="all",
         "Returning to the current special season preserves an explicit All episode selection");
        type.GetMethod("Episode_Click",fields)!.Invoke(targetWindow,[episodePanel.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag=="3"),new System.Windows.RoutedEventArgs()]);
       }
       type.GetMethod("Search_Click",fields)!.Invoke(targetWindow,[targetWindow,new System.Windows.RoutedEventArgs()]);
       WaitUi(()=>type.GetField("_searchResult",fields)!.GetValue(targetWindow) is SubtitleSearchResult{Complete:true});
       Check(targetClient.Requests==1&&targetClient.LastQuery.RequestSeason==scenario.Season&&targetClient.LastQuery.RequestEpisode==scenario.Episode,
        "An explicit search transmits the selected current special or leaves movie and unknown request coordinates empty");
       if(scenario.Season==0)
        Check(((System.Windows.Controls.StackPanel)targetWindow.FindName("EpisodeChips")).Children.Cast<System.Windows.Controls.Button>().Select(button=>(string)button.Tag).SequenceEqual(new[]{"all","3"})
         &&(string)type.GetField("_season",fields)!.GetValue(targetWindow)! =="0"
         &&(string)type.GetField("_episode",fields)!.GetValue(targetWindow)! =="3"&&targetClient.Requests==1,
         "After an empty completed search the selected special season and episode remain available without inventing a subtitle result");
       Check(new System.Windows.Interop.WindowInteropHelper(targetWindow).Handle==IntPtr.Zero,"Episode-default tests never create a desktop window");
      }
      finally{type.GetField("_canClose",fields)!.SetValue(targetWindow,true);targetWindow.Close();}
     }
    }
    finally{SetFixtureMediaTitle(previousMediaTitle,previousDisplayedTitle);}
    type.GetMethod("Media_Changed",fields)!.Invoke(window,null);
    Check(lifetime.IsCancellationRequested&&(bool)type.GetField("_canClose",fields)!.GetValue(window)!&&!window.IsCurrent,
     "Changing video ends the old search lifetime and permits final disposal");
   }
   finally{window.GetType().GetField("_canClose",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,true);window.Close();SynchronizationContext.SetSynchronizationContext(previousContext);}
  }
  catch(Exception error){resourceError=error;}
 });
 resourceThread.SetApartmentState(ApartmentState.STA);resourceThread.Start();resourceThread.Join();
 if(resourceError!=null)throw resourceError;
 Check(SubtitleCount()==2&&Selected().EndsWith(".tc.srt"),"Next episode loads local files and retains the selected variant");
 await Open("测试作品 S2E1","sub-files="+supplied);
 Check(SubtitleCount()==3,"Supplied subtitle and all local alternatives remain in the menu");
 Check(Selected().Equals(supplied,StringComparison.OrdinalIgnoreCase),"Explicit per-file subtitle takes priority over remembered local subtitles");
 await Open("测试作品 S2E1","sid=no");
 Check(SubtitleCount()==2&&player.GetPropertyString("sid")=="no","Explicit per-file subtitle disable is preserved while local alternatives remain listed");
 string nearbyVideo=Path.Combine(temp,"测试作品 S2E1.mp4");File.Copy(video,nearbyVideo);
 string nearbySubtitle=Path.ChangeExtension(nearbyVideo,".srt");File.Copy(supplied,nearbySubtitle);
 player.SetPropertyString("sub-auto","exact");
 await Drop(nearbyVideo);
 Check(SubtitleCount()==3&&Selected().Equals(tc,StringComparison.OrdinalIgnoreCase),
  "Dropping a video with a same-name subtitle restores the remembered download and keeps all alternatives");
 int scId=Enumerable.Range(0,player.GetPropertyInt("track-list/count")).Where(i=>player.GetPropertyString($"track-list/{i}/external-filename")==sc).Select(i=>player.GetPropertyInt($"track-list/{i}/id")).Single();
 player.SetPropertyInt("sid",scId);
 deadline=DateTime.UtcNow.AddSeconds(3);
 while(!library.Tracks(TraktMedia.Parse("测试作品 S2E1","",new Dictionary<string,string>())).Any(t=>t.Preferred&&t.Path==sc)&&DateTime.UtcNow<deadline)await Task.Delay(20);
 await Drop(nearbyVideo);
 Check(Selected().Equals(sc,StringComparison.OrdinalIgnoreCase),"Dropping the same video again restores the newly chosen subtitle");
 await Open("测试作品 S2E1","sub-files="+nearbySubtitle,nearbyVideo);
 Check(Selected().Equals(nearbySubtitle,StringComparison.OrdinalIgnoreCase),"Explicitly supplying the same-name subtitle still overrides the remembered download");
 await Open("测试作品 S2E1","external-files="+nearbySubtitle,nearbyVideo);
 Check(Selected().Equals(nearbySubtitle,StringComparison.OrdinalIgnoreCase),"A selected subtitle supplied through external-files retains its priority");
 await Open("测试作品 S2E1","external-files="+video,nearbyVideo);
 Check(Selected().Equals(sc,StringComparison.OrdinalIgnoreCase),"An external audio/video file does not make an auto-discovered subtitle explicit");
 await Open("测试作品 S2E1","sid=1",nearbyVideo);
 Check(Selected().Equals(nearbySubtitle,StringComparison.OrdinalIgnoreCase),"An explicit track ID still overrides memory when that track was automatically discovered");
 await Open("测试作品 S2E1","sid=no",nearbyVideo);
 Check(player.GetPropertyString("sid")=="no","An explicit subtitle disable remains effective with a same-name subtitle present");
 player.SetPropertyString("sub-auto","no");
 library.Remember("测试作品",2,tc);
 await Open("其他作品 S2E1");
 Check(SubtitleCount()==0,"Unrelated work does not load the saved pack");
 await Open("测试作品 S1E1");
 Check(SubtitleCount()==0,"Another season does not load the saved pack");
 var mixedNames=new[]{"Pack/Season 1/[Group] Fixture S1E1.sc.srt","Pack/Season 3/[Group] Fixture S3E1.tc.srt","Pack/Specials/[Group] Fixture S0E1.sc.srt"};
 var mixed=new SubtitleResult(SubtitleSource.Assrt,"collection","Collection","简繁","",[]){Collection=true,LanguageCode="zh",PackageNames=mixedNames};
 foreach(string name in mixedNames)
 {
  var query=new SubtitleQuery("测试作品",2,1,"zh");
  string path=library.FilePath(query,mixed,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  File.WriteAllText(path,"1\n00:00:00,000 --> 00:00:50,000\ncollection\n",Encoding.UTF8);
  library.Save(query,mixed,new(SubtitleSource.Assrt,mixed.Id,0,name,""),path,true);
 }
 foreach(int season in new[]{1,3,0})
 {
  await Open($"测试作品 S{season}E1");
  Check(SubtitleCount()==1&&Enumerable.Range(0,player.GetPropertyInt("track-list/count")).Where(i=>player.GetPropertyString($"track-list/{i}/type")=="sub")
    .All(i=>player.GetPropertyString($"track-list/{i}/external-filename").Contains($"S{season}E1")),"Native playback registers only the correct part of a collection: "+season+"; count="+SubtitleCount());
 }
 await Open("测试作品 S2E1");
 Check(SubtitleCount()==2&&Selected().Equals(tc,StringComparison.OrdinalIgnoreCase),"Season two's existing track choices remain unchanged after other seasons are added");

 const string importedTitle="导入回归作品";
 TraktMedia ImportedMedia(int episode)=>TraktMedia.Parse(importedTitle+" S1E"+episode,"",new Dictionary<string,string>());
 string WriteImport(string directory,string filename,string content)
 {
  string path=Path.Combine(temp,"originals",directory,filename);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  File.WriteAllText(path,"1\n00:00:00,000 --> 00:00:50,000\n"+content+"\n",Encoding.UTF8);
  return path;
 }
 string originalA=WriteImport("pack-a","[DropA] Import Fixture S1E1.sc.srt","first episode A");
 string originalA2=WriteImport("pack-a","[DropA] Import Fixture S1E2.sc.srt","second episode A");
 string originalB=WriteImport("pack-b","[DropB] Import Fixture S1E1.tc.srt","first episode B");
 string originalC=WriteImport("pack-c","[DropC] Import Fixture S1E1.en.srt","first episode C");
 string danmakuFixture=WriteImport("danmaku","danmaku.srt","secondary subtitle");
 string Saved(string original,int episode=1)=>library.Tracks(ImportedMedia(episode))
  .Single(track=>track.Title==Path.GetFileName(original)).Path;
 await Open(importedTitle+" S1E1","sub-files="+supplied);
 Check(library.Tracks(ImportedMedia(1)).Count==0&&Selected()==supplied,
  "An initial explicitly supplied subtitle remains external and is not silently imported");
 player.SetPropertyBool("pause",true);
 double seekPosition=Math.Min(1.25,player.GetPropertyDouble("duration")/3);
 player.SetPropertyDouble("time-pos",seekPosition);
 await WaitFor(()=>!player.GetPropertyBool("seeking")&&Math.Abs(player.GetPropertyDouble("time-pos")-seekPosition)<.15,
  "The import regression begins at a nonzero paused playback position");
 player.CommandV("sub-add",danmakuFixture,"auto","secondary fixture","danmaku");
 string importedSecondary=TrackId(danmakuFixture);player.SetPropertyString("secondary-sid",importedSecondary);
 int startsBefore=Volatile.Read(ref fileStarts);string pathBefore=player.GetPropertyString("path");double timeBefore=player.GetPropertyDouble("time-pos");
 player.LoadFiles([originalA],false,false);
 await WaitFor(()=>library.Tracks(ImportedMedia(1)).Any(track=>track.Title==Path.GetFileName(originalA))
  &&Selected().Equals(Saved(originalA),StringComparison.OrdinalIgnoreCase)&&!HasTrack(originalA),
  "Dropping a subtitle through the real Player.LoadFiles imports and selects its persistent copy");
 string savedA=Saved(originalA),savedA2=Saved(originalA2,2);
 Check(File.ReadAllBytes(savedA).SequenceEqual(File.ReadAllBytes(originalA))&&File.Exists(originalA2)
  &&File.ReadAllBytes(savedA2).SequenceEqual(File.ReadAllBytes(originalA2)),
  "Import preserves original files and stores the corresponding sibling episode without changing subtitle bytes");
 player.LoadFiles([originalB,originalC],false,false);
 await WaitFor(()=>library.Tracks(ImportedMedia(1)).Count==3
  &&library.Tracks(ImportedMedia(1)).Any(track=>track.Title==Path.GetFileName(originalC)&&Selected().Equals(track.Path,StringComparison.OrdinalIgnoreCase))
  &&!HasTrack(originalB)&&!HasTrack(originalC),
  "One multi-file drop imports every subtitle and leaves the last requested subtitle selected");
 string savedB=Saved(originalB),savedC=Saved(originalC);
 Check(HasTrack(savedA)&&HasTrack(savedB)&&HasTrack(savedC)&&HasTrack(supplied),
  "Imported alternatives and the initial external subtitle all remain in the native track list");
 Check(player.GetPropertyString("secondary-sid")==importedSecondary&&HasTrack(danmakuFixture)
  &&!library.Results(importedTitle).SelectMany(item=>item.Files).Any(file=>file.Name==Path.GetFileName(danmakuFixture)),
  "Import leaves the real secondary subtitle selected and never stores the danmaku fixture as a main subtitle");
 Check(Volatile.Read(ref fileStarts)==startsBefore&&player.GetPropertyString("path")==pathBefore
  &&Math.Abs(player.GetPropertyDouble("time-pos")-timeBefore)<.15,
  "Dropping multiple subtitles does not reopen the video or change its paused playback position");
 await Open(importedTitle+" S1E1");
 Check(SubtitleCount()==3&&Selected().Equals(savedC,StringComparison.OrdinalIgnoreCase),
  "Reopening restores every imported subtitle and the last selected version");
 player.SetPropertyBool("pause",true);
 startsBefore=Volatile.Read(ref fileStarts);pathBefore=player.GetPropertyString("path");timeBefore=player.GetPropertyDouble("time-pos");
 SubtitlePlayback.Current.TogglePinned(TrackId(savedA));
 Check(Selected()==savedA&&library.Tracks(ImportedMedia(1)).Single(track=>track.Path==savedA).Pinned,
  "Pinning a listed subtitle immediately selects and persists that version");
 Check(Volatile.Read(ref fileStarts)==startsBefore&&player.GetPropertyString("path")==pathBefore
  &&Math.Abs(player.GetPropertyDouble("time-pos")-timeBefore)<.15,
  "Pinning switches only the native subtitle track without reopening or seeking the video");
 await Open(importedTitle+" S1E1","sub-files="+supplied);
 Check(Selected()==savedA&&HasTrack(supplied),"A pinned version overrides explicitly supplied subtitles while keeping them available");
 await Open(importedTitle+" S1E1","sid=no");
 Check(Selected()==savedA,"A pinned version overrides a per-file sid=no option");
 await Open(importedTitle+" S1E2","sub-files="+supplied+",sid=no");
 Check(Selected()==savedA2&&!HasTrack(savedA)&&library.Tracks(ImportedMedia(2)).Single().Pinned,
  "The pinned package follows the next episode and never reuses the previous episode's file");
 SubtitlePlayback.Current.TogglePinned(TrackId(savedA2));
 Check(!library.Tracks(ImportedMedia(1)).Any(track=>track.Pinned)&&!library.Tracks(ImportedMedia(2)).Any(track=>track.Pinned),
  "Unpinning clears the work-wide override for all episodes");
 await Open(importedTitle+" S1E1","sub-files="+supplied);
 Check(Selected()==supplied,"After unpinning, explicit subtitles regain their priority");
 await Open(importedTitle+" S1E1","sid=no");
 Check(player.GetPropertyString("sid")=="no","After unpinning, explicit sid=no regains its priority");

 await Open(importedTitle+" S1E1","sub-files="+originalB);
 Check(HasTrack(originalB)&&HasTrack(savedB),"Explicit playback parameters can load an imported original alongside its managed copy");
 player.SetPropertyBool("pause",true);
 player.SetPropertyDouble("time-pos",seekPosition);
 await WaitFor(()=>!player.GetPropertyBool("seeking")&&Math.Abs(player.GetPropertyDouble("time-pos")-seekPosition)<.15,
  "Deletion checks begin at a stable nonzero playback position");
 player.CommandV("sub-add",danmakuFixture,"auto","secondary fixture","danmaku");
 importedSecondary=TrackId(danmakuFixture);player.SetPropertyString("secondary-sid",importedSecondary);
 Check(SubtitlePlayback.Current.PrepareRemoval(importedSecondary)==null,"The secondary subtitle cannot be targeted by the main subtitle deletion action");
 startsBefore=Volatile.Read(ref fileStarts);pathBefore=player.GetPropertyString("path");timeBefore=player.GetPropertyDouble("time-pos");
 var keepFile=SubtitlePlayback.Current.PrepareRemoval(TrackId(savedC))??throw new Exception("Managed subtitle should allow deletion confirmation");
 Check(File.Exists(savedC)&&HasTrack(savedC),"Preparing a deletion confirmation has no destructive side effects");
 Check(SubtitlePlayback.Current.Remove(keepFile,false)&&File.Exists(savedC)&&File.Exists(originalC)
  &&!HasTrack(savedC)&&!library.Tracks(ImportedMedia(1)).Any(track=>track.Path==savedC),
  "Unchecking local deletion removes the track and saved association but retains both local and original files");
 var deleteFileRequest=SubtitlePlayback.Current.PrepareRemoval(TrackId(savedB))??throw new Exception("Managed second subtitle should allow deletion confirmation");
 Check(SubtitlePlayback.Current.Remove(deleteFileRequest,true)&&!File.Exists(savedB)&&!File.Exists(originalB)
  &&File.Exists(savedA)&&File.Exists(savedA2)&&!HasTrack(savedB)&&!HasTrack(originalB),
  "Checking local deletion removes the managed and original files and both native tracks without deleting other episodes");
 Check(player.GetPropertyString("secondary-sid")==importedSecondary&&HasTrack(danmakuFixture)
  &&Volatile.Read(ref fileStarts)==startsBefore&&player.GetPropertyString("path")==pathBefore
  &&Math.Abs(player.GetPropertyDouble("time-pos")-timeBefore)<.15,
  "Both deletion modes preserve the secondary subtitle, video connection and paused playback position");
 var stale=SubtitlePlayback.Current.PrepareRemoval(TrackId(savedA))??throw new Exception("Remaining subtitle should allow deletion confirmation");
 await Open(importedTitle+" S1E2");
 Check(!SubtitlePlayback.Current.Remove(stale,true)&&File.Exists(savedA)&&File.Exists(savedA2)
  &&library.Tracks(ImportedMedia(1)).Any(track=>track.Path==savedA),
  "A confirmation prepared before switching episodes cannot delete files from the previous playback generation");
 await Open(importedTitle+" S1E1");
 Check(SubtitleCount()==1&&HasTrack(savedA)&&!HasTrack(savedB)&&!HasTrack(savedC),
  "Removed associations stay removed after reopening even when their retained local files still exist");

 var moviePackage=new SubtitleResult(SubtitleSource.Assrt,"movie-special","电影合集","","",[]){Season=0};
 var movieQuery=new SubtitleQuery("导入电影",0,null,"");
 const string movieSubtitleName="[Cinema] Film Chapter One.srt";
 string movieSubtitle=library.FilePath(movieQuery,moviePackage,movieSubtitleName);
 Directory.CreateDirectory(Path.GetDirectoryName(movieSubtitle)!);File.Copy(supplied,movieSubtitle);
 library.Save(movieQuery,moviePackage,new(SubtitleSource.Assrt,moviePackage.Id,0,movieSubtitleName,""),movieSubtitle,true);
 var movieMedia=TraktMedia.Parse("导入电影 (2020)","",new Dictionary<string,string>());
 await Open("导入电影 (2020)");
 Check(SubtitleCount()==0,"A subtitle previously stored under specials is not automatically assigned to an unrelated movie shelf");
 player.LoadFiles([movieSubtitle],false,false);
 await WaitFor(()=>library.Tracks(movieMedia).Count==1
  &&library.Tracks(movieMedia).Any(track=>HasTrack(track.Path)&&Selected()==track.Path)&&!HasTrack(movieSubtitle),
  "Manually dropping an already stored subtitle selects its copy in the current movie shelf");
 string savedMovie=library.Tracks(movieMedia).Single().Path;
 Check(!savedMovie.Equals(movieSubtitle,StringComparison.OrdinalIgnoreCase)
  &&savedMovie.StartsWith(library.SeasonDirectory(movieMedia.Title,null)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
  &&File.Exists(movieSubtitle)&&File.ReadAllBytes(savedMovie).SequenceEqual(File.ReadAllBytes(movieSubtitle)),
  "Movie import stores an identical copy under the movie shelf and preserves the original special-episode file");
 await Open("导入电影 (2020)");
 Check(SubtitleCount()==1&&Selected()==savedMovie&&File.Exists(movieSubtitle),
  "Reopening selects the confirmed movie copy while the original special-episode file remains intact");

 const string batchTitle="批次回归作品";
 var batchMedia=TraktMedia.Parse(batchTitle+" S1E1","",new Dictionary<string,string>());
 string wrongEpisode=WriteImport("batch-invalid","[BadBatch] Batch Fixture S1E2.sc.srt","wrong episode");
 string validEpisode=WriteImport("batch-valid","[GoodBatch] Batch Fixture S1E1.sc.srt","correct episode");
 await Open(batchTitle+" S1E1");
 player.LoadFiles([wrongEpisode,validEpisode],false,false);
 await WaitFor(()=>library.Tracks(batchMedia).Count==1
  &&library.Tracks(batchMedia).Any(track=>track.Title==Path.GetFileName(validEpisode)&&Selected()==track.Path)
  &&!HasTrack(validEpisode),
  "A mismatched first subtitle in one multi-file drop does not prevent the following valid subtitle from importing and selecting");
 string savedBatch=library.Tracks(batchMedia).Single().Path;
 Check(File.Exists(wrongEpisode)&&File.Exists(validEpisode)
  &&!library.Results(batchTitle).SelectMany(item=>item.Files).Any(file=>file.Name==Path.GetFileName(wrongEpisode)),
  "The rejected subtitle is not persisted or deleted when a later item in the same drop succeeds");
 await Open(batchTitle+" S1E1");
 Check(SubtitleCount()==1&&Selected()==savedBatch&&!HasTrack(wrongEpisode),
  "Reopening after a partial batch failure restores only the successfully imported subtitle");
 await DownloadedSubtitleChecks();
 Console.WriteLine($"Native subtitle playback checks passed: {checks}; local generated video only; no desktop window or service calls.");
}

finally
{
 player.CommandVAsync("quit");await Task.WhenAll(events,mainEvents).WaitAsync(TimeSpan.FromSeconds(5));
 player.Destroy();
 if(Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(temp).StartsWith("AnimeFusion-NativeSubtitles-"))Directory.Delete(temp,true);
}

sealed class WindowSearchClient:ISubtitleClient
{
 TaskCompletionSource _finish=new(TaskCreationOptions.RunContinuationsAsynchronously);
 readonly System.Collections.Concurrent.ConcurrentQueue<SubtitleQuery> _queries=new();
 public SubtitleSource Source=>SubtitleSource.Assrt;
 public int Requests=>_queries.Count;
 public SubtitleQuery LastQuery=>_queries.LastOrDefault();
 bool _block;
 public bool Empty;
 public bool Fail;
 public bool Block{get=>_block;set{if(value&&!_block)_finish=new(TaskCreationOptions.RunContinuationsAsynchronously);_block=value;}}
 public void Finish(){Block=false;_finish.TrySetResult();}
 public async Task<SubtitlePage> SearchAsync(SubtitleQuery query,int page,CancellationToken token,IProgress<IReadOnlyList<SubtitleResult>>? progress=null)
 {
  _queries.Enqueue(query);
  if(Block)await _finish.Task.WaitAsync(token);
  token.ThrowIfCancellationRequested();
  if(Fail)throw new SubtitleServiceException("fixture request failed");
  return new(Empty?[]:[new(Source,"fixture-"+query.Language,query.Title,"","",[]){Season=query.RequestSeason??2,Episode=query.RequestEpisode??2,LanguageCode=query.Language}],false);
 }
 public Task<SubtitleResult> DetailAsync(SubtitleResult result,CancellationToken token)=>throw new Exception("Window search checks must not request subtitle details");
}

sealed class MemoryPresentationSource:System.Windows.PresentationSource
{
 public override System.Windows.Media.Visual RootVisual{get;set;}
 public override bool IsDisposed=>false;
 protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore()=>null;
}

sealed record DownloadedSubtitleFixture(string File,string MediaTitle,IReadOnlyList<string> Names,SubtitleResult Result,int? MalId=null);

sealed class SubtitleBytesHandler(byte[] bytes):HttpMessageHandler
{
 public int Requests;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
 {
  token.ThrowIfCancellationRequested();
  if(request.RequestUri?.AbsoluteUri!="https://subtitle-fixture.invalid/track")throw new Exception("Unexpected subtitle fixture request");
  Requests++;
  return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
 }
}
