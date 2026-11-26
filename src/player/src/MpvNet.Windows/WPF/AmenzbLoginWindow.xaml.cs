using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MpvNet.Help;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.UI;

namespace MpvNet.Windows.WPF;

public partial class AmenzbLoginWindow : Window
{
    const string Origin = "https://amenzb.moe";
    readonly CancellationTokenSource _lifetime = new();
    readonly Func<CancellationToken, Task<AmenzbSession>> _readVerifiedSession;
    CancellationTokenSource? _verification;
    ulong _navigationId;
    public AmenzbSession? Session { get; private set; }
    public Theme? Theme => Theme.Current;

    public AmenzbLoginWindow() : this(null) { }

    internal AmenzbLoginWindow(Func<CancellationToken, Task<AmenzbSession>>? readVerifiedSession)
    {
        _readVerifiedSession = readVerifiedSession ?? ReadVerifiedSessionAsync;
        InitializeComponent();
        DataContext = this;
        Browser.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(Path.GetTempPath(), "mpv-AnimeFusion", "ameNZB-WebView2"),
            IsInPrivateModeEnabled = true
        };
    }

    static bool IsWebsite(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true }
        && uri.Host.Equals("amenzb.moe", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0;

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            if (_lifetime.IsCancellationRequested) return;
            var core = Browser.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !IsWebsite(uri))
                { args.Cancel = true; return; }
                if (!args.IsRedirected)
                    BeginNavigation(args.NavigationId);
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && IsWebsite(uri))
                    core.Navigate(uri.AbsoluteUri);
            };
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.DOMContentLoaded += async (_, args) => await DocumentReadyAsync(args.NavigationId, core.Source);
            core.NavigationCompleted += (_, args) =>
                CompleteNavigation(args.NavigationId, args.IsSuccess, args.HttpStatusCode, args.WebErrorStatus);
            core.Navigate(Origin + "/login");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (_lifetime.IsCancellationRequested) return;
            Status.Text = "请先安装 Microsoft Edge WebView2 运行时。";
            RuntimeButton.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            if (!_lifetime.IsCancellationRequested) Status.Text = "无法打开登录窗口，请关闭后重试。";
        }
    }

    void BeginNavigation(ulong id)
    {
        _navigationId = id;
        _verification?.Cancel();
        _verification = null;
        FinishButton.IsEnabled = true;
        RetryButton.Visibility = Visibility.Collapsed;
        Status.Text = "";
    }

    Task DocumentReadyAsync(ulong id, string source)
    {
        if (id == _navigationId && Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && IsWebsite(uri) && uri.AbsolutePath is "/" or "/browse" or "/profile")
            return VerifyAsync();
        return Task.CompletedTask;
    }

    void CompleteNavigation(ulong id, bool success, int httpStatus, CoreWebView2WebErrorStatus error)
    {
        if (_lifetime.IsCancellationRequested || id != _navigationId || _verification != null
            || error == CoreWebView2WebErrorStatus.OperationCanceled)
            return;
        // The site renders its own HTTP error pages, including a usable login form after a rejected password.
        if (httpStatus >= 400) return;
        if (!success)
        {
            Status.Text = "无法连接 ameNZB，请重试。";
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    async void Finish_Click(object sender, RoutedEventArgs e) => await VerifyAsync();

    async Task VerifyAsync()
    {
        if (_lifetime.IsCancellationRequested || _verification != null || !FinishButton.IsEnabled) return;
        using var check = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _verification = check;
        try
        {
            FinishButton.IsEnabled = false;
            Status.Text = "正在确认登录…";
            RetryButton.Visibility = Visibility.Collapsed;
            var session = await _readVerifiedSession(check.Token);
            check.Token.ThrowIfCancellationRequested();
            Session = session;
            DialogResult = true;
        }
        catch (Exception) when (check.IsCancellationRequested) { }
        catch (Exception error)
        {
            Status.Text = error switch
            {
                AmenzbLoginRequiredException => "请先在网页中完成登录。",
                SubtitleServiceException => error.Message,
                _ => "无法确认登录，请再试一次。"
            };
        }
        finally
        {
            if (_verification == check)
            {
                _verification = null;
                if (!_lifetime.IsCancellationRequested) FinishButton.IsEnabled = true;
            }
        }
    }

    async Task<AmenzbSession> ReadVerifiedSessionAsync(CancellationToken token)
    {
        var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(Origin + "/profile");
        token.ThrowIfCancellationRequested();
        var cookie = cookies.FirstOrDefault(c => c.Name == "mysession" && c.Path == "/" && c.IsSecure && c.IsHttpOnly
            && c.Domain.Equals("amenzb.moe", StringComparison.OrdinalIgnoreCase));
        if (cookie == null) throw new AmenzbLoginRequiredException();
        var session = new AmenzbSession(cookie.Name, cookie.Value, cookie.IsSession ? null : cookie.Expires.ToUniversalTime());
        using var http = AmenzbClient.CreateHttpClient();
        await AmenzbClient.ValidateSessionAsync(http, session, token);
        return session;
    }

    void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested || RetryButton.Visibility != Visibility.Visible) return;
        RetryButton.Visibility = Visibility.Collapsed;
        Browser.CoreWebView2.Navigate(Origin + "/");
    }

    void Runtime_Click(object sender, RoutedEventArgs e) =>
        ProcessHelp.ShellExecute("https://developer.microsoft.com/microsoft-edge/webview2/#download");

    void Window_Closed(object? sender, EventArgs e)
    {
        _lifetime.Cancel();
        Browser.Dispose();
        _lifetime.Dispose();
    }
}
