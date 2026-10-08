using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using MpvNet.Windows.Native;
using MpvNet.Windows.WPF.Controls;
using MpvNet.Windows.UI;

namespace MpvNet.Windows.WPF;

public partial class DanmakuSearchWindow : Window
{
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly ObservableCollection<ShowChoice> _shows = new();
    readonly ObservableCollection<EpisodeChoice> _episodes = new();
    readonly ObservableCollection<SourceChoice> _sources = new();
    readonly ObservableCollection<ChoiceChip> _seasons = new();
    readonly ObservableCollection<ChoiceChip> _platforms = new();
    readonly Dictionary<int, string> _serverNames = new();
    readonly int? _initialSeason;
    string _lastState = "";
    string _displayedKeyword = "";
    string _selectedTitle = "";
    string _selectedSeason = "全部";
    string _selectedPlatform = "全部";
    int _selectedSearchSource;
    int _selectedServer;
    int _pendingSearches;
    int _failedSearches;
    int _respondedSearches;
    int _totalSearches;
    int? _pendingSearchSource;
    string? _pendingKeyword;
    bool _initialSeasonApplied;
    bool _initialSearchDispatched;
    bool _ready;
    string? _pendingEpisodeId;
    int _episodeLoadGeneration;
    int _pendingEpisodeGeneration = -1;

    public Theme? Theme => Theme.Current;

