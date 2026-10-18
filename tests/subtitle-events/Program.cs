using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MpvNet;
using MpvNet.Native;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;
using MpvNet.Windows.UI;
using MpvNet.Windows.WPF;
using Forms = System.Windows.Forms;

static class Checks
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    static int _checks;
    static readonly List<Exception> Errors = [];
    static void Check(bool value, string message)
    { if (!value) throw new Exception(message); _checks++; }
    static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        if (Errors.Count > 0) throw new AggregateException("A routed subtitle event escaped to the dispatcher", Errors);
    }
    static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(560,620));
        element.Arrange(new Rect(0,0,560,620));
        element.UpdateLayout(); Pump();
    }
    static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    static void Wait(Func<bool> ready)
    {
        for (int n = 0; n < 10000; n++)
        {
            Pump(); if (ready()) return;
            Thread.Sleep(1);
        }
        throw new TimeoutException("The isolated search did not finish");
    }
    static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for(int n=0;n<VisualTreeHelper.GetChildrenCount(parent);n++)
        {
            var child=VisualTreeHelper.GetChild(parent,n);
            if(child is T item)yield return item;
            foreach(var descendant in Children<T>(child))yield return descendant;
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        string native=Environment.GetEnvironmentVariable("ANIMEFUSION_NATIVE_DIRECTORY")??throw new Exception("Native directory required");
        NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly,(name,_,_)=>NativeLibrary.Load(Path.Combine(native,name)));
        string temporary=Path.Combine(Path.GetTempPath(),"AnimeFusion-SubtitleEvents-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        nint core=LibMpv.mpv_create();
        byte[] Bytes(string value)=>Encoding.UTF8.GetBytes(value+"\0");
        foreach(var option in new Dictionary<string,string>{{"config","no"},{"load-scripts","no"},{"vo","null"},{"ao","null"},{"idle","yes"},{"force-window","no"}})
            Check(LibMpv.mpv_set_option_string(core,Bytes(option.Key),Bytes(option.Value))>=0,"Headless native option accepted");
        Check((int)LibMpv.mpv_initialize(core)>=0,"Idle native core initialized without opening a file");
        Global.Player.MainHandle=core;Global.Player.Handle=LibMpv.mpv_create_client(core,"subtitle-events");
        SubtitlePlayback.Initialize(new SubtitleLibrary(Path.Combine(temporary,"Subtitles")));
        Theme.Current=new(){Foreground=Brushes.White,Foreground2=Brushes.LightGray,Background=Brushes.DarkSlateGray,
            MenuBackground=Brushes.Black,MenuHighlight=Brushes.DimGray,Heading=Brushes.DeepSkyBlue};
        WpfApplication.Init();
        Application.Current.DispatcherUnhandledException+=(_,args)=>{Errors.Add(args.Exception);args.Handled=true;};
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window=new SubtitleSearchWindow();
        try
        {
            RunSourceSettingsChecks(temporary);
            var type=window.GetType();
            var media=TraktMedia.Parse("事件回归 S4E1","",new Dictionary<string,string>());
            type.GetField("_media",Fields)!.SetValue(window,media);
            type.GetField("_titles",Fields)!.SetValue(window,new SubtitleTitles(media,default));
            type.GetField("_store",Fields)!.SetValue(window,new SubtitleStore(Path.Combine(temporary,"account")));
            ((SubtitleSearch)type.GetField("_search",Fields)!.GetValue(window)!).Dispose();
            var client=new MemoryClient();
            type.GetField("_search",Fields)!.SetValue(window,new SubtitleSearch(client));
            var input=(TextBox)window.FindName("SearchBox"); input.Text=media.Title;
            if(args.Contains("--visible"))
            {
                RunVisible(window,media,client);
                return;
            }
            var content=(FrameworkElement)window.Content;
            type.GetMethod("Window_Loaded",Fields)!.Invoke(window,[window,new RoutedEventArgs()]);
            Layout(content);
            var search=(Button)window.FindName("SearchButton");
            RunFilterTitleChecks(window,media,client);
            RunEditedActiveSearchChecks(window,media,client);
            int filterSearches=client.Requests;
            for(int n=0;n<5;n++)
            {
                input.Text=n==0?media.Title:"手工搜索 "+n;
                Click(search);
                Wait(()=>type.GetField("_searchResult",Fields)!.GetValue(window) is SubtitleSearchResult{Complete:true});
                Layout(content);
                foreach(var (panel,tag) in new[]{("SourceChips","assrt"),("SourceChips","all"),("SeasonChips","4"),("EpisodeChips","1"),("LanguageChips","ja"),("LanguageChips","")})
                {
                    var button=((Panel)window.FindName(panel)).Children.Cast<Button>().Single(item=>(string)item.Tag==tag);
                    Click(button);Layout(content);
                }
                Check(Errors.Count==0,"Search and default-filter routed clicks complete without dispatcher exceptions");
            }
            var aliases=(ListBox)window.FindName("AliasesList");
            var popup=(Popup)window.FindName("AliasesPopup");
            aliases.ItemsSource=new[]{"事件回归","Event Check","イベント検証"};
            Layout((FrameworkElement)popup.Child);
            for(int index=0;index<3;index++)
            {
                var item=(ListBoxItem)aliases.ItemContainerGenerator.ContainerFromIndex(index)!;
                var label=Children<TextBlock>(item).Single(text=>text.Text==(string)item.DataContext);
                aliases.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                    {RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent,Source=label});
                Pump();
                Check(input.Text==(string)item.DataContext+" S04E01","Alias-row routed click retains the selected season and episode without opening a popup");
            }
            Check(!popup.IsOpen&&PresentationSource.FromVisual(popup.Child)==null&&new WindowInteropHelper(window).Handle==IntPtr.Zero,
                "Event checks create neither a desktop window nor a popup presentation source");
            Check(Global.Player.GetPropertyString("path")==""&&client.Requests==filterSearches+5,"No video was opened and all explicit searches used the in-memory client");
            RunSourceSearchChecks(temporary);
            Console.WriteLine($"Subtitle routed event checks passed: {_checks}; no file playback, network, Show or popup opening.");
        }
        finally
        {
            if(!(bool)window.GetType().GetField("_closed",Fields)!.GetValue(window)!)
            { window.GetType().GetField("_canClose",Fields)!.SetValue(window,true);window.Close(); }
            LibMpv.mpv_destroy(Global.Player.Handle);LibMpv.mpv_destroy(core);
            string full=Path.GetFullPath(temporary);
            if(full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("AnimeFusion-SubtitleEvents-",StringComparison.Ordinal))Directory.Delete(full,true);
        }
    }

    static void RunSourceSettingsChecks(string temporary)
    {
        var sources=Enum.GetValues<SubtitleSource>();
        var defaults=new SubtitleCredentials("");
        Check(sources.All(defaults.IsEnabled),"All three subtitle sources are enabled by default");
        var store=new SubtitleStore(Path.Combine(temporary,"source-settings"));
        using var handler=new NoNetworkHandler();
        using var http=new HttpClient(handler);
        var window=new SubtitleSourcesWindow(store,http);
        try
        {
            Layout((FrameworkElement)window.Content);
            Check(!Directory.Exists(store.Root)&&!window.Saved&&handler.Requests==0,
                "Constructing and laying out source settings neither writes settings nor requests a service");
            Check(new[]{"AssrtEnabledToggle","AmenzbEnabledToggle","TsukihimeEnabledToggle"}
                .All(name=>((ToggleButton)window.FindName(name)).IsChecked==true),
                "A new source settings window displays all sources enabled");
            Check(Children<TextBlock>((FrameworkElement)window.Content).Any(text=>text.Text=="ameNZB（测试）")
                &&Children<TextBlock>((FrameworkElement)window.Content).Any(text=>text.Text=="TsukiHime（测试）"),
                "The two experimental source labels are present in the settings layout");
            Check(new WindowInteropHelper(window).Handle==IntPtr.Zero&&PresentationSource.FromVisual((Visual)window.Content)==null,
                "Source settings layout creates no native window or presentation source");
        }
        finally{window.Close();}

        const string fixtureKey="isolated-source-settings-key";
        store.Save(new(fixtureKey));
        string savedPath=Path.Combine(store.Root,"account.bin");
        byte[] before=File.ReadAllBytes(savedPath);
        window=new SubtitleSourcesWindow(store,http);
        int changes=0;
        window.SettingsChanged+=(_,_)=>changes++;
        try
        {
            Layout((FrameworkElement)window.Content);
            Check(File.ReadAllBytes(savedPath).SequenceEqual(before)&&!window.Saved,
                "Opening settings with a saved API key does not rewrite its encrypted account file");
            foreach(var (name,source) in new[]{("AssrtEnabledToggle",SubtitleSource.Assrt),
                ("AmenzbEnabledToggle",SubtitleSource.Amenzb),("TsukihimeEnabledToggle",SubtitleSource.Tsukihime)})
            {
                var toggle=(ToggleButton)window.FindName(name);
                var expected=store.Credentials with {};
                toggle.IsChecked=false;Click(toggle);Pump();Layout((FrameworkElement)window.Content);
                var reloaded=new SubtitleStore(store.Root);
                Check(!store.Credentials.IsEnabled(source)&&!reloaded.Credentials.IsEnabled(source)&&window.Saved,
                    "Clicking a source switch persists its disabled state for the next settings session");
                Check(sources.Where(other=>other!=source).All(other=>store.Credentials.IsEnabled(other)==expected.IsEnabled(other))
                    &&store.Credentials.AssrtToken==fixtureKey&&reloaded.Credentials.AssrtToken==fixtureKey,
                    "Changing one source preserves the other switches and the saved ASSRT key");
                Check(((TextBlock)window.FindName("SourcesStatus")).Text==""&&handler.Requests==0,
                    "Saving a source switch succeeds without any API request");
            }
            Check(((FrameworkElement)window.FindName("AssrtSettings")).Visibility==Visibility.Collapsed
                &&((PasswordBox)window.FindName("AssrtTokenBox")).Password==fixtureKey,
                "Disabling ASSRT hides its key controls while retaining the key");
            Check(sources.All(source=>!new SubtitleStore(store.Root).Credentials.IsEnabled(source)),
                "All-disabled source settings survive an independent store reload");
            Check(changes==3,"Each successfully saved source switch raises exactly one settings-change event");
            var assrt=(ToggleButton)window.FindName("AssrtEnabledToggle");
            assrt.IsChecked=true;Click(assrt);Layout((FrameworkElement)window.Content);
            Check(((FrameworkElement)window.FindName("AssrtSettings")).Visibility==Visibility.Visible
                &&((PasswordBox)window.FindName("AssrtTokenBox")).Password==fixtureKey
                &&new SubtitleStore(store.Root).Credentials.AssrtToken==fixtureKey,
                "Re-enabling ASSRT restores the existing key controls without discarding its key");
            Check(changes==4,"Re-enabling a source immediately notifies the search window once");
            foreach(string name in new[]{"AssrtEnabledToggle","AmenzbEnabledToggle","TsukihimeEnabledToggle"})
            {
                var toggle=(ToggleButton)window.FindName(name);
                Check(toggle.ActualWidth>0&&toggle.ActualHeight>0
                    &&((FrameworkElement)toggle.Template.FindName("Thumb",toggle)).HorizontalAlignment
                        ==(toggle.IsChecked==true?HorizontalAlignment.Right:HorizontalAlignment.Left),
                    "Each rendered switch places its thumb on the side indicated by its saved state");
            }
        }
        finally{window.Close();}

        window=new SubtitleSourcesWindow(new SubtitleStore(store.Root),http);
        int failures=0;
        window.SettingsChanged+=(_,_)=>failures++;
        try
        {
            Check(((ToggleButton)window.FindName("AssrtEnabledToggle")).IsChecked==true
                &&((ToggleButton)window.FindName("AmenzbEnabledToggle")).IsChecked==false
                &&((ToggleButton)window.FindName("TsukihimeEnabledToggle")).IsChecked==false,
                "Reopening the settings window restores each independently saved switch");
            var savedStore=(SubtitleStore)window.GetType().GetField("_store",Fields)!.GetValue(window)!;
            var expected=savedStore.Credentials;
            byte[] expectedFile=File.ReadAllBytes(savedPath);
            Directory.CreateDirectory(savedPath+".tmp");
            var toggle=(ToggleButton)window.FindName("AmenzbEnabledToggle");
            toggle.IsChecked=true;Click(toggle);Pump();
            Check(toggle.IsChecked==false&&savedStore.Credentials==expected&&!window.Saved&&failures==0,
                "A failed switch save restores the control and retains the previous in-memory settings");
            Check(File.ReadAllBytes(savedPath).SequenceEqual(expectedFile)
                &&new SubtitleStore(store.Root).Credentials==expected
                &&((TextBlock)window.FindName("SourcesStatus")).Text.Length>0&&handler.Requests==0,
                "A failed switch save preserves the saved key and flags and displays an error without networking");
            Check(new WindowInteropHelper(window).Handle==IntPtr.Zero,
                "Settings persistence and failure checks never create a desktop window");
        }
        finally{window.Close();}
    }

    static void RunSourceSearchChecks(string temporary)
    {
        var store=new SubtitleStore(Path.Combine(temporary,"source-search-settings"));
        var media=TraktMedia.Parse("来源开关回归 S4E1","",new Dictionary<string,string>());
        var library=new SubtitleLibrary(Path.Combine(temporary,"source-search-library"));
        string path=Path.Combine(library.Root,"来源开关回归 S04E01.ja.srt");
        File.WriteAllText(path,"1\n00:00:00,000 --> 00:00:01,000\nlocal source fixture\n");
        var file=new SubtitleFile(SubtitleSource.Assrt,"source-local",1,Path.GetFileName(path),"");
        var local=new SubtitleResult(SubtitleSource.Assrt,"source-local",media.Title,"","",[file])
            {Season=4,Episode=1,LanguageCode="ja",Individual=true};
        library.Save(new(media.Title,4,1,"ja"),local,file,path,true);
        var clients=Enum.GetValues<SubtitleSource>().Select(source=>new MemoryClient(source)).ToArray();
        var window=new SubtitleSearchWindow();
        var type=window.GetType();
        try
        {
            type.GetField("_media",Fields)!.SetValue(window,media);
            type.GetField("_titles",Fields)!.SetValue(window,new SubtitleTitles(media,default));
            type.GetField("_store",Fields)!.SetValue(window,store);
            type.GetField("_library",Fields)!.SetValue(window,library);
            void UseMemorySearch()
            {
                ((SubtitleSearch)type.GetField("_search",Fields)!.GetValue(window)!).Dispose();
                type.GetField("_search",Fields)!.SetValue(window,new SubtitleSearch(clients));
            }
            UseMemorySearch();
            var input=(TextBox)window.FindName("SearchBox");input.Text=media.Title;
            type.GetMethod("Window_Loaded",Fields)!.Invoke(window,[window,new RoutedEventArgs()]);
            var content=(FrameworkElement)window.Content;
            Layout(content);
            var search=(Button)window.FindName("SearchButton");
            Button Chip(string tag)=>((Panel)window.FindName("SourceChips")).Children.Cast<Button>().Single(item=>(string)item.Tag==tag);
            int Requests()=>clients.Sum(client=>client.Requests);
            SubtitleSearchResult? State()=>(SubtitleSearchResult?)type.GetField("_searchResult",Fields)!.GetValue(window);
            void Apply(SubtitleCredentials credentials)
            {
                int before=Requests();
                store.Save(credentials);
                type.GetMethod("ApplySourceSettings",Fields)!.Invoke(window,[]);
                UseMemorySearch();Layout(content);
                Check(Requests()==before,"Applying source settings refreshes controls without starting a search");
            }
            void Search(params SubtitleSource[] expected)
            {
                int[] before=clients.Select(client=>client.Requests).ToArray();
                Click(search);Wait(()=>State() is {Complete:true});Layout(content);
                Check(clients.Select((client,index)=>client.Requests-before[index])
                    .SequenceEqual(clients.Select(client=>expected.Contains(client.Source)?1:0)),
                    "An explicit search requests exactly the enabled sources selected by the current source filter");
            }
            Check(Requests()==0&&new[]{"all","assrt","amenzb","tsukihime","local"}
                .All(tag=>Chip(tag).Visibility==Visibility.Visible),
                "The new search window exposes all default sources without automatically searching");
            Check((string)Chip("amenzb").Content=="ameNZB（测试）"&&(string)Chip("tsukihime").Content=="TsukiHime（测试）",
                "Search source chips identify the two experimental providers");
            Search(SubtitleSource.Assrt,SubtitleSource.Amenzb,SubtitleSource.Tsukihime);
            Click(Chip("assrt"));
            Apply(store.Credentials with {AssrtEnabled=false});
            Check((string)type.GetField("_source",Fields)!.GetValue(window)! == "all"
                &&Chip("assrt").Visibility==Visibility.Collapsed&&Chip("local").Visibility==Visibility.Visible,
                "Disabling the selected source removes its chip and returns the selection to all while keeping local");
            int beforeDisabled=Requests();Click(Chip("assrt"));Pump();
            Check((string)type.GetField("_source",Fields)!.GetValue(window)! == "all"&&Requests()==beforeDisabled,
                "A routed click on a disabled source cannot reactivate or request it");
            Search(SubtitleSource.Amenzb,SubtitleSource.Tsukihime);
            Click(Chip("tsukihime"));Search(SubtitleSource.Tsukihime);
            Apply(store.Credentials with {AmenzbEnabled=false,TsukihimeEnabled=false});
            Check(new[]{"assrt","amenzb","tsukihime"}.All(tag=>Chip(tag).Visibility==Visibility.Collapsed)
                &&Chip("all").Visibility==Visibility.Visible&&Chip("local").Visibility==Visibility.Visible,
                "All online source chips collapse when disabled, leaving all and local available");
            Search();
            var rows=(ObservableCollection<SubtitleResult>)type.GetField("_results",Fields)!.GetValue(window)!;
            bool Visible(SubtitleResult row)=>(bool)type.GetMethod("FilterResult",Fields)!.Invoke(window,[row])!;
            Check(rows.Any(row=>row.Id=="source-local"&&row.Local&&Visible(row)),
                "A downloaded ASSRT subtitle remains visible under all even when ASSRT and all online sources are disabled");
            Click(Chip("local"));Search();
            Check(rows.Any(row=>row.Id=="source-local"&&Visible(row))&&rows.Where(Visible).All(row=>row.Local),
                "Explicit local search retains downloaded subtitles and makes no provider request");
            Apply(store.Credentials with {AssrtEnabled=true});
            Check((string)type.GetField("_source",Fields)!.GetValue(window)! == "local"
                &&Chip("assrt").Visibility==Visibility.Visible,
                "Re-enabling an online source restores its chip without replacing a local selection");
            Click(Chip("all"));Search(SubtitleSource.Assrt);
            var client=clients.Single(client=>client.Source==SubtitleSource.Assrt);
            client.HoldNext=true;
            int beforeHold=client.Requests;Click(search);
            Wait(()=>client.Requests==beforeHold+1&&State() is {Complete:false});
            var token=client.PendingToken;var pending=client.Pending!;
            using var noNetwork=new NoNetworkHandler();
            using var http=new HttpClient(noNetwork);
            var settings=new SubtitleSourcesWindow(store,http);
            int notifications=0;
            try
            {
                settings.SettingsChanged+=(_,_)=>
                {
                    notifications++;
                    type.GetMethod("ApplySourceSettings",Fields)!.Invoke(window,[]);
                    UseMemorySearch();
                };
                var toggle=(ToggleButton)settings.FindName("AssrtEnabledToggle");
                toggle.IsChecked=false;Click(toggle);Pump();
                Check(notifications==1&&noNetwork.Requests==0&&!store.Credentials.AssrtEnabled
                    &&new WindowInteropHelper(settings).Handle==IntPtr.Zero,
                    "A real source settings click immediately applies its change event without opening a window or networking");
            }
            finally{settings.Close();}
            Wait(()=>client.Cancelled>0);
            pending.TrySetResult();Pump();
            Check(token.IsCancellationRequested&&State()==null&&(string)search.Content=="搜索",
                "Disabling a source cancels its pending page and the old response cannot restore an active search");
            Check(new WindowInteropHelper(window).Handle==IntPtr.Zero
                &&PresentationSource.FromVisual(content)==null&&Global.Player.GetPropertyString("path")=="",
                "Source search event checks use no desktop window or video playback");
        }
        finally
        {
            type.GetField("_canClose",Fields)!.SetValue(window,true);window.Close();
        }
    }

    static void RunFilterTitleChecks(SubtitleSearchWindow window,TraktMedia media,MemoryClient client)
    {
        var type=window.GetType();
        var content=(FrameworkElement)window.Content;
        var input=(TextBox)window.FindName("SearchBox");
        var search=(Button)window.FindName("SearchButton");
        var aliases=(ListBox)window.FindName("AliasesList");
        var popup=(Popup)window.FindName("AliasesPopup");
        void Pick(string panel,string tag)
        {
            int requests=client.Requests;
            var button=((Panel)window.FindName(panel)).Children.Cast<Button>().Single(item=>(string)item.Tag==tag);
            Click(button);Layout(content);
            Check(client.Requests==requests,"Changing a filter updates the input without searching");
        }
        void Alias(string name)
        {
            int requests=client.Requests;
            aliases.ItemsSource=new[]{name};Layout((FrameworkElement)popup.Child);
            var item=(ListBoxItem)aliases.ItemContainerGenerator.ContainerFromIndex(0)!;
            var label=Children<TextBlock>(item).Single(text=>text.Text==name);
            aliases.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                {RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent,Source=label});
            Pump();
            Check(client.Requests==requests&&!popup.IsOpen,"Selecting a cached alias updates only the input without searching or showing a popup");
        }
        void Search(string text,int? season,int? episode,string? draft=null)
        {
            Check(input.Text==(draft??text),"The search box contains the expected text before the explicit search");
            int requests=client.Requests;
            Click(search);
            Wait(()=>client.Requests==requests+1&&type.GetField("_searchResult",Fields)!.GetValue(window) is SubtitleSearchResult{Complete:true});
            Layout(content);
            var query=client.Queries.Last();
            Check(input.Text==text&&query.Title==text&&query.RequestSeason==season&&query.RequestEpisode==episode,
                "Explicit search sends the visible title and the same structured season/episode conditions");
        }

        Check(input.Text==media.Title&&client.Requests==0
            &&new[]{"_source","_season","_episode"}.All(field=>(string)type.GetField(field,Fields)!.GetValue(window)! == "all")
            &&(string)type.GetField("_language",Fields)!.GetValue(window)! == "",
            "A newly loaded window starts with all filters and an unsuffixed title without searching");
        Pick("SeasonChips","4");Pick("EpisodeChips","1");
        Check(input.Text==media.Title+" S04E01","Selecting the current season and episode appends one S04E01 suffix");
        Alias("Event Check");
        Search("Event Check S04E01",4,1);
        Pick("EpisodeChips","2");
        Check(input.Text=="Event Check S04E02","Changing episode replaces its existing suffix");
        Pick("SeasonChips","3");Pick("EpisodeChips","3");Pick("EpisodeChips","3");
        Search("Event Check S03E03",3,3);
        Pick("SeasonChips","all");Pick("EpisodeChips","1");
        Search("Event Check E01",null,1);
        Pick("SeasonChips","4");Pick("EpisodeChips","all");
        Search("Event Check S04",4,null);
        Pick("SeasonChips","all");
        Search("Event Check",null,null);
        input.Text="手工作品 S09E08";
        Search("手工作品 S09E08",null,null);
        Alias("Event Check");
        Pick("SeasonChips","0");Pick("EpisodeChips","1");
        Search("Event Check S00E01",0,1);
        input.Text="手工标题 S12E09";Pick("SeasonChips","4");
        Check(input.Text=="手工标题 S04E01","A manually typed S/E suffix is replaced instead of duplicated");
        input.Text="手工标题 E08";Pick("EpisodeChips","2");
        Check(input.Text=="手工标题 S04E02","A manually typed episode-only suffix is replaced by the selected conditions");
        input.Text="手工标题 S07";Pick("SeasonChips","all");
        Check(input.Text=="手工标题 E02","A manually typed season-only suffix is replaced by the selected episode");
        Pick("EpisodeChips","all");
        Check(input.Text=="手工标题","Selecting all season and episode removes the standard suffix");
        input.Text="";Pick("SeasonChips","4");Pick("EpisodeChips","1");
        Check(input.Text=="","Selecting conditions leaves an empty search box empty");
        int beforeEmpty=client.Requests;Click(search);Pump();
        Check(client.Requests==beforeEmpty,"An empty search does not request a provider using a season/episode-only keyword");
        Alias(media.Title);
        Check(input.Text==media.Title+" S04E01","Selecting an alias from an empty box restores its title and retained conditions");
        input.Text="重新输入标题";
        Search("重新输入标题 S04E01",4,1,"重新输入标题");
        Alias(media.Title);
        Pick("SourceChips","assrt");Pick("LanguageChips","ja");
        Search(media.Title+" S04E01",4,1);
        Check(client.Queries.Last().Language=="ja","Explicit search preserves the selected language together with visible season/episode text");
        Pick("SourceChips","all");Pick("LanguageChips","");Pick("EpisodeChips","all");Pick("SeasonChips","all");
        Check(input.Text==media.Title&&Errors.Count==0,"Resetting filters restores the base title without a routed-event failure");
    }

    static void RunEditedActiveSearchChecks(SubtitleSearchWindow window,TraktMedia media,MemoryClient client)
    {
        var type=window.GetType();
        var content=(FrameworkElement)window.Content;
        var input=(TextBox)window.FindName("SearchBox");
        var search=(Button)window.FindName("SearchButton");
        SubtitleSearchResult? State()=>(SubtitleSearchResult?)type.GetField("_searchResult",Fields)!.GetValue(window);
        void Pick(string panel,string tag)
        {
            Click(((Panel)window.FindName(panel)).Children.Cast<Button>().Single(item=>(string)item.Tag==tag));
            Layout(content);
        }
        Pick("SeasonChips","4");Pick("EpisodeChips","1");
        foreach(bool paused in new[]{false,true})
        foreach(string draft in new[]{media.Title,media.Title+" S04E02"})
        {
            input.Text=media.Title+" S04E01";
            int before=client.Requests;
            client.HoldNext=true;
            Click(search);
            Wait(()=>client.Requests==before+1&&State() is {Complete:false});
            var previous=State();
            var pending=client.Pending!;
            var cancelledPage=client.PendingToken;
            Check((string)search.Content=="暂停","An in-flight memory search exposes the pause action");
            if(paused)
            {
                int cancelled=client.Cancelled;
                Click(search);
                Wait(()=>State() is {Paused:true}&&client.Cancelled>cancelled);
                Check(client.Requests==before+1&&(string)search.Content=="继续","Pausing cancels the pending page without starting a replacement request");
            }
            input.Text=draft;Pump();
            Check((string)search.Content=="搜索"&&client.Requests==before+1,
                "Editing an in-flight or paused keyword exposes search without issuing a request");
            Click(search);
            Wait(()=>client.Requests==before+2&&State() is {Complete:true});
            var query=client.Queries.Last();
            Check(cancelledPage.IsCancellationRequested&&!ReferenceEquals(previous,State())&&State() is {Paused:false}
                &&input.Text==media.Title+" S04E01"&&query.Title==input.Text&&query.RequestSeason==4&&query.RequestEpisode==1,
                "The edited search starts a fresh request even when normalization returns to the old keyword");
            pending.TrySetResult();Pump();
            Check(client.Requests==before+2&&State() is {Complete:true,Paused:false}&&(string)search.Content=="搜索",
                "Releasing the cancelled old page cannot resume or overwrite the replacement search");
        }
        Pick("EpisodeChips","all");Pick("SeasonChips","all");
    }

    static void RunVisible(SubtitleSearchWindow window,TraktMedia media,MemoryClient client)
    {
        var type=window.GetType();
        var titles=(SubtitleTitles)type.GetField("_titles",Fields)!.GetValue(window)!;
        var queries=(Dictionary<string,Task<SubtitleQuery>>)typeof(SubtitleTitles).GetField("_queries",Fields)!.GetValue(titles)!;
        string[] names=[media.Title,"Event Check","イベント検証"];
        queries[media.Title]=Task.FromResult(new SubtitleQuery(media.Title,4,1,""){Names=names});
        Forms.Application.EnableVisualStyles();
        using var owner=new Forms.Form{Text="字幕弹窗隔离回归",Width=1000,Height=850,StartPosition=Forms.FormStartPosition.CenterScreen};
        Exception? failure=null;
        void ThreadError(object sender,ThreadExceptionEventArgs args)
        { Console.Error.WriteLine(args.Exception);Errors.Add(args.Exception); }
        Forms.Application.ThreadException+=ThreadError;
        owner.Shown+=(_,_)=>owner.BeginInvoke(new Action(()=>
        {
            try
            {
                new WindowInteropHelper(window).Owner=owner.Handle;
                System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
                window.Show();
                Wait(()=>window.IsVisible&&window.IsLoaded);
                Check(new WindowInteropHelper(window).Handle!=IntPtr.Zero,"The subtitle window has a real owned WPF HWND");
                var input=(TextBox)window.FindName("SearchBox");
                var search=(Button)window.FindName("SearchButton");
                var button=(Button)window.FindName("AliasesButton");
                var popup=(Popup)window.FindName("AliasesPopup");
                var aliases=(ListBox)window.FindName("AliasesList");
                input.Text="手工输入的其他名称";
                Click(search);
                Wait(()=>type.GetField("_searchResult",Fields)!.GetValue(window) is SubtitleSearchResult{Complete:true});
                Check(client.Requests==1,"Visible manual search uses the in-memory provider exactly once");
                for(int index=0;index<names.Length;index++)
                {
                    input.Text=index==0?"":"与当前作品不同的输入";
                    Click(button);
                    Wait(()=>popup.IsOpen&&aliases.Items.Count>=names.Length&&PresentationSource.FromVisual(popup.Child)!=null);
                    Check(aliases.Items.Cast<string>().Contains(names[index]),"The visible popup displays cached current-work names independently of input text");
                    aliases.UpdateLayout();
                    var item=(ListBoxItem)aliases.ItemContainerGenerator.ContainerFromIndex(aliases.Items.IndexOf(names[index]))!;
                    var label=Children<TextBlock>(item).Single(text=>text.Text==names[index]);
                    aliases.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                        {RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent,Source=label});
                    Pump();
                    Check(input.Text==names[index]&&!popup.IsOpen,"Selecting a visible alias closes its popup and updates only the input");
                }
                input.Text="尚未选择别名";input.Focus();
                RaiseKey(input,Key.Down);Wait(()=>popup.IsOpen&&aliases.Items.Count>=names.Length);
                RaiseKey(input,Key.Down);Pump();
                Check(aliases.SelectedIndex>=0,"Down opens the popup and focuses an alias row");
                string selected=(string)aliases.SelectedItem;
                RaiseKey(aliases,Key.Enter);Pump();
                Check(input.Text==selected&&!popup.IsOpen,"Enter accepts the focused alias and closes the popup");
                Click(button);Wait(()=>popup.IsOpen);Click(button);Pump();
                Check(!popup.IsOpen,"The alias button toggles an open real popup closed");
                window.Close();Pump();
                Check(!window.IsVisible&&window.IsCurrent,"Closing the search hides the same reusable window");
                window.RestoreResults();window.Show();window.Activate();Pump();
                Click(button);Wait(()=>popup.IsOpen&&PresentationSource.FromVisual(popup.Child)!=null);
                Check(aliases.Items.Cast<string>().Contains(media.Title),"The actual popup reopens after hiding and restoring the owned window");
                RaiseKey(aliases,Key.Escape);Pump();
                Check(!popup.IsOpen&&client.Requests==1&&Global.Player.GetPropertyString("path")=="",
                    "Popup lifecycle uses no network or video and never searches by itself");
                Console.WriteLine($"Visible subtitle owner/popup checks passed: {_checks}; no network or file playback.");
            }
            catch(Exception error){failure=error;Console.Error.WriteLine(error);}
            finally
            {
                type.GetField("_canClose",Fields)!.SetValue(window,true);window.Close();owner.Close();
            }
        }));
        try{Forms.Application.Run(owner);}
        finally{Forms.Application.ThreadException-=ThreadError;}
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static void RaiseKey(UIElement element,Key key)
    {
        var source=PresentationSource.FromVisual(element)??throw new Exception("Visible keyboard target has no presentation source");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,key)
            {RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=element});
    }
}

