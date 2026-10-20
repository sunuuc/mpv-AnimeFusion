using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using MpvNet.Windows.Subtitles;

static class Program
{
    static int _checks;
    static readonly string Root = Path.Combine(Path.GetTempPath(), "AnimeFusion-Subtitles-" + Guid.NewGuid().ToString("N"));
    const string Results = """
        {"status":0,"sub":{"subs":[{"id":1,"native_name":"YuruYuri 第二季","lang":{"desc":"简"}}]}}
        """;
    static readonly SubtitleQuery Query = new("YuruYuri", 2, 1, "zh");
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _checks++;
    }
    static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.MovedPermanently)
        => new(status) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };
    static SubtitleStore Store(string name) => CreateStore(name);
    static SubtitleStore CreateStore(string name)
    { var store=new SubtitleStore(Path.Combine(Root,name));store.Save(new(new string('a',32)));return store; }
    static T? VisualChild<T>(System.Windows.DependencyObject parent) where T:System.Windows.DependencyObject
    {
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);
            if(child is T match)return match;
            if(VisualChild<T>(child) is T descendant)return descendant;
        }
        return null;
    }
    static void Advance(System.Windows.Controls.ListBox list, int milliseconds)
    {
        var frame=new System.Windows.Threading.DispatcherFrame();
        var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(8)};
        var deadline=DateTime.UtcNow.AddMilliseconds(milliseconds);
        timer.Tick+=(_,_)=>{list.UpdateLayout();if(DateTime.UtcNow>=deadline){timer.Stop();frame.Continue=false;}};
        timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);list.UpdateLayout();
    }
    static void Wheel(System.Windows.Controls.ScrollViewer scroll,int delta)
    { scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,delta){RoutedEvent=System.Windows.Input.Mouse.MouseWheelEvent}); }
    static async Task Fails(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (SubtitleServiceException error) { Check(error.Message.Contains(expected), "Expected concise error: " + expected); return; }
        throw new Exception("Expected request failure");
    }
    static byte[] Zip(string[] names)
    {
        using var bytes=new MemoryStream();
        using(var archive=new System.IO.Compression.ZipArchive(bytes,System.IO.Compression.ZipArchiveMode.Create,true))
            foreach(string name in names)
            { using var stream=archive.CreateEntry(name).Open();stream.Write(Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nfixture\n")); }
        return bytes.ToArray();
    }
    static void LibraryChecks()
    {
        static MpvNet.Windows.Trakt.TraktMedia Media(string title) => MpvNet.Windows.Trakt.TraktMedia.Parse(title, "", new Dictionary<string, string>());
        string directory = Path.Combine(Root, "import-source"); Directory.CreateDirectory(directory);
        string[] names = ["[Group] Library Fixture S01E01.sc.ass", "[Group] Library Fixture S01E02.sc.ass", "[Group] Library Fixture S02E01.sc.ass",
            "[Group] Library Fixture S01E01.tc.ass", "[Other] Library Fixture S01E03.sc.ass", "[Group] Other Fixture S01E02.sc.ass"];
        foreach (string name in names) File.WriteAllText(Path.Combine(directory, name), "原始字幕\n" + name);
        var library = new SubtitleLibrary(Path.Combine(Root, "import-library"));
        var first = Media("Library Fixture S01E01");
        string original = Path.Combine(directory, names[0]);
        string imported = library.Import(first, original);
        Check(imported != original && File.ReadAllBytes(imported).SequenceEqual(File.ReadAllBytes(original)), "Local import preserves original bytes in a managed copy");
        Check(Path.GetFileName(imported) == names[0] && File.Exists(original), "Local import preserves the original filename and source file");
        Check(library.Tracks(first).Count == 2 && library.Results(first.Title).Single().Files.Count == 4, "Local import includes matching sibling episodes and variants without another work or release group");
        Check(library.Tracks(Media("Library Fixture S01E03")).Count == 0, "A different release group does not enter the imported set");
        library.SetPinned(first.Title, imported, true);
        library.Remember(first.Title, 1, library.Tracks(first).Single(track => track.Path.EndsWith(".tc.ass")).Path);
        Check(library.Tracks(first).First().Pinned && library.Tracks(first).First().Path == imported, "Pinned version sorts before the ordinary remembered version");
        Check(new SubtitleLibrary(library.Root).Tracks(Media("Library Fixture S01E02")).Single().Pinned, "Pinned set survives reopening and follows the next matching episode");
        Check(library.Tracks(Media("Library Fixture S02E01")).Single().Pinned, "A pinned set follows an explicitly numbered second season");
        Check(library.Tracks(Media("Library Fixture S02E02")).Count == 0, "Pinned set cannot substitute another season or episode when the requested episode is absent");
        Check(library.Import(first, imported) == imported && library.Results(first.Title).Single().Files.Count == 4, "Reimporting a current managed subtitle does not duplicate it");
        try { library.Import(first, Path.Combine(directory, names[1])); throw new Exception("Mismatched episode should fail"); }
        catch (SubtitleServiceException) { Check(library.Tracks(first).Count == 2, "An explicitly mismatched import is rejected before changing the current episode library"); }
        library.SetPinned(first.Title, imported, false);
        Check(library.Tracks(first).All(track => !track.Pinned), "Unpin restores ordinary version selection");
        library.SetPinned(first.Title, imported, true);
        string[] importedSet = library.Results(first.Title).Single().Files.Select(file => file.LocalPath).ToArray();
        Check(library.Remove(first.Title, imported, false).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(importedSet)
            && importedSet.All(File.Exists) && names.Take(4).All(name => File.Exists(Path.Combine(directory, name))),
            "Default deletion removes the whole cross-season package records while retaining every managed and original file");
        Check(library.Results(first.Title).Count == 0 && library.Tracks(first).Count == 0
            && library.Tracks(Media("Library Fixture S01E02")).Count == 0 && library.Tracks(Media("Library Fixture S02E01")).Count == 0,
            "Deleting a set removes its other episodes, languages and pin rather than leaving a partial package");
        imported = library.Import(first, original);
        var deletedSet = library.Remove(first.Title, imported, true);
        string[] originalSet = names.Take(4).Select(name => Path.Combine(directory, name)).ToArray();
        Check(importedSet.All(file => !File.Exists(file)) && originalSet.All(file => !File.Exists(file))
            && names.Skip(4).All(name => File.Exists(Path.Combine(directory, name))),
            "Confirmed set deletion removes every managed and original episode without touching another release or work");
        Check(deletedSet.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(importedSet.Concat(originalSet)),
            "Deletion returns every managed and original subtitle path in the set for native track removal");

        var movie = Media("Standalone Movie (2013)");
        var storedResult = new SubtitleResult(SubtitleSource.Assrt, "fixture", "电影合集", "", "", []);
        string stored = library.FilePath(new(movie.Title, 0, null, ""), storedResult, "第一章.ass");
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!); File.WriteAllText(stored, "第一章");
        string archive = library.ArchivePath(new(movie.Title, 0, null, ""), storedResult with { DownloadName = "原始字幕包.zip" });
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!); File.WriteAllBytes(archive, Zip(["第一章.ass", "第二章.ass"]));
        string unrelatedArchive = library.ArchivePath(new(movie.Title, 0, null, ""), storedResult with { Id = "other", DownloadName = "其他字幕包.zip" });
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedArchive)!); File.WriteAllBytes(unrelatedArchive, Zip(["其他字幕.ass"]));
        library.Save(new(movie.Title, 0, null, ""), storedResult, new(SubtitleSource.Assrt, "fixture", 0, "第一章.ass", ""), stored, true);
        string otherMovie = Path.Combine(Path.GetDirectoryName(stored)!, "第二章.ass"); File.WriteAllText(otherMovie, "第二章");
        library.Save(new(movie.Title, 0, null, ""), storedResult, new(SubtitleSource.Assrt, "fixture", 0, "第二章.ass", ""), otherMovie, true);
        Check(library.Tracks(movie).Count == 0, "An unclassified shelf does not appear as a movie before explicit association");
        string movieCopy = library.Import(movie, stored);
        Check(movieCopy != stored && File.Exists(movieCopy) && File.Exists(stored), "An existing unclassified shelf subtitle is copied into the explicitly selected movie shelf");
        Check(library.Tracks(movie).Single().Path == movieCopy, "Associating a stored movie subtitle never attaches another movie from the same package");
        var movieResult = library.Results(movie.Title).Single();
        Check(library.CurrentFile(new(movie.Title, null, null, ""), movieResult, movieResult.Files)?.LocalPath == movieCopy,
            "A movie associated from a special shelf can also load from the local search result");
        library.SetPinned(movie.Title, movieCopy, true);
        Check(library.Tracks(movie).Single().Pinned, "A movie subtitle can be pinned independently of an existing unclassified shelf");
        var shared = Media("Another Movie");
        library.Save(new(shared.Title, null, null, ""), storedResult, new(SubtitleSource.Assrt, "fixture", 0, "第一章.ass", ""), movieCopy, true);
        try { library.Remove(movie.Title, movieCopy, true); throw new Exception("Shared deletion should fail"); }
        catch (SubtitleServiceException) { Check(File.Exists(movieCopy) && library.Tracks(movie).Count == 1, "Physical deletion refuses a file still referenced by another work without dropping its record"); }
        library.Remove(shared.Title, movieCopy, false); var deletedMovie = library.Remove(movie.Title, movieCopy, true);
        Check(!File.Exists(movieCopy) && !File.Exists(stored) && !File.Exists(archive) && File.Exists(unrelatedArchive)
            && !File.Exists(otherMovie) && library.Tracks(movie).Count == 0 && library.Results(movie.Title).Count == 0,
            "Deleting the same source package removes its movie and unclassified shelves and original archive while preserving a different package");
        Check(deletedMovie.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([movieCopy, stored, otherMovie]),
            "Subtitle track removal includes the whole associated package and original but excludes the archive");

        string pairs = Path.Combine(Root, "pair-source"); Directory.CreateDirectory(pairs);
        string idx = Path.Combine(pairs, "Pair Fixture.idx"), sub = Path.ChangeExtension(idx, ".sub");
        File.WriteAllText(idx, "fixture"); File.WriteAllText(sub, "fixture");
        var pairMovie = Media("Pair Fixture"); string managedIdx = library.Import(pairMovie, sub);
        Check(library.Tracks(pairMovie).Single().Path == managedIdx, "IDX and SUB import as one selectable subtitle");
        Check(library.Remove(pairMovie.Title, managedIdx, true).Count == 4 && !File.Exists(managedIdx) && !File.Exists(Path.ChangeExtension(managedIdx, ".sub"))
            && !File.Exists(idx) && !File.Exists(sub), "Removing an IDX subtitle deletes both managed and original IDX/SUB companions");

        string changedOriginal = Path.Combine(directory, "Changed Source.srt"); File.WriteAllText(changedOriginal, "original subtitle");
        var changedMovie = Media("Changed Source"); string changedCopy = library.Import(changedMovie, changedOriginal);
        File.WriteAllText(changedOriginal, "edited subtitle");
        try { library.Remove(changedMovie.Title, changedCopy, true); throw new Exception("A changed original must not be deleted"); }
        catch (SubtitleServiceException error)
        {
            Check(error.Message.Contains("已更改") && File.Exists(changedOriginal) && File.Exists(changedCopy)
                && library.Tracks(changedMovie).Count == 1, "An original changed after import fails deletion before touching files or records");
        }

        string readonlyOriginal = Path.Combine(directory, "Readonly Source.srt"); File.WriteAllText(readonlyOriginal, "original subtitle");
        var readonlyMovie = Media("Readonly Source"); string readonlyCopy = library.Import(readonlyMovie, readonlyOriginal);
        File.SetAttributes(readonlyOriginal, File.GetAttributes(readonlyOriginal) | FileAttributes.ReadOnly);
        try
        {
            try { library.Remove(readonlyMovie.Title, readonlyCopy, true); throw new Exception("A readonly original must prevent partial deletion"); }
            catch (SubtitleServiceException error)
            {
                Check(error.Message.Contains("只读") && File.Exists(readonlyOriginal) && File.Exists(readonlyCopy)
                    && new SubtitleLibrary(library.Root).Tracks(readonlyMovie).Single().Path == readonlyCopy,
                    "A readonly original fails before deleting the managed copy or saved record, so deletion remains retryable");
            }
        }
        finally { File.SetAttributes(readonlyOriginal, File.GetAttributes(readonlyOriginal) & ~FileAttributes.ReadOnly); }

        string sharedOriginal = Path.Combine(directory, "Shared Source.srt"); File.WriteAllText(sharedOriginal, "shared subtitle");
        var sharedMovie = Media("Shared Source"); string sharedCopy = library.Import(sharedMovie, sharedOriginal);
        var sharedResult = library.Results(sharedMovie.Title).Single();
        string sharedSeasonCopy = library.FilePath(new(sharedMovie.Title, 0, null, ""), sharedResult, "Shared Source.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedSeasonCopy)!); File.Copy(sharedCopy, sharedSeasonCopy);
        library.Save(new(sharedMovie.Title, 0, null, ""), sharedResult, new(SubtitleSource.Assrt, sharedResult.Id, 0, "Shared Source.srt", ""),
            sharedSeasonCopy, true, imported: true, originalPath: sharedOriginal,
            originalHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sharedOriginal))));
        var anotherSharedMovie = Media("Other Shared Movie"); string anotherSharedCopy = library.Import(anotherSharedMovie, sharedOriginal);
        try { library.Remove(sharedMovie.Title, sharedCopy, true); throw new Exception("An original used by another work must not be deleted"); }
        catch (SubtitleServiceException)
        {
            Check(File.Exists(sharedOriginal) && File.Exists(sharedCopy) && File.Exists(sharedSeasonCopy) && File.Exists(anotherSharedCopy),
                "An external original used by another work protects the entire cross-shelf set before any file is deleted");
        }
        library.Remove(anotherSharedMovie.Title, anotherSharedCopy, false);
        new SubtitleLibrary(library.Root).Remove(sharedMovie.Title, sharedCopy, true);
        Check(!File.Exists(sharedOriginal) && !File.Exists(sharedCopy) && !File.Exists(sharedSeasonCopy) && File.Exists(anotherSharedCopy),
            "Once other-work records are removed, the whole source set deletes while its unreferenced copy outside the set remains");

        string invalidOriginal = Path.Combine(directory, "Invalid Original.srt"); File.WriteAllText(invalidOriginal, "subtitle");
        var invalidMovie = Media("Invalid Original"); string invalidCopy = library.Import(invalidMovie, invalidOriginal);
        string anotherDirectory = Path.Combine(Root, "unrelated-source"); Directory.CreateDirectory(anotherDirectory);
        string unrelatedSource = Path.Combine(anotherDirectory, Path.GetFileName(invalidOriginal)); File.Copy(invalidOriginal, unrelatedSource);
        string invalidIndex = Path.Combine(library.SeasonDirectory(invalidMovie.Title, null), "subtitles.json");
        var invalidShelf = System.Text.Json.JsonSerializer.Deserialize<SubtitleShelf>(File.ReadAllBytes(invalidIndex))!;
        invalidShelf = invalidShelf with { Packages = invalidShelf.Packages.Select(package => package with
            { Files = package.Files.Select(file => file with { OriginalPath = unrelatedSource }).ToArray() }).ToArray() };
        File.WriteAllBytes(invalidIndex, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(invalidShelf));
        try { library.Remove(invalidMovie.Title, invalidCopy, true); throw new Exception("An unrelated source path must not be deleted"); }
        catch (SubtitleServiceException)
        {
            Check(File.Exists(invalidCopy) && File.Exists(invalidOriginal) && File.Exists(unrelatedSource),
                "Matching filenames and bytes cannot authorize deletion outside the recorded import family directory");
        }

        string variants = Path.Combine(Root, "variant-source"); Directory.CreateDirectory(variants);
        string[] versions = ["en", "ja", "sc", "tc", "full.en", "signs.en"];
        foreach (int episode in new[] { 1, 2 })
            foreach (string version in versions)
                File.WriteAllText(Path.Combine(variants, $"[Group] Variant Fixture S01E{episode:00}.{version}.srt"), "fixture");
        var variantMedia = Media("Variant Fixture S01E01");
        library.Import(variantMedia, Path.Combine(variants, "[Group] Variant Fixture S01E01.en.srt"));
        Check(library.Tracks(variantMedia).Count == versions.Length, "Parallel languages and named editions all enter the imported set");
        Check(library.Tracks(variantMedia).Single(track => track.Title.EndsWith("S01E01.en.srt")).Language == "en"
            && library.Tracks(variantMedia).Single(track => track.Title.EndsWith("S01E01.ja.srt")).Language == "ja",
            "Imported track language follows its filename rather than a shared package language");
        foreach (string version in versions)
        {
            var chosen = library.Tracks(variantMedia).Single(track => track.Title.EndsWith("S01E01." + version + ".srt"));
            library.SetPinned(variantMedia.Title, chosen.Path, true);
            Check(library.Tracks(variantMedia).Single(track => track.Pinned).Path == chosen.Path,
                "Only the chosen imported language and edition is pinned: " + version);
            var nextVariant = new SubtitleLibrary(library.Root).Tracks(Media("Variant Fixture S01E02"));
            Check(nextVariant.Single(track => track.Pinned).Title.EndsWith("S01E02." + version + ".srt"),
                "Reopening and advancing keeps the chosen imported language and edition: " + version);
            library.Remember(variantMedia.Title, 1, chosen.Path);
            var local = library.Results(variantMedia.Title).Single();
            Check(library.CurrentFile(new(variantMedia.Title, 1, 2, ""), local, local.Files)!.Name.EndsWith("S01E02." + version + ".srt"),
                "Explicit local search reuse respects the imported language and edition: " + version);
        }
        var onlineVariants = new SubtitleResult(SubtitleSource.Assrt, "versions", "字幕包", "多语言", "", []) { LanguageCode = "en,ja,zh" };
        var onlineMedia = Media("Downloaded Fixture S01E01");
        foreach (int episode in new[] { 1, 2 })
            foreach (string version in versions)
            {
                var query = new SubtitleQuery(onlineMedia.Title, 1, episode, "");
                string filename = $"[Group] Downloaded Fixture S01E{episode:00}.{version}.srt";
                string filePath = library.FilePath(query, onlineVariants, filename);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!); File.WriteAllText(filePath, "fixture");
                library.Save(query, onlineVariants, new(SubtitleSource.Assrt, onlineVariants.Id, 0, filename, ""), filePath, true);
            }
        foreach (string version in versions)
        {
            string chosen = library.Tracks(onlineMedia).Single(track => track.Title.EndsWith("S01E01." + version + ".srt")).Path;
            library.SetPinned(onlineMedia.Title, chosen, true);
            Check(new SubtitleLibrary(library.Root).Tracks(Media("Downloaded Fixture S01E02")).Single(track => track.Pinned).Title.EndsWith("S01E02." + version + ".srt"),
                "Downloaded sets also pin exactly one language and edition: " + version);
        }
        string titledDirectory = Path.Combine(Root, "episode-title-source"); Directory.CreateDirectory(titledDirectory);
        string[] titledNames = ["[Group] Titled Fixture - 01 - The Beginning.en.srt", "[Group] Titled Fixture - 02 - The Next Step.en.srt"];
        foreach (string name in titledNames) File.WriteAllText(Path.Combine(titledDirectory, name), "fixture");
        var titledMedia = Media("Titled Fixture S01E01");
        string titledPath = library.Import(titledMedia, Path.Combine(titledDirectory, titledNames[0]));
        library.SetPinned(titledMedia.Title, titledPath, true);
        Check(new SubtitleLibrary(library.Root).Tracks(Media("Titled Fixture S01E02")).Single().Pinned,
            "Episode-specific titles do not change the pinned set identity");
        foreach (string title in new[] { "It", "No", "No Subtitle", "It Follows" })
        {
            string filePath = Path.Combine(titledDirectory, title + ".srt"); File.WriteAllText(filePath, "fixture");
            var media = Media(title); library.Import(media, filePath);
            Check(library.Tracks(media).Single().Language.Length == 0, "Ordinary title words cannot invent an imported language: " + title);
        }
    }
    static async Task MovieDownloadChecks()
    {
        var library = new SubtitleLibrary(Path.Combine(Root, "movie-download-library"));
        var query = new SubtitleQuery("Example Movie", null, null, "zh") { Year = 2011 };
        var names = new[] { "Example.Movie.2011.chs.ass", "Example.Movie.2011.cht.ass", "Example.Movie.2011.en.ass",
            "Another.Movie.2011.chs.ass", "Example.Movie.2012.chs.ass", "Example.Movie.2.2011.chs.ass", "Example Movie S02E01.chs.ass" };
        var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(names)) });
        using var http = new HttpClient(handler);
        var result = new SubtitleResult(SubtitleSource.Assrt, "movie-collection", "电影多版本合集", "简繁英", "", [])
        {
            Collection = true, LanguageCode = "zh,en", DownloadName = "movie-collection.zip",
            DownloadUrl = "https://download.example/movie-collection.zip"
        };
        var download = new SubtitleDownload(http, library);
        var aliasQuery = query with { Names = ["Official Film Alias"] };
        Check(SubtitleLibrary.FileQuery(aliasQuery, result, "[Group][Official Film Alias][2011].chs.ass").Season == null,
            "A movie collection accepts an exact known alias and parsed release year");
        Check(SubtitleLibrary.FileQuery(aliasQuery, result, "[Group][Official Film Alias][2012].chs.ass").Season == -1,
            "A parsed different release year remains unclassified even with an exact alias");
        string? selected = await download.DownloadAsync(query, result, null, [], null, default);
        Check(selected != null && selected.EndsWith(names[0]), "A movie collection selects the matching film instead of saving it only as a season");
        var media = MpvNet.Windows.Trakt.TraktMedia.Parse("Example Movie (2011)", "", new Dictionary<string, string>());
        var tracks = library.Tracks(media);
        Check(tracks.Count == 3 && tracks.All(track => !track.Path.EndsWith(names[3]) && !track.Path.EndsWith(names[4])),
            "Movie variants restore to the current film without another title, sequel or release year");
        library.Remember(media.Title, null, selected!);
        string? english = await download.DownloadAsync(query with { Language = "en" }, result, null, [], null, default);
        Check(english?.EndsWith(names[2]) == true && handler.Count == 1,
            "An explicit language filter selects that saved movie language before a different remembered language");
        library.Remember(media.Title, null, english!);
        Check(await download.DownloadAsync(query with { Language = "" }, result, null, [], null, default) == english,
            "All languages retains the last selected movie variant without another request");
        Check(library.Package(query, result)!.Files.Count == names.Length,
            "A movie collection retains every subtitle while loading only the matching movie");
        var local = library.Results(query.Title).Single();
        Check(library.CurrentFile(query with { Language = "en" }, local, local.Files)?.LocalPath == english,
            "Selecting the completed local movie result preserves its matching language");
        Check(library.CurrentFile(query with { Language = "ja" }, local, local.Files) == null,
            "A missing requested language never substitutes a different recorded language");

        var singleHandler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\nmovie\n") });
        using var singleHttp = new HttpClient(singleHandler);
        var single = result with { Id = "movie-single", DownloadName = names[0], DownloadUrl = "https://download.example/movie.ass" };
        var singleDownload = new SubtitleDownload(singleHttp, library);
        string? singlePath = await singleDownload.DownloadAsync(query, single, null, [], null, default);
        Check(singlePath != null && library.Tracks(media).Any(track => track.Path == singlePath),
            "A single-file movie download also enters its movie shelf when the service marks it a collection");
        Check(await singleDownload.DownloadAsync(query, single, null, [], null, default) == singlePath && singleHandler.Count == 1,
            "A saved single-file movie reuses local bytes without another download");

        using var cancelled = new CancellationTokenSource();
        using var cancelHttp = new HttpClient(new Handler((_, _) =>
        {
            cancelled.Cancel();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(names)) };
        }));
        var interrupted = result with { Id = "cancelled-movie" };
        try
        {
            await new SubtitleDownload(cancelHttp, library).DownloadAsync(query, interrupted, null, [], null, cancelled.Token);
            throw new Exception("A cancelled movie download must not return a subtitle to load");
        }
        catch (OperationCanceledException)
        {
            Check(library.Package(query, interrupted) == null,
                "Cancellation before subtitle transfer completes cannot publish a partial movie package");
        }
    }
    static async Task SpecialArchiveChecks()
    {
        var query = new SubtitleQuery("魔力充电娘", 1, 2, "");
        var result = new SubtitleResult(SubtitleSource.Assrt, "regular-and-ova", "充电娘", "中文", "", [])
        {
            Season = 1, DownloadName = "充电娘.zip", DownloadUrl = "https://download.example/regular-and-ova.zip"
        };
        string[] names = Enumerable.Range(1, 6).Select(episode => $"充电娘/OVA/充电娘 OVA {episode:00}.ass")
            .Concat(Enumerable.Range(1, 12).Select(episode => $"充电娘/充电娘 {episode:00}.ass")).ToArray();
        var library = new SubtitleLibrary(Store("regular-and-ova").Root);
        var handler = new Handler((request, _) =>
        {
            Check(request.RequestUri!.AbsolutePath == "/regular-and-ova.zip", "Regular and OVA subtitles come from one complete package request");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(names)) };
        });
        using var http = new HttpClient(handler);
        var download = new SubtitleDownload(http, library);
        string? selected = await download.DownloadAsync(query, result, null, [], null, default);
        Check(selected != null && selected.EndsWith(Path.Combine("充电娘", "充电娘 02.ass"))
            && selected.StartsWith(library.SeasonDirectory(query.Title, 1) + Path.DirectorySeparatorChar),
            "A package whose OVA entries precede the regular episodes selects regular episode two despite the shorter archive work name");
        var saved = library.Results(query.Title).Single();
        Check(saved.Files.Count == 18 && saved.Files.Count(file => file.Season == 1) == 12
            && saved.Files.Count(file => file.Season == 0) == 6,
            "The complete package saves twelve regular episodes in season one and six OVA episodes in season zero");
        Check(saved.Files.Where(file => file.Season == 1).Select(file => file.Episode).Order().SequenceEqual(Enumerable.Range(1, 12).Select(episode => (double?)episode))
            && saved.Files.Where(file => file.Season == 0).Select(file => file.Episode).Order().SequenceEqual(Enumerable.Range(1, 6).Select(episode => (double?)episode)),
            "Separate regular and OVA shelves retain their own episode numbering");
        Check(saved.Files.Where(file => file.Season == 0).All(file => file.LocalPath.StartsWith(library.SeasonDirectory(query.Title, 0) + Path.DirectorySeparatorChar))
            && library.Tracks(MpvNet.Windows.Trakt.TraktMedia.Parse("魔力充电娘 S01E02", "", new Dictionary<string, string>())).Single().Path == selected,
            "Playback restores only the regular episode while the OVA subtitles remain stored separately");
        Check(library.CurrentFile(query, saved, saved.Files)?.LocalPath == selected,
            "Reopening the complete local result still selects regular episode two");
        Check(await download.DownloadAsync(query, result, null, [], null, default) == selected && handler.Count == 1,
            "The corrected regular episode reuses the whole local package without another request");
        Check(await download.DownloadAsync(query with { Season = 0 }, result, null, [], null, default) is { } special
            && special.EndsWith(Path.Combine("充电娘", "OVA", "充电娘 OVA 02.ass"))
            && special.StartsWith(library.SeasonDirectory(query.Title, 0) + Path.DirectorySeparatorChar) && handler.Count == 1,
            "An explicit season-zero request selects the corresponding OVA from the same saved package");
        Check(await download.DownloadAsync(query with { Season = 2 }, result, null, [], null, default) == null && handler.Count == 1,
            "The saved regular and OVA package cannot supply the same episode number in another season");
        Check(Directory.GetFiles(library.Root, "*.zip", SearchOption.AllDirectories).Length == 1
            && File.Exists(library.ArchivePath(query, result)), "Regular and OVA shelves share one retained original archive");

        string[] specialNames = ["Archive Fixture S00E02.ass", "S00/Archive Fixture 02.ass", "Archive Fixture OVA 02.ass",
            "OVA/Archive Fixture 02.ass", "Archive Fixture OAD 02.ass", "Archive Fixture SP02.ass", "SP/Archive Fixture 02.ass",
            "Archive Fixture Special 02.ass", "Special/Archive Fixture 02.ass", "特典/Archive Fixture 02.ass",
            "充电娘OVA02.ass", "充电娘OAD02.ass", "充电娘SP02.ass"];
        var fixtureQuery = new SubtitleQuery("Archive Fixture", 1, 2, "");
        foreach (bool collection in new[] { false, true })
        {
            var fixtureResult = result with { Id = "explicit-special", Title = "Archive Fixture", Collection = collection, PackageNames = specialNames };
            foreach (string name in specialNames)
            {
                Check(SubtitleLibrary.FileQuery(fixtureQuery, fixtureResult, name).Season == 0,
                    "A special file or parent folder identifies season zero independently of the collection flag: " + name + "; collection=" + collection);
                Check(library.CurrentFile(fixtureQuery, fixtureResult, [new(SubtitleSource.Assrt, fixtureResult.Id, 0, name, "")]) == null,
                    "A marked special never substitutes the same-numbered regular episode: " + name + "; collection=" + collection);
            }
            const string secondSeason = "Archive Fixture S02E02.ass";
            Check(SubtitleLibrary.FileQuery(fixtureQuery, fixtureResult, secondSeason).Season == 2
                && library.CurrentFile(fixtureQuery, fixtureResult, [new(SubtitleSource.Assrt, fixtureResult.Id, 0, secondSeason, "")]) == null,
                "An explicitly different regular season is also rejected independently of the collection flag");
            foreach (string folder in new[] { "OVA", "S00" })
            {
                string conflict = folder + "/Archive Fixture S01E02.ass";
                var file = new SubtitleFile(SubtitleSource.Assrt, fixtureResult.Id, 0, conflict, "");
                Check(SubtitleLibrary.FileQuery(fixtureQuery, fixtureResult, conflict).Season == -1
                    && library.CurrentFile(fixtureQuery, fixtureResult, [file]) == null
                    && library.CurrentFile(fixtureQuery with { Season = 0 }, fixtureResult, [file]) == null,
                    "A dedicated special folder conflicting with an explicit regular season remains unclassified: " + conflict + "; collection=" + collection);
            }
            const string sharedRoot = "TV+OVA/Archive Fixture S01E02.ass";
            Check(fixtureQuery.MatchesFile(sharedRoot)
                && SubtitleLibrary.FileQuery(fixtureQuery, fixtureResult, sharedRoot).Season == 1
                && library.CurrentFile(fixtureQuery, fixtureResult, [new(SubtitleSource.Assrt, fixtureResult.Id, 0, sharedRoot, "")])?.Name == sharedRoot,
                "A shared TV-and-OVA archive root does not override an explicitly regular leaf filename");
            string[] sharedNames = ["TV+OVA/Archive Fixture/Archive Fixture 02.ass", "TV+OVA/Archive Fixture/OVA/Archive Fixture OVA 02.ass"];
            var sharedResult = fixtureResult with { PackageNames = sharedNames };
            var sharedFiles = sharedNames.Select(name => new SubtitleFile(SubtitleSource.Assrt, sharedResult.Id, 0, name, "")).ToArray();
            Check(SubtitleLibrary.FileQuery(fixtureQuery, sharedResult, sharedNames[0]).Season == 1
                && SubtitleLibrary.FileQuery(fixtureQuery, sharedResult, sharedNames[1]).Season == 0,
                "A mixed wrapper retains an unmarked regular episode in season one and its dedicated OVA sibling in season zero; collection=" + collection);
            Check(library.CurrentFile(fixtureQuery, sharedResult, sharedFiles)?.Name == sharedNames[0]
                && library.CurrentFile(fixtureQuery with { Season = 0 }, sharedResult, sharedFiles)?.Name == sharedNames[1],
                "Same-numbered regular and OVA files under a mixed wrapper select independently without requiring S01 in the regular filename; collection=" + collection);
            const string onlySharedRegular = "TV+OVA/Archive Fixture 02.ass";
            var sharedRegularResult = fixtureResult with { PackageNames = [onlySharedRegular] };
            Check(SubtitleLibrary.FileQuery(fixtureQuery, sharedRegularResult, onlySharedRegular).Season == 1
                && library.CurrentFile(fixtureQuery, sharedRegularResult, [new(SubtitleSource.Assrt, sharedRegularResult.Id, 0, onlySharedRegular, "")])?.Name == onlySharedRegular,
                "A mixed wrapper containing only an unmarked regular file cannot turn it into a special; collection=" + collection);
        }
        foreach (string title in new[] { "Special A", "SPY", "Yumeiro Patissiere SP Professional" })
        {
            var ordinaryQuery = new SubtitleQuery(title, 1, 2, "");
            string ordinaryFile = title + " S01E02.ass";
            var ordinaryResult = result with { Id = "ordinary-title", Title = title };
            Check(ordinaryQuery.MatchesEpisode(ordinaryFile) && ordinaryQuery.MatchesFile(ordinaryFile)
                && SubtitleLibrary.FileQuery(ordinaryQuery, ordinaryResult, ordinaryFile).Season == 1
                && library.CurrentFile(ordinaryQuery, ordinaryResult, [new(SubtitleSource.Assrt, ordinaryResult.Id, 0, ordinaryFile, "")])?.Name == ordinaryFile,
                "An ordinary work title is not mistaken for a special-episode marker: " + title);
        }
        var episodeResult = result with { Title = "Archive Fixture", PackageNames = ["Archive Fixture 02.ass", "OVA/Archive Fixture 13.ass"] };
        Check(episodeResult.EpisodesFor(1).SequenceEqual([2]) && episodeResult.EpisodesFor(0).SequenceEqual([13])
            && episodeResult.EpisodesFor(null).SequenceEqual([2, 13]),
            "A special-only episode number is offered in season zero and All, never in the regular-season episode filter");
        Check(SubtitleLibrary.FileQuery(query, result, "充电娘/充电娘 02.ass").Season == 1,
            "An unmarked regular file in a non-collection archive retains the known current season without requiring a title alias");
        Check(SubtitleLibrary.FileQuery(query with { Season = 0 }, result, "充电娘/充电娘 02.ass").Season == -1,
            "A season-zero request cannot relabel an unmarked regular episode from a regular-season package as a special");

        var onlyLibrary = new SubtitleLibrary(Store("only-specials").Root);
        var onlyHandler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(["OVA/Archive Fixture 02.ass"])) });
        using var onlyHttp = new HttpClient(onlyHandler);
        var onlyResult = result with { Id = "only-specials", Title = "Archive Fixture", DownloadName = "only-specials.zip" };
        var onlyDownload = new SubtitleDownload(onlyHttp, onlyLibrary);
        Check(await onlyDownload.DownloadAsync(fixtureQuery, onlyResult, null, [], null, default) == null,
            "Downloading a special-only archive while playing a regular episode saves it without loading the OVA");
        var onlySaved = onlyLibrary.Results(fixtureQuery.Title).Single();
        Check(onlySaved.Files.Single().Season == 0
            && onlyLibrary.CurrentFile(fixtureQuery, onlySaved, onlySaved.Files) == null
            && onlyLibrary.Tracks(MpvNet.Windows.Trakt.TraktMedia.Parse("Archive Fixture S01E02", "", new Dictionary<string, string>())).Count == 0,
            "Reopening a special-only local result cannot load it into the regular episode or subtitle menu");
        Check(await onlyDownload.DownloadAsync(fixtureQuery, onlyResult, null, [], null, default) == null && onlyHandler.Count == 1,
            "Repeated selection of the saved special-only archive keeps the regular episode empty without redownloading");
    }
    static async Task IndividualSpecialChecks()
    {
        static string Ass(string title) => "[Script Info]\nTitle: " + title
            + "\nScriptType: v4.00+\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,fixture\n";
        var query = new SubtitleQuery("Archive Fixture", 1, 2, "");
        foreach (var source in new[] { SubtitleSource.Amenzb, SubtitleSource.Tsukihime })
        {
            var result = new SubtitleResult(source, "individual-special", "Archive Fixture S01E02", "中文", "", [])
            {
                Individual = true, Season = 1, Episode = 2, DownloadName = "Archive Fixture S01E02.ass",
                DownloadUrl = "https://download.example/individual.ass"
            };
            foreach (var mismatch in new[]
            {
                result with { DownloadName = "Archive Fixture OVA02.ass" },
                result with { DownloadName = "Archive Fixture OVA02.ass.xz" },
                result with { Title = "Archive Fixture OVA02" }
            })
            {
                var library = new SubtitleLibrary(Path.Combine(Root, source + "-special-metadata-" + Guid.NewGuid().ToString("N")));
                var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(Ass("Archive Fixture S01E02")) });
                using var http = new HttpClient(handler);
                await Fails(() => new SubtitleDownload(http, library).DownloadAsync(query, mismatch, null, [], null, default), "季集信息不一致");
                Check(handler.Count == 0 && library.Results(query.Title).Count == 0 && Directory.GetFiles(library.Root, "*", SearchOption.AllDirectories).Length == 0,
                    source + " rejects a special marker contradicting the regular metadata before requesting or saving any subtitle: " + mismatch.Title + "; " + mismatch.DownloadName);
            }
            foreach (string title in new[] { "Archive Fixture OVA02", "Archive Fixture OAD02", "Archive Fixture SP02", "Archive Fixture S00E02" })
            {
                var library = new SubtitleLibrary(Path.Combine(Root, source + "-special-content-" + Guid.NewGuid().ToString("N")));
                var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(Ass(title)) });
                using var http = new HttpClient(handler);
                await Fails(() => new SubtitleDownload(http, library).DownloadAsync(query, result, null, [], null, default), "季集信息不一致");
                Check(handler.Count == 1 && library.Results(query.Title).Count == 0 && Directory.GetFiles(library.Root, "*", SearchOption.AllDirectories).Length == 0,
                    source + " rejects an ASS special title after one download without saving or exposing the mismatched regular episode: " + title);
            }

            var specialLibrary = new SubtitleLibrary(Path.Combine(Root, source + "-correct-special"));
            var specialHandler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(Ass("Archive Fixture S00E02")) });
            using var specialHttp = new HttpClient(specialHandler);
            var specialDownload = new SubtitleDownload(specialHttp, specialLibrary);
            var specialResult = result with { Season = 0, Title = "Archive Fixture S00E02", DownloadName = "Archive Fixture S00E02.ass" };
            var specialQuery = query with { Season = 0 };
            string? selected = await specialDownload.DownloadAsync(specialQuery, specialResult, null, [], null, default);
            Check(selected != null && selected.StartsWith(specialLibrary.SeasonDirectory(query.Title, 0) + Path.DirectorySeparatorChar)
                && specialHandler.Count == 1, source + " downloads a correctly identified season-zero episode into its special shelf");
            Check(await specialDownload.DownloadAsync(query, specialResult, null, [], null, default) == null && specialHandler.Count == 1
                && await specialDownload.DownloadAsync(specialQuery, specialResult, null, [], null, default) == selected,
                source + " reuses the correct special locally only for season zero and cannot load it into the same-numbered regular episode");
            var local = specialLibrary.Results(query.Title).Single();
            Check(specialLibrary.CurrentFile(query, local, local.Files) == null
                && specialLibrary.CurrentFile(specialQuery, local, local.Files)?.LocalPath == selected,
                source + " retains special-only selection when reopening its saved local result");

            var attachedLibrary = new SubtitleLibrary(Path.Combine(Root, source + "-attached-ova"));
            const string attachedTitle = "Archive Fixture Season 1 OVA 02";
            var attachedHandler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(Ass(attachedTitle)) });
            using var attachedHttp = new HttpClient(attachedHandler);
            var attachedDownload = new SubtitleDownload(attachedHttp, attachedLibrary);
            var attachedResult = specialResult with { Id = "attached-ova", Title = attachedTitle };
            string? attached = await attachedDownload.DownloadAsync(specialQuery, attachedResult, null, [], null, default);
            Check(attached != null && attachedHandler.Count == 1,
                source + " accepts a season-zero OVA whose release and ASS title identify the regular season it accompanies");
            Check(await attachedDownload.DownloadAsync(query, attachedResult, null, [], null, default) == null && attachedHandler.Count == 1,
                source + " never loads an accompanying Season 1 OVA title into the regular season-one episode");

            var reverseLibrary = new SubtitleLibrary(Path.Combine(Root, source + "-regular-content-in-special"));
            var reverseHandler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(Ass("Archive Fixture S01E02")) });
            using var reverseHttp = new HttpClient(reverseHandler);
            await Fails(() => new SubtitleDownload(reverseHttp, reverseLibrary).DownloadAsync(specialQuery, specialResult, null, [], null, default), "季集信息不一致");
            Check(reverseHandler.Count == 1 && reverseLibrary.Results(query.Title).Count == 0
                && Directory.GetFiles(reverseLibrary.Root, "*", SearchOption.AllDirectories).Length == 0,
                source + " rejects explicitly regular ASS content labelled as season zero without saving or loading it");

            var rangeLibrary = new SubtitleLibrary(Path.Combine(Root, source + "-range-title"));
            var rangeHandler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent(Ass("Archive Fixture S01E02")) });
            using var rangeHttp = new HttpClient(rangeHandler);
            var rangeResult = result with { Title = "Archive Fixture S01E01-E04", DownloadName = "track.ass" };
            string? rangePath = await new SubtitleDownload(rangeHttp, rangeLibrary).DownloadAsync(query, rangeResult, null, [], null, default);
            Check(rangePath != null && rangeHandler.Count == 1 && rangeLibrary.Results(query.Title).Single().Files.Single().Episode == 2,
                source + " accepts an independently identified episode two from a multi-episode release title with a generic attachment filename");
        }
    }
    static async Task ContentCoordinatesChecks()
    {
        static MpvNet.Windows.Trakt.TraktMedia Media(string title) => MpvNet.Windows.Trakt.TraktMedia.Parse(title, "", new Dictionary<string, string>());
        var media = Media("Content Fixture (2007) S00E02 - OVA 1 「Scenery」");
        var query = new SubtitleQuery(media.Title, media.Season, media.Episode, "").WithPlayback(media);
        Check(media.DisplayTitle == "Content Fixture (2007) S00E02 - OVA 1 「Scenery」" && media.Episode == 2
            && query.Episode == 2 && query.ContentType == "OVA" && query.ContentEpisode == 1,
            "Subtitle content coordinates retain OVA one separately from the player's second special and preserve the original display title");
        const string ovaOne = "[Group] Content Fixture OVA [01].ass";
        Check(query.MatchesContent(ovaOne, 0, 1), "An OVA-one title matches the content number rather than the global S00E02 playback position");
        foreach (string name in new[] { "[Group] Content Fixture SP [01].ass", "[Group] Content Fixture PV [01].ass",
            "[Group] Content Fixture NCED [01].ass", "[Group] Content Fixture OVA [02].ass" })
            Check(!query.MatchesContent(name, 0), "A distinct extra type or content number cannot substitute OVA one: " + name);
        Check(query.MatchesContent("Content Fixture S00E02.ass", 0, 2)
            && !query.MatchesContent("Content Fixture S00E01.ass", 0, 1),
            "Explicit S00E filenames keep the player's global special numbering even when its episode title has an independent OVA number");
        var regular = new SubtitleQuery(media.Title, 1, 2, "").WithPlayback(Media("Content Fixture S01E02"));
        foreach (string type in new[] { "OVA", "OAD", "SP", "PV", "NCED", "NCOP" })
            Check(!regular.MatchesContent($"[Group] Content Fixture {type} [02].ass"),
                "A regular episode rejects the same numbered extra by native content classification: " + type);
        Check(regular.MatchesContent("[Group] Content Fixture [02].ass", 1, 2), "A regular episode still accepts its unmarked ordinary subtitle");
        var sp = new SubtitleQuery(media.Title, 0, 2, "").WithPlayback(Media("Content Fixture S00E02 - SP 1"));
        Check(sp.ContentType == "SP" && sp.ContentEpisode == 1 && sp.MatchesContent("Content Fixture SP01.ass", 0, 1)
            && !sp.MatchesContent(ovaOne, 0, 1), "Special-one and OVA-one content coordinates stay distinct in both directions");
        var folderLibrary = new SubtitleLibrary(Path.Combine(Root, "folder-only-types"));
        var folderResult = new SubtitleResult(SubtitleSource.Assrt, "folder-types", "Content Fixture", "", "", [])
            { Collection = true, PackageNames = ["OVA/Content Fixture 01.ass", "SP/Content Fixture 01.ass"] };
        foreach (string name in folderResult.PackageNames)
        {
            var file = new SubtitleFile(SubtitleSource.Assrt, folderResult.Id, 0, name, "");
            string path = folderLibrary.FilePath(query, folderResult, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "[Script Info]\n[Events]\n");
            folderLibrary.Save(query, folderResult, file, path, true);
        }
        Check(folderLibrary.Tracks(Media("Content Fixture S00E01")).Count == 0
            && folderLibrary.Tracks(Media("Content Fixture S00E02 - OVA 1")).Single().Path.Contains("\\OVA\\", StringComparison.Ordinal),
            "Reopening preserves directory-only OVA and SP evidence, refusing ambiguous same-numbered extras until the content type is known");

        const string root = "[VCB-Studio] Ichigo Mashimaro [Ma10p_720p]/";
        const string suffix = "[Ma10p_720p][x265_flac].caso_sc.ass";
        string[] names = new[] { root + "SPs/[VCB-Studio] Ichigo Mashimaro [NCED01][Ma10p_720p][x265_flac].ass",
                root + "SPs/[VCB-Studio] Ichigo Mashimaro [PV01][Ma10p_720p][x265_flac].ass" }
            .Concat(Enumerable.Range(2, 5).Select(number => root + $"SPs/[VCB-Studio] Ichigo Mashimaro [PV{number:00}]" + suffix))
            .Append(root + "SPs/[VCB-Studio] Ichigo Mashimaro [SP01][Ma10p_720p][x265_flac].ass")
            .Concat(Enumerable.Range(1, 2).Select(number => root + $"[VCB-Studio] Ichigo Mashimaro encore [{number:00}]" + suffix))
            .Append(root + "[VCB-Studio] Ichigo Mashimaro OVA [01][Ma10p_720p][x265_flac_aac].caso_cv_sc.ass")
            .Concat(Enumerable.Range(1, 3).Select(number => root + $"[VCB-Studio] Ichigo Mashimaro OVA [{number:00}][Ma10p_720p][x265_flac_aac].caso_sc.ass"))
            .Concat(Enumerable.Range(1, 12).Select(number => root + $"[VCB-Studio] Ichigo Mashimaro [{number:00}]" + suffix)).ToArray();
        var archiveMedia = Media("草莓棉花糖 (2007) S00E02 - OVA 1 「景色。」");
        var work = new SubtitleWork(archiveMedia, ["草莓棉花糖", "Ichigo Mashimaro", "苺ましまろ"], null);
        var result = new SubtitleResult(SubtitleSource.Assrt, "typed-collection", "草莓棉花糖 / Ichigo Mashimaro [TV+OVAs Fin]", "中文", "", [])
        {
            Season = 1, PackageNames = names, DownloadName = "typed-collection.zip", DownloadUrl = "https://download.example/typed-collection.zip"
        };
        var mapping = work.ForDownload(new("草莓棉花糖", null, null, ""), result);
        Check(mapping.Current && mapping.Query.Season == 0 && mapping.Query.Episode == 2
            && mapping.Query.ContentType == "OVA" && mapping.Query.ContentEpisode == 1,
            "A mixed archive's season-one label cannot replace current special playback coordinates or clear its OVA-one content identity");
        var library = new SubtitleLibrary(Path.Combine(Root, "typed-collection"));
        var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(names)) });
        using var http = new HttpClient(handler);
        var download = new SubtitleDownload(http, library);
        string? selected = await download.DownloadAsync(mapping.Query, result, null, [], null, default);
        Check(selected != null && selected.Contains("Ichigo Mashimaro OVA [01]", StringComparison.Ordinal),
            "The reported archive selects OVA one rather than PV, SP, NCED, OVA two or the ordinary second episode");
        var local = library.Results(archiveMedia.Title).Single();
        Check(local.Files.Count == 26 && local.Files.Where(file => file.Name.Contains(" encore ", StringComparison.Ordinal)).All(file => file.Season == -1),
            "A multi-title archive retains all files while the distinct encore title remains unclassified rather than joining regular episodes");
        Check(local.Files.Count(file => file.Season == 0) == 12 && local.Files.Count(file => file.Season == 1) == 12,
            "OVA, SP, PV and NCED native types enter the special shelf while ordinary episodes retain season one");
        library.Remember(archiveMedia.Title, 0, selected!);
        var reopened = new SubtitleLibrary(library.Root);
        var reopenedLocal = reopened.Results(archiveMedia.Title).Single();
        Check(reopened.CurrentFile(mapping.Query, reopenedLocal, reopenedLocal.Files)?.LocalPath is { } restored
            && restored.Contains("Ichigo Mashimaro OVA [01]", StringComparison.Ordinal),
            "Reopening the saved local archive restores OVA-one matching independently of the player's S00E02 numbering");
        var tracks = reopened.Tracks(archiveMedia);
        Check(tracks.Count == 2 && tracks.All(track => track.Path.Contains("Ichigo Mashimaro OVA [01]", StringComparison.Ordinal))
            && tracks.Any(track => track.Preferred), "The restarted subtitle menu exposes only the two actual OVA-one variants and preserves the remembered selection");
        var regularTracks = reopened.Tracks(Media("草莓棉花糖 S01E02"));
        Check(regularTracks.Count == 1 && regularTracks[0].Path.Contains("Ichigo Mashimaro [02]", StringComparison.Ordinal)
            && !regularTracks[0].Path.Contains(" encore ", StringComparison.Ordinal),
            "Reopening ordinary episode two cannot include encore, OVA or another extra with the same numeric suffix");
        Check(await download.DownloadAsync(mapping.Query, result, null, [], null, default) is { } reused
            && reused.Contains("Ichigo Mashimaro OVA [01]", StringComparison.Ordinal) && handler.Count == 1,
            "The complete typed archive reuses one retained package without another download");

        foreach (var source in new[] { SubtitleSource.Amenzb, SubtitleSource.Tsukihime })
        {
            var individualLibrary = new SubtitleLibrary(Path.Combine(Root, source + "-content-coordinate"));
            var individualHandler = new Handler((_, _) => new(HttpStatusCode.OK)
            {
                Content = new StringContent("[Script Info]\nTitle: Content Fixture OVA 01\n[Events]\n")
            });
            using var individualHttp = new HttpClient(individualHandler);
            var individualDownload = new SubtitleDownload(individualHttp, individualLibrary);
            var individual = new SubtitleResult(source, "content-ova", "Content Fixture OVA 01", "中文", "", [])
            {
                Individual = true, Season = 0, Episode = 1, DownloadName = "Content Fixture OVA 01.ass",
                DownloadUrl = "https://download.example/content-ova.ass"
            };
            string? path = await individualDownload.DownloadAsync(query, individual, null, [], null, default);
            Check(path != null && individualHandler.Count == 1,
                source + " loads a single extracted OVA-one file for a current S00E02 whose episode title explicitly identifies OVA one");
            var individualLocal = new SubtitleLibrary(individualLibrary.Root).Results(media.Title).Single();
            Check(individualLocal.Files.Single().Episode == 1
                && individualLibrary.CurrentFile(query, individualLocal, individualLocal.Files)?.LocalPath == path
                && individualLibrary.Tracks(media).Single().Path == path,
                source + " retains actual OVA-one coordinates in storage and restores it through local result and subtitle-menu selection");
            var other = new SubtitleQuery(media.Title, 0, 3, "").WithPlayback(Media("Content Fixture S00E03 - OVA 2"));
            Check(await individualDownload.DownloadAsync(other, individual, null, [], null, default) == null
                && individualLibrary.Tracks(Media("Content Fixture S01E01")).Count == 0 && individualHandler.Count == 1,
                source + " reuses saved bytes without substituting OVA one for OVA two or regular episode one");
        }
    }
    static string RepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "release.json"))) return directory.FullName;
        throw new InvalidOperationException("Repository source is required.");
    }
    static async Task Main(string[] args)
    {
        try
        {
            _checks += await SubtitlePreferenceChecks.RunAsync(Root);
            _checks += SubtitleSetRemovalChecks.Run(Root);
            if (args.Contains("--library-set-only"))
            {
                LibraryChecks();
                Console.WriteLine($"Subtitle set and preference checks passed: {_checks}; isolated local files only.");
                return;
            }
            _checks += await SubtitleSearchCrashChecks.RunAsync(RepositoryRoot(), Root);
            if (args.Contains("--search-crash-only")) { Console.WriteLine($"Subtitle search crash checks passed: {_checks}; public fixture, no desktop interaction or network access."); return; }
            _checks += await SubtitleFileDownloadChecks.RunAsync(Root);
            if (args.Contains("--file-download-only")) { Console.WriteLine($"Subtitle file download checks passed: {_checks}; isolated mock downloads only."); return; }
            await ContentCoordinatesChecks();
            if (args.Contains("--content-coordinates-only")) { Console.WriteLine($"Subtitle content coordinate checks passed: {_checks}; isolated mock archives only."); return; }
            await SpecialArchiveChecks();
            await IndividualSpecialChecks();
            if (args.Contains("--special-archive-only")) { Console.WriteLine($"Special archive subtitle checks passed: {_checks}; isolated mock downloads only."); return; }
            await MovieDownloadChecks();
            if (args.Contains("--movie-download-only")) { Console.WriteLine($"Movie subtitle download checks passed: {_checks}; isolated mock downloads only."); return; }
            LibraryChecks();
            if (args.Contains("--library-only")) { Console.WriteLine($"Subtitle library checks passed: {_checks}; no desktop interaction or network access."); return; }
            var rootStore=Store("direct-root");
            Check(rootStore.Root==Path.Combine(Root,"direct-root")&&!Directory.Exists(Path.Combine(rootStore.Root,"Downloads")),"Subtitle storage uses Subtitles directly without a Downloads child");
            var directLibrary=new SubtitleLibrary(rootStore.Root);
            var directQuery=new SubtitleQuery("Root fixture",1,1,"");
            var directResult=new SubtitleResult(SubtitleSource.Assrt,"root","fixture","简","",[]);
            foreach (string format in new[] { "", "不明", "其他", "  不明  " })
                Check((directResult with { Format = format }).FormatLabel == "字幕", "Unspecified format badge uses 字幕");
            foreach (string format in new[] { "ASS", "SSA", "SRT" })
                Check((directResult with { Format = format }).FormatLabel == format, "Known format badge remains unchanged");
            var languageHandler = new Handler((_, _) => Json("""
                {"status":0,"sub":{"subs":[
                {"id":1,"native_name":"fixture","lang":{"langlist":{"langfra":true}}},
                {"id":2,"native_name":"fixture","lang":{"langlist":{"langkor":true}}},
                {"id":3,"native_name":"fixture","lang":{"langlist":{"langspa":true}}},
                {"id":4,"native_name":"fixture","lang":{"langlist":{"langdeu":true}}},
                {"id":5,"native_name":"fixture","lang":{"langlist":{"langara":true}}},
                {"id":6,"native_name":"fixture","lang":{"langlist":{"langhin":true}}},
                {"id":7,"native_name":"fixture","lang":{"langlist":{"langeng":true,"langjpn":true}}},
                {"id":8,"native_name":"fixture","lang":{"langlist":{"langfuture":true}}},
                {"id":9,"native_name":"fixture"},
                {"id":10,"native_name":"fixture","lang":{"langlist":{"langdou":true}}},
                {"id":11,"native_name":"fixture","lang":{"desc":"简繁","langlist":{"langchs":true,"langcht":true}}}
                ]}}
                """));
            using (var languageHttp = new HttpClient(languageHandler))
            {
                var page = await new AssrtClient(languageHttp, Store("dynamic-language")).SearchAsync(Query with { Language = "" }, 1, default);
                foreach (var (id, code) in new[] { ("1", "fr"), ("2", "ko"), ("3", "es"), ("4", "de"), ("5", "ar"), ("6", "hi") })
                {
                    var item = page.Results.Single(result => result.Id == id);
                    Check(item.MatchesFilter(null, code, null), "Returned language flag dynamically resolves and filters: " + code);
                    Check(item.Languages.Single().Name.Length > 0, "Returned language has a system language name: " + code);
                }
                Check(page.Results.Single(result => result.Id == "7").Languages.Count == 2, "Multilingual result appears in every returned language");
                var chinese = page.Results.Single(result => result.Id == "11");
                Check(chinese.Languages.Select(language => language.Code).ToHashSet().SetEquals(["zh-Hans", "zh-Hant"])
                    && chinese.MatchesFilter(null, "zh-Hans", null) && chinese.MatchesFilter(null, "zh-Hant", null),
                    "Explicit simplified and traditional tracks remain separately filterable");
                foreach (string id in new[] { "8", "9", "10" })
                    Check(page.Results.Single(result => result.Id == id).MatchesFilter(null, "other", null), "Unknown or unspecified language uses Other");
                Check(!page.Results.Single(result => result.Id == "10").MatchesFilter(null, "zh", null), "Bilingual flag alone does not invent Chinese");
                Check(languageHandler.Count == 1, "Language filters never issue an additional service request");
            }
            Check(directLibrary.FilePath(directQuery,directResult,"episode.srt").StartsWith(Path.Combine(rootStore.Root,"Root fixture","第 1 季")),"Work and season folders are direct children of the configured subtitle root");
            var templatePath=Path.Combine(RepositoryRoot(),
                "src/player/src/MpvNet.Windows/WPF/SubtitleSearchWindow.xaml");
            var template=System.Xml.Linq.XDocument.Load(templatePath);
            var runs=template.Descendants().Where(e=>e.Name.LocalName=="Run" && (string?)e.Attribute("Text") is string t && t.StartsWith("{Binding"));
            Check(runs.All(e=>e.Attribute("Text")!.Value.Contains("Mode=OneWay")),"Read-only subtitle display bindings are one-way");
            Check(template.Descendants().Any(e => (string?)e.Attribute("Content") == "搜索" && (string?)e.Attribute("Click") == "Search_Click"), "Explicit search button is present and connected");
            Check(!template.Descendants().Any(e => (string?)e.Attribute("Content") == "加载更多"), "Pagination button remains removed");
            Check(template.Descendants().Any(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "SeasonChips")), "Season filter is present");
            Check(template.Descendants().Any(e=>e.Attributes().Any(a=>a.Name.LocalName=="Name"&&a.Value=="ResultCount")),"Search result total remains visible below the list");
            Check(!template.Descendants().Any(e => (string?)e.Attribute("Click") == "Language_Click"), "No language button list is hardcoded in XAML");
            Check(template.Descendants().Any(e=>(string?)e.Attribute("Text")=="{Binding FormatLabel}")
                && !template.Descendants().Any(e=>(string?)e.Attribute("Text")=="字幕" || (string?)e.Attribute("Text")=="{Binding Summary}"),"The left badge shows the returned format without a duplicate source or language row");
            Check(template.Descendants().Single(e=>(string?)e.Attribute("Text")=="{Binding Details}").Attribute("TextWrapping")==null,
                "Language, date and size stay on one metadata line");
            Check(!template.ToString().Contains("提供字幕"), "Footer source hint removed");
            Check(template.Descendants().Count(e => e.Name.LocalName == "ListBox" && !e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "AliasesList")) == 2
                && template.Descendants().Any(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "FilesList"))
                && template.Descendants().Any(e => (string?)e.Attribute("Click") == "Back_Click"),
                "Search results lead to a separate file list with a return action");
            Check(template.Descendants().Count(e => (string?)e.Attribute("Content") == "下载全部"
                && (string?)e.Attribute("Click") == "DownloadAll_Click") == 1
                && !template.ToString().Contains("下载并加载"), "The file page has one concise download-all action");
            string windowCode=File.ReadAllText(templatePath+".cs");
            Check(template.ToString().Contains("PreviewMouseLeftButtonUp=\"Result_Click\"")
                && template.ToString().Contains("PreviewMouseLeftButtonUp=\"File_Click\"")
                && windowCode.Contains("await OpenFilesAsync(result)") && !windowCode.Contains("Result_Changed"),
                "Explicit result selection opens files and explicit file selection downloads; automatic result selection never downloads");
            Check(windowCode.Contains("if (_closed) return") && windowCode.IndexOf("        Close();",windowCode.IndexOf("async Task LoadAsync",StringComparison.Ordinal),StringComparison.Ordinal)
                <windowCode.IndexOf("await SubtitlePlayback.Current.DownloadAsync",StringComparison.Ordinal),"The search window closes before playback owns the download");
            Check(!windowCode.Contains("_download.DownloadAsync")&&!windowCode.Contains("NewRequest()"),"Window lifetime no longer owns or cancels the selected download");
            var sourceTemplate=System.Xml.Linq.XDocument.Load(Path.Combine(Path.GetDirectoryName(templatePath)!,"SubtitleSourcesWindow.xaml"));
            Check(sourceTemplate.Descendants().Count(e=>(string?)e.Attribute("Content")=="获取密钥")==1
                && sourceTemplate.Descendants().Count(e=>(string?)e.Attribute("Content")=="验证")==1, "ASSRT uses one concise get-key and verify control");
            Check(!sourceTemplate.Descendants().Any(e=>e.Name.LocalName=="Expander") && !sourceTemplate.ToString().Contains("登录")
                && !sourceTemplate.ToString().Contains("高级设置"), "Unsupported standalone account mode and expanded explanation removed");
            string playbackCode=File.ReadAllText(Path.Combine(Path.GetDirectoryName(templatePath)! ,"../Subtitles/SubtitlePlayback.cs"));
            Check(playbackCode.Contains(@"\fs8\bord0.6") && !playbackCode.Contains("字幕已加载")
                && playbackCode.Contains("if (text.Length == 0)"), "Progress uses compact OSD units and successful loading clears the notice immediately");
            Check(!windowCode.Contains("SeriesQuery"), "Search results are not hidden according to the current video's type");
            Check(windowCode.Contains("Status.Text = paused ?") && windowCode.Contains("if (hasResults || loading || paused) EmptyState.Visibility = Visibility.Collapsed")
                && !windowCode.Contains("Empty(\"正在搜索字幕"),"Search progress only appears in the footer, including when no results exist yet");
            Check(windowCode.Contains("failed.Fail(SubtitleSource.Assrt, error)")&&windowCode.Contains("ApplySearch(failed)"),"Query errors use the same non-overlapping result state as service errors");
            Check(windowCode.Contains("_searchGeneration == generation") && windowCode.Contains("_searchLifetime.Cancel(); _searchLifetime.Dispose();")
                && windowCode.Contains("ResetSearch();"),"Refresh cancels the old request, replaces results and ignores stale replies");
            int editHandler=windowCode.IndexOf("void SearchBox_TextChanged(",StringComparison.Ordinal);
            string editBody=windowCode[editHandler..windowCode.IndexOf("\n    }",editHandler,StringComparison.Ordinal)];
            Check(!editBody.Contains("Search(") && !editBody.Contains("NamesAsync(") && !windowCode.Contains("_autoSearch"),
                "Editing subtitle search text only dismisses aliases and has no automatic request or debounce timer");
            string danmakuCode=File.ReadAllText(Path.Combine(Path.GetDirectoryName(templatePath)!,"DanmakuSearchWindow.xaml.cs"));
            Check(danmakuCode.Contains("Search_Click(object sender, RoutedEventArgs e) => Search(refresh: true)")
                && danmakuCode.Contains("refresh ? \"true\" : \"false\""),"Danmaku search button explicitly refreshes through the existing script command");
            int danmakuFilter=danmakuCode.IndexOf("void SourceChip_Click(",StringComparison.Ordinal);
            Check(danmakuCode[danmakuFilter..danmakuCode.IndexOf("void Search_Click(",danmakuFilter,StringComparison.Ordinal)].Contains("player_ui-danmaku-search-filter"),"Danmaku source chip retains cached filtering");
            foreach(string method in new[]{"Source_Click","Language_Click","Season_Click","Episode_Click"})
            {
                int start=windowCode.IndexOf("void "+method+"(",StringComparison.Ordinal);
                int end=windowCode.IndexOf("\n    }",start,StringComparison.Ordinal);
                string handlerBody=windowCode[start..end];
                Check(handlerBody.Contains("RefreshResults()") && !handlerBody.Contains("Search()") && !handlerBody.Contains("ResetSearch()"),"Filter handler is local only: "+method);
            }
            Exception? templateError=null;
            var renderThread=new Thread(()=>
            {
                try
                {
                    var itemTemplate=template.Descendants().Single(e=>e.Attributes().Any(a=>a.Name.LocalName=="Name" && a.Value=="ResultsList"))
                        .Elements().Single(e=>e.Name.LocalName=="ListBox.ItemTemplate").Elements().Single(e=>e.Name.LocalName=="DataTemplate");
                    var dataTemplate=(System.Windows.DataTemplate)System.Windows.Markup.XamlReader.Parse(itemTemplate.ToString());
                    var panel=(System.Windows.FrameworkElement)dataTemplate.LoadContent();
                    panel.DataContext=new SubtitleResult(SubtitleSource.Assrt,"1","fixture","简","简 · 2016-06-13 · 605.3 KB",[]){Format="SSA"};
                    panel.Measure(new System.Windows.Size(520,1000));panel.Arrange(new System.Windows.Rect(panel.DesiredSize));panel.UpdateLayout();
                    Check(panel.ActualHeight>0,"Real result template renders without read-only binding crash");
                    Check(panel.ActualHeight==84,"Compact result card stays at its normal minimum height");
                    Check(VisualChild<System.Windows.Controls.TextBlock>(panel)!.Text=="SSA","The actual result template binds its format badge");
                    var resources=System.Xml.Linq.XDocument.Load(Path.Combine(Path.GetDirectoryName(templatePath)!,"Resources.xaml"));
                    var shared=resources.Descendants().First(e=>e.Name.LocalName=="Style" && e.Attributes().Any(a=>a.Name.LocalName=="Key"&&a.Value=="MatchList"));
                    foreach(var element in shared.Descendants().Where(e=>e.Name.NamespaceName=="clr-namespace:HandyControl.Controls"))
                        element.Name=System.Xml.Linq.XName.Get(element.Name.LocalName,"clr-namespace:HandyControl.Controls;assembly=mpv-AnimeFusion");
                    foreach(var color in new[]{System.Windows.Media.Colors.DimGray,System.Windows.Media.Colors.WhiteSmoke})
                    {
                        MpvNet.Windows.UI.Theme.Current=new(){Foreground=System.Windows.Media.Brushes.White,Background=new System.Windows.Media.SolidColorBrush(color)};
                        var dictionary=new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("ResourceDictionary","http://schemas.microsoft.com/winfx/2006/xaml/presentation"),
                            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml"),
                            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns+"ui","clr-namespace:MpvNet.Windows.UI;assembly=mpv-AnimeFusion"),new System.Xml.Linq.XElement(shared));
                        dictionary.Add(new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns+"hc","clr-namespace:HandyControl.Controls;assembly=mpv-AnimeFusion"));
                        var styles=(System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
                        var list=new System.Windows.Controls.ListBox{Style=(System.Windows.Style)styles["MatchList"],Width=500,Height=280};
                        Check(System.Windows.Controls.VirtualizingPanel.GetScrollUnit(list)==System.Windows.Controls.ScrollUnit.Pixel
                            && System.Windows.Controls.VirtualizingPanel.GetIsVirtualizingWhenGrouping(list)
                            && System.Windows.Controls.ScrollViewer.GetPanningMode(list)==System.Windows.Controls.PanningMode.VerticalOnly,
                            "Shared search list uses native pixel scrolling, including grouped results and touch panning");
                        list.ItemContainerStyle=new System.Windows.Style(typeof(System.Windows.Controls.ListBoxItem));
                        list.ItemContainerStyle.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.HeightProperty,160d));
                        for(int i=0;i<40;i++)list.Items.Add(new SubtitleResult(SubtitleSource.Assrt,i.ToString(),"fixture "+i,"中文","",[]){Season=i/10+1});
                        foreach(bool enabled in new[]{true,false,true})
                        {
                            list.IsEnabled=enabled;list.ApplyTemplate();list.Measure(new System.Windows.Size(500,280));list.Arrange(new System.Windows.Rect(0,0,500,280));list.UpdateLayout();
                            var border=(System.Windows.Controls.Border)list.Template.FindName("Bd",list);
                            Check(border.Background is System.Windows.Media.SolidColorBrush brush&&brush.Color.A==0,"Shared list remains transparent in enabled/disabled/restored states for either theme");
                        }
                        foreach(bool grouped in new[]{false,true})
                        {
                            if(grouped)
                            {
                                var groupedTemplate=System.Xml.Linq.XDocument.Load(Path.Combine(Path.GetDirectoryName(templatePath)!,"DanmakuSearchWindow.xaml"));
                                var groupStyle=groupedTemplate.Descendants().First(e=>e.Name.LocalName=="Style"&&e.Attributes().Any(a=>a.Name.LocalName=="Key"&&a.Value=="ResultGroupItem"));
                                dictionary.Add(new System.Xml.Linq.XElement(groupStyle));
                                var groupResources=(System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
                                list.GroupStyle.Add(new System.Windows.Controls.GroupStyle{ContainerStyle=(System.Windows.Style)groupResources["ResultGroupItem"]});
                                System.Windows.Data.CollectionViewSource.GetDefaultView(list.Items).GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Season"));
                            }
                            list.Measure(new System.Windows.Size(500,280));list.Arrange(new System.Windows.Rect(0,0,500,280));list.UpdateLayout();
                            var scroll=VisualChild<System.Windows.Controls.ScrollViewer>(list)!;
                            scroll.ScrollToVerticalOffset(7);list.UpdateLayout();
                            Check(Math.Abs(scroll.VerticalOffset-7)<.1,"Small scroll offset stays within an item instead of snapping to a row, grouped="+grouped);
                            scroll.ScrollToVerticalOffset(0);list.UpdateLayout();scroll.LineDown();list.UpdateLayout();
                            Check(scroll.VerticalOffset>0&&scroll.VerticalOffset<160,"Native line scroll moves fewer pixels than one result, grouped="+grouped);
                            scroll.ScrollToVerticalOffset(0);list.UpdateLayout();
                            VisualChild<System.Windows.Controls.VirtualizingStackPanel>(list)!.MouseWheelDown();list.UpdateLayout();
                            Check(scroll.VerticalOffset>0&&scroll.VerticalOffset<160,"Native wheel scroll stays below one tall result, grouped="+grouped);
                            Check(scroll is HandyControl.Controls.ScrollViewer{IsPixelScrollingEnabled:true},"Both lists use direct pixel input in the existing HandyControl control");
                            scroll.ScrollToVerticalOffset(0);list.UpdateLayout();
                            Wheel(scroll,-2);list.UpdateLayout();
                            Check(Math.Abs(scroll.VerticalOffset-2)<.1,"Tiny input moves two pixels rather than a fixed wheel step, grouped="+grouped);
                            scroll.ScrollToVerticalOffset(0);list.UpdateLayout();
                            for(int i=0;i<10;i++)Wheel(scroll,-2);
                            list.UpdateLayout();
                            Check(Math.Abs(scroll.VerticalOffset-20)<.1,"All fine input received before layout is applied on that frame without lag or lost distance, grouped="+grouped);
                            Wheel(scroll,2);list.UpdateLayout();
                            Check(Math.Abs(scroll.VerticalOffset-18)<.1,"Direction reverses on the next layout without waiting for animation, grouped="+grouped);
                            Advance(list,200);
                            Check(Math.Abs(scroll.VerticalOffset-18)<.1,"Stopping input leaves no animation tail or old completion jump, grouped="+grouped);
                            scroll.ScrollToVerticalOffset(0);list.UpdateLayout();
                            for(int i=1;i<=30;i++)
                            {
                                Wheel(scroll,-2);list.UpdateLayout();
                                Check(Math.Abs(scroll.VerticalOffset-i*2)<.1,"Every fine input tracks the current frame, grouped="+grouped+", sample="+i);
                            }
                            scroll.ScrollToVerticalOffset(0);list.UpdateLayout();Wheel(scroll,120);list.UpdateLayout();Wheel(scroll,-2);list.UpdateLayout();
                            Check(Math.Abs(scroll.VerticalOffset-2)<.1,"Overscroll at the top does not delay a reversed input");
                            scroll.ScrollToBottom();list.UpdateLayout();Wheel(scroll,-120);list.UpdateLayout();double bottom=scroll.VerticalOffset;
                            Wheel(scroll,2);list.UpdateLayout();Check(Math.Abs(scroll.VerticalOffset-(bottom-2))<.1,"Overscroll at the bottom does not delay a reversed input");
                            scroll.ScrollToVerticalOffset(43);list.UpdateLayout();Wheel(scroll,-2);list.UpdateLayout();
                            Check(Math.Abs(scroll.VerticalOffset-45)<.1,"Native scrollbar or programmatic scrolling resets the wheel baseline");
                            list.ItemContainerStyle=new System.Windows.Style(typeof(System.Windows.Controls.ListBoxItem));list.ItemTemplate=dataTemplate;
                            for(int i=0;i<list.Items.Count;i++)list.Items[i]=new SubtitleResult(SubtitleSource.Assrt,i.ToString(),string.Join(' ',Enumerable.Repeat("variable title",i%4+1)),"简","简 · 2016-06-13 · 605.3 KB",[]){Season=i/10+1,Format="SSA"};
                            list.UpdateLayout();scroll.ScrollToTop();list.UpdateLayout();
                            for(int i=1;i<=60;i++)
                            {
                                Wheel(scroll,-3);list.UpdateLayout();
                                Check(Math.Abs(scroll.VerticalOffset-i*3)<.1,"Variable-height actual result cards follow each layout without a row snap, grouped="+grouped+", sample="+i);
                            }
                            list.ItemTemplate=null;
                            list.ItemContainerStyle=new System.Windows.Style(typeof(System.Windows.Controls.ListBoxItem));
                            list.ItemContainerStyle.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.HeightProperty,160d));
                        }
                    }
                    Check(template.Descendants().Where(e=>e.Name.LocalName=="ListBox").All(e=>(string?)e.Attribute("Style")=="{StaticResource MatchList}"),"Subtitle results and files use the shared themed list");
                    var danmakuTemplate=System.Xml.Linq.XDocument.Load(Path.Combine(Path.GetDirectoryName(templatePath)!,"DanmakuSearchWindow.xaml"));
                    Check(danmakuTemplate.Descendants().Where(e=>e.Name.LocalName=="ListBox").All(e=>(string?)e.Attribute("Style")=="{StaticResource MatchList}"),"Danmaku works and episodes use the same pixel scrolling style");
                }
                catch(Exception error){templateError=error;}
            });
            renderThread.SetApartmentState(ApartmentState.STA);renderThread.Start();renderThread.Join();
            if(templateError!=null)throw templateError;
            if(args.Contains("--scroll-only")){Console.WriteLine($"Search UI and scrolling checks passed: {_checks}; no desktop interaction.");return;}
            Check(!Query.MatchesTitle(["Laid-Back Camp Movie"]), "Other works rejected");
            var shortTitle = Query with {Title="摇曳百合",Names=[]};
            Check(shortTitle.MatchesTitle(["Yuru Yuri 2/摇曳百合 第二季/輕鬆百合 第二季"]),"Short CJK title matches a season pack without catalogue aliases");
            Check(shortTitle.MatchesTitle(["轻松百合♪♪ 第二季 BDRIP 全/輕鬆百合♪♪ 第二季 BDRIP 全/摇曳百合♪♪ 第二季 BDRIP 全"]),"Existing filename parser handles multilingual pack release labels");
            Check(!shortTitle.MatchesTitle(["摇曳百合的其他故事 第二季"]),"Short names do not match unrelated longer titles");
            Check(!shortTitle.MatchesTitle(["", " / "]),"Empty API title and alias fields do not enter the filename parser");
            Check(!shortTitle.MatchesTitle(["Other Work 1.HDTV.addic7ed ["]),"Publication-name matching only parses the anime title, not episode-title metadata");
            Check(Query.MatchesEpisode("YuruYuri S02E01") && !Query.MatchesEpisode("YuruYuri S01E01")
                && !Query.MatchesEpisode("YuruYuri S02E02"), "Season and episode verified");
            Check(!Query.MatchesEpisode("YuruYuri OVA"), "Specials excluded from regular episode requests");
            Check(Query.MatchesEpisode("轻松百合♪♪ 第二季 BDRIP 全/搖曳百合♪♪ 第二季 BDRIP 全"), "Second-season pack retained");
            Check((Query with { Season = 0 }).MatchesEpisode("YuruYuri OVA"), "Explicit special request retained");
            Check(Query.MatchesFile("[Group] YuruYuri [01].ass") && !Query.MatchesFile("[Group] YuruYuri [02].ass"), "Pack files use existing filename parser");
            Check(MpvNet.Windows.Bangumi.BangumiMedia.FromPlayback("[Group][Series][11][B24E8407].sc.ass","[Group][Series][11][B24E8407].sc.ass").EpisodeNumber==11
                &&MpvNet.Windows.Bangumi.BangumiMedia.FromPlayback("[Group][Series][12][115BD6E2].sc.ass","[Group][Series][12][115BD6E2].sc.ass").EpisodeNumber==12,
                "Hexadecimal checksums cannot become E8407 or E2 episode markers");
            Check(!(Query with { Episode = null, Year = 2012 }).MatchesEpisode("YuruYuri (2018)"), "Movie year verified");
            var collectionNames=new[]{"[Group][Yuru Yuri][01].sc.ass","[Group][Yuru Yuri 2][01].sc.ass","[Group][Yuru Yuri San Hai][01].sc.ass"};
            var collectionQuery=Query with{Names=["YuruYuri"]};
            var collectionResult=new SubtitleResult(SubtitleSource.Assrt,"collection","Collection","中文","",[]){Collection=true,PackageNames=collectionNames};
            var collectionFiles=collectionNames.Select(name=>new SubtitleFile(SubtitleSource.Assrt,"collection",0,name,"")).ToArray();
            var collectionLibrary=new SubtitleLibrary(Path.Combine(Root,"collection-library"));
            Check(collectionLibrary.CurrentFile(collectionQuery,collectionResult,collectionFiles)?.Name==collectionNames[1],"Mixed-season collection chooses the numbered current-season title, not another season's first episode; actual="
                +collectionLibrary.CurrentFile(collectionQuery,collectionResult,collectionFiles)?.Name+"; titles="+string.Join('|',collectionNames.Select(name=>string.Join(',',AnitomySharp.AnitomySharp.Parse(name).Where(e=>e.Category==AnitomySharp.Element.ElementCategory.ElementAnimeTitle).Select(e=>e.Value)))));
            foreach(var file in collectionFiles)
            {
                string path=collectionLibrary.FilePath(collectionQuery,collectionResult,file.Name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"fixture");
                collectionLibrary.Save(collectionQuery,collectionResult,file,path,true);
            }
            var collectionTracks=collectionLibrary.Tracks(MpvNet.Windows.Trakt.TraktMedia.Parse("YuruYuri S2E1","",new Dictionary<string,string>()));
            Check(collectionTracks.Count==1&&collectionTracks[0].Path.EndsWith(collectionNames[1]),"Other season files remain stored but cannot enter the current episode's subtitle menu");
            int queries=0;
            var cacheHandler=new Handler((_,_)=>Json("""
                {"status":0,"sub":{"subs":[
                {"id":1,"native_name":"YuruYuri 第一季","lang":{"desc":"英"}},
                {"id":2,"native_name":"YuruYuri 第二季","lang":{"desc":"简"}},
                {"id":3,"native_name":"YuruYuri 第二季","lang":{"desc":"日"}}]}}
                """));
            using(var cacheHttp=new HttpClient(cacheHandler))
            {
                using var search=new SubtitleSearch(new AssrtClient(cacheHttp,Store("cache")));
                Task<SubtitleQuery> MakeQuery(CancellationToken token){queries++;return Task.FromResult(Query with {Season=null,Language=""});}
                var progress=new Progress<SubtitleSearchResult>();
                var first=await search.SearchAsync("YuruYuri",MakeQuery,progress,default);
                Check(first.Complete&&first.Results.Count==3&&cacheHandler.Count==1,"Initial search retains all seasons and languages");
                Check(first.Results.Count(r=>r.MatchesFilter(null,"zh-Hans",2))==1,"Simplified Chinese second season filters stored results");
                Check(first.Results.Count(r=>r.MatchesFilter(null,"en",1))==1,"English first season filters stored results");
                var repeated=await search.SearchAsync(" yuruyuri ",MakeQuery,progress,default);
                Check(ReferenceEquals(first,repeated)&&queries==1&&cacheHandler.Count==1,"Filters and automatic reuse make no new requests");
                Check(first.Results.Count(r=>r.MatchesFilter(null,"",null))==3,"All restores the complete cached result");
                var refreshed=await search.SearchAsync("YuruYuri",MakeQuery,progress,default,refresh:true);
                Check(!ReferenceEquals(first,refreshed)&&queries==2&&cacheHandler.Count==2,"Explicit search refreshes and replaces the snapshot");
                var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending=search.SearchAsync("cancelled",async token=>{started.SetResult();await Task.Delay(Timeout.Infinite,token);return Query;},progress,default);
                await started.Task;
                var replacement=await search.SearchAsync("cancelled",MakeQuery,progress,default,refresh:true);
                try{await pending;throw new Exception("Old search must cancel");}
                catch(OperationCanceledException){Check(replacement.Complete,"Explicit refresh cancels old replies");}
            }
            Check(Enum.GetNames<SubtitleSource>().SequenceEqual(new[]{"Assrt", "Amenzb", "Tsukihime"})&&!sourceTemplate.ToString().Contains("OpenSubtitles")
                &&!windowCode.Contains("OpenSubtitles")&&!template.ToString().Contains("OpenSubtitles"),"The three supported sources are available without restoring OpenSubtitles");
            var packStore = Store("packs");
            packStore.Save(new(new string('a',32)));
            var packHandler = new Handler((request,count)=>
            {
                string body=request.Content!.ReadAsStringAsync().Result;
                Check(request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath=="/v1/sub/search"&&body.Contains("q=YuruYuri%21")
                    &&body.Contains("filelist=1"),"The selected title and punctuation reach the package metadata POST without another alias");
                return Json("""
                {"status":0,"sub":{"subs":[
                {"id":1,"native_name":"YuruYuri 第一季","lang":{"desc":"简"}},
                {"id":2,"native_name":"YuruYuri 第二季","lang":{"desc":"简"}},
                {"id":3,"native_name":"YuruYuri","videoname":"YuruYuri","lang":{"desc":"简"}},
                {"id":4,"native_name":"YuruYuri 第二季 OVA","lang":{"desc":"简"}},
                {"id":5,"native_name":"ゆるゆり/摇曳百合/輕鬆百合/YuruYuri","videoname":"[字幕组][合集(1+2+3期+OVA)]","subtype":"SSA","size":619874,"lang":{"desc":"繁"},"vote_score":16,"down_count":0,"view_count":7041,"upload_time":"2016-06-13 02:47:55","producer":{"producer":"制作组","verifier":"校对组","source":"转载","uploader":"上传者"}}]}}
                """);
            });
            using(var packHttp=new HttpClient(packHandler))
            {
                var result=await new AssrtClient(packHttp,packStore).SearchAsync(Query with {Title="YuruYuri！", Names=["摇曳百合", "YuruYuri!"]},1,default);
                Check(result.Results.Count==5 && result.Results.Any(r=>r.Id=="5"), "All retains the full API response, including season collections and specials");
                Check(result.Results.Count(r=>r.MatchesFilter(null,"",null))==5,"All is not the union of recognized season chips");
                var collection=result.Results.Single(r=>r.Id=="5");
                Check(collection.MatchesFilter(null,"zh-Hant",2)&&collection.Collection,"Collections remain visible for the current season rather than being assumed to be season one");
                Check(collection.Title=="ゆるゆり/摇曳百合/輕鬆百合/YuruYuri/[字幕组][合集(1+2+3期+OVA)]","Result title retains both the work aliases and the full release name");
                Check(collection.Format=="SSA"&&collection.Details=="繁 · 2016-06-13 · 605.3 KB","Metadata only displays language, date and package size without a raw vote score");
                Check(result.Results[0].Format==""&&result.Results[0].Details=="简","Missing format, date and size have no placeholder labels");
                Check(result.Results.Single(r=>r.Id=="3").Title=="YuruYuri","Identical work and release names are not repeated");
            }
            var sparseHandler=new Handler((request,count)=>Json(count==1
                ? "{\"status\":0,\"sub\":{\"subs\":["+string.Join(',',Enumerable.Range(1,15).Select(id=>$"{{\"id\":{id},\"native_name\":\"Other Work\",\"lang\":{{\"desc\":\"简\"}}}}"))+"]}}"
                : "{\"status\":0,\"sub\":{\"subs\":[{\"id\":99,\"native_name\":\"YuruYuri 第二季\",\"lang\":{\"desc\":\"简\"}}]}}"));
            using(var sparseHttp=new HttpClient(sparseHandler))
            using(var sparseSearch=new SubtitleSearch(new AssrtClient(sparseHttp,packStore)))
            {
                var result=await sparseSearch.SearchAsync("YuruYuri",_=>Task.FromResult(Query),new Progress<SubtitleSearchResult>(),default);
                Check(result.Complete && result.Results.Count==16 && result.Results.Any(r=>r.Id=="99") && sparseHandler.Count==2,"All keeps both pages without local title suppression");
            }
            var invalidHandler=new Handler((_,_)=>Json("{\"status\":0,\"sub\":{\"subs\":[{\"fileid\":1,\"videoname\":\"YuruYuri\"}]}}"));
            using(var invalidHttp=new HttpClient(invalidHandler))
                await Fails(()=>new AssrtClient(invalidHttp,packStore).SearchAsync(Query,1,default),"返回结果异常");
            var validAliasHandler=new Handler((_,count)=>Json(count==1
                ? "{\"status\":0,\"sub\":{\"subs\":[{\"fileid\":1}]}}"
                : "{\"status\":0,\"sub\":{\"subs\":[{\"id\":2,\"native_name\":\"YuruYuri 第二季\",\"lang\":{\"desc\":\"简\"}}]}}"));
            using(var validAliasHttp=new HttpClient(validAliasHandler))
            {
                await Fails(()=>new AssrtClient(validAliasHttp,packStore).SearchAsync(Query with {Names=["Alias"]},1,default),"返回结果异常");
                Check(validAliasHandler.Count==1,"A malformed response is reported without retrying a different alias");
            }
            var movieHandler=new Handler((request,_)=>
            {
                Check(request.Headers.Authorization==null&&!request.Headers.Contains("Api-Key"),"Download host receives no credentials");
                return new(HttpStatusCode.OK){Content=new StringContent("1\n00:00:00,000 --> 00:00:01,000\nfixture\n")};
            });
            using(var movieHttp=new HttpClient(movieHandler))
            {
                var library=new SubtitleLibrary(Store("movie").Root);
                var downloader=new SubtitleDownload(movieHttp,library);
                var query=Query with{Season=null,Episode=null};
                var result=new SubtitleResult(SubtitleSource.Assrt,"1","YuruYuri","中文","",[]){DownloadName="fixture.srt",DownloadUrl="https://download.example/fixture.srt"};
                string file=(await downloader.DownloadAsync(query,result,null,[],null,default))!;
                Check(File.ReadAllText(file).Contains("fixture"),"Movie download retains original subtitle contents");
                Check(await downloader.DownloadAsync(query,result,null,[],null,default)==file&&movieHandler.Count==1,"Saved movie subtitle reuses local bytes");
            }
            var wholeStore=Store("whole-season");
            var detailStore=Store("package-detail");detailStore.Save(new(new string('a',32)));
            var detailHandler=new Handler((request,_)=>
            {
                Check(request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath=="/v1/sub/detail"
                    &&request.Content!.ReadAsStringAsync().Result=="id=7","Package metadata uses the official detail POST and requests the full package");
                Check(request.Headers.CacheControl?.NoCache==true,"Temporary package links bypass cached detail responses");
                return Json("{\"status\":0,\"sub\":{\"subs\":[{\"native_name\":\"作品\",\"videoname\":\"[字幕组]合集\",\"subtype\":\"ASS\",\"lang\":{\"desc\":\"简繁\"},\"upload_time\":\"2016-06-13 02:47:55\",\"size\":2097152,\"filename\":\"season.rar\",\"url\":\"https://download.example/season.rar\",\"filelist\":[{\"f\":\"single.ass\",\"url\":\"https://download.example/onthefly/single.ass\"}]}]}}");
            });
            using(var detailHttp=new HttpClient(detailHandler))
            {
                var detail=await new AssrtClient(detailHttp,detailStore).DetailAsync(new(SubtitleSource.Assrt,"7","Fixture","中文","",[]),default);
                Check(detail.DownloadName=="season.rar"&&detail.DownloadUrl.EndsWith("/season.rar")
                    &&detail.Files.Count==1&&detail.Files[0].Name=="single.ass"
                    &&detail.Files[0].Url.EndsWith("/onthefly/single.ass"),
                    "Detail retains the original package and its individually downloadable subtitle files");
                Check(detail.Title=="作品/[字幕组]合集"&&detail.Format=="ASS"&&detail.Details=="简繁 · 2016-06-13 · 2 MB","Detail uses the same full title and compact metadata as search");
            }
            var wholeLibrary=new SubtitleLibrary(wholeStore.Root);
            var names=new[]{"[CASO][Yuruyuri2][01][BDRIP][1920x1080][x264_FLAC][C38B0799].sc.ass","[CASO][Yuruyuri2][01][BDRIP][1920x1080][x264_FLAC][C38B0799].tc.ass",
                "[CASO][Yuruyuri2][02][BDRIP][1920x1080][x264_FLAC][C38B0799].sc.ass","[CASO][Yuruyuri2][02][BDRIP][1920x1080][x264_FLAC][C38B0799].tc.ass"};
            var wholeHandler=new Handler((request,_)=>
            {
                Check(request.RequestUri!.AbsolutePath=="/season.zip","Series only requests the original compressed package, never an episode URL");
                Check(!request.Headers.Contains("Api-Key")&&request.Headers.Authorization==null,"Package host receives no API credentials");
                return new(HttpStatusCode.OK){Content=new ByteArrayContent(Zip(names))};
            });
            using(var wholeHttp=new HttpClient(wholeHandler))
            {
                var query=Query with{Title="摇曳百合"};
                var result=new SubtitleResult(SubtitleSource.Assrt,"7","整季字幕","简繁","简繁 · 2016-06-13 · 2 MB",[]){Format="ASS",Season=2,LanguageCode="zh",DownloadName="season.zip",DownloadUrl="https://download.example/season.zip"};
                var download=new SubtitleDownload(wholeHttp,wholeLibrary);
                var progressMessages=new List<string>();
                string selected=(await download.DownloadAsync(query,result,null,[],new Messages(progressMessages),default))!;
                Check(progressMessages.Contains("下载字幕 100%")&&progressMessages.Contains("解压字幕 100%"),"Download bytes and archive entries report actual progress events");
                Check(progressMessages.Count(message=>message.StartsWith("下载字幕 "))<=11&&progressMessages.Count(message=>message.StartsWith("解压字幕 "))<=11,"Percentage progress is bounded without a polling timer");
                Check(wholeHandler.Count==1&&wholeLibrary.Package(query,result)!.Files.Count==4,"One package request saves all episode variants locally");
                Check(File.Exists(wholeLibrary.ArchivePath(query,result)),"Original compressed package remains permanently in the shared work/package directory");
                Check(selected.EndsWith(".sc.ass")&&selected.Contains("[01]"),"Current episode is selected after extraction");
                wholeLibrary.Remember(query.Title,2,selected);
                var next=MpvNet.Windows.Trakt.TraktMedia.Parse("摇曳百合 S2E2","",new Dictionary<string,string>());
                var tracks=wholeLibrary.Tracks(next);
                Check(tracks.Count==2&&tracks.Single(t=>t.Preferred).Path.EndsWith(".sc.ass"),"Next episode lists its saved alternatives and prefers the previous language/version");
                wholeLibrary.Remember(query.Title,2,tracks.Single(t=>t.Path.EndsWith(".tc.ass")).Path);
                string nextPath=(await download.DownloadAsync(query with{Episode=2},result,null,[],null,default))!;
                Check(nextPath.Contains("[02]")&&nextPath.EndsWith(".tc.ass")&&wholeHandler.Count==1,"Next episode and remembered version use the cached package without network");
                Check(await download.DownloadAsync(query with{Episode=3},result,null,[],null,default)==null,"No current episode never loads a different episode");
                Check(new SubtitleLibrary(wholeLibrary.Root).Tracks(next).Single(t=>t.Preferred).Path.EndsWith(".tc.ass"),"Track choice persists after reopening");
                Check(wholeLibrary.Tracks(MpvNet.Windows.Trakt.TraktMedia.Parse("摇曳百合 S1E2","",new Dictionary<string,string>())).Count==0,"Different seasons never share cached subtitles");
                Check(await download.DownloadAsync(query with{Season=1},result,null,[],null,default)==null&&wholeHandler.Count==1,"A cached single-season archive cannot supply another season merely because its file lacks an explicit season token");
                Check(wholeLibrary.Results(query.Title).Single().Files.Count==4,"Local search lists the complete extracted pack");
                var saved=wholeLibrary.Results(query.Title).Single();
                Check(saved.Title==result.Title&&saved.Format==result.Format&&saved.Details==result.Details,"Local results retain the same full title, format and compact metadata after reopening");
                Check(!File.ReadAllText(Path.Combine(wholeLibrary.SeasonDirectory(query.Title,2),"subtitles.json")).Contains("https://"),"Local index does not store expiring download links");
                await Fails(()=>download.DownloadAsync(query,result with{DownloadName="single.srt"},null,[],null,default),"整包");
                Check(wholeHandler.Count==1,"A single-file series result is rejected before any request");
            }
            var mixedStore=Store("mixed-seasons");var mixedLibrary=new SubtitleLibrary(mixedStore.Root);
            var mixedNames=new[]{"Collection/Example/[Group][Example][01].sc.ass","Collection/Example2/[Group][Example 2][01].sc.ass",
                "Collection/Example2/[Group][Example 2][02].sc.ass","Collection/Example3/[Group][Different official title][01].tc.ass",
                "Collection/Extras/[Group][Example][OVA1].tc.ass","Collection/Other/[Group][Unidentified title][01].ass"};
            var mixedHandler=new Handler((_,_)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(Zip(mixedNames))});
            using(var mixedHttp=new HttpClient(mixedHandler))
            {
                var query=new SubtitleQuery("Example",2,1,"zh");
                var result=new SubtitleResult(SubtitleSource.Assrt,"mixed","Whole collection","简繁","",[]){LanguageCode="zh",DownloadName="collection.zip",DownloadUrl="https://download.example/collection.zip"};
                var download=new SubtitleDownload(mixedHttp,mixedLibrary);
                var selected=await download.DownloadAsync(query,result,null,[],null,default);
                Check(selected!=null&&selected.Contains("第 2 季")&&selected.EndsWith("[01].sc.ass"),"Archive folders identify a multi-season collection even when the API does not mark it");
                var saved=mixedLibrary.Results("Example").Single();
                Check(saved.Files.Count==6&&saved.AvailableSeasons.ToHashSet().SetEquals(new int?[]{-1,0,1,2,3}),"A collection creates season, special and unclassified indexes while remaining one local result");
                Check(saved.Files.Any(file=>file.LocalPath.Contains("第 1 季"))&&saved.Files.Any(file=>file.LocalPath.Contains("第 3 季"))
                    &&saved.Files.Any(file=>file.LocalPath.Contains("特别篇"))&&saved.Files.Any(file=>file.LocalPath.Contains("未分类")),"Extracted files are physically stored in their own sections");
                Check(saved.MatchesFilter(null,"zh",1)&&saved.MatchesFilter(null,"zh",3)&&!saved.MatchesFilter(null,"zh",4),"Local season filters reflect downloaded sections rather than every season");
                foreach(int season in new[]{1,2,3})
                {
                    string title=$"Example S{season}E1";
                    var tracks=mixedLibrary.Tracks(MpvNet.Windows.Trakt.TraktMedia.Parse(title,"",new Dictionary<string,string>()));
                    Check(tracks.Count==1&&tracks[0].Path.Contains($"第 {season} 季"),"Playback lists only its own season and episode: "+title);
                    string? path=await download.DownloadAsync(query with{Season=season},result,null,[],null,default);
                    Check(path==tracks[0].Path&&mixedHandler.Count==1,"Cross-season reuse makes no additional package request: "+season);
                }
                mixedLibrary.Remember("Example",2,selected!);
                Check(mixedLibrary.Tracks(MpvNet.Windows.Trakt.TraktMedia.Parse("Example S2E2","",new Dictionary<string,string>())).Single().Preferred,"Season preference applies to the next episode after splitting a collection");
                Check(File.Exists(mixedLibrary.ArchivePath(query,result))&&Directory.GetFiles(mixedLibrary.Root,"*.zip",SearchOption.AllDirectories).Length==1,"Every season shares exactly one original archive");
                Check(mixedLibrary.CurrentFile(query with{Season=4},saved,saved.Files)==null,"An unrecognized or missing season cannot load another season's episode");
            }
            foreach(string entryName in new[]{"../../escape.srt","folder/episode [01].srt"})
            {
                var archiveStore=Store(Guid.NewGuid().ToString("N"));var archiveLibrary=new SubtitleLibrary(archiveStore.Root);
                var archiveHandler=new Handler((_,_)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(Zip([entryName]))});
                using var archiveHttp=new HttpClient(archiveHandler);
                var result=new SubtitleResult(SubtitleSource.Assrt,"9","Fixture","中文","",[]){Season=2,LanguageCode="zh",DownloadName="fixture.zip",DownloadUrl="https://download.example/fixture.zip"};
                var download=new SubtitleDownload(archiveHttp,archiveLibrary);
                if(entryName.StartsWith(".."))await Fails(()=>download.DownloadAsync(Query,result,null,[],null,default),"文件名无效");
                else
                {
                    await download.DownloadAsync(Query,result,null,[],null,default);
                    Check(File.Exists(archiveLibrary.FilePath(Query,result,entryName)),"Original package subdirectories are retained");
                }
            }
            var redirectStore=Store("redirect");
            var handler=new Handler((request,count)=>
            {
                Check(request.Headers.Authorization?.Parameter==new string('a',32),"ASSRT key stays in the authorization header");
                return count==1?Redirect("/v1/sub/search?q=YuruYuri"):Json(Results);
            });
            using(var http=new HttpClient(handler))
            {
                await Fails(()=>new AssrtClient(http,redirectStore).SearchAsync(Query,1,default),"服务暂时不可用");
                Check(handler.Count==1,"Search POST is not redirected or retried");
            }
            handler=new((_,_)=>Redirect("https://other.example/v1/sub/search"));
            using(var http=new HttpClient(handler))
            {
                await Fails(()=>new AssrtClient(http,redirectStore).SearchAsync(Query,1,default),"服务暂时不可用");
                Check(handler.Count==1,"Foreign redirect cannot receive the key");
            }
            handler=new((_,_)=>Json("{}",HttpStatusCode.TooManyRequests));
            using(var http=new HttpClient(handler))
                await Fails(()=>new AssrtClient(http,redirectStore).SearchAsync(Query,1,default),"请求太频繁");
            handler = new((_, _) => throw new Exception("Invalid credentials must not make a network request"));
            using (var http = new HttpClient(handler))
            {
                var client = new AssrtClient(http, new SubtitleStore(Path.Combine(Root,"no-key")));
                await Fails(() => client.SearchAsync(Query, 1, default), "请填写 API 密钥");
                await Fails(() => client.ValidateTokenAsync("invalid-fixture", default), "API 密钥格式不正确");
                Check(handler.Count == 0, "Missing or malformed ASSRT key checked locally");
            }
            Console.WriteLine($"Subtitle checks passed: {_checks}; no desktop interaction or real service credentials used.");
        }
        finally
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
    sealed class Messages(List<string> messages):IProgress<string>
    { public void Report(string message)=>messages.Add(message); }
    sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, ++Count));
        }
    }
}
