using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Data;
using MpvNet.Windows.Native;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;
using MpvNet.Windows.UI;
using MpvNet.Windows.WPF.Controls;
using MpvNet.Help;

namespace MpvNet.Windows.WPF;

public partial class SubtitleSearchWindow : Window
{
    readonly HttpClient _api = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    readonly SubtitleStore _store;
    readonly AssrtClient _assrt;
    readonly SubtitleLibrary _library;
    readonly TraktMedia _media;
    readonly SubtitleTitles _titles;
    readonly ObservableCollection<SubtitleResult> _results = new();
    SubtitleSearch _search;
    CancellationTokenSource _searchLifetime;
    SubtitleSearchResult? _searchResult;
    string _displayedKeyword = "";
    int _searchGeneration;
    readonly CancellationTokenSource _lifetime = new();
    SubtitleQuery? _query;
    string _source = "all", _language = "", _season = "all", _episode = "all";
    IReadOnlyCollection<SubtitleSource>? _requestedSources;
    string _requestedSource = "all";
    string _requestedLanguage = "";
    int? _requestedSeason, _requestedEpisode;
    bool _ready, _closed, _canClose;
    int _aliasesGeneration;
    bool _aliasesWereOpen;
    public bool IsCurrent => !_closed && !_canClose;
    public Theme? Theme => Theme.Current;

    public SubtitleSearchWindow()
    {
        _media = TraktMedia.Parse(Player.GetPropertyString("media-title"), Player.GetPropertyString("path"), new Dictionary<string, string>());
        _titles = new(_media, _lifetime.Token);
        _store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv-AnimeFusion", "Subtitles"));
        _assrt = new(_api, _store);
        _library = SubtitlePlayback.Current.Library;
        _search = CreateSearch();
        _searchLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        InitializeComponent();
        DataContext = this;
        SearchBox.Text = _media.Title;
        var results = CollectionViewSource.GetDefaultView(_results);
        results.Filter = FilterResult;
        ResultsCollection.Collection = results;
        Player.StartFile += Media_Changed;
        Player.Shutdown += Media_Changed;
    }

    SubtitleSearch CreateSearch() => new(_assrt, new AmenzbClient(_api), new TsukihimeClient(_api));

