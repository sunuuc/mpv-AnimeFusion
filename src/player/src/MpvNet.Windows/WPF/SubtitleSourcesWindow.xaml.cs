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
    CancellationTokenSource? _verification;
    bool _closed;
    public bool Saved { get; private set; }
    public event EventHandler? SettingsChanged;
    public Theme? Theme => Theme.Current;

    public SubtitleSourcesWindow(SubtitleStore store, HttpClient http)
    {
        _store = store; _http = http;
        InitializeComponent(); DataContext = this;
        AssrtTokenBox.Password = store.Credentials.AssrtToken;
        SourcesStatus.Text = store.LoadError;
        RefreshSources();
    }
    void RefreshSources()
    {
        AssrtEnabledToggle.IsChecked = _store.Credentials.AssrtEnabled;
        AmenzbEnabledToggle.IsChecked = _store.Credentials.AmenzbEnabled;
        TsukihimeEnabledToggle.IsChecked = _store.Credentials.TsukihimeEnabled;
        AssrtSettings.Visibility = _store.Credentials.AssrtEnabled ? Visibility.Visible : Visibility.Collapsed;
    }
    void Source_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        bool saved = false;
        try
        {
            _store.Save(_store.Credentials with
            {
                AssrtEnabled = AssrtEnabledToggle.IsChecked == true,
                AmenzbEnabled = AmenzbEnabledToggle.IsChecked == true,
                TsukihimeEnabled = TsukihimeEnabledToggle.IsChecked == true
            });
            Saved = saved = true; SourcesStatus.Text = "";
            if (!_store.Credentials.AssrtEnabled)
            {
                var verification = _verification;
                _verification = null;
                verification?.Cancel();
                Busy(false); AssrtStatus.Text = "";
            }
        }
        catch (Exception) { SourcesStatus.Text = "来源设置保存失败，请重试。"; }
        RefreshSources();
        if (saved) SettingsChanged?.Invoke(this, EventArgs.Empty);
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
        if (_closed || !_store.Credentials.AssrtEnabled || _verification != null) return;
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _verification = verification;
        bool saved = false;
        Busy(true); AssrtStatus.Text = "正在验证…";
        try
        {
            string key = AssrtTokenBox.Password.Trim();
            await new AssrtClient(_http, _store).ValidateTokenAsync(key, verification.Token);
            if (_closed || verification.IsCancellationRequested || _verification != verification || !_store.Credentials.AssrtEnabled) return;
            _store.Save(_store.Credentials with { AssrtToken = key });
            Saved = saved = true; AssrtStatus.Text = "验证成功";
        }
        catch (OperationCanceledException) when (verification.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed && _verification == verification) AssrtStatus.Text = Error(error); }
        finally
        {
            if (_verification == verification)
            { _verification = null; if (!_closed) Busy(false); }
        }
        if (saved) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
    void Website_Click(object sender, RoutedEventArgs e) => ProcessHelp.ShellExecute((string)((Button)sender).Tag);
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Window_Closed(object? sender, EventArgs e)
    { _closed = true; _lifetime.Cancel(); AssrtTokenBox.Clear(); }
}
