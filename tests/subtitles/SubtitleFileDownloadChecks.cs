using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;

static class SubtitleFileDownloadChecks
{
    static byte[] Ass(string title) => Encoding.UTF8.GetBytes("[Script Info]\nTitle: " + title
        + "\nScriptType: v4.00+\n[Events]\nFormat: Layer, Start, End, Style, Text\n"
        + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,fixture\n");

    static byte[] Zip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var (name, content) in files)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        return bytes.ToArray();
    }

    static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    static TraktMedia Media(string title) => TraktMedia.Parse(title, "", new Dictionary<string, string>());

    public static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        async Task Fails(Func<Task> action)
        {
            try { await action(); }
            catch (SubtitleServiceException) { checks++; return; }
            throw new Exception("A missing or failed download must fail without switching download modes");
        }

        var query = new SubtitleQuery("Episode Fixture", 1, 2, "zh-Hans");
        string[] names = ["[字幕组] Episode Fixture S01E01.sc.ass", "[字幕组] Episode Fixture S01E02.sc.ass",
            "[字幕组] Episode Fixture S01E03.sc.ass", "[字幕组] Episode Fixture OVA01.sc.ass"];
        var contents = names.ToDictionary(name => name, name => Ass(name), StringComparer.Ordinal);
        SubtitleFile[] files = names.Select((name, index) => new SubtitleFile(SubtitleSource.Assrt, "42", 701 + index,
            name, $"https://download.example/file/{index + 1}.ass", "1 KB")).ToArray();
        var result = new SubtitleResult(SubtitleSource.Assrt, "42", "Episode Fixture S01", "简", "", files)
        {
            Season = 1, LanguageCode = "zh-Hans", Format = "ASS", DownloadName = "全部字幕.zip",
            DownloadUrl = "https://download.example/all.zip", PackageNames = names
        };
        var detailStore = new SubtitleStore(Path.Combine(root, "selected-file-details"));
        detailStore.Save(new(new string('a', 32)));
        string[] detailUrls = files.Select(file => $"https://download.example/onthefly/42/-/{file.FileId}/{Uri.EscapeDataString(file.Name)}").ToArray();
        SubtitleResult fileOnlyDetail = null!;
        var detailHandler = new Handler(request =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/sub/detail",
                "File listings use the official detail request rather than fetching episode downloads");
            bool filesOnly = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult() == "id=43";
            var entries = files.Select((file, index) => new { f = file.Name, s = "1 KB",
                url = filesOnly ? detailUrls[index].Replace("/42/", "/43/", StringComparison.Ordinal) : detailUrls[index] }).ToArray();
            if (filesOnly) entries = entries.Skip(1).Take(1).ToArray();
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = 0,
                sub = new
                {
                    subs = new[] { new { native_name = "Episode Fixture S01", subtype = "ASS",
                        filename = filesOnly ? "" : "全部字幕.zip", url = filesOnly ? "" : result.DownloadUrl, filelist = entries } }
                }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        using (var detailHttp = new HttpClient(detailHandler))
        {
            var client = new AssrtClient(detailHttp, detailStore);
            var detail = await client.DetailAsync(result with { Files = [], DownloadName = "", DownloadUrl = "" }, default);
            Check(detail.Files.Count == files.Length && detail.Files.Select(file => file.Name).SequenceEqual(names)
                && detail.Files.Select(file => file.FileId).SequenceEqual([701, 702, 703, 704])
                && detail.Files.Select(file => file.Url).SequenceEqual(detailUrls)
                && detail.Files.All(file => file.Size.Length > 0),
                "ASSRT detail retains original names and sizes and reads actual file IDs from onthefly URLs");
            fileOnlyDetail = await client.DetailAsync(result with { Id = "43", Files = [], DownloadName = "", DownloadUrl = "" }, default);
            Check(fileOnlyDetail.Files.Count == 1 && fileOnlyDetail.DownloadName.Length == 0 && fileOnlyDetail.DownloadUrl.Length == 0
                && detailHandler.Paths.Count == 2,
                "A detail response with individual files and no package remains a usable one-request file listing");
        }
        var handler = new Handler(request =>
        {
            Check(request.Headers.Authorization == null && !request.Headers.Contains("Api-Key"),
                "File and package hosts never receive the ASSRT credential");
            if (request.RequestUri!.AbsolutePath == "/all.zip") return Bytes(Zip(contents));
            int number = int.Parse(Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath));
            return Bytes(contents[names[number - 1]]);
        });
        using var http = new HttpClient(handler);
        var library = new SubtitleLibrary(Path.Combine(root, "selected-files"));
        var download = new SubtitleDownload(http, library);

        string? selected = await download.DownloadAsync(query, result, files[1], files, null, default);
        Check(selected != null && Path.GetFileName(selected) == names[1]
            && File.ReadAllBytes(selected).SequenceEqual(contents[names[1]]),
            "Selecting a file preserves its original filename and bytes");
        Check(handler.Paths.SequenceEqual(["/file/2.ass"]) && library.Package(query, result) is { Complete: false, Files.Count: 1 },
            "A selected episode makes exactly one request and does not mark its package complete");
        Check(library.Results(query.Title).Single() is { Complete: false, Files.Count: 1 }
            && library.Tracks(Media("Episode Fixture S01E02")).Single().Path == selected
            && library.Tracks(Media("Episode Fixture S01E01")).Count == 0,
            "Partial local results expose only the downloaded current episode and retain their incomplete status");
        Check(!File.Exists(library.ArchivePath(query, result)), "A single file download does not fetch or create the original archive");
        Check(await download.DownloadAsync(query, result, files[1], files, null, default) == selected && handler.Paths.Count == 1,
            "Repeated single file selection reuses local bytes without requesting another episode");

        Check(await download.DownloadAsync(query, result, files[0], files, null, default) == null
            && handler.Paths.SequenceEqual(["/file/2.ass", "/file/1.ass"]),
            "Selecting another episode saves that file but cannot load it into the current episode");
        Check(await download.DownloadAsync(query, result, files[3], files, null, default) == null
            && handler.Paths.Last() == "/file/4.ass"
            && library.Tracks(Media("Episode Fixture S01E01")).Count == 1
            && library.Tracks(Media("Episode Fixture S00E01")).Count == 1,
            "A manually selected OVA stays separate from the ordinary season even when their episode numbers coincide");
        Check(library.Results(query.Title).Single() is { Complete: false, Files.Count: 3 },
            "Downloading several individual files does not claim that the original package was downloaded");

        var reopened = new SubtitleLibrary(library.Root);
        var local = reopened.Results(query.Title).Single();
        var localCurrent = local.Files.Single(file => Path.GetFileName(file.Name) == names[1]);
        var localDownload = new SubtitleDownload(http, reopened);
        Check(await localDownload.DownloadAsync(query, local, localCurrent, local.Files, null, default) == selected && handler.Paths.Count == 3,
            "Reopening and selecting a saved local file loads it without a detail or download request");

        string? complete = await download.DownloadAsync(query, result, null, files, null, default);
        Check(complete == selected && handler.Paths.SequenceEqual(["/file/2.ass", "/file/1.ass", "/file/4.ass", "/all.zip"]),
            "Download all after partial downloads requests the complete archive once and returns only the current episode");
        var package = library.Package(query, result)!;
        Check(package.Complete && package.Files.Count == names.Length
            && package.Files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names)
            && package.Files.All(file => File.ReadAllBytes(file.Path).SequenceEqual(contents[file.Name])),
            "The complete archive fills missing episodes without losing, duplicating or altering previously downloaded files");
        Check(new SubtitleLibrary(library.Root).Results(query.Title).Single() is { Complete: true, Files.Count: 4 }
            && File.Exists(library.ArchivePath(query, result)),
            "Complete status and the original archive survive reopening the library");
        Check(await download.DownloadAsync(query, result, files[1], files, null, default) == selected
            && library.Results(query.Title).Single().Complete && handler.Paths.Count == 4,
            "Selecting a single file from a complete package never downgrades or downloads that package again");
        Check(await download.DownloadAsync(query, result, null, files, null, default) == selected
            && await download.DownloadAsync(query with { Episode = 9 }, result, null, files, null, default) == null
            && handler.Paths.Count == 4,
            "Complete package reuse makes no request and never substitutes another episode for a missing current episode");

        var noPackage = fileOnlyDetail;
        var noPackageLibrary = new SubtitleLibrary(Path.Combine(root, "files-without-package"));
        var noPackageHandler = new Handler(_ => Bytes(contents[names[1]]));
        using var noPackageHttp = new HttpClient(noPackageHandler);
        var noPackageDownload = new SubtitleDownload(noPackageHttp, noPackageLibrary);
        Check(await noPackageDownload.DownloadAsync(query, noPackage, noPackage.Files[0], noPackage.Files, null, default) != null
            && noPackageHandler.Paths.Count == 1,
            "A source offering only individual file URLs still supports single file downloads");
        await Fails(() => noPackageDownload.DownloadAsync(query, noPackage, null, noPackage.Files, null, default));
        Check(noPackageHandler.Paths.Count == 1 && !noPackageLibrary.Package(query, noPackage)!.Complete,
            "Download all without a package fails locally rather than downloading the file list in a loop");

        foreach (bool all in new[] { false, true })
        {
            var failedLibrary = new SubtitleLibrary(Path.Combine(root, "failed-mode-" + all));
            var failedHandler = new Handler(_ => new(HttpStatusCode.NotFound));
            using var failedHttp = new HttpClient(failedHandler);
            await Fails(() => new SubtitleDownload(failedHttp, failedLibrary)
                .DownloadAsync(query, result, all ? null : files[1], files, null, default));
            Check(failedHandler.Paths.SequenceEqual([all ? "/all.zip" : "/file/2.ass"])
                && failedLibrary.Results(query.Title).Count == 0,
                "A failed " + (all ? "package" : "file") + " request never falls back to the other download mode or saves a result");
        }

        var collectionQuery = new SubtitleQuery("Example", 2, 1, "zh-Hans");
        string[] collectionNames = ["Collection/Example/[Group][Example][01].sc.ass",
            "Collection/Example2/[Group][Example 2][01].sc.ass", "Collection/Other/[Group][Unidentified title][01].sc.ass"];
        SubtitleFile[] collectionFiles = collectionNames.Select((name, index) => new SubtitleFile(SubtitleSource.Assrt,
            "mixed-files", 901 + index, name, $"https://download.example/mixed/{index + 1}.ass")).ToArray();
        var collection = new SubtitleResult(SubtitleSource.Assrt, "mixed-files", "Whole collection", "简", "", collectionFiles)
        {
            Collection = true, LanguageCode = "zh-Hans", PackageNames = collectionNames,
            DownloadName = "mixed.zip", DownloadUrl = "https://download.example/mixed.zip"
        };
        var collectionHandler = new Handler(_ => Bytes(Ass("Fixture")));
        using (var collectionHttp = new HttpClient(collectionHandler))
        {
            var collectionLibrary = new SubtitleLibrary(Path.Combine(root, "collection-selected-file"));
            var collectionDownload = new SubtitleDownload(collectionHttp, collectionLibrary);
            Check(await collectionDownload.DownloadAsync(collectionQuery, collection, collectionFiles[2], collectionFiles, null, default) == null,
                "A selected file from an unidentified work in a collection cannot borrow the current work and season merely because its episode number matches");
            var unidentified = collectionLibrary.Results(collectionQuery.Title).Single();
            Check(unidentified.Files.Single().Season == -1 && unidentified.Files.Single().Episode == null
                && collectionLibrary.Tracks(Media("Example S02E01")).Count == 0 && collectionHandler.Paths.Count == 1,
                "The unidentified collection file remains in the unclassified shelf and is absent from current playback tracks");
            Check(await collectionDownload.DownloadAsync(collectionQuery, unidentified, unidentified.Files[0], unidentified.Files, null, default) == null
                && await collectionDownload.DownloadAsync(collectionQuery, collection, collectionFiles[2], collectionFiles, null, default) == null
                && collectionHandler.Paths.Count == 1,
                "Neither local file selection nor online file reuse reclassifies an already saved unidentified collection file");
            Check(await collectionDownload.DownloadAsync(collectionQuery, collection, collectionFiles[0], collectionFiles, null, default) == null
                && collectionLibrary.Tracks(Media("Example S01E01")).Count == 1
                && collectionLibrary.Tracks(Media("Example S02E01")).Count == 0,
                "Selecting a known first-season file saves it to that season rather than loading it into the second season");
            Check(await collectionDownload.DownloadAsync(collectionQuery, collection, collectionFiles[1], collectionFiles, null, default) is { } current
                && current.Contains("第 2 季", StringComparison.Ordinal) && collectionHandler.Paths.Count == 3,
                "The corresponding second-season file remains downloadable after other collection files were saved");
        }

        string pairedName = "[字幕组] Episode Fixture S01E02";
        SubtitleFile[] paired = [new(SubtitleSource.Assrt, "44", 801, pairedName + ".idx", "https://download.example/paired.idx"),
            new(SubtitleSource.Assrt, "44", 802, pairedName + ".sub", "https://download.example/paired.sub"),
            new(SubtitleSource.Assrt, "44", 803, "Episode Fixture S01E03.ass", "https://download.example/other.ass")];
        byte[] indexBytes = Encoding.UTF8.GetBytes("# VobSub index file, v7\nid: zh, index: 0\ntimestamp: 00:00:00:000, filepos: 000000000\n");
        byte[] subBytes = [0, 0, 1, 0xba, 0x44, 0, 4, 0, 4, 1, 0x89, 0xc3, 0xf8];
        var pairedHandler = new Handler(request => Bytes(request.RequestUri!.AbsolutePath == "/paired.idx" ? indexBytes : subBytes));
        using var pairedHttp = new HttpClient(pairedHandler);
        var pairedLibrary = new SubtitleLibrary(Path.Combine(root, "paired-files"));
        var pairedDownload = new SubtitleDownload(pairedHttp, pairedLibrary);
        var pairedResult = result with { Id = "44", Files = paired };
        string? pairedPath = await pairedDownload.DownloadAsync(query, pairedResult, paired[1], paired, null, default);
        Check(pairedPath != null && pairedPath.EndsWith(".idx", StringComparison.OrdinalIgnoreCase)
            && pairedHandler.Paths.ToHashSet().SetEquals(["/paired.idx", "/paired.sub"])
            && pairedHandler.Paths.Count == 2,
            "A selected VobSub file downloads only its required IDX/SUB pair and loads the index");
        Check(File.ReadAllBytes(pairedPath!).SequenceEqual(indexBytes)
            && File.ReadAllBytes(Path.ChangeExtension(pairedPath, ".sub")).SequenceEqual(subBytes)
            && pairedLibrary.Results(query.Title).Single() is { Complete: false, Files.Count: 2 },
            "The paired subtitle retains both original files and remains a partial package");
        Check(await pairedDownload.DownloadAsync(query, pairedResult, paired[0], paired, null, default) == pairedPath
            && pairedHandler.Paths.Count == 2, "Repeated IDX selection reuses both local files without another request");
        var failedPairLibrary = new SubtitleLibrary(Path.Combine(root, "failed-pair"));
        var failedPairHandler = new Handler(request => request.RequestUri!.AbsolutePath == "/paired.sub"
            ? Bytes(subBytes) : new(HttpStatusCode.NotFound));
        using (var failedPairHttp = new HttpClient(failedPairHandler))
        {
            await Fails(() => new SubtitleDownload(failedPairHttp, failedPairLibrary)
                .DownloadAsync(query, pairedResult, paired[0], paired, null, default));
            Check(failedPairHandler.Paths.Contains("/paired.idx") && failedPairHandler.Paths.Count <= 2
                && failedPairHandler.Paths.All(path => path is "/paired.sub" or "/paired.idx")
                && failedPairLibrary.Results(query.Title).Count == 0
                && (!Directory.Exists(failedPairLibrary.Root) || Directory.GetFiles(failedPairLibrary.Root, "*.idx", SearchOption.AllDirectories).Length == 0),
                "A failed companion download leaves no unusable IDX or partial library result and never fetches another episode");
        }
        foreach (var unmatched in new[]
        {
            new[] { paired[0] },
            new[] { paired[0] with { Name = "第 1 季/" + paired[0].Name },
                paired[1] with { Name = "第 2 季/" + paired[1].Name } }
        })
        {
            var missingPairLibrary = new SubtitleLibrary(Path.Combine(root, "missing-pair-" + Guid.NewGuid().ToString("N")));
            var missingPairHandler = new Handler(_ => throw new Exception("An unpaired IDX must fail before making a request"));
            using var missingPairHttp = new HttpClient(missingPairHandler);
            await Fails(() => new SubtitleDownload(missingPairHttp, missingPairLibrary)
                .DownloadAsync(query, pairedResult, unmatched[0], unmatched, null, default));
            Check(missingPairHandler.Paths.Count == 0 && missingPairLibrary.Results(query.Title).Count == 0,
                "An IDX without its same-directory companion fails without pairing a subtitle from another season");
        }
        const string extractedAss = "[Script Info]\nScriptType: v4.00+\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,字幕测试\n";
        const string extractedXz = "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4AC4AJNdAC2UyMtHifLr5yTJxebCHTfb/GkkOgEs1COSDHvfUSrshW6QnNfR6ZWxm8Q+CW/q7qsh638zJ7Yu5LkcyowAXGERIoRjG1+NV1UFfBEQXPEo3kVfrZ/873YWf5/f9pDXRC+fluA5kNMgn/5hGxyoKb7jskCmK43XrzkqP4OXo55utka3NnFOfX4nefbuuNqCK99UAAAACl4SWxIvwe0AAa8BuQEAAHE1x3exxGf7AgAAAAAEWVo=";
        foreach (var source in new[] { SubtitleSource.Amenzb, SubtitleSource.Tsukihime })
        {
            bool compressed = source == SubtitleSource.Tsukihime;
            var individual = new SubtitleResult(source, "extracted-file", "Fixture S04E08", "简", "", [])
            {
                Individual = true, Season = 4, Episode = 8, LanguageCode = "zh-Hans", Format = "ASS",
                DownloadName = "Fixture.S04E08.zh-Hans.ass" + (compressed ? ".xz" : ""),
                DownloadUrl = "https://download.example/extracted" + (compressed ? ".xz" : ".ass")
            };
            var extractedHandler = new Handler(_ => Bytes(compressed ? Convert.FromBase64String(extractedXz) : Encoding.UTF8.GetBytes(extractedAss)));
            using var extractedHttp = new HttpClient(extractedHandler);
            var extractedLibrary = new SubtitleLibrary(Path.Combine(root, "extracted-file-" + source));
            var extractedDownload = new SubtitleDownload(extractedHttp, extractedLibrary);
            var extractedQuery = new SubtitleQuery("Fixture", 4, 8, "zh-Hans");
            var file = new SubtitleFile(source, individual.Id, 0, "Fixture.S04E08.zh-Hans.ass", individual.DownloadUrl);
            string? extractedPath = await extractedDownload.DownloadAsync(extractedQuery, individual, file, [file], null, default);
            Check(extractedPath != null && File.ReadAllBytes(extractedPath).SequenceEqual(Encoding.UTF8.GetBytes(extractedAss))
                && extractedHandler.Paths.Count == 1,
                source + " keeps its individual extraction path when selected from the file page, including XZ decoding");
            Check(await extractedDownload.DownloadAsync(extractedQuery, individual, null, [file], null, default) == extractedPath
                && extractedLibrary.Results(extractedQuery.Title).Single().Files.Count == 1 && extractedHandler.Paths.Count == 1,
                source + " download-all for an individual release reuses that one extracted subtitle without enumerating a season");
            Check(await extractedDownload.DownloadAsync(extractedQuery with { Episode = 9 }, individual, file, [file], null, default) == null
                && extractedHandler.Paths.Count == 1, source + " individual file reuse still protects the current episode");
        }
        var confirmedQuery = new SubtitleQuery("Example", 3, 1, "zh-Hans");
        string[] confirmedNames = ["Collection/Example/[Group][Example][01].sc.ass",
            "Collection/Example2/[Group][Example 2][01].sc.ass", "Collection/Example3/Different official title [01].sc.ass"];
        var confirmedResult = new SubtitleResult(SubtitleSource.Assrt, "confirmed-local-season", "Whole collection", "简", "", [])
        {
            Collection = true, LanguageCode = "zh-Hans", PackageNames = confirmedNames,
            DownloadName = "confirmed.zip", DownloadUrl = "https://download.example/confirmed.zip"
        };
        var confirmedHandler = new Handler(_ => Bytes(Zip(confirmedNames.ToDictionary(name => name, _ => Ass("Fixture")))));
        using (var confirmedHttp = new HttpClient(confirmedHandler))
        {
            var confirmedLibrary = new SubtitleLibrary(Path.Combine(root, "confirmed-local-season"));
            var confirmedDownload = new SubtitleDownload(confirmedHttp, confirmedLibrary);
            string? confirmedPath = await confirmedDownload.DownloadAsync(confirmedQuery, confirmedResult, null, [], null, default);
            var confirmedLocal = new SubtitleLibrary(confirmedLibrary.Root).Results(confirmedQuery.Title).Single();
            var confirmedFile = confirmedLocal.Files.Single(file => file.Name.EndsWith("Different official title [01].sc.ass", StringComparison.Ordinal));
            Check(confirmedPath == confirmedFile.LocalPath && confirmedFile.Season == 3 && confirmedFile.Episode == 1
                && confirmedPath!.Contains("第 3 季", StringComparison.Ordinal) && confirmedHandler.Paths.Count == 1,
                "A complete collection confirms the different official title's episode-one file as season three from its archive directory");
            Check(await confirmedDownload.DownloadAsync(confirmedQuery, confirmedLocal, confirmedFile, [confirmedFile], null, default) == confirmedPath
                && confirmedHandler.Paths.Count == 1,
                "Selecting only that saved local file preserves its confirmed season-three identity without the other collection files");
            foreach (int otherSeason in new[] { 1, 2 })
                Check(await confirmedDownload.DownloadAsync(confirmedQuery with { Season = otherSeason }, confirmedLocal,
                    confirmedFile, [confirmedFile], null, default) == null && confirmedHandler.Paths.Count == 1,
                    "A confirmed season-three file cannot load as the same numbered episode of season " + otherSeason);
        }
        return checks;
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }
}
