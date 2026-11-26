using AnimeJaNai.Localization;
using AnimeJaNaiConfEditor.Services;
using AnimeJaNaiConfEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReactiveUI.Avalonia;
using FluentAvalonia.UI.Controls;
using Material.Icons.Avalonia;
using ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);
            LanguagePanel.Attach(this);
            this.FindControl<ListBox>("PlaybackHistoryList")!.AddHandler(
                KeyDownEvent, HistoryEntryKeyDown, RoutingStrategies.Tunnel);
            Closing += MainWindow_Closing;
            Opened += MainWindow_Opened;
            Activated += (_, _) =>
            {
                if (DataContext is MainWindowViewModel { SelectedTabIndex: 2 } vm)
                    _ = vm.PlaybackHistory.RefreshAsync();
            };
        }

        private async void OpenAboutLink(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                await Launcher.LaunchUriAsync(uri);
        }

        private async void PreviousHistoryPage(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            bool focusFirst = sender is Button button && button.Classes.Contains(":focus-visible");
            await vm.PlaybackHistory.PreviousPageAsync();
            ScrollHistoryPageToTop(focusFirst);
        }

        private async void NextHistoryPage(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            bool focusFirst = sender is Button button && button.Classes.Contains(":focus-visible");
            await vm.PlaybackHistory.NextPageAsync();
            ScrollHistoryPageToTop(focusFirst);
        }

        private void ScrollHistoryPageToTop(bool focusFirst)
        {
            var list = this.FindControl<ListBox>("PlaybackHistoryList");
            if (list?.Items.Count is not > 0) return;
            list.ScrollIntoView(0);
            if (focusFirst)
                Dispatcher.UIThread.Post(() => list.ContainerFromIndex(0)?.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
        }

        private static void HistoryEntryKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.None || e.Source is not ListBoxItem { Content: PlaybackHistoryItem entry })
                return;

            switch (e.Key)
            {
                case Key.Enter: entry.IsExpanded = !entry.IsExpanded; break;
                case Key.Right: entry.IsExpanded = true; break;
                case Key.Left: entry.IsExpanded = false; break;
                default: return;
            }
            e.Handled = true;
        }

        private async void OpenAboutFile(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string name }) return;
            var path = Path.Combine(MainWindowViewModel.RootDir, name);
            if (Directory.Exists(path))
            {
                await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
            }
            else if (File.Exists(path))
            {
                var file = await StorageProvider.TryGetFileFromPathAsync(path);
                if (file != null) await Launcher.LaunchFileAsync(file);
            }
        }

        private void MainWindow_Opened(object? sender, EventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {

            }
        }

        private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {

            }
        }

        private async void ImportFullConfButtonClick(object? sender, RoutedEventArgs e) =>
            await ImportConfigurationAsync(true);

        private async void ImportCurrentProfileConfButtonClick(object? sender, RoutedEventArgs e) =>
            await ImportConfigurationAsync(false);

        private async Task ImportConfigurationAsync(bool full)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var picker = GetTopLevel(this)!.StorageProvider;
            var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiText.T(full ? "Import Full Conf File" : "Import Profile Conf File"),
                FileTypeFilter = [new FilePickerFileType(UiText.T(full ? "mpv-AnimeFusion Conf File" : "mpv-AnimeFusion Profile Conf File"))
                    { Patterns = [full ? "*.conf" : "*.pconf"] }, FilePickerFileTypes.All],
                SuggestedStartLocation = await picker.TryGetFolderFromPathAsync(vm.BackupPath)
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;

            var title = UiText.T(full ? "Confirm Full Conf Import" : "Confirm Profile Conf Import");
            if (full || vm.CurrentSlot.Chains.Count > 0)
            {
                var prompt = full ? UiText.T("Back up and replace all profiles with this file?\n\n") :
                    UiText.F($"Back up the configuration and replace profile {vm.CurrentSlot.ProfileName} with this file?\n\n");
                if (!await ConfirmImportAsync(title, prompt + path)) return;
            }

            await ApplyImportAsync(() =>
            {
                var parser = new Salaros.Configuration.ConfigParser(path);
                if (full) vm.ImportFullConfiguration(parser);
                else vm.ReadAnimeJaNaiConfToCurrentSlot(parser, true);
            });
        }

        private async void CloneSelectedProfileToCurrentProfile(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm || vm.SelectedProfileToClone is not { } source) return;
            if (vm.CurrentSlot.Chains.Count > 0 && !await ConfirmImportAsync(UiText.T("Confirm Profile Conf Import"),
                    UiText.F($"Back up the configuration and replace profile {vm.CurrentSlot.ProfileName} with {source.ProfileName}?")))
                return;
            await ApplyImportAsync(() => vm.ReadAnimeJaNaiConfToCurrentSlot(vm.ParsedAnimeJaNaiProfileConf(source), true));
        }

        private async Task<bool> ConfirmImportAsync(string title, string prompt)
        {
            var dialog = new FATaskDialog
            {
                XamlRoot = this, Title = title, Content = prompt,
                Buttons = { new FATaskDialogButton(UiText.T("OK"), FATaskDialogStandardResult.OK),
                            new FATaskDialogButton(UiText.T("Cancel"), FATaskDialogStandardResult.Cancel) }
            };
            return Equals(await dialog.ShowAsync(), FATaskDialogStandardResult.OK);
        }

        private async Task ApplyImportAsync(Action import)
        {
            try { import(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                var dialog = new FATaskDialog
                {
                    XamlRoot = this, Title = UiText.T("Import failed"), Content = ex.Message,
                    Buttons = { new FATaskDialogButton(UiText.T("OK"), FATaskDialogStandardResult.OK) }
                };
                await dialog.ShowAsync();
            }
        }

        private async void ExportFullConfButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                var storageProvider = topLevel.StorageProvider;

                // Start async operation to open the dialog.
                var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = AnimeJaNai.Localization.UiText.T("Export Full Conf File"),
                    DefaultExtension = "conf",
                    FileTypeChoices = new FilePickerFileType[]
                    {
                    new(AnimeJaNai.Localization.UiText.T("mpv-AnimeFusion Conf File (*.conf)")) { Patterns = new[] { "*.conf" } },
                    },
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(vm.BackupPath),
                });

                if (file is not null)
                {

                    //vm.OutputFilePath = file.TryGetLocalPath() ?? "";

                    var outPath = file.TryGetLocalPath();

                    if (outPath != null)
                    {
                        vm.WriteAnimeJaNaiConf(outPath);
                    }

                }
            }
        }

        private async void ExportCurrentProfileConfButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                var storageProvider = topLevel.StorageProvider;

                // Start async operation to open the dialog.
                var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = AnimeJaNai.Localization.UiText.T("Export Current Profile Conf File"),
                    DefaultExtension = "conf",
                    FileTypeChoices = new FilePickerFileType[]
                    {
                    new(AnimeJaNai.Localization.UiText.T("mpv-AnimeFusion Profile Conf File (*.pconf)")) { Patterns = new[] { "*.pconf" } },
                    },
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(vm.BackupPath),
                });

                if (file is not null)
                {
                    var outPath = file.TryGetLocalPath();

                    if (outPath != null)
                    {
                        vm.WriteAnimeJaNaiCurrentProfileConf(outPath);
                    }
                }
            }
        }

    }
}
