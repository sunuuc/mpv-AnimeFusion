using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Net.Http;
using MpvNet;
using MpvNet.Native;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;

string native=Environment.GetEnvironmentVariable("ANIMEFUSION_NATIVE_DIRECTORY")??throw new Exception("Native runtime path required");
string video=Environment.GetEnvironmentVariable("ANIMEFUSION_TEST_VIDEO")??throw new Exception("Generated test video required");
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
 await SubtitlePlayback.Current.DownloadAsync(new("测试作品",2,1,"zh"),localResult,progressStore,true);
 Check(SubtitlePlayback.Current.DownloadStatus==""&&Selected().Equals(sc,StringComparison.OrdinalIgnoreCase),"Playback-owned loading clears progress and selects the real subtitles without a success notice");
 Check(player.GetPropertyInt("secondary-sid")==secondary,"Download progress leaves the secondary/danmaku track selected");
 await SubtitlePlayback.Current.DownloadAsync(new("测试作品",2,99,"zh"),localResult,progressStore,true);
 Check(SubtitlePlayback.Current.DownloadStatus=="没有当前集的字幕。","Loading errors are presented in playback, not the closed search window");
 int tcId=Enumerable.Range(0,player.GetPropertyInt("track-list/count")).Where(i=>player.GetPropertyString($"track-list/{i}/external-filename")==tc).Select(i=>player.GetPropertyInt($"track-list/{i}/id")).Single();
 player.SetPropertyInt("sid",tcId);
 var deadline=DateTime.UtcNow.AddSeconds(3);
 while(!library.Tracks(TraktMedia.Parse("测试作品 S2E2","",new Dictionary<string,string>())).Any(t=>t.Preferred&&t.Path.EndsWith(".tc.srt"))&&DateTime.UtcNow<deadline)await Task.Delay(20);
 Check(library.Tracks(TraktMedia.Parse("测试作品 S2E2","",new Dictionary<string,string>())).Single(t=>t.Preferred).Path.EndsWith(".tc.srt"),"Real sid change persists version preference");
 await Open("测试作品 S2E2");
 Check(SubtitlePlayback.Current.DownloadStatus=="","Switching media clears the previous download notice and generation");
 Check(typeof(SubtitleResult).Assembly.GetType("MpvNet.Windows.Subtitles.OpenSubtitlesClient")==null,"Removed service implementation is absent from the loaded player assembly");
 Check(Enum.GetNames<SubtitleSource>().SequenceEqual(new[]{"Assrt"}),"ASSRT is the only online subtitle source in the loaded assembly");
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
    var pending=new SubtitleSearchResult(new("测试作品",2,2,""));
    type.GetField("_searchResult",fields)!.SetValue(window,pending);
    items.Add(result);items.Add(result with{Id="2"});
    void Refresh()=>type.GetMethod("RefreshResults",fields)!.Invoke(window,null);
    var empty=(System.Windows.Controls.StackPanel)window.FindName("EmptyState");
    var status=(System.Windows.Controls.TextBlock)window.FindName("Status");
    var count=(System.Windows.Controls.TextBlock)window.FindName("ResultCount");
    var languages=(System.Windows.Controls.WrapPanel)window.FindName("LanguageChips");
    string[] LanguageTags()=>languages.Children.Cast<System.Windows.Controls.Button>().Select(button=>(string)button.Tag).ToArray();
    Refresh();
    Check(LanguageTags().SequenceEqual(new[]{"","zh"}),"Compiled language filter shows only languages present in the results");
    items.Add(result with{Id="fr",LanguageCode="fr"});items.Add(result with{Id="ko",LanguageCode="ko"});
    items.Add(result with{Id="other",LanguageCode=""});Refresh();
    Check(LanguageTags().ToHashSet().SetEquals(new[]{"","zh","fr","ko","other"})&&!LanguageTags().Contains("en")&&!LanguageTags().Contains("ja"),
     "Compiled window dynamically adds French, Korean and Other without inventing absent English or Japanese");
    type.GetMethod("Language_Click",fields)!.Invoke(window,[languages.Children.Cast<System.Windows.Controls.Button>().Single(button=>(string)button.Tag=="ko"),new System.Windows.RoutedEventArgs()]);
    Check(System.Windows.Data.CollectionViewSource.GetDefaultView(items).Cast<SubtitleResult>().Single().Id=="ko",
     "Dynamic language button filters cached results without a search");
    items.RemoveAt(4);items.RemoveAt(3);items.RemoveAt(2);Refresh();
    Check(LanguageTags().SequenceEqual(new[]{"","zh"})&&(string)type.GetField("_language",fields)!.GetValue(window)! =="",
     "Absent language buttons disappear and an unavailable selection returns to All");
    type.GetField("_language",fields)!.SetValue(window,"zh");Refresh();
    Check(empty.Visibility==System.Windows.Visibility.Collapsed&&status.Text=="正在搜索字幕…"&&count.Text=="共 2 条搜索结果",
     "Actual compiled window immediately shows local count and only footer progress while network search is pending");
    items.Clear();Refresh();
    Check(empty.Visibility==System.Windows.Visibility.Collapsed&&status.Text=="正在搜索字幕…"&&count.Text=="共 0 条搜索结果",
     "Pending empty search also has no central progress overlay");
    typeof(SubtitleSearchResult).GetProperty("Complete")!.SetValue(pending,true);Refresh();
    Check(empty.Visibility==System.Windows.Visibility.Visible&&status.Text=="","Finished empty search displays one central empty state without duplicate footer text");
    items.Add(result);Refresh();
    Check(empty.Visibility==System.Windows.Visibility.Collapsed&&status.Text==""&&count.Text=="共 1 条搜索结果","Finished results hide all loading placeholders");
    type.GetField("_ready",fields)!.SetValue(window,true);
    var searchBox=(System.Windows.Controls.TextBox)window.FindName("SearchBox");
    type.GetField("_displayedKeyword",fields)!.SetValue(window,searchBox.Text);
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
     &&(string)type.GetField("_language",fields)!.GetValue(window)! =="zh",
     "Reopening keeps the season and language filters");
    var handler=new SearchHandler();var api=new HttpClient(handler);
    ((HttpClient)type.GetField("_api",fields)!.GetValue(window)!).Dispose();
    ((SubtitleSearch)type.GetField("_search",fields)!.GetValue(window)!).Dispose();
    var assrt=new AssrtClient(api,fixtureStore);
    type.GetField("_api",fields)!.SetValue(window,api);
    type.GetField("_assrt",fields)!.SetValue(window,assrt);
    type.GetField("_search",fields)!.SetValue(window,new SubtitleSearch(assrt));
    void WaitForSearch()
    {
     var until=DateTime.UtcNow.AddSeconds(20);
     while(DateTime.UtcNow<until)
     {
      var frame=new System.Windows.Threading.DispatcherFrame();
      System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));
      System.Windows.Threading.Dispatcher.PushFrame(frame);
      if(type.GetField("_searchResult",fields)!.GetValue(window) is SubtitleSearchResult{Complete:true})return;
      Thread.Sleep(1);
     }
     throw new Exception("Mock subtitle search did not complete");
    }
    type.GetMethod("Search",fields)!.Invoke(window,[true]);WaitForSearch();
    int requests=handler.Requests;Check(requests>0,"The first window search performs the isolated HTTP request");
    var completed=type.GetField("_searchResult",fields)!.GetValue(window);
    generation=(int)type.GetField("_searchGeneration",fields)!.GetValue(window)!;
    var displayed=items.ToArray();var originalQuery=type.GetField("_query",fields)!.GetValue(window);
    foreach(string text in new[]{"测试作品 新", "测试作品", "测试作", ""})searchBox.Text=text;
    searchBox.Text="测试作";
    var editFrame=new System.Windows.Threading.DispatcherFrame();
    var editTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(800)};
    editTimer.Tick+=(_,_)=>{editTimer.Stop();editFrame.Continue=false;};editTimer.Start();
    System.Windows.Threading.Dispatcher.PushFrame(editFrame);
    Check(handler.Requests==requests&&(int)type.GetField("_searchGeneration",fields)!.GetValue(window)! ==generation,
     "Typing, deleting and clearing text issue zero extra requests after the former debounce interval");
    Check(items.SequenceEqual(displayed)&&Equals(originalQuery,type.GetField("_query",fields)!.GetValue(window)),
     "Editing preserves the searched results and their query for filtering and downloading");
    type.GetMethod("OnClosing",fields)!.Invoke(window,[new System.ComponentModel.CancelEventArgs()]);
    window.RestoreResults();
    type.GetMethod("Window_Loaded",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);
    Check(handler.Requests==requests&&ReferenceEquals(completed,type.GetField("_searchResult",fields)!.GetValue(window)),
     "A real window close/restore sequence adds zero HTTP requests and retains the same complete search snapshot");
    Check(searchBox.Text=="测试作"&&items.Select(item=>(item.Id,item.Title)).SequenceEqual(displayed.Select(item=>(item.Id,item.Title))),
     "Reopening retains the edited draft and existing results without searching the draft");
    var oldRequest=(System.Threading.CancellationTokenSource)type.GetField("_searchLifetime",fields)!.GetValue(window)!;
    type.GetMethod("Search_Click",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);
    Check(oldRequest.IsCancellationRequested,"An explicit search cancels the previous request before submitting the new keyword");
    WaitForSearch();
    Check(handler.Requests>requests&&!ReferenceEquals(completed,type.GetField("_searchResult",fields)!.GetValue(window)),
     "The explicit Search button still issues a new request after restoring the cached window");
    Check(((SubtitleSearchResult)type.GetField("_searchResult",fields)!.GetValue(window)!).Query.Title=="测试作",
     "The explicit Search button submits the edited keyword");
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
 Console.WriteLine($"Native subtitle playback checks passed: {checks}; local generated video only; no desktop window or service calls.");
}

finally
{
 player.CommandV("quit");await events.WaitAsync(TimeSpan.FromSeconds(5));
 LibMpv.mpv_destroy(player.Handle);LibMpv.mpv_destroy(core);
 if(Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(temp).StartsWith("AnimeFusion-NativeSubtitles-"))Directory.Delete(temp,true);
}

sealed class SearchHandler:HttpMessageHandler
{
 public int Requests;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
 {
  Requests++;
  return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"status\":0,\"sub\":{\"subs\":[{\"id\":101,\"native_name\":\"测试作品 第二季\",\"lang\":{\"desc\":\"简\"}}]}}",Encoding.UTF8,"application/json")});
 }
}
