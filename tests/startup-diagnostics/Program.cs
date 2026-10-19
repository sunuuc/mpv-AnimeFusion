using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using MpvNet;

static class Checks
{
    const string Secret = "credential-never-write-this";
    static int checks;

    static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        checks++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowOriginal() => throw new ArgumentException("https://example.invalid/video?token=" + Secret);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Exception Original()
    {
        try { ThrowOriginal(); }
        catch (Exception inner)
        {
            try { throw new InvalidOperationException("private media title " + Secret, inner); }
            catch (Exception error) { return error; }
        }
        throw new Exception("Fixture did not throw");
    }

    static void Main()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "AnimeFusion-Diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("ANIMEVE_HOME", temporary);
        var originalProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy("http://proxy-user:" + Secret + "@127.0.0.1:9876");
        try
        {
            Check(Path.GetFullPath(Global.Player.ConfigFolder).TrimEnd(Path.DirectorySeparatorChar) == temporary,
                "All diagnostic writes must use the isolated test folder.");
            StartupDiagnostics.Begin();
            Exception original = Original();
            StartupDiagnostics.Failed(original);
            var networkError = new HttpRequestException(HttpRequestError.ConnectionError,
                "https://private.invalid/video?token=" + Secret,
                new SocketException((int)SocketError.ConnectionReset), HttpStatusCode.BadGateway);
            Diagnostics.Write("subtitle", "attempt-failed", new
            {
                elapsed_ms = 450,
                attempt = 2,
                status = 502,
                token = Secret,
                nested = new { headers = Secret, filePath = @"C:\Users\private-user\secret.txt" },
                text = "GET https://private.invalid/video?key=" + Secret + "\nAuthorization: Bearer " + Secret,
                query = Secret
            }, networkError);
            Terminal.WriteError(networkError);
            if (!Diagnostics.Enabled)
            {
                StartupDiagnostics.OptionError(-3);
                Check(!Directory.EnumerateFileSystemEntries(temporary).Any(),
                    "The production build creates neither diagnostic files nor a logs directory.");
                Check(Global.Player.Handle == 0 && Global.Player.MainHandle == 0,
                    "No native player or media was opened.");
                Console.WriteLine($"PASS {checks} production logging checks; no native player, UI or network.");
                return;
            }
            string log = Directory.GetFiles(Path.Combine(temporary, "logs"), "*.jsonl").Single();
            using var logStream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var logReader = new StreamReader(logStream);
            string logText = logReader.ReadToEnd();
            string[] logLines = logText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Check(logLines.Length >= 4, "Startup, network and terminal events reach the same detailed log.");
            Check(!logText.Contains(Secret) && !logText.Contains("private.invalid")
                && !logText.Contains("private-user"), "Diagnostics remove nested credentials, URLs and local paths.");
            using (var entry = JsonDocument.Parse(logLines.Single(line => line.Contains("attempt-failed"))))
            {
                var value = entry.RootElement;
                Check(value.GetProperty("time").GetDateTimeOffset() > DateTimeOffset.MinValue,
                    "The event carries a timestamp.");
                Check(value.GetProperty("data").GetProperty("elapsed_ms").GetInt32() == 450,
                    "Request timing remains available.");
                Check(value.GetProperty("error").GetProperty("http_status").GetInt32() == 502,
                    "Actual HTTP status is retained.");
                Check(value.GetProperty("error").GetProperty("inner").GetProperty("socket_error").GetString() == "ConnectionReset",
                    "Inner socket cause remains available without unsafe exception text.");
            }
            Check(Diagnostics.Sanitize("Cookie: auth=" + Secret).Contains(Secret) == false,
                "Native cookie headers are removed.");
            Check(!Diagnostics.Sanitize("X-Emby-Token: " + Secret).Contains(Secret),
                "Custom authentication headers are removed.");
            Check(!Diagnostics.Sanitize("Setting option 'http-header-fields' = 'X-Private: " + Secret + "'").Contains(Secret),
                "Native option dumps cannot expose arbitrary header values.");
            Check(!Diagnostics.Sanitize("GET /private/video?id=" + Secret + " HTTP/1.1").Contains(Secret),
                "Relative HTTP request targets cannot expose addresses or query strings.");
            Check(SpinWait.SpinUntil(() =>
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Contains("network-proxy");
            }, 5000), "System proxy environment is recorded once without blocking startup.");
            using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                string text = reader.ReadToEnd();
                Check(text.Contains("127.0.0.1") && text.Contains("9876") && !text.Contains(Secret)
                    && !text.Contains("proxy-user"), "Proxy host and port are recorded without credentials.");
            }
            string path = Path.Combine(temporary, "startup-error.json");
            string saved = File.ReadAllText(path);
            using var document = JsonDocument.Parse(saved);
            var error = document.RootElement.GetProperty("error");
            Check(error.GetProperty("type").GetString() == typeof(InvalidOperationException).FullName,
                "The first exception type is preserved.");
            Check(error.GetProperty("code").GetInt32() == original.HResult, "The error code is preserved.");
            Check(error.GetProperty("frames").EnumerateArray().Any(frame => frame.GetProperty("method").GetString()!.EndsWith(".Original")),
                "The original throwing method is recorded.");
            var inner = error.GetProperty("inner");
            Check(inner.GetProperty("type").GetString() == typeof(ArgumentException).FullName,
                "The inner exception survives wrapper exceptions.");
            Check(inner.GetProperty("frames").EnumerateArray().Any(frame => frame.GetProperty("method").GetString()!.EndsWith(".ThrowOriginal")),
                "The inner throwing method is recorded.");
            Check(!saved.Contains(Secret) && !saved.Contains("example.invalid") && !saved.Contains("private media title"),
                "Exception messages cannot leak credentials, playback addresses or titles.");
            Check(error.GetProperty("frames").EnumerateArray().All(frame =>
                frame.GetProperty("file").GetString() is not { } file || !file.Contains('\\') && !file.Contains('/')),
                "Source locations contain filenames rather than machine paths.");
            StartupDiagnostics.Failed(new IOException("secondary failure"));
            Check(File.ReadAllText(path) == saved, "A shutdown error cannot replace the first exception.");
            StartupDiagnostics.OptionError(-3);
            Check(File.ReadAllText(path) == saved, "Later normal diagnostic updates do not erase the failure.");
            Check(!File.Exists(path + ".tmp"), "Successful diagnostic writes leave no temporary file.");
            Check(Global.Player.Handle == 0 && Global.Player.MainHandle == 0, "No native player or media was opened.");
            Console.WriteLine($"PASS {checks} first-error persistence and privacy checks; no native player, UI or network.");
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
            // The target is the unique temporary folder created by this test.
            Directory.Delete(temporary, true);
        }
    }
}
