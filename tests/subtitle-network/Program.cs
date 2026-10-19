using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using MpvNet.Windows.Subtitles;

static class Program
{
    static int _checks;
    static readonly string Root = Path.Combine(Path.GetTempPath(), "AnimeFusion-SubtitleNetwork-" + Guid.NewGuid().ToString("N"));
    static readonly SubtitleQuery Query = new("Network Fixture", 1, 1, "zh-Hans");
    const string Ass = "[Script Info]\nScriptType: v4.00+\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,网络恢复测试\n";
    const string EmptyPage = "{\"results\":[],\"start\":0,\"total\":0}";

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    static SubtitleResult Result(string id) => new(SubtitleSource.Amenzb, id, "Network Fixture S01E01", "简体中文", "", [])
    {
        Individual = true, Season = 1, Episode = 1, LanguageCode = "zh-Hans", Format = "ASS",
        DownloadName = "Network.Fixture.S01E01.zh-Hans.ass",
        DownloadUrl = "https://subtitles.example/" + id + ".ass"
    };

    static SubtitleLibrary Library(string name) => new(Path.Combine(Root, name));
    static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    static HttpResponseMessage Subtitle() => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Ass)) };
    static HttpResponseMessage Body(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    static HttpRequestException ConnectionError() => new(HttpRequestError.ConnectionError, "Fixture connection reset",
        new SocketException((int)SocketError.ConnectionReset));
    static HttpRequestException DnsError() => new(HttpRequestError.NameResolutionError, "Fixture DNS failure",
        new SocketException((int)SocketError.HostNotFound));
    static HttpIOException EndedError() => new(HttpRequestError.ResponseEnded, "Fixture response interrupted");

    static Task<string?> Download(HttpClient http, SubtitleLibrary library, string id, CancellationToken token = default)
        => new SubtitleDownload(http, library).DownloadAsync(Query, Result(id), null, [], null, token);

    static async Task<Exception> Fails(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch (Exception error) when (error is not TimeoutException) { return error; }
        throw new InvalidOperationException("Expected the fixture request to fail");
    }

    static void EmptyLibrary(SubtitleLibrary library, string message) =>
        Check(!Directory.EnumerateFiles(library.Root, "*", SearchOption.AllDirectories).Any(), message);

    static async Task JsonRecoveryChecks()
    {
        var requests = new List<HttpRequestMessage>();
        var times = new List<long>();
        using var handler = new Handler((request, _) =>
        {
            requests.Add(request); times.Add(Stopwatch.GetTimestamp());
            return requests.Count switch
            {
                1 => throw ConnectionError(),
                2 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                _ => Task.FromResult(Json(EmptyPage))
            };
        });
        using var http = new HttpClient(handler);
        var result = await new TsukihimeClient(http).SearchAsync(Query, 1, default);
        Check(result.Results.Count == 0 && !result.HasMore && handler.Count == 3,
            "A JSON search recovers from a connection error and a temporary HTTP error within three attempts.");
        Check(requests.Distinct(ReferenceEqualityComparer.Instance).Count() == 3,
            "Each JSON retry receives a new request message.");
        Check(times.Zip(times.Skip(1), Stopwatch.GetElapsedTime).Select(time => time.TotalSeconds).ToArray()
            is [>= .9, >= 1.9], "Transient failures retain the one-second and two-second retry delays.");

        var brokenBody = new InterruptedStream(Encoding.UTF8.GetBytes("{\"results\":"));
        using var bodyHandler = new Handler((_, _) => Task.FromResult(brokenBody.Reads == 0 ? Body(brokenBody) : Json(EmptyPage)));
        using var bodyHttp = new HttpClient(bodyHandler);
        var recovered = await new TsukihimeClient(bodyHttp).SearchAsync(Query, 1, default);
        Check(bodyHandler.Count == 2 && recovered.Results.Count == 0 && brokenBody.Disposed,
            "A JSON body interrupted after its response headers is retried and its failed response is disposed.");

        using var invalidHandler = new Handler((_, _) => Task.FromResult(Json("{invalid")));
        using var invalidHttp = new HttpClient(invalidHandler);
        var parseError = await Fails(() => new TsukihimeClient(invalidHttp).SearchAsync(Query, 1, default));
        Check(parseError is JsonException && invalidHandler.Count == 1,
            "A complete but malformed JSON response is not treated as a transient connection failure.");

        int htmlCalls = 0;
        using var htmlHandler = new Handler((_, _) => ++htmlCalls == 1 ? throw DnsError()
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<html><body></body></html>", Encoding.UTF8, "text/html") }));
        using var htmlHttp = new HttpClient(htmlHandler);
        var htmlPage = await new AmenzbClient(htmlHttp).SearchAsync(Query, 1, default);
        Check(htmlHandler.Count == 2 && htmlPage.Results.Count == 0 && !htmlPage.HasMore,
            "The HTML subtitle provider uses the same transient request recovery as JSON searches.");
    }

    static async Task HttpStatusChecks()
    {
        foreach (var status in new[] { 408, 500, 502, 503, 504 })
        {
            int calls = 0;
            using var handler = new Handler((_, _) => Task.FromResult(++calls == 1
                ? new HttpResponseMessage((HttpStatusCode)status) : Subtitle()));
            using var http = new HttpClient(handler);
            string? path = await Download(http, Library("recover-" + status), "recover-" + status);
            Check(handler.Count == 2 && path != null && File.ReadAllText(path) == Ass,
                "A temporary HTTP status is retried once before publishing the complete subtitle: " + status);
        }
        foreach (var status in new[] { 400, 401, 403, 404, 406, 429 })
        {
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
            using var http = new HttpClient(handler);
            var library = Library("reject-" + status);
            await Fails(() => Download(http, library, "reject-" + status));
            Check(handler.Count == 1, "A permanent, authentication, quota or rate-limit response is not retried: " + status);
            EmptyLibrary(library, "A rejected download does not create a partial file or index: " + status);
        }

        using var exhaustedHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var exhaustedHttp = new HttpClient(exhaustedHandler);
        var exhaustedLibrary = Library("http-exhausted");
        await Fails(() => Download(exhaustedHttp, exhaustedLibrary, "http-exhausted"));
        Check(exhaustedHandler.Count == 3, "Repeated retryable HTTP errors stop after three attempts.");
        EmptyLibrary(exhaustedLibrary, "Exhausted HTTP retries do not leave files or an index.");
    }

    static async Task TransportChecks()
    {
        var errors = new (string Name, Func<Exception> Create)[]
        {
            ("dns", DnsError), ("connection", ConnectionError), ("response", EndedError)
        };
        foreach (var entry in errors)
        {
            int calls = 0;
            using var handler = new Handler((_, _) => ++calls == 1 ? throw entry.Create() : Task.FromResult(Subtitle()));
            using var http = new HttpClient(handler);
            var path = await Download(http, Library(entry.Name), entry.Name);
            Check(handler.Count == 2 && path != null && File.ReadAllText(path) == Ass,
                "A transient transport failure can recover without a second user download: " + entry.Name);
        }

        var certificates = new Exception[]
        {
            new HttpRequestException(HttpRequestError.SecureConnectionError, "Fixture certificate rejection", new AuthenticationException()),
            new HttpRequestException("Fixture certificate rejection", new AuthenticationException()),
            new AuthenticationException("Fixture certificate rejection")
        };
        for (int index = 0; index < certificates.Length; index++)
        {
            var error = certificates[index];
            using var handler = new Handler((_, _) => throw error);
            using var http = new HttpClient(handler);
            var library = Library("certificate-" + index);
            await Fails(() => Download(http, library, "certificate-" + index));
            Check(handler.Count == 1, "A certificate failure is not retried or silently bypassed.");
            EmptyLibrary(library, "Certificate failures leave the local library unchanged.");
        }

        int exhausted = 0;
        var exhaustionStreams = new List<InterruptedStream>();
        using var exhaustedHandler = new Handler((_, _) =>
        {
            exhausted++;
            var stream = new InterruptedStream(Encoding.UTF8.GetBytes(Ass[..35]));
            exhaustionStreams.Add(stream);
            return Task.FromResult(Body(stream));
        });
        using var exhaustedHttp = new HttpClient(exhaustedHandler);
        var exhaustedLibrary = Library("exhausted");
        await Fails(() => Download(exhaustedHttp, exhaustedLibrary, "exhausted"));
        Check(exhausted == 3 && exhaustionStreams.All(stream => stream.Disposed),
            "Repeated body interruption stops after three requests and disposes every failed response.");
        EmptyLibrary(exhaustedLibrary, "Exhausted retries leave neither a partial subtitle nor a completed library index.");
    }

    static async Task CompleteBodyAndCacheChecks()
    {
        var interrupted = new InterruptedStream(Encoding.UTF8.GetBytes(Ass[..40]));
        int calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1 ? Body(interrupted) : Subtitle()));
        using var http = new HttpClient(handler);
        var library = Library("complete-body");
        string? path = await Download(http, library, "complete-body");
        Check(handler.Count == 2 && path != null && File.ReadAllBytes(path).SequenceEqual(Encoding.UTF8.GetBytes(Ass)),
            "A retry downloads the full body without concatenating bytes from the interrupted attempt.");
        Check(interrupted.Disposed && !Directory.EnumerateFiles(library.Root, "*.tmp", SearchOption.AllDirectories).Any(),
            "Recovery closes the failed body and leaves no temporary files.");
        Check(await Download(http, library, "complete-body") == path && handler.Count == 2,
            "Reopening the downloaded subtitle uses the existing local copy without another HTTP request.");

        int truncatedCalls = 0;
        using var truncatedHandler = new Handler((_, _) =>
        {
            if (++truncatedCalls > 1) return Task.FromResult(Subtitle());
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Ass[..40])) };
            response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(Ass);
            return Task.FromResult(response);
        });
        using var truncatedHttp = new HttpClient(truncatedHandler);
        string? completePath = await Download(truncatedHttp, Library("short-body"), "short-body");
        Check(truncatedHandler.Count == 2 && completePath != null && File.ReadAllText(completePath) == Ass,
            "An early EOF with a larger declared Content-Length is retried instead of saving a valid-looking truncated subtitle.");

        using var invalidHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("This is not a subtitle.", Encoding.UTF8, "text/plain") }));
        using var invalidHttp = new HttpClient(invalidHandler);
        var invalidLibrary = Library("invalid-subtitle");
        await Fails(() => Download(invalidHttp, invalidLibrary, "invalid-subtitle"));
        Check(invalidHandler.Count == 1, "A fully downloaded but unsupported subtitle is not downloaded repeatedly.");
        EmptyLibrary(invalidLibrary, "An unsupported subtitle is not committed to the library.");
    }

    static async Task TimeoutChecks()
    {
        int headerCalls = 0;
        using var headerHandler = new Handler(async (_, token) =>
        {
            if (++headerCalls == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Subtitle();
        });
        using var headerHttp = new HttpClient(headerHandler) { Timeout = TimeSpan.FromMilliseconds(150) };
        var headerPath = await Download(headerHttp, Library("header-timeout"), "header-timeout");
        Check(headerHandler.Count == 2 && headerPath != null && File.ReadAllText(headerPath) == Ass,
            "A timed-out response header is retried with a fresh per-attempt deadline.");

        var stalledBody = new InterruptedStream(Encoding.UTF8.GetBytes(Ass[..30]), stall: true);
        int bodyCalls = 0;
        using var bodyHandler = new Handler((_, _) => Task.FromResult(++bodyCalls == 1 ? Body(stalledBody) : Subtitle()));
        using var bodyHttp = new HttpClient(bodyHandler) { Timeout = TimeSpan.FromMilliseconds(150) };
        string? bodyPath = await Download(bodyHttp, Library("body-timeout"), "body-timeout").WaitAsync(TimeSpan.FromSeconds(8));
        Check(bodyHandler.Count == 2 && stalledBody.Disposed && bodyPath != null && File.ReadAllText(bodyPath) == Ass,
            "The per-attempt timeout also covers a response body that stalls after headers and partial data.");
    }

    static async Task CancellationChecks()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            using var handler = new Handler((_, _) => throw new InvalidOperationException("Cancelled request must not be sent"));
            using var http = new HttpClient(handler);
            var library = Library("already-cancelled");
            Check(await Fails(() => Download(http, library, "already-cancelled", cancelled.Token)) is OperationCanceledException
                && handler.Count == 0, "A cancelled user operation performs no HTTP request.");
            EmptyLibrary(library, "Cancelling before the first request leaves no files.");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new Handler((_, _) => { first.TrySetResult(); throw ConnectionError(); });
            using var http = new HttpClient(handler);
            var library = Library("cancel-retry");
            var pending = Download(http, library, "cancel-retry", cancellation.Token);
            await first.Task;
            await Task.Delay(75);
            var time = Stopwatch.StartNew(); cancellation.Cancel();
            Check(await Fails(() => pending) is OperationCanceledException && handler.Count == 1 && time.Elapsed.TotalSeconds < 1,
                "Cancelling while waiting to retry stops promptly and prevents the next attempt.");
            EmptyLibrary(library, "Cancelling a pending retry leaves no partial subtitle or index.");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var body = new InterruptedStream(Encoding.UTF8.GetBytes(Ass[..30]), stall: true);
            using var handler = new Handler((_, _) => Task.FromResult(Body(body)));
            using var http = new HttpClient(handler);
            var library = Library("cancel-body");
            var pending = Download(http, library, "cancel-body", cancellation.Token);
            await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            Check(await Fails(() => pending) is OperationCanceledException && handler.Count == 1 && body.Disposed,
                "User cancellation during a partial body is not mistaken for a retryable timeout.");
            EmptyLibrary(library, "Cancelling a partial body does not publish its received prefix.");
        }
    }

    static async Task PostCloneAndRateChecks()
    {
        var store = new SubtitleStore(Path.Combine(Root, "post-account"));
        store.Save(new(new string('a', 32)));
        var requests = new List<HttpRequestMessage>();
        var bodies = new List<byte[]>();
        var times = new List<long>();
        using var handler = new Handler(async (request, token) =>
        {
            requests.Add(request); times.Add(Stopwatch.GetTimestamp());
            bodies.Add(await request.Content!.ReadAsByteArrayAsync(token));
            Check(request.Method == HttpMethod.Post && request.Headers.Authorization?.Scheme == "Bearer"
                && request.Headers.Authorization.Parameter == new string('a', 32)
                && request.Content.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded"
                && request.Headers.CacheControl is { NoCache: true, NoStore: true },
                "A cloned POST retains the original body type, cache policy and fixture authorization without logging their values.");
            return requests.Count == 1 ? new(HttpStatusCode.ServiceUnavailable) : Json("{\"status\":0,\"sub\":{\"subs\":[]}}");
        });
        using var http = new HttpClient(handler);
        var result = await new AssrtClient(http, store).SearchAsync(Query, 1, default);
        Check(!result.HasMore && handler.Count == 2 && !ReferenceEquals(requests[0], requests[1])
            && bodies[0].SequenceEqual(bodies[1]) && bodies[0].Length > 0,
            "A retried ASSRT POST uses an independent request and identical form bytes.");
        Check(Stopwatch.GetElapsedTime(times[0], times[1]).TotalSeconds >= 12.8,
            "ASSRT retries still respect its thirteen-second request spacing.");
    }

    static async Task Main()
    {
        Directory.CreateDirectory(Root);
        string? previousConfig = Environment.GetEnvironmentVariable("ANIMEVE_HOME");
        string config = Path.Combine(Root, "config");
        Directory.CreateDirectory(config);
        Environment.SetEnvironmentVariable("ANIMEVE_HOME", config);
        try
        {
            await JsonRecoveryChecks();
            await HttpStatusChecks();
            await TransportChecks();
            await CompleteBodyAndCacheChecks();
            await TimeoutChecks();
            await CancellationChecks();
            await PostCloneAndRateChecks();
            if (MpvNet.Diagnostics.Enabled)
            {
                var logs = Directory.GetFiles(Path.Combine(config, "logs"), "diagnostic-*.jsonl");
                var entries = logs.SelectMany(path =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                }).ToArray();
                var events = entries.Select(line => JsonDocument.Parse(line)).ToArray();
                try
                {
                    var names = events.Select(entry => entry.RootElement.GetProperty("event").GetString()).ToHashSet();
                    Check(names.Contains("attempt") && names.Contains("retry") && names.Contains("failed")
                        && names.Contains("cancelled") && names.Contains("complete"),
                        "The diagnostic build records attempts, retries, final failures, cancellations and successful recovery.");
                    Check(!entries.Any(line => line.Contains(new string('a', 32), StringComparison.Ordinal)
                        || line.Contains("Bearer a", StringComparison.Ordinal)
                        || line.Contains("Network+Fixture", StringComparison.Ordinal)),
                        "Request diagnostics omit the fixture authorization and POST form values.");
                }
                finally { foreach (var entry in events) entry.Dispose(); }
            }
            Console.WriteLine($"Subtitle network checks passed: {_checks}; only in-memory HTTP handlers and an isolated temporary library, no real network, desktop or video access.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANIMEVE_HOME", previousConfig);
            string target = Path.GetFullPath(Root);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("AnimeFusion-SubtitleNetwork-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected fixture path");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Count++;
            return respond(request, token);
        }
    }

    sealed class InterruptedStream(byte[] prefix, bool stall = false) : Stream
    {
        int _position;
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            if (_position < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer); _position += count;
                return count;
            }
            Waiting.TrySetResult();
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw EndedError();
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
