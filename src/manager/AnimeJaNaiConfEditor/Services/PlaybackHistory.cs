using AnimeJaNaiConfEditor.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AnimeJaNaiConfEditor.Services;

public sealed record PlaybackHistorySnapshot(string Path, long Length);
public sealed record PlaybackHistoryPage(IReadOnlyList<PlaybackHistoryItem> Items, long? NextCursor);

internal sealed class PlaybackHistoryChangedException : IOException { }

public static class PlaybackHistory
{
    public const int PageSize = 20;
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mpv-AnimeFusion", "watch_history.jsonl");

    public static PlaybackHistorySnapshot CreateSnapshot(string path)
    {
        try
        {
            using var stream = Open(path);
            long end = stream.Length;
            var lines = new ReverseLines(stream, end);
            // Native appends always end in LF. Leave an unfinished tail for
            // the next refresh instead of racing the writer on this page.
            while (end > 0 && lines.ByteAt(end - 1) != '\n') end--;
            return new(path, end);
        }
        catch (FileNotFoundException) { return new(path, 0); }
        catch (DirectoryNotFoundException) { return new(path, 0); }
    }

    public static PlaybackHistoryPage ReadPage(PlaybackHistorySnapshot snapshot, long cursor,
        int pageSize = PageSize)
    {
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (cursor < 0 || cursor > snapshot.Length) throw new ArgumentOutOfRangeException(nameof(cursor));
        var items = new List<PlaybackHistoryItem>(pageSize);
        if (snapshot.Length == 0) return new(items, null);
        try
        {
            using var stream = Open(snapshot.Path);
            if (stream.Length < snapshot.Length) throw new PlaybackHistoryChangedException();
            var lines = new ReverseLines(stream, snapshot.Length);
            while (cursor > 0)
            {
                long before = cursor;
                string line = lines.ReadLine(ref cursor);
                var item = Parse(line);
                if (item == null) continue;
                // Probe one valid record to know whether another page exists,
                // but leave it unconsumed at the next page's byte cursor.
                if (items.Count == pageSize) return new(items, before);
                items.Add(item);
            }
            return new(items, null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or
            DirectoryNotFoundException or EndOfStreamException)
        {
            throw new PlaybackHistoryChangedException();
        }
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    private static PlaybackHistoryItem? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            var startedAt = DateTimeOffset.FromUnixTimeSeconds(record.GetProperty("time").GetInt64());
            var endedAt = DateTimeOffset.FromUnixTimeSeconds(record.GetProperty("end_time").GetInt64());
            var file = record.GetProperty("path").GetString();
            var start = record.GetProperty("start").GetDouble();
            var end = record.GetProperty("end").GetDouble();
            var durationValue = record.GetProperty("duration");
            double? duration = durationValue.ValueKind == JsonValueKind.Null ? null : durationValue.GetDouble();
            if (string.IsNullOrWhiteSpace(file) || !double.IsFinite(start) || !double.IsFinite(end) ||
                start < 0 || end < 0 || duration is double length && (!double.IsFinite(length) || length < 0))
                return null;
            var title = record.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
            JsonElement? mediaInfo = record.TryGetProperty("media_info", out var info) ? info : null;
            return new(title, file, startedAt, endedAt, start, end, duration, mediaInfo);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            // A damaged line must not hide other completed sessions.
            return null;
        }
    }

    private sealed class ReverseLines(FileStream stream, long length)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private long _bufferStart = -1;
        private int _bufferLength;

        public byte ByteAt(long position)
        {
            if (position < _bufferStart || position >= _bufferStart + _bufferLength)
            {
                _bufferStart = position / _buffer.Length * _buffer.Length;
                _bufferLength = (int)Math.Min(_buffer.Length, length - _bufferStart);
                stream.Position = _bufferStart;
                stream.ReadExactly(_buffer.AsSpan(0, _bufferLength));
            }
            return _buffer[(int)(position - _bufferStart)];
        }

        public string ReadLine(ref long cursor)
        {
            long end = cursor;
            if (end > 0 && ByteAt(end - 1) == '\n') end--;
            if (end > 0 && ByteAt(end - 1) == '\r') end--;
            long start = end;
            while (start > 0 && ByteAt(start - 1) != '\n') start--;
            cursor = start;
            long size = end - start;
            if (size > int.MaxValue) throw new IOException("Playback history entry is too large.");
            var bytes = new byte[(int)size];
            stream.Position = start;
            stream.ReadExactly(bytes);
            // Decode only complete lines, including UTF-8 characters split
            // across the fixed-size scan buffers.
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
