using AnimeJaNai.Localization;
using AnimeJaNaiConfEditor.Services;
using AnimeJaNaiConfEditor.ViewModels;
using AnimeJaNaiConfEditor.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using ReactiveUI.Avalonia;
using System.Text.Json;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: ManagerHistory <empty fixture directory> [native history fixture]");
    Environment.ExitCode = 2;
    return;
}
var fixture = Path.GetFullPath(args[0]);
Directory.CreateDirectory(fixture);
InterfaceLanguage.SettingsPath = Path.Combine(fixture, "interface-language.json");
InterfaceLanguage.Save("zh-CN");
InterfaceLanguage.Reload();
AppBuilder.Configure<AnimeJaNaiConfEditor.App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseReactiveUI(_ => { }).SetupWithoutStarting();

var assertions = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    assertions++;
    Console.WriteLine("PASS " + label);
}
void Near(double value, double expected, string label) => Check(Math.Abs(value - expected) < 0.002, label);
var file = Path.Combine(fixture, "watch_history.jsonl");
IReadOnlyList<PlaybackHistoryItem> Read(string path)
{
    var snapshot = PlaybackHistory.CreateSnapshot(path);
    return PlaybackHistory.ReadPage(snapshot, snapshot.Length).Items;
}
string Row(string? title, double start, double end, double? duration = 1440, long time = 1_792_140_000,
    string path = "D:\\影视\\同一集.mkv") => JsonSerializer.Serialize(new
    {
        title, path, time, end_time = time + 900, start, end, duration
    });

Check(Read(file).Count == 0, "a new installation has empty history");
File.WriteAllLines(file, [Row("较早的记录", 10, 90, time: 1_792_130_000), Row("第一遍", 300, 1200),
    Row("第二遍", 1200, 300), "{\"time\": 1}", "not json", "{\"partial\":"]);
var rows = Read(file);
Check(rows.Count == 3, "duplicate paths remain separate and damaged rows cannot hide sessions");
Check(rows.Select(row => row.Title).SequenceEqual(["第二遍", "第一遍", "较早的记录"]),
    "newest sessions first with reverse append order for equal timestamps");
Near(rows[0].RangeStart, 5d / 24, "reverse playback starts the range at five minutes");
Near(rows[0].RangeEnd, 20d / 24, "reverse playback ends the range at twenty minutes");
Near(rows[1].RangeStart, rows[0].RangeStart, "forward and reverse sessions have the same left endpoint");
Near(rows[1].RangeEnd, rows[0].RangeEnd, "forward and reverse sessions have the same right endpoint");
Check(rows[0].ProgressText == "20:00 → 05:00 / 24:00", "labels preserve entry and exit direction");
Check(rows[0].WatchedAtText.Contains('–'), "watching date includes entry and exit timestamps");

var unknown = new PlaybackHistoryItem("未知时长", "local.mkv", rows[0].StartedAt, rows[0].EndedAt, 60, 90, null);
Check(unknown.WatchedRange.Value == 0 && unknown.AfterRange.Value == 1 && unknown.ProgressText == "01:00 → 01:30",
    "unknown duration does not invent a percentage or a total duration");
var clipped = new PlaybackHistoryItem("边界", "local.mkv", rows[0].StartedAt, rows[0].EndedAt, 1400, 1450, 1440);
Check(clipped.RangeEnd == 1 && clipped.AfterRange.Value == 0, "end rounding cannot overflow the full timeline");
var same = new PlaybackHistoryItem("同一位置", "local.mkv", rows[0].StartedAt, rows[0].EndedAt, 300, 300, 1440);
Check(same.WatchedRange.Value == 0, "a zero-length session does not pretend to cover a time interval");
var longFilm = new PlaybackHistoryItem("电影", "local.mkv", rows[0].StartedAt, rows[0].EndedAt, 3601, 7205, 9000);
Check(longFilm.ProgressText == "1:00:01 → 2:00:05 / 2:30:00", "long film positions retain hours");

File.WriteAllLines(file, [Row(null, 0, 1, path: "https://user:password@example.test/movie.mkv?api_key=secret"),
    Row("https://user:password@example.test/film.mp4?token=secret", 0, 1),
    Row("作品 / 第 1 集", 0, 1), Row(null, 0, 1), Row("bad", -1, 1)]);
var titled = Read(file);
Check(titled.Count == 4, "invalid negative media positions are excluded");
Check(titled[0].Title == "同一集.mkv" && titled[1].Title == "作品 / 第 1 集", "fallback uses the filename but real titles remain intact");
Check(titled[2].Title == "film.mp4" && titled[3].Title == "movie.mkv", "URL title and fallback never expose query credentials");
Check(titled[0].Source == "D:\\影视\\同一集.mkv", "local history retains the complete original source path");
Check(titled[3].Source == "https://example.test/movie.mkv", "stream source hides user information and authentication parameters");
var fragment = new PlaybackHistoryItem("来源", "https://u:p@example.test:8080/show/episode.mkv?k=secret#token",
    rows[0].StartedAt, rows[0].EndedAt, 0, 1, 2);
