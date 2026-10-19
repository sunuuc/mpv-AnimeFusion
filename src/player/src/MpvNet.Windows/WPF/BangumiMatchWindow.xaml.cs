using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BangumiNet.Api.V0.Models;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.UI;
using MpvNet.Windows.WPF.ViewModels;

namespace MpvNet.Windows.WPF;

public partial class BangumiMatchWindow : Window
{
    public Theme? Theme => Theme.Current;
    readonly BangumiPlayback _sync;
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _request;
    BangumiMedia _media;
    int _generation;
    IReadOnlyList<Episode> _episodes = [];
    bool _busy;
    bool _closed;
    string _message = "";

    public BangumiMatchWindow() : this(BangumiPlayback.Current) { }
    public BangumiMatchWindow(BangumiPlayback sync)
    {
        _sync = sync;
        _media = sync.Media;
        _generation = sync.Generation;
        InitializeComponent();
        DataContext = this;
        QueryBox.Text = _media.Title;
        QueryBox.SetMedia(new(_media, new Dictionary<string, string>()), _lifetime.Token);
        _sync.Changed += Sync_Changed;
        Closed += (_, _) =>
        {
            _closed = true;
            _sync.Changed -= Sync_Changed;
            _request?.Cancel();
            _request?.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Refresh();
        if (sync.Subject != null)
        {
            var item = CreateSubject(sync.Subject);
            SubjectsList.Items.Add(item); SubjectsList.SelectedItem = item;
            Task cover = item.LoadCoverAsync(t => _sync.Client.CoverFileAsync(item.Subject.Images?.Large ?? item.Subject.Images?.Common ?? "", t), _lifetime.Token);
        }
    }

    void Sync_Changed() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        if (_generation != _sync.Generation)
        {
            _request?.Cancel();
            _busy = false;
            _media = _sync.Media;
            _generation = _sync.Generation;
            QueryBox.Text = _media.Title;
            QueryBox.SetMedia(new(_media, new Dictionary<string, string>()), _lifetime.Token);
            SubjectsList.Items.Clear();
            EpisodesBox.Items.Clear();
            _episodes = [];
            _message = "";
        }
        Refresh();
    }));

    void Refresh()
    {
        bool connected = _sync.Client.Account != null;
        SearchButton.IsEnabled = connected && !_busy;
        EpisodesBox.IsEnabled = connected && !_busy && EpisodesBox.Items.Count > 0;
        BindButton.IsEnabled = connected && !_busy && EpisodesBox.SelectedItem != null;
        StatusText.Text = connected ? _message : "请先登录。";
    }

    CancellationToken BeginRequest(string message)
    {
        _request?.Cancel(); _request?.Dispose();
        _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _busy = true; _message = message; Refresh();
        return _request.Token;
    }

    void EndRequest(CancellationToken token)
    {
        if (!_closed && !token.IsCancellationRequested) { _busy = false; Refresh(); }
    }

    async void Search_Click(object sender, RoutedEventArgs e)
    {
        string keyword = QueryBox.Text.Trim();
        if (keyword.Length == 0) return;
        var token = BeginRequest("正在搜索作品…");
        try
        {
            SubjectsList.Items.Clear(); EpisodesBox.Items.Clear(); _episodes = [];
            var subjects = await BangumiPlayback.RetryReadAsync(t => _sync.Client.SearchAsync(keyword, t), token);
            token.ThrowIfCancellationRequested();
            var items = subjects.Select(CreateSubject).ToArray();
            foreach (var item in items) SubjectsList.Items.Add(item);
            Task covers = LoadCoversAsync(items);
            _message = subjects.Count == 0 ? "未找到条目。" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = BangumiPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }

    static MatchSubjectViewModel<Subject> CreateSubject(Subject subject)
    {
        string title = BangumiPlayback.DisplayTitle(subject);
        string subtitle = subject.Name != title ? subject.Name ?? "" : "";
        string details = string.Join(" · ", new[] { subject.Date, subject.Platform }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new(subject, title, details, subject.Rating?.Score, subtitle);
    }

    async Task LoadCoversAsync(IEnumerable<MatchSubjectViewModel<Subject>> items)
    {
        var token = _lifetime.Token;
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(items.Select(async item =>
        {
            try
            {
                await gate.WaitAsync(token);
                try { await item.LoadCoverAsync(t => _sync.Client.CoverFileAsync(item.Subject.Images?.Large ?? item.Subject.Images?.Common ?? "", t), token); }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
        }));
    }

    async void Subject_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not MatchSubjectViewModel<Subject> item) return;
        var subject = item.Subject;
        var token = BeginRequest("正在加载剧集…");
        EpisodesBox.Items.Clear(); _episodes = [];
        try
        {
            var episodes = await BangumiPlayback.RetryReadAsync(t => _sync.Client.EpisodesAsync(subject.Id!.Value, t), token);
            token.ThrowIfCancellationRequested();
            _episodes = episodes;
            foreach (var episode in _episodes)
                EpisodesBox.Items.Add(new ComboBoxItem { Content = BangumiPlayback.DisplayEpisode(episode, BangumiMedia.IsMovie(subject)), Tag = episode });
            int season = Math.Max(_media.Season, BangumiMedia.Parse(BangumiPlayback.DisplayTitle(subject)).Season);
            var match = _sync.Selection is { } selected && selected.SubjectId == subject.Id
                ? _episodes.FirstOrDefault(e => e.Id == selected.EpisodeId)
                : BangumiMedia.MatchEpisode(_episodes, _media.EpisodeNumber, season, BangumiMedia.IsMovie(subject));
            EpisodesBox.SelectedItem = EpisodesBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((Episode)i.Tag).Id == match?.Id);
            _message = _episodes.Count == 0 ? "没有可选剧集。" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = BangumiPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }

    void Episode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_busy && EpisodesBox.SelectedItem != null) _message = "";
        Refresh();
    }

    void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not MatchSubjectViewModel<Subject> item
            || EpisodesBox.SelectedItem is not ComboBoxItem { Tag: Episode episode }) return;
        var subject = item.Subject;
        try
        {
            _sync.Bind(_media, _generation, subject, episode, _episodes);
            _generation = _sync.Generation;
            Close();
        }
        catch (Exception error) { _message = BangumiPlayback.ErrorText(error); Refresh(); }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
