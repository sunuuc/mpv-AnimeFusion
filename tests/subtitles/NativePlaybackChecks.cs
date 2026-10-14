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
Check((int)LibMpv.mpv_initialize(core)>=0,"Native initialized without a desktop window");
player.MainHandle=core;player.Handle=LibMpv.mpv_create_client(core,"subtitle-library-check");
SubtitlePlayback.Initialize(library);
TaskCompletionSource loaded=null;
player.FileLoaded+=()=>loaded?.TrySetResult();
var events=Task.Run(()=>player.EventLoop());
async Task Open(string title,string options="")
{
 loaded=new(TaskCreationOptions.RunContinuationsAsynchronously);
 player.CommandV("loadfile",video,"replace","-1","force-media-title="+title+(options.Length>0?","+options:""));
 await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
}
string Selected()=>player.GetPropertyString("current-tracks/sub/external-filename");
int SubtitleCount()=>Enumerable.Range(0,player.GetPropertyInt("track-list/count")).Count(i=>player.GetPropertyString($"track-list/{i}/type")=="sub");
try
{
 await Open("测试作品 S2E1");
 Check(SubtitleCount()==2,"All downloaded current-episode alternatives enter the real subtitle menu");
 Check(Selected().Equals(sc,StringComparison.OrdinalIgnoreCase),"Previous local choice selected on file load");
 Check(player.GetPropertyString("current-tracks/sub/title")==Path.GetFileName(sc),"Registered subtitle titles contain only the original filename");
 player.CommandV("sub-add",supplied,"auto","secondary fixture");
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
   var resources=(System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(new Uri("/mpv-AnimeFusion;component/WPF/Resources.xaml",UriKind.Relative));
   var application=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
   application.Resources=resources;
   var list=new System.Windows.Controls.ListBox{Style=(System.Windows.Style)resources["MatchList"],Width=500,Height=280};
   Check(System.Windows.Controls.VirtualizingPanel.GetScrollUnit(list)==System.Windows.Controls.ScrollUnit.Pixel
    &&System.Windows.Controls.VirtualizingPanel.GetIsVirtualizingWhenGrouping(list),"Compiled player resources contain pixel scrolling for grouped lists");
   list.ApplyTemplate();list.Measure(new System.Windows.Size(500,280));list.Arrange(new System.Windows.Rect(0,0,500,280));list.UpdateLayout();
   var border=(System.Windows.Controls.Border)list.Template.FindName("Bd",list);
   Check(border.Child is HandyControl.Controls.ScrollViewer{IsPixelScrollingEnabled:true},"Actual compiled list template enables direct pixel input without per-event animations");
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
    type.GetMethod("OnClosing",fields)!.Invoke(window,[new System.ComponentModel.CancelEventArgs()]);
    window.RestoreResults();
    type.GetMethod("Window_Loaded",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);
    Check(handler.Requests==requests&&ReferenceEquals(completed,type.GetField("_searchResult",fields)!.GetValue(window)),
     "A real window close/restore sequence adds zero HTTP requests and retains the same complete search snapshot");
    type.GetMethod("Search_Click",fields)!.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);WaitForSearch();
    Check(handler.Requests>requests&&!ReferenceEquals(completed,type.GetField("_searchResult",fields)!.GetValue(window)),
     "The explicit Search button still issues a new request after restoring the cached window");
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