Check(fragment.Source == "https://example.test:8080/show/episode.mkv", "stream source keeps host, port and path without fragments");

PlaybackHistoryItem mediaItem;
using (var mediaDocument = JsonDocument.Parse("""
    {"container":"matroska,webm","tracks":[
      {"id":1,"type":"video","selected":true,"codec":"h264","codec_description":"H.264 / AVC","profile":"High","format":"yuv420p","bit_depth":8,"width":1920,"height":1080,"fps":23.976,"bitrate":5000000},
      {"id":1,"type":"audio","selected":true,"codec":"aac","language":"jpn","sample_rate":48000,"channels":2,"channel_layout":"stereo","bitrate":192000},
      {"id":1,"type":"sub","selected":false,"codec":"ass","title":"简体中文","language":"chi"},
      {"id":"bad","type":"video","width":0,"height":-1,"fps":0}
    ]}
    """))
{
    mediaItem = new PlaybackHistoryItem("媒体信息测试", "https://example.invalid/unreachable.mkv?api_key=secret",
        rows[0].StartedAt, rows[0].EndedAt, 300, 1200, 1440, mediaDocument.RootElement);
}
Check(mediaItem.HasMediaInfo && mediaItem.MediaInfoSections.Count == 4,
    "recorded media metadata survives JSON disposal without retaining a reader or opening the source");
var mediaValues = mediaItem.MediaInfoSections.SelectMany(section => section.Values).ToArray();
Check(mediaValues.Any(value => value.Value == "1920 × 1080") && mediaValues.Any(value => value.Value == "23.976 fps") &&
    mediaValues.Any(value => value.Value == "5 Mb/s") && mediaValues.Any(value => value.Value == "48000 Hz"),
    "native track dimensions, frame rate, bitrate and sample rate use their recorded units");
Check(mediaItem.MediaInfoSections.Any(section => section.Title == "视频 #1 · 播放轨道") &&
    mediaItem.MediaInfoSections.Any(section => section.Title == "字幕 #1"), "track headings preserve type, native ID and selected state");
Check(mediaValues.All(value => value.Value != "0 fps") && !rows[0].HasMediaInfo,
    "unavailable fields are hidden and old sessions do not invent media information");

var main = new MainWindow();
var tabs = main.GetLogicalDescendants().OfType<TabItem>().ToArray();
Check(tabs.Length == 4 && (string?)tabs[2].Header == "播放记录" && (string?)tabs[3].Header == "关于",
    "history is the tab immediately before About");
var list = main.FindControl<ListBox>("PlaybackHistoryList")!;
Check(list.ItemTemplate != null, "history uses the manager's actual item template");
void CheckLayout(PlaybackHistoryItem item, double expectedStart, double expectedWidth, string label)
{
    var card = list.ItemTemplate!.Build(item)!;
    card.DataContext = item;
    var host = new Window { Width = 660, Height = 300, Content = card };
    host.Show();
    Dispatcher.UIThread.RunJobs();
    var timeline = card.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "PlaybackTimeline");
    var fill = card.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PlaybackRangeFill");
    Check(timeline.Bounds.Width > 0, label + " has a full-width timeline");
    Near(fill.Bounds.X / timeline.Bounds.Width, expectedStart, label + " actual fill starts at the correct offset");
    Near(fill.Bounds.Width / timeline.Bounds.Width, expectedWidth, label + " actual fill spans only entry-to-exit");
    Check(fill.Background != null, label + " uses the current accent resource");
    host.Close();
}
CheckLayout(rows[1], 5d / 24, 15d / 24, "forward session");
CheckLayout(rows[0], 5d / 24, 15d / 24, "reverse session");
CheckLayout(unknown, 0, 0, "unknown duration");
CheckLayout(same, 5d / 24, 0, "unchanged position");
var longTitle = new PlaybackHistoryItem(string.Concat(Enumerable.Repeat("完整剧集标题和版本信息 ", 30)),
    "local.mkv", rows[0].StartedAt, rows[0].EndedAt, 300, 1200, 1440);
var longCard = list.ItemTemplate!.Build(longTitle)!;
longCard.DataContext = longTitle;
var longHost = new Window { Width = 660, Height = 500, Content = longCard };
longHost.Show();
Dispatcher.UIThread.RunJobs();
var titleBlock = longCard.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == longTitle.Title);
Check(titleBlock.Bounds.Height > 40 && titleBlock.MaxLines == 0 && titleBlock.TextTrimming == Avalonia.Media.TextTrimming.None,
    "the full long title wraps without line limits or ellipsis");
longHost.Close();

// Exercise the actual list container and the native Expander header in memory.
// These headless input events never touch a desktop window or access a stream.
list.ClearValue(ItemsControl.ItemsSourceProperty);
list.ClearValue(Visual.IsVisibleProperty);
list.ItemsSource = new[] { mediaItem, rows[0] };
var historyContent = (Control)tabs[2].Content!;
tabs[2].Content = null;
var listHost = new Window { Width = 760, Height = 700, Content = historyContent };
listHost.Show();
Dispatcher.UIThread.RunJobs();
var containers = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
Check(containers.Length == 2, "history page realizes both fixture sessions");
var expander = containers[0].GetVisualDescendants().OfType<Expander>().Single();
var hoverBackground = containers[0].GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
    .Single(control => ReferenceEquals(control.TemplatedParent, containers[0]));
