using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MpvNet.Windows.Trakt;
using MpvNet.Windows.UI;

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
        KindBox.SelectedIndex = _media.Kind == "movie" ? 1 : 0;
        _sync.Changed += Sync_Changed;
        Closed += (_, _) =>
        {
            _closed = true; _sync.Changed -= Sync_Changed;
            _request?.Cancel(); _request?.Dispose(); _lifetime.Cancel(); _lifetime.Dispose();
        };
        Refresh();
        if (_sync.Subject != null) { SubjectsList.Items.Add(_sync.Subject); SubjectsList.SelectedItem = _sync.Subject; }
    }
    void Sync_Changed() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        if (_generation != _sync.Generation)
        {
            _request?.Cancel(); _busy = false; _media = _sync.Media; _generation = _sync.Generation;
            QueryBox.Text = _media.Title; SubjectsList.Items.Clear(); SeasonsBox.Items.Clear(); EpisodesBox.Items.Clear(); _episodes = []; _message = "";
        }
        Refresh();
    }));
    void Refresh()
    {
        bool connected = _sync.Client.Account != null;
        SearchButton.IsEnabled = connected && !_busy;
        BindButton.IsEnabled = connected && !_busy && EpisodesBox.SelectedItem != null;
        StatusText.Text = _message.Length > 0 ? _message : _sync.Status;
    }
    CancellationToken BeginRequest()
    {
        _request?.Cancel(); _request?.Dispose(); _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _busy = true; _message = "正在搜索作品…"; Refresh(); return _request.Token;
    }
    void EndRequest(CancellationToken token)
    { if (!_closed && !token.IsCancellationRequested) { _busy = false; Refresh(); } }

    async void Search_Click(object sender, RoutedEventArgs e)
    {
        string keyword = QueryBox.Text.Trim(); if (keyword.Length == 0) return;
        string kind = ((ComboBoxItem)KindBox.SelectedItem).Tag.ToString()!;
        var token = BeginRequest();
        try
        {
            SubjectsList.Items.Clear(); SeasonsBox.Items.Clear(); EpisodesBox.Items.Clear();
            var subjects = await _sync.Client.SearchAsync(keyword, kind, token); token.ThrowIfCancellationRequested();
            foreach (var subject in subjects) SubjectsList.Items.Add(subject);
            _message = subjects.Count == 0 ? "未找到条目。" : "请选择作品、季度和当前剧集。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = TraktPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }
    async void Subject_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not TraktSubject subject) return;
        var token = BeginRequest();
        try
        {
            SeasonsBox.Items.Clear(); EpisodesBox.Items.Clear(); _episodes = [];
            if (subject.Kind == "movie")
            {
                _episodes = [new(0, 1, "正片", subject.Ids)];
                EpisodesBox.Items.Add(new ComboBoxItem { Content = "正片", Tag = _episodes[0] }); EpisodesBox.SelectedIndex = 0;
            }
            else
            {
                var seasons = await _sync.Client.SeasonsAsync(subject.Ids.Trakt, token); token.ThrowIfCancellationRequested();
                foreach (var season in seasons.OrderBy(s => s.Number))
                    SeasonsBox.Items.Add(new ComboBoxItem { Content = season.Number == 0 ? "特别篇" : "第 " + season.Number + " 季", Tag = season.Number });
                _message = "请选择季度和当前剧集。";
                int wanted = _sync.Subject?.Ids.Trakt == subject.Ids.Trakt ? _sync.Season : _media.Season;
                SeasonsBox.SelectedItem = SeasonsBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == wanted);
            }
            _message = "请选择当前剧集。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = TraktPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }
    async void Season_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not TraktSubject subject || SeasonsBox.SelectedItem is not ComboBoxItem { Tag: int season }) return;
        var token = BeginRequest();
        try
        {
            EpisodesBox.Items.Clear(); _episodes = await _sync.Client.EpisodesAsync(subject.Ids.Trakt, season, token); token.ThrowIfCancellationRequested();
            foreach (var episode in _episodes) EpisodesBox.Items.Add(new ComboBoxItem { Content = episode.Display, Tag = episode });
            EpisodesBox.SelectedItem = EpisodesBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((TraktEpisode)i.Tag).Number == _media.Episode);
            _message = _episodes.Count == 0 ? "该季度没有剧集。" : "请选择当前剧集。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = TraktPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }
    void Episode_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not TraktSubject subject || EpisodesBox.SelectedItem is not ComboBoxItem { Tag: TraktEpisode episode }) return;
        try { _sync.Bind(_media, _generation, subject, episode, _episodes); _generation = _sync.Generation; Close(); }
        catch (Exception error) { _message = TraktPlayback.ErrorText(error); Refresh(); }
    }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
