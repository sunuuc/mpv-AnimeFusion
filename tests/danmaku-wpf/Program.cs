using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MpvNet.Windows.UI;
using MpvNet.Windows.WPF;

static class Program
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    static object Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);

    [STAThread]
    static void Main()
    {
        // Construct actual WPF controls without showing a window or sending desktop input.
        Theme.Current = new Theme { Foreground = Brushes.White, Foreground2 = Brushes.Gray,
            Background = Brushes.Black, MenuBackground = Brushes.DarkSlateGray, MenuHighlight = Brushes.Purple };
        var search = new DanmakuSearchWindow("间谍过家家");
        using var data = JsonDocument.Parse("""
        {"results":[
          {"label":"间谍过家家","season":3,"server_index":1,"platforms":[{"id":"1","name":"哔哩哔哩","episode_count":13}]},
          {"label":"间谍过家家","season":1,"server_index":1,"platforms":[{"id":"2","name":"爱奇艺","episode_count":25}]},
          {"label":"间谍过家家","season":3,"server_index":2,"platforms":[{"id":"3","name":"爱奇艺","episode_count":13}]}
        ]}
        """);
        Set(search, "_selectedSearchSource", 1);
        Set(search, "_selectedSeason", "第 3 季");
        Set(search, "_selectedPlatform", "爱奇艺");
        Call(search, "ReadShows", data.RootElement);
        var platforms = ((IEnumerable)Field(search, "_platforms")).Cast<object>().ToList();
        var names = platforms.Select(x => x.GetType().GetProperty("Name")!.GetValue(x)).ToArray();
        Check(names.SequenceEqual(new object[] { "全部", "哔哩哔哩" }), "Empty contextual platforms must be hidden");
        Check((string)Field(search, "_selectedPlatform") == "全部", "An invalid platform selection must reset");
        var input = (TextBox)search.FindName("SearchBox");
        input.Text = "English 中文";
        input.Select(2, 0); input.SelectedText = "abc";
        Check(input.Text == "Enabcglish 中文", "Native input must retain ordinary editing");
        ((IList)Field(search, "_shows")).Clear();
        Set(search, "_failedSearches", 1);
        ((TextBlock)search.FindName("Status")).Text = "服务器返回：配额不足";
        using var sources = JsonDocument.Parse("""{"servers":[{"index":1,"note":"ME"}]}""");
        Call(search, "ReadSources", sources.RootElement);
        Call(search, "UpdateEmptyState");
        Check(((TextBlock)search.FindName("EmptyHeading")).Text == "请求失败", "Failure heading should be concise");
        Check(((TextBlock)search.FindName("EmptyDescription")).Text == "服务器返回：配额不足", "Server detail must be retained");
        using var oldState = JsonDocument.Parse("""
        {"servers":[{"index":1,"note":"ME"},{"index":2,"note":"logvar"}],
         "search_source":1,"search_keyword":"间谍过家家","search_failed":1,"search_responded":0,
         "status":"服务器返回：配额不足","results":[]}
        """);
        input.Text = "间谍过家家";
        Call(search, "ApplyState", oldState.RootElement);
        var choices = ((IEnumerable)Field(search, "_sources")).Cast<object>().ToArray();
        Call(search, "SourceChip_Click", new Button { DataContext = choices[2] }, new RoutedEventArgs());
        Check(((TextBlock)search.FindName("EmptyHeading")).Text == "正在搜索作品", "A route switch must not display stale failure");
        Check(((TextBlock)search.FindName("EmptyDescription")).Text == "", "Searching must have only one status line");
        Call(search, "ApplyState", oldState.RootElement);
        Check((int)Field(search, "_selectedSearchSource") == 2, "An old route snapshot must not undo a pending switch");
        using var newState = JsonDocument.Parse("""
        {"servers":[{"index":1,"note":"ME"},{"index":2,"note":"logvar"}],
         "search_source":2,"search_keyword":"间谍过家家","search_failed":0,"search_responded":1,
         "results":[{"label":"间谍过家家","season":3,"server_index":2,
         "platforms":[{"id":"3","name":"爱奇艺","episode_count":13}]}]}
        """);
        Call(search, "ApplyState", newState.RootElement);
        Check(((FrameworkElement)search.FindName("EmptyState")).Visibility == Visibility.Collapsed, "The acknowledged route must display its results");
        ((IList)Field(search, "_shows")).Clear();
        Set(search, "_failedSearches", 1);
        Set(search, "_respondedSearches", 1);
        Call(search, "UpdateEmptyState");
        Check(((TextBlock)search.FindName("EmptyHeading")).Text == "没有找到相关作品", "A successful empty response must not be called a request failure");
        var seasonal = new DanmakuSearchWindow("无职转生", 3);
        Set(seasonal, "_pendingSearches", 1);
        using var partial = JsonDocument.Parse("""{"results":[{"label":"无职转生 第一季","season":1,"server_index":1,"platforms":[{"id":"1","name":"弹弹play"}]}]}""");
        Call(seasonal, "ReadShows", partial.RootElement);
        Check(!(bool)Field(seasonal, "_initialSeasonApplied"), "Partial results must not consume the requested season");
        Call(seasonal, "ReadShows", data.RootElement);
        Check((string)Field(seasonal, "_selectedSeason") == "第 3 季", "The requested season must be selected when available");
        var routes = new DanmakuSourcesWindow(new[] { "A", "https://a.invalid", "B", "https://b.invalid", "C", "https://c.invalid" });
        var grid = (DataGrid)routes.FindName("SourceGrid");
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        grid.GetBindingExpression(ItemsControl.ItemsSourceProperty)!.UpdateTarget();
        grid.SelectedItem = routes.Sources[2];
        Check(grid.SelectedItem == routes.Sources[2], "Route selection must be established");
        Call(routes, "MoveUp_Click", grid, new RoutedEventArgs());
        Check(string.Join(",", routes.Sources.Select(x => x.Name)) == "A,C,B", "Up must reorder the saved collection");
        Call(routes, "MoveDown_Click", grid, new RoutedEventArgs());
        Check(string.Join(",", routes.Sources.Select(x => x.Name)) == "A,B,C", "Down must restore order");
        Check(routes.Sources.Select(x => x.Priority).SequenceEqual(new[] { 1, 2, 3 }), "Priority numbers must follow order");
        Check(routes.GetType().GetMethod("SetCurrent_Click", Private) == null, "Current-route control must be removed");
        Console.WriteLine("PASS actual WPF filtering, request failures, native input and route ordering (no windows shown)");
    }
}
