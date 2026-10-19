using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MpvNet.Windows.Trakt;
using MpvNet.Windows.UI;
using MpvNet.Windows.WPF.ViewModels;

namespace MpvNet.Windows.WPF;

public partial class TraktMatchWindow : Window
{
    public Theme? Theme => Theme.Current;
    readonly TraktPlayback _sync = TraktPlayback.Current;
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _request;
    TraktMedia _media;
    int _generation;
    IReadOnlyList<TraktEpisode> _episodes = [];
    bool _busy, _closed;
    string _message = "";

    public TraktMatchWindow()
    {
        _media = _sync.Media; _generation = _sync.Generation;
        InitializeComponent(); DataContext = this; QueryBox.Text = _media.Title;
        QueryBox.SetMedia(_media, _lifetime.Token);
        _sync.Changed += Sync_Changed;
        Closed += (_, _) =>
        {
            _closed = true; _sync.Changed -= Sync_Changed;
            _request?.Cancel(); _request?.Dispose(); _lifetime.Cancel(); _lifetime.Dispose();
        };
        Refresh();
        if (_sync.Subject != null)
        {
            var item = CreateSubject(_sync.Subject);
            SubjectsList.Items.Add(item); SubjectsList.SelectedItem = item;
            Task cover = item.LoadCoverAsync(t => _sync.Client.CoverFileAsync(item.Subject.Poster, t), _lifetime.Token);
        }
    }
    void Sync_Changed() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        if (_generation != _sync.Generation)
        {
            _request?.Cancel(); _busy = false; _media = _sync.Media; _generation = _sync.Generation;
            QueryBox.Text = _media.Title; SubjectsList.Items.Clear(); EpisodesBox.Items.Clear(); _episodes = []; _message = "";
            QueryBox.SetMedia(_media, _lifetime.Token);
        }
        Refresh();
    }));
    void Refresh()
    {
        bool connected = _sync.Client.Account != null;
        SearchButton.IsEnabled = connected && !_busy;
        BindButton.IsEnabled = connected && !_busy && EpisodesBox.SelectedItem != null;
        EpisodesBox.IsEnabled = connected && !_busy && EpisodesBox.Items.Count > 0;
        StatusText.Text = connected ? _message : "请先登录。";
    }
    CancellationToken BeginRequest(string message)
    {
        _request?.Cancel(); _request?.Dispose(); _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _busy = true; _message = message; Refresh(); return _request.Token;
    }
    void EndRequest(CancellationToken token)
    { if (!_closed && !token.IsCancellationRequested) { _busy = false; Refresh(); } }

    async void Search_Click(object sender, RoutedEventArgs e)
    {
        string keyword = QueryBox.Text.Trim(); if (keyword.Length == 0) return;
        var token = BeginRequest("正在搜索作品…");
        try
        {
            SubjectsList.Items.Clear(); EpisodesBox.Items.Clear();
            var subjects = (await _sync.Client.SearchAsync(keyword, "show", token))
                .Concat(await _sync.Client.SearchAsync(keyword, "movie", token)).ToArray();
            token.ThrowIfCancellationRequested();
            var items = subjects.Select(CreateSubject).ToArray();
            foreach (var item in items) SubjectsList.Items.Add(item);
            Task covers = LoadCoversAsync(items);
            _message = subjects.Length == 0 ? "未找到条目。" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = TraktPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }
    static MatchSubjectViewModel<TraktSubject> CreateSubject(TraktSubject subject)
        => new(subject, subject.Title, subject.Details, subject.Rating);

    async Task LoadCoversAsync(IEnumerable<MatchSubjectViewModel<TraktSubject>> items)
    {
        var token = _lifetime.Token;
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(items.Select(async item =>
        {
            try
            {
                await gate.WaitAsync(token);
                try { await item.LoadCoverAsync(t => _sync.Client.CoverFileAsync(item.Subject.Poster, t), token); }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
        }));
    }
    async void Subject_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not MatchSubjectViewModel<TraktSubject> item) return;
        var subject = item.Subject;
        var token = BeginRequest("正在加载剧集…");
        try
        {
            EpisodesBox.Items.Clear(); _episodes = [];
            IReadOnlyList<TraktEpisode> episodes = subject.Kind == "movie" ? [new(0, 1, "正片", subject.Ids)]
                : await _sync.Client.AllEpisodesAsync(subject.Ids.Trakt, token);
            token.ThrowIfCancellationRequested();
            _episodes = episodes;
            foreach (var episode in _episodes)
            {
                string label = subject.Kind == "movie" ? "正片"
                    : (episode.Season == 0 ? "特别篇" : "第 " + episode.Season + " 季") + " · " + episode.Display;
                EpisodesBox.Items.Add(new ComboBoxItem { Content = label, Tag = episode });
            }
            bool sameSubject = _sync.Subject?.Ids.Trakt == subject.Ids.Trakt;
            int wanted = sameSubject ? _sync.Season : _media.Season;
            EpisodesBox.SelectedItem = EpisodesBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
            {
                var episode = (TraktEpisode)i.Tag;
                return subject.Kind == "movie" || (sameSubject && _sync.Selection is { } selected
                    ? episode.Ids.Trakt == selected.Ids.Trakt : episode.Season == wanted && episode.Number == _media.Episode);
            });
            _message = _episodes.Count == 0 ? "没有可选剧集。" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = TraktPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }
    void Episode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_busy && EpisodesBox.SelectedItem != null) _message = "";
        Refresh();
    }
    void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not MatchSubjectViewModel<TraktSubject> item || EpisodesBox.SelectedItem is not ComboBoxItem { Tag: TraktEpisode episode }) return;
        var subject = item.Subject;
        try { _sync.Bind(_media, _generation, subject, episode, _episodes.Where(e => e.Season == episode.Season).ToArray()); _generation = _sync.Generation; Close(); }
        catch (Exception error) { _message = TraktPlayback.ErrorText(error); Refresh(); }
    }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
