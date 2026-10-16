using AnimeJaNaiConfEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;

internal static class HistoryLayoutChecks
{
    public static void Run(Window host, ListBox list, Action<bool, string> check)
    {
        string longTrackTitle = string.Concat(Enumerable.Repeat("日语原声 / 无损多声道 / 完整音轨说明 ", 12));
        var tracks = new List<object>
        {
            new { id = 1, type = "video", codec = "hevc", codec_description = "H.265 / HEVC",
                profile = "Main 10", width = 3840, height = 2160, fps = 23.976, bit_depth = 10 }
        };
        for (int number = 1; number <= 6; number++)
            tracks.Add(new { id = number, type = "audio", title = longTrackTitle, language = "jpn",
                codec = "flac", codec_description = "FLAC (Free Lossless Audio Codec)",
                sample_rate = 48000, channels = 6, channel_layout = "5.1", bitrate = 1500000 });
        using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(new { container = "matroska,webm", tracks }));
        var started = DateTimeOffset.FromUnixTimeSeconds(1_792_140_000);
        var items = Enumerable.Range(0, 20).Select(index => new PlaybackHistoryItem(
            $"第 {index + 1} 条播放记录", "https://unreachable.invalid/local-history-only.mkv",
            started.AddMinutes(index), started.AddMinutes(index + 15), 300, 1200, 1440,
            index == 0 ? metadata.RootElement : null)).ToArray();
        items[0].IsExpanded = true;
        list.ItemsSource = items;
        host.Height = 700;
        var widths = new[] { 760d, 1280d, 1920d };
        var columns = new List<int>();
        var heights = new List<double>();
        foreach (double width in widths)
        {
            host.Width = width;
            Dispatcher.UIThread.RunJobs();
            var scrolls = list.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
            check(scrolls.Length == 1, $"history at width {width:0} has one native scroll viewport without nested scrolling");
            var scroll = scrolls[0];
            scroll.Offset = default;
            Dispatcher.UIThread.RunJobs();
            var entries = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            check(entries.Length == 20, $"all twenty history rows have measured layout at width {width:0}");
            var first = entries.Single(entry => ReferenceEquals(entry.DataContext, items[0]));
            var sections = first.GetVisualDescendants().OfType<ItemsControl>()
                .Single(control => control.Name == "PlaybackMediaInfoSections");
            var wrap = sections.GetVisualDescendants().OfType<WrapPanel>().Single();
            var sectionCards = wrap.Children.ToArray();
            check(sectionCards.Length == 8 && sectionCards.All(card => card.Bounds.Width > 0 && card.Bounds.Height > 0),
                $"general, video and six audio sections are fully measured at width {width:0}");
            int rowColumns = sectionCards.Count(card => Math.Abs(card.Bounds.Y - sectionCards[0].Bounds.Y) < 0.01);
            columns.Add(rowColumns);
            heights.Add(sections.Bounds.Height);
            check(rowColumns >= 2 && sectionCards[1].Bounds.X > sectionCards[0].Bounds.X,
                $"metadata uses multiple columns at width {width:0}");
            check(sectionCards.All(card => card.Bounds.Left >= 0 && card.Bounds.Right <= wrap.Bounds.Width + 0.01),
                $"metadata sections stay inside the available width at {width:0}");
            var longValues = sections.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.Text == longTrackTitle).ToArray();
            check(longValues.Length == 6 && longValues.All(block => block.Bounds.Height > block.FontSize * 2 &&
                block.TextWrapping == TextWrapping.Wrap && block.MaxLines == 0 && block.TextTrimming == TextTrimming.None),
                $"all long audio titles wrap completely at width {width:0}");
            var fields = sections.GetVisualDescendants().OfType<Grid>()
                .Where(grid => grid.DataContext is PlaybackMediaInfoValue).ToArray();
            check(fields.Length > 40 && fields.All(grid =>
            {
                var texts = grid.GetVisualChildren().OfType<TextBlock>().ToArray();
                return texts.Length == 2 && texts[0].Bounds.Right <= texts[1].Bounds.Left &&
                    texts[1].Bounds.Right <= grid.Bounds.Width + 0.01;
            }), $"metadata labels and values neither overlap nor overflow at width {width:0}");

            var vertical = scroll.GetVisualDescendants().OfType<ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical);
            var barRight = vertical.TranslatePoint(new Point(vertical.Bounds.Width, 0), list)!.Value.X;
            check(Math.Abs(barRight - list.Bounds.Width) < 0.01,
                $"the vertical scrollbar remains on the history list's far right at width {width:0}");
            var pageBarRight = vertical.TranslatePoint(new Point(vertical.Bounds.Width, 0), host)!.Value.X;
            check(Math.Abs(pageBarRight - host.ClientSize.Width) < 0.01,
                $"the vertical scrollbar reaches the page's far right without an outer inset at width {width:0}");
            var extent = scroll.Extent;
            foreach (double position in new[] { 100d, extent.Height * 0.4, extent.Height, 0d })
            {
                scroll.Offset = new Vector(0, position);
                Dispatcher.UIThread.RunJobs();
                if (scroll.Extent != extent)
                    throw new Exception($"History scroll extent changed at width {width}: {extent} -> {scroll.Extent}");
            }
            check(scroll.Extent == extent, $"scrolling never changes the exact page extent at width {width:0}");
            Console.WriteLine($"LAYOUT width={width:0} columns={rowColumns} details-height={sections.Bounds.Height:0.###} extent={extent}");
        }
        check(columns[0] < columns[1] && columns[1] < columns[2],
            "wider windows automatically fit more native metadata columns");
        check(heights[0] > heights[1] && heights[1] > heights[2],
            "wider metadata layouts use less vertical space without hiding any fields");

        var pageScroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
        pageScroll.Offset = new Vector(0, pageScroll.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        check(pageScroll.Offset.Y > 0, "history is scrolled away from the first record before changing pages");
        var nextPage = Enumerable.Range(20, 20).Select(index => new PlaybackHistoryItem(
            $"第 {index + 1} 条播放记录", "https://unreachable.invalid/local-history-only.mkv",
            started.AddMinutes(index), started.AddMinutes(index + 15), 300, 1200, 1440, null)).ToArray();
        list.ItemsSource = nextPage;
        Dispatcher.UIThread.RunJobs();
        list.ScrollIntoView(nextPage[0]);
        Dispatcher.UIThread.RunJobs();
        var nextEntries = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        check(nextEntries.Length == 20 && nextEntries.All(entry => nextPage.Contains(entry.DataContext)),
            "changing pages replaces all twenty measured history rows");
        var nextFirst = nextEntries.Single(entry => ReferenceEquals(entry.DataContext, nextPage[0]));
        var viewport = pageScroll.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
        var firstTop = nextFirst.TranslatePoint(default, viewport)!.Value.Y;
        check(firstTop >= 0 && firstTop + nextFirst.Bounds.Height <= viewport.Bounds.Height + 0.01,
            "ScrollIntoView reveals the complete first record after changing pages from the old page's end");
    }
}
