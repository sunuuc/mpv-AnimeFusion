using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

#if ANIMEFUSION_DIAGNOSTICS
[assembly: AssemblyMetadata("AnimeFusion.Diagnostics.Enabled", "true")]
#endif

namespace MpvNet;

public static class Diagnostics
{
    public static bool Enabled =>
#if ANIMEFUSION_DIAGNOSTICS
        true;
#else
        false;
#endif

    public static void Write(string operation, string eventName, object? data = null, Exception? error = null)
    {
#if ANIMEFUSION_DIAGNOSTICS
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now,
                elapsed_ms = Clock.ElapsedMilliseconds,
                process = Environment.ProcessId,
                thread = Environment.CurrentManagedThreadId,
                operation = Sanitize(operation),
                @event = Sanitize(eventName),
                data = Clean(JsonSerializer.SerializeToElement(data)),
                error = error == null ? null : ErrorDetails(error)
            });
            lock (Sync)
            {
                if (Closing) return;
                if (Writer == null)
                {
                    string folder = Path.Combine(Player.ConfigFolder, "logs");
                    Directory.CreateDirectory(folder);
                    LogPath = Path.Combine(folder, FileName);
                    OpenWriter();
                }
                if (WrittenBytes >= 8 * 1024 * 1024)
                {
                    Writer!.Dispose();
                    Writer = null;
                    for (int i = 6; i >= 1; i--)
                    {
                        string old = LogPath + "." + i;
                        if (File.Exists(old)) File.Move(old, LogPath + "." + (i + 1), true);
                    }
                    File.Move(LogPath, LogPath + ".1", true);
                    OpenWriter();
                }
                Writer!.WriteLine(json);
                WrittenBytes += Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or ArgumentException or JsonException or NotSupportedException) { }
#endif
    }

    public static string Sanitize(string text)
    {
#if ANIMEFUSION_DIAGNOSTICS
        text = Headers.Replace(text, "$1<redacted>");
        text = Options.Replace(text, "$1<redacted>");
        text = Bearer.Replace(text, "Bearer <redacted>");
        text = Urls.Replace(text, "<url>");
        text = Paths.Replace(text, "<path>");
        text = Secrets.Replace(text, "$1<redacted>");
        return text.Length > 4096 ? text[..4096] + "…" : text;
#else
        return "";
#endif
    }

#if ANIMEFUSION_DIAGNOSTICS
    static readonly object Sync = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly string FileName = "diagnostic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
        + "-" + Environment.ProcessId + ".jsonl";
    static StreamWriter? Writer;
    static string LogPath = "";
    static long WrittenBytes;
    static bool Closing;

    static Diagnostics()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Sync)
            {
                Closing = true;
                try { Writer?.Dispose(); }
                catch (IOException) { }
                Writer = null;
            }
        };
    }

    static void OpenWriter()
    {
        var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 4096);
        WrittenBytes = stream.Length;
        Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    static readonly Regex Headers = new(@"(?im)((?:authorization|proxy-authorization|cookie|set-cookie|[\w-]*(?:token|api-key))\s*:\s*)[^\r\n]+", RegexOptions.Compiled);
    static readonly Regex Options = new(@"(?im)((?:http-header-fields|referrer|user-agent|stream-open-filename|filename|media-title)\s*['\"" ]*[:=]\s*)[^\r\n]+", RegexOptions.Compiled);
    static readonly Regex Bearer = new(@"(?i)\bBearer\s+[^\s,;]+", RegexOptions.Compiled);
    static readonly Regex Urls = new(@"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s<>\""']+", RegexOptions.Compiled);
    static readonly Regex Paths = new(@"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\""<>|]*|(?<![\w:])/[^\s\r\n\""<>|]*", RegexOptions.Compiled);
    static readonly Regex Secrets = new(@"(?i)((?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|passwd|signature)\s*['\"" ]*[=:]\s*)[^\s,;]+", RegexOptions.Compiled);

    static object? Clean(JsonElement value, string key = "")
    {
        string name = key.Replace("_", "").Replace("-", "").ToLowerInvariant();
        if (name.Contains("token") || name.Contains("password") || name.Contains("secret")
            || name.Contains("authorization") || name.Contains("cookie") || name.Contains("apikey")
            || name.Contains("header") || name is "body" or "query" or "signature" or "credentials")
            return "<redacted>";
        if (name.EndsWith("url") || name.EndsWith("uri") || name is "path" or "filename" or "directory")
            return value.ValueKind == JsonValueKind.Null ? null : "<redacted>";
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Clean(p.Value, p.Name)),
            JsonValueKind.Array => value.EnumerateArray().Select(item => Clean(item, key)).ToArray(),
            JsonValueKind.String => Sanitize(value.GetString() ?? ""),
            JsonValueKind.Number => value.Clone(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    // Messages and exception.Data can contain unlabelled tokens or request bodies.
    // Native error codes and nested exception types retain the actual failure cause.
    static object ErrorDetails(Exception error, int depth = 0) => new
    {
        type = error.GetType().FullName,
        code = error.HResult,
        http_status = error is HttpRequestException http ? (int?)http.StatusCode : null,
        http_error = error is HttpRequestException request ? request.HttpRequestError.ToString() : null,
        socket_error = error is SocketException socket ? socket.SocketErrorCode.ToString() : null,
        native_error = error is Win32Exception native ? (int?)native.NativeErrorCode : null,
        json_line = error is JsonException json ? json.LineNumber : null,
        json_byte = error is JsonException jsonOffset ? jsonOffset.BytePositionInLine : null,
        frames = new StackTrace(error, true).GetFrames().Select(frame => new
        {
            method = frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name,
            file = Path.GetFileName(frame.GetFileName()),
            line = frame.GetFileLineNumber()
        }).ToArray(),
        inner = error.InnerException == null || depth >= 7 ? null : ErrorDetails(error.InnerException, depth + 1)
    };
#endif
}