    public DanmakuSearchWindow(string initialText, int? season = null)
    {
        InitializeComponent();
        // Search progress and empty-result guidance live in the centered empty state.
        // Keep the footer status as backing data only so it cannot duplicate that copy.
        Status.Visibility = Visibility.Collapsed;
        DataContext = this;
        SearchBox.Text = initialText;
        _initialSeason = season;
        ShowList.ItemsSource = _shows;
        EpisodeList.ItemsSource = _episodes;
        SourceChips.ItemsSource = _sources;
        SeasonChips.ItemsSource = _seasons;
        PlatformChips.ItemsSource = _platforms;
        var view = CollectionViewSource.GetDefaultView(_shows);
        view.SortDescriptions.Add(new SortDescription(nameof(ShowChoice.SeasonSort), ListSortDirection.Ascending));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ShowChoice.Group)));
        view.Filter = FilterShow;
        var episodeView = CollectionViewSource.GetDefaultView(_episodes);
        episodeView.SortDescriptions.Add(new SortDescription(nameof(EpisodeChoice.GroupOrder), ListSortDirection.Ascending));
        episodeView.SortDescriptions.Add(new SortDescription(nameof(EpisodeChoice.NumberSort), ListSortDirection.Ascending));
        episodeView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EpisodeChoice.Group)));
        _refresh.Tick += (_, _) => ReadState();
    }

    static string Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    static int Number(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.TryGetInt32(out int number) ? number : 0;
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitToOwner();
        ReadState();
        _ready = true;
        UpdateEmptyState();
        _refresh.Start();
        TextInputFocus.Focus(this, SearchBox, selectAll: true);
        SearchInitialIfReady();
    }

    void Window_Closed(object? sender, EventArgs e) => _refresh.Stop();

    void FitToOwner()
    {
        IntPtr owner = new WindowInteropHelper(this).Owner;
        if (owner == IntPtr.Zero || !WinApi.GetWindowRect(owner, out var bounds)) return;

        int dpi = WinApi.GetDpiForWindow(owner);
        double scale = dpi > 0 ? dpi / 96d : VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) return;

        double availableWidth = Math.Max(1, bounds.Right - bounds.Left) / scale * 0.72;
        double availableHeight = Math.Max(1, bounds.Bottom - bounds.Top) / scale * 0.82;
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = Math.Max(MinWidth, Math.Min(Width, availableWidth));
        MaxHeight = Math.Max(MinHeight, Math.Min(Height, availableHeight));
    }

    void ReadState()
    {
        string state = Player.GetPropertyString("user-data/player_ui/danmaku");
        if (string.IsNullOrEmpty(state) || state == _lastState) return;
        _lastState = state;
        JsonDocument document;
        try { document = JsonDocument.Parse(state); }
        catch (JsonException) { return; }
        using (document) ApplyState(document.RootElement);
    }

    void ApplyState(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (_pendingSearchSource is int pendingSource && Number(root, "search_source") != pendingSource) return;
        if (_pendingKeyword is string pendingKeyword && Text(root, "search_keyword") != pendingKeyword) return;
        _pendingSearchSource = null;
        _pendingKeyword = null;
        ReadSources(root);
        _pendingSearches = Number(root, "search_pending");
        _failedSearches = Number(root, "search_failed");
        _respondedSearches = Number(root, "search_responded");
        _totalSearches = Number(root, "search_total");
        _episodeLoadGeneration = Number(root, "episode_load_generation");
        _displayedKeyword = Text(root, "search_keyword");
        string status = Text(root, "status");
        Status.Text = status == "无法取得片名"
            ? "没识别到片名，可直接输入作品名搜索。"
            : status;
        if (_pendingEpisodeId is not null && _episodeLoadGeneration > _pendingEpisodeGeneration)
        {
            bool loaded = root.TryGetProperty("loaded", out var loadedValue)
                && loadedValue.ValueKind == System.Text.Json.JsonValueKind.True;
            int count = Number(root, "count");
            if (loaded && count > 0)
            {
                Close();
                return;
            }

            if (status.Contains("获取弹幕中", StringComparison.Ordinal))
            {
                Status.Visibility = Visibility.Visible;
            }
            else if (status.Contains("失败", StringComparison.Ordinal)
                || status.Contains("无效", StringComparison.Ordinal)
                || status.Contains("没有弹幕", StringComparison.Ordinal))
            {
                _pendingEpisodeId = null;
                _pendingEpisodeGeneration = -1;
                Status.Visibility = Visibility.Visible;
            }
        }
        bool showEpisodes = Text(root, "search_view") == "episodes";
        if (showEpisodes && root.TryGetProperty("selected_show", out var selectedShow)
            && selectedShow.ValueKind == JsonValueKind.Object)
        {
            string title = Text(selectedShow, "label");
            string platform = Text(selectedShow, "platform");
            if (!string.IsNullOrWhiteSpace(title))
                _selectedTitle = string.IsNullOrWhiteSpace(platform) ? title : $"{title} · {platform}";
            int selectedServer = Number(selectedShow, "server_index");
            if (selectedServer > 0) _selectedServer = selectedServer;
        }
        ShowList.Visibility = showEpisodes ? Visibility.Collapsed : Visibility.Visible;
        EpisodeList.Visibility = showEpisodes ? Visibility.Visible : Visibility.Collapsed;
        FiltersVisibility(showEpisodes);
        if (showEpisodes)
        {
            Heading.Text = "搜索弹幕";
            Subheading.Text = _selectedTitle;
            Subheading.Visibility = string.IsNullOrEmpty(_selectedTitle) ? Visibility.Collapsed : Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Collapsed;
            ReadEpisodes(root);
        }
        else
        {
            Heading.Text = "搜索弹幕";
            Subheading.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
            ReadShows(root);
        }
        UpdateEmptyState();
        SearchInitialIfReady();
    }

    void FiltersVisibility(bool episodes)
    {
        SourceRow.Visibility = episodes ? Visibility.Collapsed : Visibility.Visible;
        Filters.Visibility = episodes ? Visibility.Collapsed : Visibility.Visible;
    }

    void SearchInitialIfReady()
    {
        string keyword = SearchBox.Text.Trim();
        if (!_ready || _initialSearchDispatched || _sources.Count == 0 || keyword.Length < 2) return;
        if (_shows.Count > 0 && _displayedKeyword == keyword) return;
        if (_displayedKeyword == keyword && Status.Text.Length > 0
            && !Status.Text.Contains("无法取得片名", StringComparison.Ordinal)) return;
        _initialSearchDispatched = true;
        Search();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        UpdateEmptyState();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Search();
    }

    void UpdateSearchPlaceholder()
    {
        SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateEmptyState()
    {
        bool episodesView = EpisodeList.Visibility == Visibility.Visible;
        bool hasItems = episodesView ? _episodes.Count > 0 : _shows.Any(x => FilterShow(x));
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        SearchButton.IsEnabled = _sources.Any(x => x.Index > 0);
        if (hasItems) return;

        if (episodesView)
        {
            bool loading = Status.Text.Contains("获取剧集列表中", StringComparison.Ordinal);
            EmptyHeading.Text = loading ? "正在获取剧集" : "暂时没有可用剧集";
            EmptyDescription.Text = loading ? "" : Status.Text;
            return;
        }

        if (_sources.Count == 0)
        {
            EmptyHeading.Text = "还没有弹幕线路";
            EmptyDescription.Text = "搜索弹幕需要先配置线路。";
        }
        else if (_pendingSearches > 0)
        {
            EmptyHeading.Text = "正在搜索作品";
            EmptyDescription.Text = "";
        }
        else if (_failedSearches > 0 && _respondedSearches == 0)
        {
            EmptyHeading.Text = "请求失败";
            EmptyDescription.Text = Status.Text;
        }
        else if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            EmptyHeading.Text = "片名未识别";
            EmptyDescription.Text = "输入作品名，再按回车或点搜索。";
        }
        else if (_displayedKeyword == SearchBox.Text.Trim())
        {
            EmptyHeading.Text = "没有找到相关作品";
            EmptyDescription.Text = "试试更短的作品名，或切换线路、季度和平台。";
        }
        else
        {
            EmptyHeading.Text = "准备好搜索";
            EmptyDescription.Text = "选择线路、季度和平台，点搜索查看结果。";
        }
    }

    void ReadSources(JsonElement root)
    {
        if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array) return;
        var choices = new List<SourceChoice>();
        _serverNames.Clear();
        foreach (JsonElement server in servers.EnumerateArray())
        {
            int index = Number(server, "index");
            if (index < 1) continue;
            string name = Text(server, "note");
            if (string.IsNullOrWhiteSpace(name)) name = $"线路 {index}";
            _serverNames[index] = name;
            choices.Add(new SourceChoice(index, name, false));
        }
        if (choices.Count > 0) choices.Insert(0, new SourceChoice(0, "全部", false));

        int selected = root.TryGetProperty("search_source", out var searchSource)
            && searchSource.TryGetInt32(out int requestedSource) ? requestedSource : _selectedSearchSource;
        if (choices.All(x => x.Index != selected)) selected = 0;
        int currentSelection = _sources.FirstOrDefault(x => x.IsSelected)?.Index ?? 0;
        bool sameSources = choices.Count == _sources.Count
            && choices.Select(x => (x.Index, x.Name)).SequenceEqual(_sources.Select(x => (x.Index, x.Name)));
        if (sameSources && currentSelection == selected)
        {
            _selectedSearchSource = selected;
            if (!root.TryGetProperty("search_view", out var unchangedView) || unchangedView.GetString() != "episodes")
                _selectedServer = Number(root, "source");
            return;
        }
        _selectedSearchSource = selected;
        _sources.Clear();
        foreach (SourceChoice choice in choices)
            _sources.Add(choice with { IsSelected = choice.Index == selected });
        if (!root.TryGetProperty("search_view", out var view) || view.GetString() != "episodes")
            _selectedServer = Number(root, "source");
    }

    void ReadShows(JsonElement root)
    {
        _shows.Clear();
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement show in results.EnumerateArray())
            {
                int season = Number(show, "season");
                string kind = Text(show, "kind");
                string group = kind.Contains("电影", StringComparison.Ordinal) ? "电影"
                    : season > 0 ? $"第 {season} 季" : "其他作品";
                int groupSort = group == "电影" ? 0 : season > 0 ? season : int.MaxValue;
                string title = Text(show, "label");
                string image = Text(show, "image_url");
                int year = Number(show, "year");
                int server = Number(show, "server_index");
                string sourceName = _serverNames.GetValueOrDefault(server, $"线路 {server}");
                if (!show.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement platform in platforms.EnumerateArray())
                {
                    string id = Text(platform, "id");
                    if (id == "") continue;
                    string platformName = Text(platform, "name");
                    int count = Number(platform, "episode_count");
                    string detail = string.Join(" · ", new[]
                    {
                        year > 0 ? year.ToString() : "",
                        string.IsNullOrWhiteSpace(kind) ? "" : kind,
                        count > 0 ? $"共 {count} 集" : ""
                    }.Where(x => x.Length > 0));
                    _shows.Add(new ShowChoice(id, server, title, group, groupSort,
                        platformName, detail, image, $"{sourceName} · {platformName}"));
                }
            }
        }
        RefreshFilters();
    }

    void ReadEpisodes(JsonElement root)
    {
        _episodes.Clear();
        if (!root.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement episode in episodes.EnumerateArray())
        {
            string id = Text(episode, "id");
            if (id == "") continue;
            string number = Text(episode, "number");
            string label = Text(episode, "label");
            bool extra = episode.TryGetProperty("extra", out var extraValue) && extraValue.ValueKind == JsonValueKind.True;
            int localNumber = Number(episode, "number_value");
            string numberLabel = extra ? "番外" : localNumber > 0 ? $"第 {localNumber} 集" : number;
            _episodes.Add(new EpisodeChoice(id, label, _selectedTitle,
                extra ? "预告与其他" : "正片", extra ? 1 : 0,
                localNumber > 0 ? localNumber : int.MaxValue, numberLabel));
        }
    }

    void RefreshFilters()
    {
        string requested = _initialSeason is > 0 ? $"第 {_initialSeason} 季" : "";
        var routeShows = _shows.Where(x => _selectedSearchSource == 0 || x.ServerIndex == _selectedSearchSource).ToList();
        var seasons = routeShows.Select(x => x.Group).Distinct()
            .OrderBy(x => x == "电影" ? 0 : x == "第 1 季" ? 1 : x == "第 2 季" ? 2
                : x == "其他作品" ? int.MaxValue : ParseSeason(x))
            .ToList();
        if (!_initialSeasonApplied && seasons.Contains(requested)) _selectedSeason = requested;
        else if (_selectedSeason != "全部" && !seasons.Contains(_selectedSeason)) _selectedSeason = "全部";
        if (_selectedSeason == requested || _shows.Count > 0 && _pendingSearches == 0) _initialSeasonApplied = true;
        var platforms = routeShows.Where(x => _selectedSeason == "全部" || x.Group == _selectedSeason).Select(x => x.Platform).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct().OrderBy(x => x, StringComparer.CurrentCulture).ToList();

        if (_selectedPlatform != "全部" && !platforms.Contains(_selectedPlatform)) _selectedPlatform = "全部";

        _seasons.Clear();
        _seasons.Add(new ChoiceChip("全部", "全部", _selectedSeason == "全部"));
        foreach (string season in seasons)
            _seasons.Add(new ChoiceChip(season, season, season == _selectedSeason));

        _platforms.Clear();
        _platforms.Add(new ChoiceChip("全部", "全部", _selectedPlatform == "全部"));
        foreach (string platform in platforms)
            _platforms.Add(new ChoiceChip(platform, platform, platform == _selectedPlatform));

        CollectionViewSource.GetDefaultView(_shows).Refresh();
        UpdateEmptyState();
    }

    static int ParseSeason(string value)
    {
        string digits = new(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int season) ? season : int.MaxValue - 1;
    }

    bool FilterShow(object item)
    {
        if (item is not ShowChoice show) return false;
        return (_selectedSeason == "全部" || show.Group == _selectedSeason)
            && (_selectedPlatform == "全部" || show.Platform == _selectedPlatform)
            && (_selectedSearchSource == 0 || show.ServerIndex == _selectedSearchSource);
    }

    void SeasonChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChoiceChip choice }) return;
        _selectedSeason = choice.Value;
        RefreshFilters();
    }

    void PlatformChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChoiceChip choice }) return;
        _selectedPlatform = choice.Value;
        RefreshFilters();
    }

    void SourceChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SourceChoice choice }) return;
        if (_selectedSearchSource == choice.Index) return;
        _selectedSearchSource = choice.Index;
        foreach (SourceChoice item in _sources.ToArray())
        {
            int index = _sources.IndexOf(item);
            _sources[index] = item with { IsSelected = item.Index == _selectedSearchSource };
        }
        if (!string.IsNullOrWhiteSpace(_displayedKeyword))
        {
            _pendingSearchSource = _selectedSearchSource;
            _pendingSearches = 1;
            _failedSearches = 0;
            _respondedSearches = 0;
            Status.Text = "";
        }
        RefreshFilters();
        if (_ready && !string.IsNullOrWhiteSpace(_displayedKeyword))
            Player.CommandV("script-message", "player_ui-danmaku-search-filter", _selectedSearchSource.ToString());
    }

    void Search_Click(object sender, RoutedEventArgs e) => Search();

    void Search()
    {
        string keyword = SearchBox.Text.Trim();
        if (keyword.Length < 2)
        {
            Status.Text = "请输入至少两个字的作品名。";
            UpdateEmptyState();
            return;
        }
        if (_sources.Count == 0 || !_sources.Any(x => x.Index > 0))
        {
            Status.Text = "还没有可搜索的弹幕线路。";
            UpdateEmptyState();
            return;
        }
        _initialSearchDispatched = true;
        _selectedTitle = "";
        _pendingEpisodeId = null;
        _pendingEpisodeGeneration = -1;
        Status.Visibility = Visibility.Collapsed;
        _pendingSearchSource = _selectedSearchSource;
        _pendingKeyword = keyword;
        _pendingSearches = 1;
        _failedSearches = 0;
        _respondedSearches = 0;
        if (!string.Equals(keyword, _displayedKeyword, StringComparison.Ordinal))
        {
            _shows.Clear();
            _episodes.Clear();
            RefreshFilters();
        }
        Status.Text = "搜索作品中…";
        UpdateEmptyState();
        Player.CommandV("script-message", "player_ui-danmaku-search-query", keyword,
            _selectedSearchSource.ToString(), _initialSeason?.ToString() ?? "", "true");
    }

    void Show_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShowList.SelectedItem is not ShowChoice choice) return;
        _pendingEpisodeId = null;
        _pendingEpisodeGeneration = -1;
        Status.Visibility = Visibility.Collapsed;
        ShowList.SelectedItem = null;
        _selectedTitle = $"{choice.Name} · {choice.Group} · {choice.Platform}";
        _selectedServer = choice.ServerIndex;
        Player.CommandV("script-message", "player_ui-danmaku-show", choice.Id, choice.ServerIndex.ToString());
    }

    void Episode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EpisodeList.SelectedItem is not EpisodeChoice episode) return;
        if (_pendingEpisodeId is not null)
        {
            EpisodeList.SelectedItem = null;
            return;
        }
        _pendingEpisodeId = episode.Id;
        _pendingEpisodeGeneration = _episodeLoadGeneration;
        EpisodeList.SelectedItem = null;
        Status.Text = "正在加载所选剧集弹幕…";
        Status.Visibility = Visibility.Visible;
        Player.CommandV("script-message", "player_ui-danmaku-pick", episode.Id,
            $"{_selectedTitle} · {episode.Label}", _selectedServer.ToString());
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        _pendingEpisodeId = null;
        _pendingEpisodeGeneration = -1;
        Status.Visibility = Visibility.Collapsed;
        _selectedTitle = "";
        Player.CommandV("script-message", "player_ui-danmaku-search-back");
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    sealed record SourceChoice(int Index, string Name, bool IsSelected);
    sealed record ChoiceChip(string Name, string Value, bool IsSelected);
    sealed record ShowChoice(string Id, int ServerIndex, string Name, string Group, int SeasonSort,
        string Platform, string Detail, string ImageUrl, string Provenance);
    sealed record EpisodeChoice(string Id, string Label, string Detail,
        string Group, int GroupOrder, int NumberSort, string NumberLabel);
}
