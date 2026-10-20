using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;

static class SubtitlePreferenceChecks
{
    public static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            checks++;
        }
        static TraktMedia Media(string title) => TraktMedia.Parse(title, "", new Dictionary<string, string>());
        static SubtitleQuery Query(TraktMedia media) => new SubtitleQuery(media.Title, media.Season, media.Episode, "").WithPlayback(media);

        string[] regularNames = (from episode in new[] { 1, 2 }
            from edition in new[] { "signs", "full" }
            from language in new[] { "en", "ja" }
            select $"[Group] Preference Fixture S01E{episode:00}.{edition}.{language}.ass").ToArray();
        using var handler = new ArchiveHandler(regularNames);
        using var http = new HttpClient(handler);
        var library = new SubtitleLibrary(Path.Combine(root, "download-edition-preference"));
        var media = Media("Preference Fixture S01E01");
        var query = Query(media);
        var result = new SubtitleResult(SubtitleSource.Assrt, "download-editions", media.Title, "英语 / 日语", "", [])
        {
            Season = 1, LanguageCode = "en,ja", PackageNames = regularNames,
            DownloadName = "editions.zip", DownloadUrl = "https://download.example/editions.zip"
        };
        await new SubtitleDownload(http, library).DownloadAsync(query, result, null, [], null, default);
        var other = result with { Id = "other-package" };
        await new SubtitleDownload(http, library).DownloadAsync(query, other, null, [], null, default);
        Check(handler.Count == 2, "Both ordinary packages are saved from their own mocked archive request");
        foreach (string edition in new[] { "full", "signs" })
        {
            string directory = library.PackageDirectory(query, result) + Path.DirectorySeparatorChar;
            var chosen = library.Tracks(media).Single(track => track.Path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
                && track.Title.EndsWith($".{edition}.en.ass", StringComparison.Ordinal));
            library.Remember(media.Title, 1, chosen.Path);
            var reopened = new SubtitleLibrary(library.Root);
            foreach (int episode in new[] { 1, 2 })
            {
                var playing = Media($"Preference Fixture S01E{episode:00}");
                var preferred = reopened.Tracks(playing).Where(track => track.Preferred).ToArray();
                Check(preferred.Length == 1 && preferred[0].Path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
                    && preferred[0].Title.EndsWith($"S01E{episode:00}.{edition}.en.ass", StringComparison.Ordinal)
                    && preferred[0].Language == "en",
                    "Reopening preserves exactly the chosen package, language and edition for the matching episode");
                var local = reopened.Results(media.Title).Single(item => item.Id == result.Id);
                Check(reopened.CurrentFile(Query(playing), local, local.Files)?.LocalPath == preferred[0].Path,
                    "Cached package selection uses the same complete edition identity as the restored track list");
            }
            var missing = Media("Preference Fixture S01E03");
            var saved = reopened.Results(media.Title).Single(item => item.Id == result.Id);
            Check(reopened.Tracks(missing).Count == 0 && reopened.CurrentFile(Query(missing), saved, saved.Files) == null,
                "Remembering a version cannot substitute an existing episode for a missing later episode");
            Check(reopened.Tracks(Media("Preference Fixture S02E01")).Count == 0,
                "An ordinary preference never moves a saved package into another season");
        }
        Check(handler.Count == 2, "Reopening and preferring saved editions makes no new archive request");

        string[] extraNames = (from type in new[] { "OVA", "SP" }
            from episode in new[] { 1, 2 }
            from edition in new[] { "signs", "full" }
            select $"[Group] Preference Extras {type} {episode:00}.{edition}.en.ass").ToArray();
        using var extraHandler = new ArchiveHandler(extraNames);
        using var extraHttp = new HttpClient(extraHandler);
        var extras = new SubtitleLibrary(Path.Combine(root, "download-extra-preference"));
        var first = Media("Preference Extras S00E02 - OVA 1");
        var extraResult = result with
        {
            Id = "extra-editions", Title = "Preference Extras OVA", Season = 0, LanguageCode = "en",
            PackageNames = extraNames, DownloadName = "extras.zip", DownloadUrl = "https://download.example/extras.zip"
        };
        await new SubtitleDownload(extraHttp, extras).DownloadAsync(Query(first), extraResult, null, [], null, default);
        var full = extras.Tracks(first).Single(track => track.Title.EndsWith("OVA 01.full.en.ass", StringComparison.Ordinal));
        extras.Remember(first.Title, 0, full.Path);
        var restored = new SubtitleLibrary(extras.Root);
        foreach (var playing in new[] { first, Media("Preference Extras S00E03 - OVA 2") })
        {
            var tracks = restored.Tracks(playing);
            var preferred = tracks.Where(track => track.Preferred).ToArray();
            string episode = playing.Episode == 2 ? "01" : "02";
            Check(tracks.Count == 2 && preferred.Length == 1
                && preferred[0].Title.EndsWith($"OVA {episode}.full.en.ass", StringComparison.Ordinal),
                "A special preference follows the matching OVA number without loading same-numbered SP files");
            var local = restored.Results(first.Title).Single();
            Check(restored.CurrentFile(Query(playing), local, local.Files)?.LocalPath == preferred[0].Path,
                "A special package reuses the remembered edition with separate playback and content coordinates");
        }
        var absent = Media("Preference Extras S00E04 - OVA 3");
        var savedExtras = restored.Results(first.Title).Single();
        Check(restored.Tracks(absent).Count == 0 && restored.CurrentFile(Query(absent), savedExtras, savedExtras.Files) == null
            && restored.Tracks(Media("Preference Extras S01E01")).Count == 0 && extraHandler.Count == 1,
            "A saved special preference cannot reuse the previous OVA or enter a regular episode");
        return checks;
    }

    sealed class ArchiveHandler(string[] names) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri?.Host != "download.example") throw new Exception("Unexpected subtitle endpoint");
            Count++;
            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                foreach (string name in names)
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write(Encoding.UTF8.GetBytes("[Script Info]\nTitle: preference fixture\n[Events]\n"));
                }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) });
        }
    }
}
