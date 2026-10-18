using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.UI;
using MpvNet.Windows.WPF;

static class Checks
{
    static int _checks;
    static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        _checks++;
    }

    static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }

    static T Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(child); current != null; current = VisualTreeHelper.GetParent(current))
            if (current is T typed) return typed;
        throw new Exception("Missing visual ancestor: " + typeof(T).Name);
    }

    static void Layout(FrameworkElement content, Size size)
    {
        content.Measure(size);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        content.UpdateLayout();
    }

    static Window ReadWindow()
    {
        string? directory = Directory.GetCurrentDirectory();
        while (directory != null && !System.IO.File.Exists(Path.Combine(directory, "src/player/src/MpvNet.Windows/WPF/SubtitleSearchWindow.xaml")))
            directory = Path.GetDirectoryName(directory);
        if (directory == null) throw new Exception("Repository source path is required.");
        var document = XDocument.Load(Path.Combine(directory, "src/player/src/MpvNet.Windows/WPF/SubtitleSearchWindow.xaml"));
        document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
        var events = new HashSet<string> { "Click", "Loaded", "Closed", "KeyDown", "PreviewKeyDown", "TextChanged", "PreviewMouseLeftButtonDown", "PreviewMouseLeftButtonUp" };
        foreach (var attribute in document.Descendants().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).ToArray()) attribute.Remove();
        const string controls = "clr-namespace:HandyControl.Controls;assembly=mpv-AnimeFusion";
        foreach (var element in document.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:HandyControl.Controls"))
            element.Name = XName.Get(element.Name.LocalName, controls);
        document.Root.SetAttributeValue(XNamespace.Xmlns + "hc", controls);
        // Load the real layout and shared resources without its player/network event handlers.
        // No window is shown and no native player, desktop input or network is used.
        return (Window)XamlReader.Parse(document.ToString());
    }

    [STAThread]
    static void Main()
    {
        Theme.Current = new Theme
        {
            Foreground = Brushes.White, Foreground2 = Brushes.LightGray, Background = Brushes.DarkSlateGray,
            MenuBackground = Brushes.Black, MenuHighlight = Brushes.DimGray, Heading = Brushes.DeepSkyBlue
        };
        WpfApplication.Init();
        var window = ReadWindow();
        try
        {
            window.DataContext = new { Theme = Theme.Current };
            var content = (FrameworkElement)window.Content;
            var results = (ListBox)window.FindName("ResultsList");
            var header = (ListBoxItem)window.FindName("SearchControls");
            var field = (Grid)window.FindName("SearchField");
            var language = (StackPanel)window.FindName("LanguageChips");
            var episodes = (StackPanel)window.FindName("EpisodeChips");
            var seasons = (Panel)window.FindName("SeasonChips");
            var collection = (CollectionContainer)window.FindName("ResultsCollection");
            var search = (Button)window.FindName("SearchButton");
            Check(window.FindName("PauseSearchButton") == null,
                "Search, pause and resume share the input-row button without a separate footer control.");
            ((TextBlock)window.FindName("ResultCount")).Text = "共 6537 条搜索结果，显示 7 条";
            ((TextBlock)window.FindName("Status")).Text = "搜索已暂停";
            var rows = new ObservableCollection<SubtitleResult>(Enumerable.Range(0, 10000).Select(index =>
                new SubtitleResult(SubtitleSource.Assrt, index.ToString(), $"实际字幕标题 {index} — 完整名称", "简体中文", "简体中文 · 2026-10-16", [])
                    { LanguageCode = "zh-Hans", Format = "ASS", Season = 4, Episode = index % 24 + 1 }));
            collection.Collection = rows;
            void Chip(Panel panel, string label) => panel.Children.Add(new Button { Content = label, Style = (Style)window.FindResource("FilterButton") });
            foreach (string label in new[] { "全部", "第 1 季", "第 2 季", "第 3 季", "第 4 季", "其他作品" }) Chip(seasons, label);
            Chip(episodes, "全部");
            for (int episode = 1; episode <= 24; episode++) Chip(episodes, $"第 {episode} 集");
            foreach (string label in new[] { "全部", "简体中文", "繁体中文", "英语", "日语", "中文", "法语", "德语", "阿拉伯语", "印度尼西亚语", "西班牙语", "越南语", "其他" }) Chip(language, label);

            var aliases = (ListBox)window.FindName("AliasesList");
            var aliasStatus = (TextBlock)window.FindName("AliasesStatus");
            var aliasPanel = (FrameworkElement)VisualTreeHelper.GetParent(aliases);
            aliases.ItemsSource = new[] { "摇曳百合 第二季", "ゆるゆり♪♪", "YuruYuri: Happy Go Lily ♪♪" };
            aliasStatus.Text = "别名获取失败，请重试";
            aliasStatus.Visibility = Visibility.Visible;
            Layout(aliasPanel, new Size(430, 500));
            Check(aliases.ActualHeight > 0 && aliasStatus.ActualHeight > 0
                && aliasStatus.TransformToAncestor(aliasPanel).Transform(new Point()).Y >= aliases.ActualHeight,
                "A failed alias request leaves the known names visible with its status below, never covering the selectable names.");

            foreach (Size size in new[] { new Size(560, 620), new Size(500, 430), new Size(740, 700) })
            {
                Layout(content, size);
                var outer = Children<ScrollViewer>(results).First();
                outer.ScrollToTop();
                Layout(content, size);
                Check(results.Items.Count == 10002 && ReferenceEquals(results.Items[0], header),
                    "The real CompositeCollection holds the scrolling controls and all subtitle results.");
                Check(results.ActualHeight > 0 && results.ActualHeight < size.Height && double.IsFinite(outer.ViewportHeight),
                    $"Results have a finite viewport at {size}.");
                Check(outer.ViewportHeight > 0 && outer.ScrollableHeight > size.Height,
                    "Long results overflow the finite outer scroller.");
                Check(VirtualizingPanel.GetIsVirtualizing(results) && VirtualizingPanel.GetScrollUnit(results) == ScrollUnit.Pixel,
                    "The actual list retains native pixel virtualization.");
                Check(field.ActualWidth > 100 && header.ActualWidth <= results.ActualWidth && header.ActualHeight > 100,
                    "The scrolling header receives the real input/filter layout rather than the subtitle item template.");
                var host = Children<VirtualizingStackPanel>(results).First();
                Check(host.Children.Count < 100, "Ten thousand results do not create ten thousand visual containers.");
                var firstRow = (ListBoxItem?)results.ItemContainerGenerator.ContainerFromIndex(1);
                if (firstRow != null)
                {
                    var text = Children<TextBlock>(firstRow).Select(item => item.Text).ToArray();
                    Check(text.Contains(rows[0].Title) && text.Contains("ASS") && text.Contains(rows[0].Details),
                        "Actual subtitle rows retain the title, format and detail template rather than the alias template.");
                }

                var languageScroll = Ancestor<ScrollViewer>(language);
                Check(language.Orientation == Orientation.Horizontal && language.Children.Cast<FrameworkElement>()
                    .Select(item => item.TransformToAncestor(language).Transform(new Point()).Y).Distinct().Count() == 1,
                    "All language buttons share one horizontal row.");
                Check(languageScroll.ScrollableWidth > 0 && languageScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
                    "Excess languages overflow horizontally without producing a second row.");
                languageScroll.ScrollToRightEnd();
                Layout(content, size);
                Check(languageScroll.HorizontalOffset > 0 && languageScroll.VerticalOffset == 0,
                    "The language row can scroll right independently without vertical scrolling.");
                languageScroll.ScrollToLeftEnd();
                Layout(content, size);

                double initialY = field.TransformToAncestor(results).Transform(new Point()).Y;
                Point searchPosition = search.TransformToAncestor(content).Transform(new Point());
                Point fieldPosition = field.TransformToAncestor(content).Transform(new Point());
                double searchWidth = search.ActualWidth, searchHeight = search.ActualHeight;
                Check(ReferenceEquals(Ancestor<ListBoxItem>(search), header)
                    && searchPosition.X >= fieldPosition.X + field.ActualWidth
                    && Math.Abs(searchPosition.Y - fieldPosition.Y) < 1
                    && search.ActualWidth > 0 && searchPosition.X + search.ActualWidth <= size.Width,
                    "The shared search control stays to the right of the input inside the scrolling header.");
                foreach (string label in new[] { "暂停", "继续", "搜索" })
                {
                    search.Content = label;
                    Layout(content, size);
                    Point position = search.TransformToAncestor(content).Transform(new Point());
                    Check(Math.Abs(position.X - searchPosition.X) < 1 && Math.Abs(position.Y - searchPosition.Y) < 1
                        && Math.Abs(search.ActualWidth - searchWidth) < 1 && Math.Abs(search.ActualHeight - searchHeight) < 1,
                        $"The {label} state uses the same button size and position at {size}.");
                }
                double offset = Math.Min(header.ActualHeight - 30, 220);
                outer.ScrollToVerticalOffset(offset);
                Layout(content, size);
                double scrolledY = field.TransformToAncestor(results).Transform(new Point()).Y;
                Check(outer.VerticalOffset > 0 && scrolledY < initialY - 50,
                    "Scrolling the results moves the search input and filters upward together.");
                Check(scrolledY < 0, "The header can move outside the visible viewport rather than remaining fixed.");
                double searchScrolledY = search.TransformToAncestor(content).Transform(new Point()).Y;
                Check(Math.Abs((searchScrolledY - searchPosition.Y) - (scrolledY - initialY)) < 1,
                    "The shared search/pause button scrolls with the input and filters rather than staying in the footer.");
                outer.ScrollToVerticalOffset(outer.ScrollableHeight / 2);
                Layout(content, size);
                Check(host.Children.Count < 100, "Middle-of-list scrolling keeps visual containers bounded.");
                outer.ScrollToTop();
                Layout(content, size);
                Check(field.ActualWidth > 100 && field.TransformToAncestor(results).Transform(new Point()).Y >= 0,
                    "The real header returns intact when scrolling back to the top.");
            }
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "Layout verification creates no desktop window.");
            Console.WriteLine($"Subtitle real WPF layout checks passed: {_checks}; no Show, desktop input, screenshots or network.");
        }
        finally { window.Close(); }
    }
}
