using Avalonia.Controls;
using AnimeJaNai.Localization;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AnimeJaNaiConfEditor.ViewModels;

public sealed class PlaybackHistoryItem : ViewModelBase
{
    public PlaybackHistoryItem(string? title, string path, DateTimeOffset startedAt,
        DateTimeOffset endedAt, double start, double end, double? duration, JsonElement? mediaInfo = null)
    {
        Title = DisplayTitle(title, path);
        Source = DisplaySource(path);
        StartedAt = startedAt;
        EndedAt = endedAt;
        Start = start;
        End = end;
        Duration = duration is > 0 && double.IsFinite(duration.Value) ? duration : null;

        if (Duration is double length)
        {
            RangeStart = Math.Clamp(Math.Min(start, end) / length, 0, 1);
            RangeEnd = Math.Clamp(Math.Max(start, end) / length, 0, 1);
        }
        MediaInfoSections = ReadMediaInfo(mediaInfo);
    }

    public string Title { get; }
    public string Source { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset EndedAt { get; }
    public double Start { get; }
    public double End { get; }
    public double? Duration { get; }
    public double RangeStart { get; }
    public double RangeEnd { get; }
    public IReadOnlyList<PlaybackMediaInfoSection> MediaInfoSections { get; }
    public bool HasMediaInfo => MediaInfoSections.Count != 0;
    public string MediaInfoEmptyText => UiText.T("No media information was recorded");
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
    public GridLength BeforeRange => new(RangeStart, GridUnitType.Star);
    public GridLength WatchedRange => new(RangeEnd - RangeStart, GridUnitType.Star);
    public GridLength AfterRange => new(1 - RangeEnd, GridUnitType.Star);
    public string ProgressText => $"{FormatPosition(Start)} → {FormatPosition(End)}" +
        (Duration is double length ? $" / {FormatPosition(length)}" : "");
    public string WatchedAtText
    {
        get
        {
            var start = StartedAt.ToLocalTime();
            var end = EndedAt.ToLocalTime();
            return start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + " – " +
                end.ToString(start.Date == end.Date ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        }
    }

    private static string FormatPosition(double value)
    {
        var seconds = (long)Math.Floor(value);
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private IReadOnlyList<PlaybackMediaInfoSection> ReadMediaInfo(JsonElement? mediaInfo)
    {
        var sections = new List<PlaybackMediaInfoSection>();
        if (!mediaInfo.HasValue || mediaInfo.Value.ValueKind != JsonValueKind.Object) return sections;
        var info = mediaInfo.Value;

        var general = new List<PlaybackMediaInfoValue>();
        AddText(general, info, "container", "Format");
        if (Duration is double duration) general.Add(new(UiText.T("Duration"), FormatPosition(duration)));
        if (general.Count != 0) sections.Add(new(UiText.T("General"), general));

        if (!info.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array) return sections;
        foreach (var track in tracks.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object || !track.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String) continue;
            string? heading = type.GetString() switch
            {
                "video" => UiText.T("Video"),
                "audio" => UiText.T("Audio"),
                "sub" => UiText.T("Subtitle"),
                _ => null
            };
            if (heading == null) continue;
            if (track.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt32(out int number)) heading += $" #{number}";
            if (track.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True)
                heading += " · " + UiText.T("Selected track");
            var values = new List<PlaybackMediaInfoValue>();
            AddText(values, track, "title", "Title");
            AddText(values, track, "language", "Language");
            AddText(values, track, "codec", "Codec");
            AddText(values, track, "codec_description", "Codec description");
            AddText(values, track, "profile", "Codec profile");
            AddText(values, track, "format", "Format");
            AddNumber(values, track, "bit_depth", "Bit depth", value => $"{value:0} bit");
            if (PositiveNumber(track, "width") is double width && PositiveNumber(track, "height") is double height)
                values.Add(new(UiText.T("Resolution"), $"{width:0} × {height:0}"));
            else
            {
                AddNumber(values, track, "width", "Width", value => $"{value:0}");
                AddNumber(values, track, "height", "Height", value => $"{value:0}");
            }
            AddNumber(values, track, "fps", "Frame rate", value => $"{value:0.###} fps");
            AddNumber(values, track, "bitrate", "Bit rate", value => value >= 1_000_000
                ? $"{value / 1_000_000:0.###} Mb/s" : $"{value / 1_000:0.###} kb/s");
            AddNumber(values, track, "sample_rate", "Sample rate", value => $"{value:0} Hz");
            AddNumber(values, track, "channels", "Channels", value => $"{value:0}");
            AddText(values, track, "channel_layout", "Channel layout");
            if (values.Count != 0) sections.Add(new(heading, values));
        }
        return sections;
    }

    private static void AddText(List<PlaybackMediaInfoValue> values, JsonElement record, string property, string label)
    {
        if (record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text)
            values.Add(new(UiText.T(label), text));
    }

    private static double? PositiveNumber(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) && double.IsFinite(number) && number > 0 ? number : null;

    private static void AddNumber(List<PlaybackMediaInfoValue> values, JsonElement record, string property,
        string label, Func<double, string> format)
    {
        if (PositiveNumber(record, property) is double value) values.Add(new(UiText.T(label), format(value)));
    }

    private static string DisplayTitle(string? title, string path)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        var value = hasTitle ? title! : path;
        // A URL can be mpv's title when the stream supplies no metadata. Its
        // query and user information are not part of the displayed film name.
        if ((!hasTitle || value.Contains("://", StringComparison.Ordinal)) &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
                return Path.GetFileName(uri.LocalPath);
            var name = Path.GetFileName(uri.AbsolutePath);
            return name.Length == 0 ? uri.Host : Uri.UnescapeDataString(name);
        }
        return hasTitle ? value : Path.GetFileName(value);
    }

    private static string DisplaySource(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            return uri.IsFile ? uri.LocalPath :
                uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        return path;
    }
}

public sealed record PlaybackMediaInfoSection(string Title, IReadOnlyList<PlaybackMediaInfoValue> Values);
public sealed record PlaybackMediaInfoValue(string Label, string Value);