Check(expander.Margin.Bottom == 0 && containers[0].Margin.Bottom == 12,
    "record spacing belongs outside the list container's hover background");
Near(hoverBackground.Bounds.Height, expander.Bounds.Height, "native hover background ends at the visible collapsed card edge");
var header = expander.GetVisualDescendants().OfType<ToggleButton>().First();
var headerContent = (Control)expander.Header!;
var contentOrigin = headerContent.TranslatePoint(default, header)!.Value;
Near(contentOrigin.X, 17, "card content retains the original left padding and border");
Near(contentOrigin.Y, 17, "card content retains the original top padding and border");
Near(header.Bounds.Width - contentOrigin.X - headerContent.Bounds.Width, 17,
    "card content uses the full width with no reserved arrow column");
Near(header.Bounds.Height - contentOrigin.Y - headerContent.Bounds.Height, 17,
    "card content retains the original bottom padding and border");
var chevron = header.GetVisualDescendants().OfType<Border>()
    .Single(control => control.Name == "ExpandCollapseChevronBorder");
Check(!chevron.IsEffectivelyVisible, "the original card has no dropdown arrow");
var headerSurface = header.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "Root");
Check(headerSurface.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 },
    "the card background remains transparent instead of the expander's white surface");
var headerPoint = header.TranslatePoint(new Point(30, header.Bounds.Height / 2), listHost)!.Value;
listHost.MouseMove(headerPoint);
listHost.MouseDown(headerPoint, MouseButton.Left);
listHost.MouseUp(headerPoint, MouseButton.Left);
Dispatcher.UIThread.RunJobs();
Check(expander.IsExpanded && mediaItem.IsExpanded, "clicking the record expands the native media-info content");
Check(!chevron.IsEffectivelyVisible, "expansion and hover do not restore the dropdown arrow");
Check(expander.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == mediaItem.Source) &&
    expander.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == "1920 × 1080"),
    "the expanded record displays its saved source and media details");
Near(hoverBackground.Bounds.Height, expander.Bounds.Height, "native hover background ends at the visible expanded card edge");
listHost.MouseDown(headerPoint, MouseButton.Left);
listHost.MouseUp(headerPoint, MouseButton.Left);
Dispatcher.UIThread.RunJobs();
Check(!expander.IsExpanded && !mediaItem.IsExpanded, "clicking the record again collapses its metadata");
HistoryLayoutChecks.Run(listHost, list, Check);
listHost.Close();

var vm = new PlaybackHistoryViewModel(file);
void Refresh()
{
    var task = vm.RefreshAsync();
    while (!task.IsCompleted)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(1);
    }
    task.GetAwaiter().GetResult();
}
Refresh();
Check(vm.HasItems && vm.Items.Count == 4 && !vm.IsLoading && !vm.HasError, "refresh loads native sessions into the page");
File.AppendAllText(file, Row("新的观看", 10, 20, time: 1_792_150_000) + Environment.NewLine);
Refresh();
Check(vm.Items.Count == 5 && vm.Items[0].Title == "新的观看", "reentering the page refreshes appended sessions without polling");
using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    Refresh();
    Check(vm.HasError && vm.ShowErrorAboveItems && vm.Items.Count == 5,
        "a temporary read failure retains the loaded sessions and displays one error");
}
File.WriteAllText(file, "");
Refresh();
Check(!vm.HasItems && vm.EmptyText == "暂无播放记录", "empty state uses the current interface language");
InterfaceLanguage.Save("en");
InterfaceLanguage.Reload();
Check(vm.EmptyText == "No playback history yet" && UiText.Key("playbackHistory") == "Playback history",
    "the new page and empty state follow English localization too");
PagingChecks.Run(Path.Combine(fixture, "paging"), Check);
if (args.Length == 2)
{
    var nativeItems = Read(Path.GetFullPath(args[1]));
    Check(nativeItems.Count >= 2 && nativeItems.GroupBy(item => item.Title).Any(group => group.Count() > 1),
        "native output retains repeated sessions in the manager reader");
    var reverse = nativeItems.FirstOrDefault(item => item.Start > item.End);
    Check(reverse != null && reverse.Duration > 0 && reverse.RangeEnd > reverse.RangeStart,
        "native backwards session becomes a visible min-to-max timeline interval");
    Check(nativeItems.Any(item => item.MediaInfoSections.SelectMany(section => section.Values)
            .Any(value => value.Label == UiText.T("Codec"))),
        "native output supplies its recorded track metadata to the manager without reopening media");
    foreach (var item in nativeItems)
        Console.WriteLine($"NATIVE {item.Title}: {item.ProgressText}; interval {item.RangeStart:F6}–{item.RangeEnd:F6}");
}
Console.WriteLine($"PASS {assertions} playback-history checks without desktop interaction");
