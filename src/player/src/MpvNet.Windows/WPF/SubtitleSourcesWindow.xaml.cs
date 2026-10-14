using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MpvNet.Help;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.UI;

namespace MpvNet.Windows.WPF;

public partial class SubtitleSourcesWindow : Window
{
    readonly SubtitleStore _store;
    readonly HttpClient _http;
    readonly CancellationTokenSource _lifetime = new();
    bool _closed;
    public bool Saved { get; private set; }
    public Theme? Theme => Theme.Current;

    public SubtitleSourcesWindow(SubtitleStore store, HttpClient http)
    {
        _store = store; _http = http;
        InitializeComponent(); DataContext = this;
        AssrtTokenBox.Password = store.Credentials.AssrtToken;
        AssrtStatus.Text = store.LoadError;
    }
    void Busy(bool value)
    {
        VerifyButton.IsEnabled = !value;
        AssrtTokenBox.IsEnabled = !value;
    }
    static string Error(Exception error) => error is SubtitleServiceException ? error.Message
        : error is OperationCanceledException ? "请求超时，请重试。"
        : error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
            ? "密钥保存失败。" : "验证失败，请重试。";

    async void Verify_Click(object sender, RoutedEventArgs e)
    {
        Busy(true); AssrtStatus.Text = "正在验证…";
        try
        {
            string key = AssrtTokenBox.Password.Trim();
            await new AssrtClient(_http, _store).ValidateTokenAsync(key, _lifetime.Token);
            if (_closed) return;
            _store.Save(_store.Credentials with { AssrtToken = key });
            Saved = true; AssrtStatus.Text = "验证成功";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) AssrtStatus.Text = Error(error); }
        finally { if (!_closed) Busy(false); }
    }
    void Website_Click(object sender, RoutedEventArgs e) => ProcessHelp.ShellExecute((string)((Button)sender).Tag);
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Window_Closed(object? sender, EventArgs e)
    { _closed = true; _lifetime.Cancel(); AssrtTokenBox.Clear(); }
}
