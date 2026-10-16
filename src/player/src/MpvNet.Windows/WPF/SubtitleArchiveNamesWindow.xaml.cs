using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.UI;
using MpvNet.Windows.WinForms;

namespace MpvNet.Windows.WPF;

public partial class SubtitleArchiveNamesWindow : Window
{
    public Theme? Theme => Theme.Current;
    public IReadOnlyList<FileChoice> Files { get; }

    public sealed class FileChoice(SubtitleArchiveName file, int number) : INotifyPropertyChanged
    {
        public string Key => file.Key;
        public string Label => $"字幕文件 {number}";
        public IReadOnlyList<SubtitleArchiveNameOption> Options => file.Options;
        SubtitleArchiveNameOption? _selected;
        public SubtitleArchiveNameOption? Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public SubtitleArchiveNamesWindow(IReadOnlyList<SubtitleArchiveName> files)
    {
        Files = files.Select((file, index) => new FileChoice(file, index + 1)).ToArray();
        InitializeComponent();
        DataContext = this;
    }

    void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (Files.Any(file => file.Selected == null))
        {
            Status.Text = "请为每个字幕文件选择显示正确的名称。";
            return;
        }
        DialogResult = true;
    }

    public static Task<IReadOnlyDictionary<string, string>?> ChooseAsync(
        IReadOnlyList<SubtitleArchiveName> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<IReadOnlyDictionary<string, string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = MainForm.Instance;
        if (owner == null || owner.IsDisposed) throw new OperationCanceledException(token);
        owner.BeginInvoke(new Action(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var window = new SubtitleArchiveNamesWindow(files);
                new WindowInteropHelper(window).Owner = owner.Handle;
                using var registration = token.Register(() => window.Dispatcher.BeginInvoke(new Action(window.Close)));
                token.ThrowIfCancellationRequested();
                bool confirmed = window.ShowDialog() == true;
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(confirmed ? window.Files.ToDictionary(file => file.Key, file => file.Selected!.Name) : null);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }));
        return completion.Task;
    }
}
