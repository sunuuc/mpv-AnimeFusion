using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MpvNet.Windows.MyAnimeList;
using MpvNet.Windows.UI;
using MpvNet.Windows.WPF.ViewModels;

namespace MpvNet.Windows.WPF;

public partial class MalMatchWindow : Window
{
    public Theme? Theme => Theme.Current;
    readonly MalPlayback _sync = MalPlayback.Current;
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _request;
    MalMedia _media;
    int _generation;
    bool _busy, _closed;
    string _message = "";

    public MalMatchWindow()
    {
        _media = _sync.Media; _generation = _sync.Generation;
        InitializeComponent(); DataContext = this; QueryBox.Text = _media.Title;
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
            QueryBox.Text = _media.Title; SubjectsList.Items.Clear(); EpisodesBox.Items.Clear(); _message = "";
        }
        Refresh();
    }));
    void Refresh()
    {
        bool connected = _sync.Client.Account != null;
        SearchButton.IsEnabled = connected && !_busy;
        BindButton.IsEnabled = connected && !_busy && TryEpisode(out int number);
        EpisodesBox.IsEnabled = connected && !_busy && SubjectsList.SelectedItem != null;
        StatusText.Text = connected ? _message : "请先登录。";
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
        var token = BeginRequest();
        try
        {
            SubjectsList.Items.Clear(); EpisodesBox.Items.Clear();
            var subjects = await _sync.Client.SearchAsync(keyword, token); token.ThrowIfCancellationRequested();
            var items = subjects.Select(CreateSubject).ToArray();
            foreach (var item in items) SubjectsList.Items.Add(item);
            Task covers = LoadCoversAsync(items);
            _message = subjects.Count == 0 ? "未找到条目。" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = MalPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }
    static MatchSubjectViewModel<MalSubject> CreateSubject(MalSubject subject)
        => new(subject, subject.Title, subject.Details, subject.Mean);

    async Task LoadCoversAsync(IEnumerable<MatchSubjectViewModel<MalSubject>> items)
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
    void Subject_Changed(object sender, SelectionChangedEventArgs e)
    {
        _message = "";
        EpisodesBox.Items.Clear();
        if (SubjectsList.SelectedItem is MatchSubjectViewModel<MalSubject> item)
        {
            var subject = item.Subject;
            int wanted = _sync.Subject?.Id == subject.Id && _sync.Selection is { } selected
                ? selected.Number : _media.Episode ?? 1;
            EpisodesBox.IsEditable = subject.NumEpisodes == 0;
            var numbers = subject.NumEpisodes > 0 ? Enumerable.Range(1, subject.NumEpisodes) : [wanted];
            foreach (int number in numbers)
                EpisodesBox.Items.Add(new ComboBoxItem { Content = "第 " + number + " 集", Tag = number });
            EpisodesBox.SelectedItem = EpisodesBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == wanted);
        }
        Refresh();
    }
    bool TryEpisode(out int number)
    {
        number = 0;
        if (SubjectsList.SelectedItem is not MatchSubjectViewModel<MalSubject> item) return false;
        if (EpisodesBox.SelectedItem is ComboBoxItem { Tag: int selected } choice
            && (!EpisodesBox.IsEditable || EpisodesBox.Text == choice.Content?.ToString())) number = selected;
        else if (EpisodesBox.IsEditable) int.TryParse(EpisodesBox.Text, out number);
        return number >= 1 && (item.Subject.NumEpisodes == 0 || number <= item.Subject.NumEpisodes);
    }
    void Episode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_busy && TryEpisode(out int number)) _message = "";
        Refresh();
    }
    void EpisodeText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || BindButton == null) return;
        if (!_busy && TryEpisode(out int number)) _message = "";
        Refresh();
    }
    void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not MatchSubjectViewModel<MalSubject> item || !TryEpisode(out int number)) return;
        var subject = item.Subject;
        try { _sync.Bind(_media, _generation, subject, number); _generation = _sync.Generation; Close(); }
        catch (Exception error) { _message = MalPlayback.ErrorText(error); Refresh(); }
    }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
