using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;
using MpvNet.Windows.UI;
using MpvNet.Windows.WPF;
using MpvNet.Windows.WPF.Controls;

static class Checks
{
    static int _checks;
    static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        _checks++;
    }

    // Read the real window layouts without account, search or native-player handlers.
    // Nothing is shown and no desktop input or network request is made.
    static Window ReadWindow(string file)
    {
        var document = XDocument.Load(file);
        document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
        var events = new HashSet<string> { "Click", "SelectionChanged", "TextBoxBase.TextChanged" };
        foreach (var attribute in document.Descendants().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).ToArray())
            attribute.Remove();
        const string controls = "clr-namespace:MpvNet.Windows.WPF.Controls;assembly=mpv-AnimeFusion";
        foreach (var element in document.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:MpvNet.Windows.WPF.Controls"))
            element.Name = XName.Get(element.Name.LocalName, controls);
        document.Root.SetAttributeValue(XNamespace.Xmlns + "controls", controls);
        return (Window)XamlReader.Parse(document.ToString());
    }

    static T Field<T>(MatchSearchControl control, string name)
        => (T)typeof(MatchSearchControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;

    [STAThread]
    static void Main()
    {
        Theme.Current = new Theme
        {
            Foreground = Brushes.White, Foreground2 = Brushes.LightGray, Background = Brushes.DarkSlateGray,
            MenuBackground = Brushes.Black, MenuHighlight = Brushes.DimGray, Heading = Brushes.DeepSkyBlue
        };
        WpfApplication.Init();
        string? root = Directory.GetCurrentDirectory();
        while (root != null && !File.Exists(Path.Combine(root, "release.json"))) root = Path.GetDirectoryName(root);
        if (root == null) throw new Exception("Repository root required.");
        foreach (string service in new[] { "Bangumi", "Trakt", "Mal" })
        {
            var window = ReadWindow(Path.Combine(root, "src/player/src/MpvNet.Windows/WPF", service + "MatchWindow.xaml"));
            try
            {
                window.DataContext = new { Theme = Theme.Current };
                var query = (MatchSearchControl)window.FindName("QueryBox");
                var search = (Button)window.FindName("SearchButton");
                Check(search.IsDefault, service + " allows an explicit Enter search.");
                Check(query.FindName("AliasesButton") is Button, service + " includes the shared alias button.");
                var popup = (Popup)query.FindName("AliasesPopup");
                Check(!popup.IsOpen, service + " starts without opening or fetching aliases.");
                using var lifetime = new CancellationTokenSource();
                var first = TraktMedia.Parse("测试作品 S04E01", "fixture.mkv", new Dictionary<string, string>());
                query.SetMedia(first, lifetime.Token);
                query.Text = "手动输入的其他作品";
                var titles = Field<SubtitleTitles>(query, "_titles");
                Check(titles.CachedNames.Contains(first.Title) && !titles.CachedNames.Contains(query.Text),
                    service + " fixes aliases to the playing work independently of the typed search.");
                int searches = 0;
                search.Click += (_, _) => searches++;
                const string alias = "Server Name Ｓ４";
                typeof(MatchSearchControl).GetMethod("SelectAlias", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(query, [alias]);
                Check(query.Text == alias && searches == 0 && !popup.IsOpen,
                    service + " selecting an alias preserves its original text without starting search.");
                var previous = Field<CancellationTokenSource>(query, "_lookup").Token;
                query.SetMedia(TraktMedia.Parse("第二部作品 S01E02", "next.mkv", new Dictionary<string, string>()), lifetime.Token);
                Check(previous.IsCancellationRequested && !ReferenceEquals(titles, Field<SubtitleTitles>(query, "_titles"))
                    && ((ListBox)query.FindName("AliasesList")).Items.Count == 0,
                    service + " changing playback cancels and clears aliases from the previous work.");
                var active = Field<CancellationTokenSource>(query, "_lookup").Token;
                lifetime.Cancel();
                Check(active.IsCancellationRequested, service + " closing the matching session cancels its alias request.");
                query.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            }
            finally { window.Close(); }
        }
        Console.WriteLine($"Match alias checks: {_checks} passed; no windows shown, desktop input, network or video.");
    }
}
