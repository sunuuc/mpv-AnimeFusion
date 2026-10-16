using AnimeJaNai.Localization;
using AnimeJaNaiConfEditor.Services;
using Avalonia.Collections;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.ViewModels;

public sealed class PlaybackHistoryViewModel : ViewModelBase
{
    private readonly string _historyPath;
    private PlaybackHistorySnapshot? _snapshot;
    private readonly List<long> _pageCursors = [];
    private int _pageIndex;
    private long? _nextCursor;

    public PlaybackHistoryViewModel(string? historyPath = null)
    {
        _historyPath = historyPath ?? PlaybackHistory.FilePath;
    }

    public AvaloniaList<PlaybackHistoryItem> Items { get; } = [];
    public bool HasItems => Items.Count != 0;
    public bool HasPages => _pageIndex > 0 || _nextCursor.HasValue;
    public string PageText => string.Format(CultureInfo.CurrentCulture, UiText.T("Page {0}"), _pageIndex + 1);
    public bool CanPreviousPage => !IsLoading && _pageIndex > 0;
    public bool CanNextPage => !IsLoading && _nextCursor.HasValue;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(EmptyText));
            this.RaisePropertyChanged(nameof(CanPreviousPage));
            this.RaisePropertyChanged(nameof(CanNextPage));
        }
    }

    private string _errorText = "";
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorText, value);
            this.RaisePropertyChanged(nameof(HasError));
            this.RaisePropertyChanged(nameof(ShowErrorAboveItems));
            this.RaisePropertyChanged(nameof(EmptyText));
        }
    }

    public bool HasError => ErrorText.Length != 0;
    public bool ShowErrorAboveItems => HasItems && HasError;
    public string EmptyText => IsLoading ? UiText.T("Loading playback history…") :
        HasError ? ErrorText : UiText.T("No playback history yet");

    public Task RefreshAsync() => LoadPageAsync(0, null);
    public Task PreviousPageAsync() => CanPreviousPage
        ? LoadPageAsync(_pageIndex - 1, _pageCursors[_pageIndex - 1]) : Task.CompletedTask;
    public Task NextPageAsync() => CanNextPage
        ? LoadPageAsync(_pageIndex + 1, _nextCursor) : Task.CompletedTask;

    private async Task LoadPageAsync(int index, long? cursor)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorText = "";
        try
        {
            var result = await Task.Run(() =>
            {
                var snapshot = cursor.HasValue ? _snapshot! : PlaybackHistory.CreateSnapshot(_historyPath);
                long start = cursor ?? snapshot.Length;
                bool reset = !cursor.HasValue;
                PlaybackHistoryPage page;
                try
                {
                    page = PlaybackHistory.ReadPage(snapshot, start);
                }
                catch (PlaybackHistoryChangedException)
                {
                    // A cleared or truncated file invalidates every stored
                    // cursor. Start again without mixing two file snapshots.
                    snapshot = PlaybackHistory.CreateSnapshot(_historyPath);
                    start = snapshot.Length;
                    page = PlaybackHistory.ReadPage(snapshot, start);
                    reset = true;
                }
                return (snapshot, start, page, reset);
            });
            if (result.reset)
            {
                _pageCursors.Clear();
                index = 0;
            }
            if (index == _pageCursors.Count) _pageCursors.Add(result.start);
            _snapshot = result.snapshot;
            _pageIndex = index;
            _nextCursor = result.page.NextCursor;
            Items.Clear();
            Items.AddRange(result.page.Items);
            this.RaisePropertyChanged(nameof(HasItems));
            this.RaisePropertyChanged(nameof(ShowErrorAboveItems));
            this.RaisePropertyChanged(nameof(HasPages));
            this.RaisePropertyChanged(nameof(PageText));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ErrorText = UiText.T("Could not read playback history");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
