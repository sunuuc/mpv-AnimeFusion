using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MpvNet.Windows.WPF.ViewModels;

public sealed class MatchSubjectViewModel<T>(T subject, string title, string details, double? score, string subtitle = "") : ObservableObject
{
    public T Subject { get; } = subject;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Details { get; } = details;
    public string Score { get; } = score is double value ? $"评分 {value:0.0}" : "评分暂无";
    BitmapSource? _cover;
    public BitmapSource? Cover { get => _cover; private set => SetProperty(ref _cover, value); }

    public async Task LoadCoverAsync(Func<CancellationToken, Task<string>> load, CancellationToken token)
    {
        try
        {
            string path = await load(token);
            if (path.Length == 0) return;
            byte[] pixels = await File.ReadAllBytesAsync(path, token);
            token.ThrowIfCancellationRequested();
            var image = BitmapSource.Create(240, 360, 96, 96, PixelFormats.Pbgra32, null, pixels, 240 * 4);
            image.Freeze();
            Cover = image;
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or IOException
            or OperationCanceledException or ArgumentException or NotSupportedException) { }
    }
}