sealed class MemoryClient(SubtitleSource source=SubtitleSource.Assrt):ISubtitleClient
{
    public SubtitleSource Source=>source;
    public int Requests;
    public List<SubtitleQuery> Queries=[];
    public bool HoldNext;
    public TaskCompletionSource? Pending;
    public CancellationToken PendingToken;
    public int Cancelled;
    public async Task<SubtitlePage> SearchAsync(SubtitleQuery query,int page,CancellationToken token,IProgress<IReadOnlyList<SubtitleResult>>? progress=null)
    {
        token.ThrowIfCancellationRequested();Requests++;Queries.Add(query);
        if(HoldNext)
        {
            HoldNext=false;PendingToken=token;
            var pending=Pending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try{await pending.Task.WaitAsync(token);}
            catch(OperationCanceledException){Interlocked.Increment(ref Cancelled);throw;}
        }
        return new SubtitlePage(new (int Season,int Episode)[]{(4,1),(4,2),(3,2),(3,3),(0,1)}
            .Select(item=>new SubtitleResult(Source,$"fixture-{item.Season}-{item.Episode}",query.Title,"","",[])
                {Season=item.Season,Episode=item.Episode,LanguageCode="ja"}).ToArray(),false);
    }
    public Task<SubtitleResult> DetailAsync(SubtitleResult result,CancellationToken token)=>throw new Exception("Unexpected detail request");
}

sealed class NoNetworkHandler:HttpMessageHandler
{
    public int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Interlocked.Increment(ref Requests);
        throw new InvalidOperationException("Source settings attempted an unexpected network request");
    }
}
