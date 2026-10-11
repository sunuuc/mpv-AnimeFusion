using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MpvNet.Windows.MyAnimeList;
using MpvNet.Windows.UI;

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
        EpisodeBox.Text = (_media.Episode ?? 1).ToString();
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
            QueryBox.Text = _media.Title; SubjectsList.Items.Clear(); EpisodeBox.Text = (_media.Episode ?? 1).ToString(); _message = "";
        }
        Refresh();
    }));
    void Refresh()
    {
        bool connected = _sync.Client.Account != null;
        SearchButton.IsEnabled = connected && !_busy;
        BindButton.IsEnabled = connected && !_busy && SubjectsList.SelectedItem != null && int.TryParse(EpisodeBox.Text, out int n) && n >= 1;
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
        var token = BeginRequest();
        try
        {
            SubjectsList.Items.Clear();
            var subjects = await _sync.Client.SearchAsync(keyword, token); token.ThrowIfCancellationRequested();
            foreach (var subject in subjects) SubjectsList.Items.Add(subject);
            _message = subjects.Count == 0 ? "未找到条目。" : "请选择该季度作品和当前剧集。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) _message = MalPlayback.ErrorText(error); }
        finally { EndRequest(token); }
    }
    void Subject_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    void Episode_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized && BindButton != null) Refresh(); }
    void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (SubjectsList.SelectedItem is not MalSubject subject || !int.TryParse(EpisodeBox.Text, out int number)) return;
        try { _sync.Bind(_media, _generation, subject, number); _generation = _sync.Generation; Close(); }
        catch (Exception error) { _message = MalPlayback.ErrorText(error); Refresh(); }
    }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