    void Media_Changed()
    {
        _canClose = true;
        _lifetime.Cancel();
        Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Close(); }));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        AliasesPopup.IsOpen = false;
        if (_ready && !_canClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void RestoreResults()
    {
        if (!_ready || _closed) return;
        if (_query == null) return;
        RefreshLocalResults(_query.Title);
        RefreshSeasons();
        RefreshResults();
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ready) { RestoreResults(); return; }
        IntPtr owner = new WindowInteropHelper(this).Owner;
        if (owner != IntPtr.Zero && WinApi.GetWindowRect(owner, out var bounds))
        {
            int dpi = WinApi.GetDpiForWindow(owner);
            double scale = dpi > 0 ? dpi / 96d : VisualTreeHelper.GetDpi(this).DpiScaleX;
            double width = Math.Max(1, bounds.Right - bounds.Left) / scale * .72;
            double height = Math.Max(1, bounds.Bottom - bounds.Top) / scale * .82;
            MinWidth = Math.Min(MinWidth, width); MinHeight = Math.Min(MinHeight, height);
            MaxWidth = Math.Max(MinWidth, Math.Min(Width, width)); MaxHeight = Math.Max(MinHeight, Math.Min(Height, height));
        }
        _ready = true;
        RefreshSources(); RefreshChips(LanguageChips, _language);
        TextInputFocus.Focus(this, SearchBox, selectAll: true);
        Status.Text = _store.LoadError;
        InitializeResults();
    }

    void InitializeResults()
    {
        string keyword = SearchBox.Text.Normalize(NormalizationForm.FormKC).Trim();
        _query = new(keyword, SelectedSeason, _titles.IsCurrent(keyword) ? _media.Episode : null, _language);
        RefreshLocalResults(keyword);
        RefreshSeasons();
        RefreshResults();
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        Player.StartFile -= Media_Changed; Player.Shutdown -= Media_Changed;
        _search.Dispose(); _searchLifetime.Cancel(); _lifetime.Cancel();
        _api.Dispose();
    }

    void Empty(string heading, string description = "")
    {
        EmptyHeading.Text = heading; EmptyDescription.Text = description;
        EmptyState.Visibility = Visibility.Visible;
    }

    static string Error(Exception error) => error is SubtitleServiceException ? error.Message
        : error is OperationCanceledException ? "请求超时，请重试。"
        : error is IOException or UnauthorizedAccessException ? "字幕文件无法保存，请检查本机目录权限。"
        : "字幕服务暂时不可用，请稍后重试。";

    void Search_Click(object sender, RoutedEventArgs e) => SearchOrPause();

    bool MatchesRequestedContext => _displayedKeyword.Length > 0 && _searchResult?.Complete != true
        && _requestedSources?.Count != 0 && _source != "local"
        && _language == _requestedLanguage
        && SelectedSeason == _requestedSeason
        && _source == _requestedSource;
    bool ControlsActiveSearch => MatchesRequestedContext && SelectedEpisode == _requestedEpisode
        && SearchBox.Text.Normalize(NormalizationForm.FormKC).Trim().Equals(_displayedKeyword, StringComparison.OrdinalIgnoreCase);

    void RefreshSearchButton() => SearchButton.Content = ControlsActiveSearch ? _searchResult?.Paused == true ? "继续" : "暂停" : "搜索";

    void SearchOrPause()
    {
        if (!_ready || _closed) return;
        if (ControlsActiveSearch)
        {
            _search.SetPaused(_displayedKeyword, _searchResult?.Paused != true, _requestedLanguage, _requestedSeason, _requestedEpisode);
            RefreshResults();
        }
        else
        {
            if (SelectedSeason != null || SelectedEpisode != null) UpdateSearchKeyword();
            Search(refresh: true);
        }
    }

    async void Search(bool refresh = false)
    {
        if (!_ready || _closed) return;
        AliasesPopup.IsOpen = false;
        string keyword = SearchBox.Text.Normalize(NormalizationForm.FormKC).Trim();
        int generation = ++_searchGeneration;
        int? requestSeason = SelectedSeason, requestEpisode = SelectedEpisode;
        if (refresh)
        {
            _searchLifetime.Cancel(); _searchLifetime.Dispose();
            _searchLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            ResetSearch();
        }
        ShowResults();
        if (keyword.Length == 0) { ResetSearch(); Empty("输入作品名开始搜索"); return; }
        _displayedKeyword = keyword;
        _requestedSource = _source;
        _requestedSources = _source == "local" ? [] : Enum.GetValues<SubtitleSource>()
            .Where(source => _store.Credentials.IsEnabled(source) && (SelectedSource == null || SelectedSource == source)).ToArray();
        string language = _requestedLanguage = _language;
        _requestedSeason = requestSeason; _requestedEpisode = requestEpisode;
        RefreshLocalResults(keyword);
        RefreshSeasons();
        RefreshResults();
        _query ??= new(keyword, SelectedSeason, _titles.IsCurrent(keyword) ? _media.Episode : null, language)
            { RequestSeason = requestSeason, RequestEpisode = requestEpisode };
        var search = _search;
        var progress = new Progress<SubtitleSearchResult>(result =>
        {
            if (!_closed && _search == search && _searchGeneration == generation && _displayedKeyword == keyword) ApplySearch(result);
        });
        try
        {
            var result = await search.SearchAsync(keyword, async token => (await _titles.QueryAsync(keyword, token)) with
                { RequestSeason = requestSeason, RequestEpisode = requestEpisode }, progress, _searchLifetime.Token,
                refresh, _requestedSources, language, requestSeason, requestEpisode);
            if (!_closed && _search == search && _searchGeneration == generation && _displayedKeyword == keyword) ApplySearch(result);
        }
        catch (OperationCanceledException) when (_closed || _search != search || _searchGeneration != generation) { }
        catch (Exception error)
        {
            if (!_closed && _search == search && _searchGeneration == generation && _displayedKeyword == keyword)
            {
                var failed = new SubtitleSearchResult(_query);
                failed.Fail(SubtitleSource.Assrt, error); failed.Complete = true;
                ApplySearch(failed);
            }
        }
    }

    void Aliases_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _aliasesWereOpen = AliasesPopup.IsOpen;

    void Aliases_Click(object sender, RoutedEventArgs e)
    {
        bool close = _aliasesWereOpen || AliasesPopup.IsOpen;
        _aliasesWereOpen = false;
        if (close) { AliasesPopup.IsOpen = false; return; }
        OpenAliases();
    }

    async void OpenAliases()
    {
        string keyword = SearchBox.Text.Normalize(NormalizationForm.FormKC).Trim();
        if (_media.Title.Length == 0 || _closed) return;
        int generation = ++_aliasesGeneration;
        AliasesList.ItemsSource = _titles.CachedNames;
        AliasesStatus.Text = "正在匹配别名…";
        AliasesStatus.Visibility = Visibility.Visible;
        AliasesPopup.IsOpen = true;
        SearchBox.Focus();
        try
        {
            var names = await _titles.NamesAsync(_lifetime.Token);
            if (_closed || generation != _aliasesGeneration || !AliasesPopup.IsOpen) return;
            AliasesList.ItemsSource = names;
            AliasesStatus.Text = names.Count == 0 ? "没有匹配的别名" : "";
            AliasesStatus.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AliasesList.SelectedItem = names.FirstOrDefault(name => name.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Terminal.WriteError(error);
            if (_closed || generation != _aliasesGeneration || !AliasesPopup.IsOpen) return;
            AliasesList.ItemsSource = _titles.CachedNames;
            AliasesStatus.Text = "别名获取失败，请重试";
        }
    }

    void Alias_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AliasesList, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: string name })
        { e.Handled = true; SelectAlias(name); }
    }

    void SelectAlias(string name)
    {
        UpdateSearchKeyword(name);
        AliasesPopup.IsOpen = false;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    void UpdateSearchKeyword(string? name = null)
    {
        string title = (name ?? SearchBox.Text).Trim();
        if (name == null || SelectedSeason != null || SelectedEpisode != null)
            title = Regex.Replace(title, @"(?:^|\s)(?:S[0-9]+(?:E[0-9]+)?|E[0-9]+)$", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).TrimEnd();
        string suffix = (SelectedSeason is { } season ? $"S{season:00}" : "")
            + (SelectedEpisode is { } episode ? $"E{episode:00}" : "");
        SearchBox.Text = title.Length > 0 && suffix.Length > 0 ? $"{title} {suffix}" : title;
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ++_aliasesGeneration;
        if (AliasesPopup != null) AliasesPopup.IsOpen = false;
        if (_ready) RefreshSearchButton();
    }

    void Aliases_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        { e.Handled = true; AliasesPopup.IsOpen = false; SearchBox.Focus(); }
        else if (e.Key == Key.Enter && AliasesList.SelectedItem is string name)
        { e.Handled = true; SelectAlias(name); }
    }

    void ApplySearch(SubtitleSearchResult result)
    {
        _searchResult = result;
        foreach (var item in result.Results)
        {
            var existing = _results.FirstOrDefault(candidate => candidate.Source == item.Source && candidate.Id == item.Id);
            if (existing == null) _results.Add(item);
            else if (existing.Local)
            {
                var updated = item with { Local = true, Files = existing.Files, Season = existing.Season, AvailableSeasons = existing.AvailableSeasons,
                    TitleNames = item.TitleNames.Concat(existing.TitleNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                if (existing != updated) _results[_results.IndexOf(existing)] = updated;
            }
        }
        RefreshSeasons();
        RefreshResults();
    }

    void RefreshLocalResults(string keyword)
    {
        var work = _titles.CurrentWork();
        var search = new SubtitleQuery(keyword, null, null, "");
        bool current = search.MatchesTitle(work.Names.Append(_media.Title))
            || _results.Any(result => work.ForDownload(search, result).Current);
        var localResults = _library.Results(keyword).AsEnumerable();
        if (current && !keyword.Equals(_media.Title, StringComparison.OrdinalIgnoreCase))
            localResults = localResults.Concat(_library.Results(_media.Title));
        foreach (var local in localResults.DistinctBy(result => (result.Source, result.Id)))
        {
            var existing = _results.FirstOrDefault(item => item.Source == local.Source && item.Id == local.Id);
            if (existing == null) _results.Add(local);
            else
            {
                var updated = existing with { Local = true, Files = local.Files, Season = local.Season, AvailableSeasons = local.AvailableSeasons,
                    TitleNames = existing.TitleNames.Concat(local.TitleNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                if (existing != updated) _results[_results.IndexOf(existing)] = updated;
            }
        }
    }

    static SubtitleSource? SourceForTag(string tag) => tag switch
    {
        "assrt" => SubtitleSource.Assrt,
        "amenzb" => SubtitleSource.Amenzb,
        "tsukihime" => SubtitleSource.Tsukihime,
        _ => null
    };
    SubtitleSource? SelectedSource => SourceForTag(_source);
    void RefreshSources()
    {
        if (SelectedSource is { } selected && !_store.Credentials.IsEnabled(selected)) _source = "all";
        foreach (Button button in SourceChips.Children)
            button.Visibility = SourceForTag((string)button.Tag) is { } source && !_store.Credentials.IsEnabled(source)
                ? Visibility.Collapsed : Visibility.Visible;
        RefreshChips(SourceChips, _source);
    }
    int? SelectedSeason => int.TryParse(_season, out int season) ? season : null;
    int? SelectedEpisode => int.TryParse(_episode, out int episode) ? episode : null;
    int? CurrentSeason => _media.Kind == "show" && !_media.Parsed.Ambiguous ? _media.Season : null;
    bool MatchesContext(SubtitleResult result) => result.MatchesFilter(SelectedSource, "", SelectedSeason)
        && (result.Local || _store.Credentials.IsEnabled(result.Source))
        && (_source != "local" || result.Local)
        && (SelectedSource == null || !result.Local)
        && (_season != "other" || (result.Local ? result.AvailableSeasons.Any(season => season is null or < 0) : result.Season is null or < 0));
    bool FilterResult(object item) => item is SubtitleResult result && MatchesContext(result)
        && result.MatchesFilter(null, _language, SelectedSeason, SelectedEpisode);

    void RefreshSeasons()
    {
        int? current = CurrentSeason;
        var seasons = _results.SelectMany(result => result.Local ? result.AvailableSeasons : [result.Season]).Where(season => season is >= 0).Select(season => season!.Value)
            .Append(current ?? -1)
            .Append(SelectedSeason ?? -1)
            .Append(_displayedKeyword.Length > 0 && _searchResult?.Complete != true ? _requestedSeason ?? -1 : -1)
            .Where(season => season >= 0).Distinct().OrderBy(season => season != current).ThenBy(season => season).ToArray();
        SeasonChips.Children.Clear();
        void Add(string tag, string label)
        {
            var button = new Button { Tag = tag, Content = label, Style = (Style)FindResource("FilterButton") };
            button.Click += Season_Click; SeasonChips.Children.Add(button);
        }
        Add("all", "全部");
        foreach (int season in seasons) Add(season.ToString(), season == 0 ? "特别篇" : $"第 {season} 季");
        if (_season == "other" || _results.Any(result => result.Local ? result.AvailableSeasons.Any(season => season is null or < 0) : result.Season is null or < 0)) Add("other", "其他作品");
        SeasonRow.Visibility = Visibility.Visible;
        RefreshChips(SeasonChips, _season);
    }

    void RefreshResults()
    {
        RefreshEpisodes();
        RefreshLanguages();
        if ((_searchResult?.Query ?? _query) is { } query) _query = query with
            { Season = SelectedSeason, Language = _language, Episode = _season == "other" ? null : query.Episode };
        CollectionViewSource.GetDefaultView(_results).Refresh();
        int visible = _results.Count(result => FilterResult(result));
        ResultCount.Text = visible == _results.Count ? $"共 {visible} 条搜索结果" : $"共 {_results.Count} 条搜索结果，显示 {visible} 条";
        string errors = _searchResult == null ? "" : string.Join("\n", _searchResult.Errors
            .Where(pair => SelectedSource == null || SelectedSource == pair.Key).Select(pair => Error(pair.Value)));
        bool searching = _displayedKeyword.Length > 0 && _searchResult?.Complete != true;
        bool paused = searching && _searchResult?.Paused == true;
        bool loading = searching && !paused && _source != "local"
            && (SelectedSource == null || _requestedSources == null || _requestedSources.Contains(SelectedSource.Value));
        bool hasResults = _results.Any(result => FilterResult(result));
        RefreshSearchButton();
        Status.Text = paused ? "搜索已暂停" : loading ? "正在搜索字幕…" : hasResults ? errors : "";
        if (hasResults || loading || paused) EmptyState.Visibility = Visibility.Collapsed;
        else if (_displayedKeyword.Length == 0) Empty("点击搜索查找字幕");
        else if (_source == "assrt" && _store.Credentials.AssrtToken.Length == 0) Empty("请填写 ASSRT API 密钥");
        else Empty(errors.Length > 0 ? "搜索失败" : "没有符合筛选的字幕", errors);
    }

    void ShowResults()
    {
        RefreshSeasons();
    }

    void RefreshLanguages()
    {
        string[] fixedLanguages = ["zh-Hans", "zh-Hant", "en", "ja"];
        var languages = _results.Where(MatchesContext)
            .SelectMany(result => result.Languages)
            .Concat(_language.Length > 0 ? [SubtitleLanguage.Find(_language) ?? new("other", "其他")] : [])
            .DistinctBy(language => language.Code)
            .OrderBy(language => language.SortOrder).ThenBy(language => language.Name).ToArray();
        LanguageChips.Children.Clear();
        void Add(string tag, string label)
        {
            var button = new Button { Tag = tag, Content = label, Style = (Style)FindResource("FilterButton") };
            button.Click += Language_Click; LanguageChips.Children.Add(button);
        }
        Add("", "全部");
        foreach (string code in fixedLanguages) Add(code, SubtitleLanguage.Find(code)!.Name);
        foreach (var language in languages.Where(language => !fixedLanguages.Contains(language.Code))) Add(language.Code, language.Name);
        LanguageRow.Visibility = Visibility.Visible;
        RefreshChips(LanguageChips, _language);
    }

    void RefreshEpisodes()
    {
        var episodes = _results.Where(MatchesContext).Where(result => result.MatchesFilter(null, _language, SelectedSeason))
            .SelectMany(result => result.EpisodesFor(SelectedSeason, _language)).ToHashSet();
        int? current = CurrentSeason is { } season && (_season == "all" || SelectedSeason == season) ? _media.Episode : null;
        // Selected values are search conditions, independent of the available results.
        // Keep them visible even when a response is empty or another filter has no match.
        if (SelectedEpisode is { } selected) episodes.Add(selected);
        if (current is { } playing) episodes.Add(playing);
        if (MatchesRequestedContext && _requestedEpisode is { } requested) episodes.Add(requested);
        EpisodeChips.Children.Clear();
        void Add(string tag, string label)
        {
            var button = new Button { Tag = tag, Content = label, Style = (Style)FindResource("FilterButton") };
            button.Click += Episode_Click; EpisodeChips.Children.Add(button);
        }
        Add("all", "全部");
        foreach (int episode in episodes.OrderBy(episode => episode != current).ThenBy(episode => episode)) Add(episode.ToString(), $"第 {episode} 集");
        EpisodeRow.Visibility = Visibility.Visible;
        RefreshChips(EpisodeChips, _episode);
    }

    void RefreshChips(Panel panel, string value)
    {
        foreach (Button button in panel.Children)
        {
            bool selected = (string)button.Tag == value;
            button.Background = selected ? Theme?.Heading : Theme?.MenuHighlight;
            button.Foreground = selected ? Brushes.White : Theme?.Foreground;
        }
    }
    void Source_Click(object sender, RoutedEventArgs e)
    {
        string source = (string)((Button)sender).Tag;
        if (SourceForTag(source) is { } online && !_store.Credentials.IsEnabled(online)) return;
        if (_source == source) return;
        _source = source; RefreshChips(SourceChips, _source); RefreshResults();
    }
    void Language_Click(object sender, RoutedEventArgs e)
    {
        string language = (string)((Button)sender).Tag;
        if (_language == language) return;
        _language = language; RefreshChips(LanguageChips, _language); RefreshResults();
    }
    void Season_Click(object sender, RoutedEventArgs e)
    {
        _season = (string)((Button)sender).Tag; UpdateSearchKeyword(); RefreshChips(SeasonChips, _season); RefreshResults();
    }

    void Episode_Click(object sender, RoutedEventArgs e)
    {
        _episode = (string)((Button)sender).Tag; UpdateSearchKeyword(); RefreshChips(EpisodeChips, _episode); RefreshResults();
    }

    void ResetSearch()
    {
        _query = null; _searchResult = null; _displayedKeyword = ""; _results.Clear();
        ResultCount.Text = "共 0 条搜索结果";
        Status.Text = ""; EmptyState.Visibility = Visibility.Collapsed; RefreshSearchButton();
    }
    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            e.Handled = true;
            if (!AliasesPopup.IsOpen) OpenAliases();
            else if (AliasesList.Items.Count > 0)
            {
                AliasesList.SelectedIndex = Math.Max(0, AliasesList.SelectedIndex);
                AliasesList.ScrollIntoView(AliasesList.SelectedItem);
                AliasesList.UpdateLayout();
                (AliasesList.ItemContainerGenerator.ContainerFromIndex(AliasesList.SelectedIndex) as ListBoxItem)?.Focus();
            }
        }
        else if (e.Key == Key.Enter) { e.Handled = true; SearchOrPause(); }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (AliasesPopup.IsOpen) AliasesPopup.IsOpen = false;
            else Close();
        }
    }

    async void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: SubtitleResult result })
            await LoadAsync(result);
    }

    async void Result_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ItemsControl.ContainerFromElement(ResultsList, Keyboard.FocusedElement as DependencyObject)
            is ListBoxItem { DataContext: SubtitleResult result })
        { e.Handled = true; await LoadAsync(result); }
    }

    async Task LoadAsync(SubtitleResult result)
    {
        if (_closed) return;
        var query = (_query ?? new(SearchBox.Text.Trim(), null, null, "")) with
        {
            Season = (result.Collection ? null : result.Season) ?? SelectedSeason ?? (_media.Kind == "show" ? _media.Season : null)
        };
        Close();
        await SubtitlePlayback.Current.DownloadAsync(query, result, _store, _titles).ConfigureAwait(false);
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SubtitleSourcesWindow(_store, _api) { Owner = this };
        window.SettingsChanged += (_, _) => ApplySourceSettings();
        window.ShowDialog();
    }
    void ApplySourceSettings()
    {
        if (_closed) return;
        _searchLifetime.Cancel();
        _searchLifetime.Dispose();
        _search.Dispose();
        _searchLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _search = CreateSearch(); ResetSearch(); RefreshSources(); InitializeResults();
    }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Folder_Click(object sender, RoutedEventArgs e)
    { Directory.CreateDirectory(_library.Root); ProcessHelp.ShellExecute(_library.Root); }
}
