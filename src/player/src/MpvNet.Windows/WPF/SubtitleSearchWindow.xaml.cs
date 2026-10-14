using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
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
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.MyAnimeList;
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
    readonly ObservableCollection<SubtitleResult> _results = new();
    SubtitleSearch _search;
    CancellationTokenSource _searchLifetime;
    SubtitleSearchResult? _searchResult;
    string _displayedKeyword = "";
    int _searchGeneration;
    readonly CancellationTokenSource _lifetime = new();
    SubtitleQuery? _query;
    string _source = "all", _language = "zh", _season = "all";
    bool _ready, _closed, _canClose;
    public bool IsCurrent => !_closed && !_canClose;
    public Theme? Theme => Theme.Current;

    public SubtitleSearchWindow()
    {
        _media = TraktMedia.Parse(Player.GetPropertyString("media-title"), Player.GetPropertyString("path"), new Dictionary<string, string>());
        _store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv-AnimeFusion", "Subtitles"));
        _assrt = new(_api, _store);
        _library = SubtitlePlayback.Current.Library;
        _search = new(_assrt);
        _searchLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (_media.Kind == "show") _season = _media.Season > 0 ? _media.Season.ToString() : "other";
        InitializeComponent();
        DataContext = this;
        SearchBox.Text = _media.Title;
        ResultsList.ItemsSource = _results;
        CollectionViewSource.GetDefaultView(_results).Filter = FilterResult;
        Player.StartFile += Media_Changed;
        Player.Shutdown += Media_Changed;
    }

    void Media_Changed()
    {
        _canClose = true;
        _lifetime.Cancel();
        Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Close(); }));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
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
        RefreshLocalResults(_displayedKeyword);
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
        RefreshChips(SourceChips, _source); RefreshChips(LanguageChips, _language);
        TextInputFocus.Focus(this, SearchBox, selectAll: true);
        Status.Text = _store.LoadError;
        RefreshSeasons();
        Search();
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

    void Search_Click(object sender, RoutedEventArgs e) => Search(refresh: true);

    async void Search(bool refresh = false)
    {
        if (!_ready || _closed) return;
        string keyword = SearchBox.Text.Normalize(NormalizationForm.FormKC).Trim();
        int generation = ++_searchGeneration;
        if (!keyword.Equals(_displayedKeyword, StringComparison.OrdinalIgnoreCase))
            _season = _media.Kind == "show" && keyword.Equals(_media.Title.Normalize(NormalizationForm.FormKC).Trim(), StringComparison.OrdinalIgnoreCase)
                ? _media.Season > 0 ? _media.Season.ToString() : "other" : "all";
        if (refresh)
        {
            _searchLifetime.Cancel(); _searchLifetime.Dispose();
            _searchLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            ResetSearch();
        }
        ShowResults();
        if (keyword.Length == 0) { ResetSearch(); Empty("输入作品名开始搜索"); return; }
        _displayedKeyword = keyword;
        RefreshLocalResults(keyword);
        RefreshSeasons();
        RefreshResults();
        _query ??= new(keyword, SelectedSeason, keyword.Equals(_media.Title, StringComparison.OrdinalIgnoreCase) ? _media.Episode : null, "");
        if (_store.Credentials.AssrtToken.Length == 0 || _source == "local") { RefreshSeasons(); RefreshResults(); return; }
        var search = _search;
        var progress = new Progress<SubtitleSearchResult>(result =>
        {
            if (!_closed && _search == search && _searchGeneration == generation && _displayedKeyword == keyword) ApplySearch(result);
        });
        try
        {
            var result = await search.SearchAsync(keyword, token => CreateQueryAsync(keyword, token), progress, _searchLifetime.Token, refresh);
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

    async Task<SubtitleQuery> CreateQueryAsync(string title, CancellationToken token)
    {
        bool current = title.Equals(_media.Title.Normalize(NormalizationForm.FormKC).Trim(), StringComparison.OrdinalIgnoreCase) && !_media.Parsed.Ambiguous;
        var names = new List<string>(BangumiMedia.TitleVariants(title));
        var media = current ? _media.Parsed : BangumiMedia.Parse(title);
        var binding = current ? BangumiPlayback.Current?.Store.FindBinding(media.Key) : null;
        if (MalPlayback.Current is { } mal)
        {
            try
            {
                var subjects = await mal.Client.CatalogueAsync(binding?.SubjectId, title, token);
                var matched = binding != null ? subjects.FirstOrDefault() : current ? media.MatchSubject(subjects) : null;
                if (matched != null) names.AddRange(BangumiMedia.SubjectTitles(matched).SelectMany(BangumiMedia.TitleVariants));
                else if (!current)
                    names.AddRange(subjects.Where(subject => BangumiMedia.SubjectTitles(subject).Any(name =>
                        BangumiMedia.TitleScore(TraktMedia.Normalize(title), TraktMedia.Normalize(BangumiMedia.Parse(name).Title)) >= .8))
                        .SelectMany(BangumiMedia.SubjectTitles).SelectMany(BangumiMedia.TitleVariants));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException
                or Microsoft.Kiota.Abstractions.ApiException or System.Text.Json.JsonException) { }
            if (current && mal.Subject is { } subject && mal.Media.Key == _media.Parsed.Key) names.AddRange(subject.Names);
        }
        if (current && TraktPlayback.Current is { Subject: { } trakt } playback && playback.Media.Key == _media.Key)
        {
            names.Add(trakt.Title);
        }
        return new(title, null, current ? _media.Episode : null, "")
        {
            Names = names.Distinct().ToArray(),
            Year = current && _media.Kind == "movie" ? _media.Parsed.Year : null
        };
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
                var updated = item with { Local = true, Files = existing.Files, Season = existing.Season, AvailableSeasons = existing.AvailableSeasons };
                if (existing != updated) _results[_results.IndexOf(existing)] = updated;
            }
        }
        RefreshSeasons();
        RefreshResults();
    }

    void RefreshLocalResults(string keyword)
    {
        foreach (var local in _library.Results(keyword))
        {
            var existing = _results.FirstOrDefault(item => item.Source == local.Source && item.Id == local.Id);
            if (existing == null) _results.Add(local);
            else
            {
                var updated = existing with { Local = true, Files = local.Files, Season = local.Season, AvailableSeasons = local.AvailableSeasons };
                if (existing != updated) _results[_results.IndexOf(existing)] = updated;
            }
        }
    }

    SubtitleSource? SelectedSource => _source == "assrt" ? SubtitleSource.Assrt : null;
    int? SelectedSeason => int.TryParse(_season, out int season) ? season : null;
    bool FilterResult(object item) => item is SubtitleResult result && result.MatchesFilter(SelectedSource, _language, SelectedSeason)
        && (_source != "local" || result.Local)
        && (_source != "assrt" || !result.Local)
        && (_season != "other" || (result.Local ? result.AvailableSeasons.Any(season => season is null or <= 0) : result.Season is null or <= 0));

    void RefreshSeasons()
    {
        bool current = _displayedKeyword.Equals(_media.Title.Normalize(NormalizationForm.FormKC).Trim(), StringComparison.OrdinalIgnoreCase);
        var seasons = _results.SelectMany(result => result.Local ? result.AvailableSeasons : [result.Season]).Where(season => season is > 0).Select(season => season!.Value)
            .Append(current && _media.Kind == "show" ? _media.Season : 0).Where(season => season > 0).Distinct().Order().ToArray();
        SeasonChips.Children.Clear();
        void Add(string tag, string label)
        {
            var button = new Button { Tag = tag, Content = label, Style = (Style)FindResource("FilterButton") };
            button.Click += Season_Click; SeasonChips.Children.Add(button);
        }
        Add("all", "全部");
        foreach (int season in seasons) Add(season.ToString(), $"第 {season} 季");
        if (_results.Any(result => result.Local ? result.AvailableSeasons.Any(season => season is null or <= 0) : result.Season is null or <= 0)) Add("other", "其他作品");
        SeasonRow.Visibility = seasons.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshChips(SeasonChips, _season);
    }

    void RefreshResults()
    {
        RefreshLanguages();
        if (_results.Count == 0 && _source != "local" && _store.Credentials.AssrtToken.Length == 0)
        { Empty("请先设置字幕来源"); return; }
        if (_searchResult != null) _query = _searchResult.Query with
            { Season = SelectedSeason, Language = _language, Episode = _season == "other" ? null : _searchResult.Query.Episode };
        CollectionViewSource.GetDefaultView(_results).Refresh();
        int visible = _results.Count(result => FilterResult(result));
        ResultCount.Text = visible == _results.Count ? $"共 {visible} 条搜索结果" : $"共 {_results.Count} 条搜索结果，显示 {visible} 条";
        string errors = _searchResult == null ? "" : string.Join("\n", _searchResult.Errors
            .Where(pair => SelectedSource == null || SelectedSource == pair.Key).Select(pair => Error(pair.Value)));
        bool loading = _source != "local" && _searchResult?.Complete != true
            && _store.Credentials.AssrtToken.Length > 0;
        bool hasResults = _results.Any(result => FilterResult(result));
        Status.Text = loading ? "正在搜索字幕…" : hasResults ? errors : "";
        if (hasResults || loading) EmptyState.Visibility = Visibility.Collapsed;
        else Empty(errors.Length > 0 ? "搜索失败" : "没有符合筛选的字幕", errors);
    }

    void ShowResults()
    {
        RefreshSeasons();
    }

    void RefreshLanguages()
    {
        var languages = _results.SelectMany(result => result.Languages).DistinctBy(language => language.Code)
            .OrderBy(language => language.Code == "other").ThenBy(language => language.Name).ToArray();
        LanguageChips.Children.Clear();
        void Add(string tag, string label)
        {
            var button = new Button { Tag = tag, Content = label, Style = (Style)FindResource("FilterButton") };
            button.Click += Language_Click; LanguageChips.Children.Add(button);
        }
        Add("", "全部");
        foreach (var language in languages) Add(language.Code, language.Name);
        if (_results.Count > 0 && _language.Length > 0 && !languages.Any(language => language.Code == _language)) _language = "";
        LanguageRow.Visibility = languages.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshChips(LanguageChips, _language);
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
        _season = (string)((Button)sender).Tag; RefreshChips(SeasonChips, _season); RefreshResults();
    }

    void ResetSearch()
    {
        _query = null; _searchResult = null; _displayedKeyword = ""; _results.Clear();
        ResultCount.Text = "共 0 条搜索结果";
        Status.Text = ""; EmptyState.Visibility = Visibility.Collapsed;
    }
    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; Search(refresh: true); } else if (e.Key == Key.Escape) Close(); }

    async void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: SubtitleResult result })
            await LoadAsync(result);
    }

    async void Result_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ResultsList.SelectedItem is SubtitleResult result)
        { e.Handled = true; await LoadAsync(result); }
    }

    async Task LoadAsync(SubtitleResult result)
    {
        if (_closed) return;
        bool current = _query?.MatchesTitle([_media.Title]) == true && !_media.Parsed.Ambiguous;
        int? season = (result.Collection ? null : result.Season) ?? SelectedSeason ?? (_media.Kind == "show" ? _media.Season : null);
        var query = (_query ?? new(SearchBox.Text.Trim(), null, null, "")) with
        {
            Title = current ? _media.Title : _query?.Title ?? SearchBox.Text.Trim(),
            Season = season,
            Episode = current && (_media.Kind != "show" || season == _media.Season) ? _media.Episode : null
        };
        Close();
        await SubtitlePlayback.Current.DownloadAsync(query, result, _store, current).ConfigureAwait(false);
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SubtitleSourcesWindow(_store, _api) { Owner = this };
        window.ShowDialog();
        if (window.Saved)
        {
            _searchLifetime.Cancel();
            _search.Dispose();
            _searchLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _search = new(_assrt); ResetSearch(); Search();
        }
    }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Folder_Click(object sender, RoutedEventArgs e)
    { Directory.CreateDirectory(_library.Root); ProcessHelp.ShellExecute(_library.Root); }
}
