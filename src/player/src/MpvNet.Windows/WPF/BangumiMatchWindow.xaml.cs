using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BangumiNet.Api.V0.Models;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.UI;

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

    public BangumiMatchWindow() : this(BangumiPlayback.Current) { }
    public BangumiMatchWindow(BangumiPlayback sync)
    {
        _sync = sync;
        _media = sync.Media;
        _generation = sync.Generation;
        InitializeComponent();
        DataContext = this;
        QueryBox.Text = _media.Title;
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
        if (sync.Subject != null) { SubjectsList.Items.Add(sync.Subject); SubjectsList.SelectedItem = sync.Subject; }
    }

    void Sync_Changed() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        if (_generation != _sync.Generation)
        {
            _request?.Cancel();
            _media = _sync.Media;
            _generation = _sync.Generation;
            QueryBox.Text = _media.Title;
            SubjectsList.Items.Clear();
            EpisodesBox.Items.Clear();
            _episodes = [];
        }
        Refresh();
    }));

    void Refresh()
    {
        bool connected = _sync.Client.Account != null;
        SearchButton.IsEnabled = connected && !_busy;
        BindButton.IsEnabled = connected && !_busy && EpisodesBox.SelectedItem != null;
        StatusText.Text = _sync.Selection is { } selected
            ? selected.SubjectTitle + " · " + selected.EpisodeTitle + "\n" + _sync.Status : _sync.Status;
    }

    async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        Refresh();
        try { await action(_lifetime.Token); }
        catch (Exception e) { if (!_closed) StatusText.Text = BangumiPlayback.ErrorText(e); }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                Refresh();
            }
        }
    }

    async void Search_Click(object sender, RoutedEventArgs e) => await RunAsync(async cancellationToken =>
    {
        string keyword = QueryBox.Text.Trim();
        if (keyword.Length == 0) return;
        _request?.Cancel();
        _request?.Dispose();
        _request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _request.Token;
        SubjectsList.Items.Clear();
        EpisodesBox.Items.Clear();
        var subjects = await BangumiPlayback.RetryReadAsync(t => _sync.Client.SearchAsync(keyword, t), token);
        token.ThrowIfCancellationRequested();
        foreach (var subject in subjects)
            SubjectsList.Items.Add(subject);
        StatusText.Text = subjects.Count == 0 ? "未找到条目。" : "选择本季条目与当前剧集。";
    });

    async void Subject_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not Subject subject) return;
        _request?.Cancel();
        _request?.Dispose();
        _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _request.Token;
        EpisodesBox.Items.Clear();
        BindButton.IsEnabled = false;
        try
        {
            var episodes = await BangumiPlayback.RetryReadAsync(t => _sync.Client.EpisodesAsync(subject.Id!.Value, t), token);
            token.ThrowIfCancellationRequested();
            _episodes = episodes;
            foreach (var episode in _episodes)
                EpisodesBox.Items.Add(new ComboBoxItem { Content = BangumiPlayback.DisplayEpisode(episode, BangumiMedia.IsMovie(subject)), Tag = episode });
            int season = Math.Max(_media.Season, BangumiMedia.Parse(BangumiPlayback.DisplayTitle(subject)).Season);
            var match = BangumiMedia.MatchEpisode(_episodes, _media.EpisodeNumber, season, BangumiMedia.IsMovie(subject));
            EpisodesBox.SelectedItem = EpisodesBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((Episode)i.Tag).Id == match?.Id);
            BindButton.IsEnabled = EpisodesBox.SelectedItem != null;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) StatusText.Text = BangumiPlayback.ErrorText(error); }
    }

    void Episode_Changed(object sender, SelectionChangedEventArgs e)
        => BindButton.IsEnabled = !_busy && _sync.Client.Account != null && EpisodesBox.SelectedItem != null;

    void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not Subject subject
            || EpisodesBox.SelectedItem is not ComboBoxItem { Tag: Episode episode }) return;
        try
        {
            _sync.Bind(_media, _generation, subject, episode, _episodes);
            _generation = _sync.Generation;
            Close();
        }
        catch (Exception error) { StatusText.Text = BangumiPlayback.ErrorText(error); }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
