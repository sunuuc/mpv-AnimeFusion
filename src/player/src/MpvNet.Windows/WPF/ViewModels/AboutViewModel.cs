
using CommunityToolkit.Mvvm.Input;
using MpvNet.Help;

namespace MpvNet.Windows.WPF.ViewModels;

public partial class AboutViewModel : ViewModelBase
{
    public Action? CloseAction { get; set; }

    public string About { get; } = AppClass.About;

    [RelayCommand]
    public void OpenSource() => ProcessHelp.ShellExecute("https://github.com/sunuuc/mpv-AnimeFusion");

    [RelayCommand]
    public void OpenNotices() => ProcessHelp.ShellExecute(Path.Combine(Folder.Startup, "docs", "OPEN_SOURCE_NOTICES.md"));

    [RelayCommand]
    public void Close() => CloseAction!();
}
