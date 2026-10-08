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
            Closing += MainWindow_Closing;
            Opened += MainWindow_Opened;
        }

        private async void OpenAboutLink(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                await Launcher.LaunchUriAsync(uri);
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

        private async void ImportFullConfButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                // Start async operation to open the dialog.
                var storageProvider = topLevel.StorageProvider;

                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = AnimeJaNai.Localization.UiText.T("Import Profile Conf File"),
                    AllowMultiple = false,
                    FileTypeFilter = new FilePickerFileType[] { new(AnimeJaNai.Localization.UiText.T("mpv-AnimeFusion Conf File")) { Patterns = new[] { "*.conf" }, MimeTypes = new[] { "*/*" } }, FilePickerFileTypes.All },
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(vm.BackupPath),
                });

                if (files.Count >= 1)
                {

                    var inPath = files[0].TryGetLocalPath();

                    if (inPath != null)
                    {
                        var td = new FATaskDialog
                        {
                            Title = AnimeJaNai.Localization.UiText.T("Confirm Full Conf Import"),
                            ShowProgressBar = false,
                            Content = AnimeJaNai.Localization.UiText.T("The following full conf file will be imported. All configuration settings will be backed up and then all configuration settings for ALL PROFILES will be replaced with the imported conf file.\n\n") +
    inPath,
                            Buttons =
            {
                new FATaskDialogButton(AnimeJaNai.Localization.UiText.T("OK"), FATaskDialogStandardResult.OK),
                new FATaskDialogButton(AnimeJaNai.Localization.UiText.T(AnimeJaNai.Localization.UiText.T("Cancel")), FATaskDialogStandardResult.Cancel)
            }
                        };


                        td.Closing += async (s, e) =>
                        {
                            if ((FATaskDialogStandardResult)e.Result == FATaskDialogStandardResult.OK)
                            {
                                var deferral = e.GetDeferral();

                                td.ShowProgressBar = true;
                                int value = 0;


                                await Task.Run(() =>
                                {
                                    vm.CheckAndDoBackup();
                                    // autoSave: true wires the imported slots/chains/models for
                                    // persistence; the explicit write commits the import itself to
                                    // animejanai.conf (assigning AnimeJaNaiConf does not trigger a save).
                                    vm.AnimeJaNaiConf = vm.ReadAnimeJaNaiConf(inPath, true);
                                    vm.WriteAnimeJaNaiConf();
                                });

                                deferral.Complete();
                            }
                        };

                        td.XamlRoot = this;
                        _ = await td.ShowAsync();
                    }

                }
            }
        }

        private async void ImportCurrentProfileConfButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                // Start async operation to open the dialog.
                var storageProvider = topLevel.StorageProvider;

                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = AnimeJaNai.Localization.UiText.T("Import Full Conf File"),
                    AllowMultiple = false,
                    FileTypeFilter = new FilePickerFileType[] { new(AnimeJaNai.Localization.UiText.T("mpv-AnimeFusion Profile Conf File")) { Patterns = new[] { "*.pconf" }, MimeTypes = new[] { "*/*" } }, FilePickerFileTypes.All },
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(vm.BackupPath),
                });

                if (files.Count >= 1)
                {

                    var inPath = files[0].TryGetLocalPath();

                    if (inPath != null)
                    {
                        if (vm.CurrentSlot.Chains.Count == 0)
                        {
                            // blank slot, no need to prompt before importing and no need to do backup
                            vm.ReadAnimeJaNaiConfToCurrentSlot(inPath, true);
                        }
                        else
                        {
                            var td = new FATaskDialog
                            {
                                Title = AnimeJaNai.Localization.UiText.T("Confirm Profile Conf Import"),
                                ShowProgressBar = false,
                                Content = AnimeJaNai.Localization.UiText.F($"The following profile conf file will be imported to the current profile {vm.CurrentSlot.ProfileName}. All configuration settings will be backed up and then all configuration settings for the current profile {vm.CurrentSlot.ProfileName} will be overwritten.\n\n") +
                                inPath,
                                Buttons =
                            {
                                new FATaskDialogButton(AnimeJaNai.Localization.UiText.T("OK"), FATaskDialogStandardResult.OK),
                                new FATaskDialogButton(AnimeJaNai.Localization.UiText.T(AnimeJaNai.Localization.UiText.T("Cancel")), FATaskDialogStandardResult.Cancel)
                            }
                            };


                            td.Closing += async (s, e) =>
                            {
                                if ((FATaskDialogStandardResult)e.Result == FATaskDialogStandardResult.OK)
                                {
                                    var deferral = e.GetDeferral();

                                    td.ShowProgressBar = true;

                                    await Task.Run(() =>
                                    {
                                        vm.CheckAndDoBackup();
                                        vm.ReadAnimeJaNaiConfToCurrentSlot(inPath, true);
                                    });

                                    deferral.Complete();
                                }
                            };

                            td.XamlRoot = this;
                            _ = await td.ShowAsync();
                        }
                    }

                } 
            }
        }

        private async void CloneSelectedProfileToCurrentProfile(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                if (vm.SelectedProfileToClone != null)
                {
                    if (vm.CurrentSlot.Chains.Count == 0)
                    {
                        // Current slot is blank - no need to ask user for confirmation before cloning and no need to do backup
                        vm.ReadAnimeJaNaiConfToCurrentSlot(
                            vm.ParsedAnimeJaNaiProfileConf(vm.SelectedProfileToClone),
                            true);
                    }
                    else
                    {
                        var td = new FATaskDialog
                        {
                            Title = AnimeJaNai.Localization.UiText.T("Confirm Profile Conf Import"),
                            ShowProgressBar = false,
                            Content = AnimeJaNai.Localization.UiText.F($"The profile {vm.SelectedProfileToClone.ProfileName} will be cloned to the current profile {vm.CurrentSlot.ProfileName}. All configuration settings will be backed up and then all configuration settings for the current profile {vm.CurrentSlot.ProfileName} will be overwritten."),
                            Buttons =
                        {
                            new FATaskDialogButton(AnimeJaNai.Localization.UiText.T("OK"), FATaskDialogStandardResult.OK),
                            new FATaskDialogButton(AnimeJaNai.Localization.UiText.T(AnimeJaNai.Localization.UiText.T("Cancel")), FATaskDialogStandardResult.Cancel)
                        }
                        };


                        td.Closing += async (s, e) =>
                        {
                            if ((FATaskDialogStandardResult)e.Result == FATaskDialogStandardResult.OK)
                            {
                                var deferral = e.GetDeferral();

                                td.ShowProgressBar = true;
                                int value = 0;


                                await Task.Run(() =>
                                {
                                    vm.CheckAndDoBackup();
                                    vm.ReadAnimeJaNaiConfToCurrentSlot(
                                        vm.ParsedAnimeJaNaiProfileConf(vm.SelectedProfileToClone),
                                        true);
                                });

                                deferral.Complete();
                            }
                        };

                        td.XamlRoot = this;
                        _ = await td.ShowAsync();
                    }
                }
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
