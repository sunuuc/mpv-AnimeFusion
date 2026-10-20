using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;

static class SubtitleSetRemovalChecks
{
    static TraktMedia Media(string title) => TraktMedia.Parse(title, "", new Dictionary<string, string>());
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static string Index(SubtitleLibrary library, string title, int? season) => Path.Combine(library.SeasonDirectory(title, season), "subtitles.json");
    static SubtitleShelf Shelf(SubtitleLibrary library, string title, int? season) => JsonSerializer.Deserialize<SubtitleShelf>(File.ReadAllBytes(Index(library, title, season)))!;
    static string SelectionPath(SubtitleLibrary library, string title) => Path.Combine(Path.GetDirectoryName(library.SeasonDirectory(title, 1))!, "selection.json");
    static SubtitleSelection Selection(SubtitleLibrary library, string title) => JsonSerializer.Deserialize<SubtitleSelection>(File.ReadAllBytes(SelectionPath(library, title)))!;
    static SubtitleResult Result(SubtitleSource source, string id, string title) => new(source, id, title, "简繁", "", [])
        { Collection = true, LanguageCode = "zh-Hans,zh-Hant", Format = "ASS" };

    static string Save(SubtitleLibrary library, string title, SubtitleResult result, int season, int episode, string name)
    {
        var query = new SubtitleQuery(title, season, episode, "");
        string path = library.FilePath(query, result, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "原始字幕\n" + name, new UTF8Encoding(false));
        library.Save(query, result, new(result.Source, result.Id, 0, name, ""), path, true);
        return path;
    }

    static string Archive(SubtitleLibrary library, string title, SubtitleResult result, IReadOnlyList<string> files)
    {
        string path = library.ArchivePath(new(title, 1, 1, ""), result with { DownloadName = "原始整包.zip" });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string file in files)
        {
            using var entry = zip.CreateEntry(Path.GetFileName(file)).Open();
            entry.Write(File.ReadAllBytes(file));
        }
        return path;
    }

    static Dictionary<string, string> Snapshot(string root) => Directory.EnumerateFiles(root, "*", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToDictionary(path => Path.GetRelativePath(root, path),
            path => File.GetAttributes(path) + ":" + Hash(path), StringComparer.OrdinalIgnoreCase);

    static bool Equal(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) => left.Count == right.Count
        && left.All(item => right.TryGetValue(item.Key, out string? value) && value == item.Value);

    static string FixturePath(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A junction fixture path must remain inside its isolated directory.");
        return fullPath;
    }

    static void Junction(string path, string target)
    {
        if (new[] { path, target }.Any(value => value.IndexOfAny(['"', '%', '\r', '\n']) >= 0))
            throw new ArgumentException("A junction fixture requires paths without command expansion characters.");
        var start = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{path}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new IOException("The junction fixture process did not start.");
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"The junction fixture could not be created: {output}{error}");
    }

    sealed record Fixture(string Root, SubtitleLibrary Library, SubtitleResult Target, string[] Files, string Archive,
        string OtherId, string OtherSource, string OtherWork, string OtherIdArchive, string OtherSourceArchive);

    static Fixture Create(string root, string name)
    {
        string directory = Path.Combine(root, name);
        if (Directory.Exists(directory)) throw new InvalidOperationException("Removal fixtures require a new isolated directory.");
        var library = new SubtitleLibrary(Path.Combine(directory, "Subtitles"));
        const string title = "Removal Fixture";
        var target = Result(SubtitleSource.Assrt, "target", title);
        string[] names = ["[Group] Removal Fixture S01E01.sc.ass", "[Group] Removal Fixture S01E01.tc.ass",
            "[Group] Removal Fixture S01E02.sc.ass", "[Group] Removal Fixture S02E01.tc.ass"];
        string[] files = names.Select((name, index) => Save(library, title, target, index == 3 ? 2 : 1, index == 2 ? 2 : 1, name)).ToArray();
        string archive = Archive(library, title, target, files);
        string otherId = Save(library, title, target with { Id = "other" }, 1, 1, names[0]);
        string otherSource = Save(library, title, target with { Source = SubtitleSource.Amenzb }, 1, 1, names[0]);
        string otherWork = Save(library, "Other Removal Work", Result(SubtitleSource.Assrt, "target", "Other Removal Work"),
            1, 1, "[Group] Other Removal Work S01E01.sc.ass");
        string otherIdArchive = Archive(library, title, target with { Id = "other" }, [otherId]);
        string otherSourceArchive = Archive(library, title, target with { Source = SubtitleSource.Amenzb }, [otherSource]);
        return new(directory, library, target, files, archive, otherId, otherSource, otherWork, otherIdArchive, otherSourceArchive);
    }

    sealed record ImportedFixture(string Root, SubtitleLibrary Library, string Title, string Selected, string[] Copies, string[] Originals);

    static ImportedFixture Import(string root, string name)
    {
        string directory = Path.Combine(root, name);
        if (Directory.Exists(directory)) throw new InvalidOperationException("Import fixtures require a new isolated directory.");
        string source = Path.Combine(directory, "source");
        Directory.CreateDirectory(source);
        string[] names = ["[Group] Imported Fixture S01E01.sc.ass", "[Group] Imported Fixture S01E01.tc.ass",
            "[Group] Imported Fixture S01E02.sc.ass", "[Group] Imported Fixture S02E01.sc.ass"];
        string[] originals = names.Select(name => Path.Combine(source, name)).ToArray();
        foreach (string original in originals) File.WriteAllText(original, "原始字幕\n" + Path.GetFileName(original), new UTF8Encoding(false));
        var library = new SubtitleLibrary(Path.Combine(directory, "Subtitles"));
        var media = Media("Imported Fixture S01E01");
        string selected = library.Import(media, originals[0]);
        return new(directory, library, media.Title, selected, library.Results(media.Title).Single().Files.Select(file => file.LocalPath).ToArray(), originals);
    }

    public static int Run(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute isolated test root is required.");
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        void FailsUnchanged(string directory, Action action, string message)
        {
            var before = Snapshot(directory);
            bool failed = false;
            try { action(); }
            catch (Exception error) when (error is SubtitleServiceException or JsonException or IOException or UnauthorizedAccessException)
            { failed = true; }
            Check(failed, message + " is rejected");
            Check(Equal(before, Snapshot(directory)), message + " changes no file bytes, attributes or index records");
        }

        const string title = "Removal Fixture";
        var records = Create(root, "records-only");
        records.Library.SetPinned(title, records.Files[0], true);
        records.Library.Remember(title, 1, records.Files[0]);
        records.Library.Remember(title, 2, records.Files[3]);
        string rivalHash = Hash(records.OtherId), sourceHash = Hash(records.OtherSource), workHash = Hash(records.OtherWork);
        string otherWorkIndex = Index(records.Library, "Other Removal Work", 1), otherWorkIndexHash = Hash(otherWorkIndex);
        var removedRecords = records.Library.Remove(title, records.Files[0], false);
        Check(removedRecords.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(records.Files),
            "Default removal returns every episode and language in the same cross-season source package");
        Check(records.Files.All(File.Exists) && File.Exists(records.Archive), "Record-only removal preserves all subtitle and archive bytes");
        Check(records.Library.Results(title).All(result => result.Source != records.Target.Source || result.Id != records.Target.Id)
            && records.Library.Tracks(Media("Removal Fixture S02E01")).Count == 0,
            "The entire target package is absent after reopening either season");
        Check(Selection(records.Library, title).Package.Length == 0
            && new[] { 1, 2 }.All(season => Shelf(records.Library, title, season).PreferredPackage.Length == 0),
            "Removing the target set clears its pinned and ordinary remembered selection");
        Check(Hash(records.OtherId) == rivalHash && Hash(records.OtherSource) == sourceHash && Hash(records.OtherWork) == workHash
            && Hash(otherWorkIndex) == otherWorkIndexHash && records.Library.Results(title).Count == 2,
            "The same ID from another source and another ID or work remain unchanged");

        var physical = Create(root, "physical");
        physical.Library.SetPinned(title, physical.OtherId, true);
        physical.Library.Remember(title, 1, physical.OtherId);
        byte[] otherSelection = File.ReadAllBytes(SelectionPath(physical.Library, title));
        string otherPackageKey = SubtitleLibrary.PackageKey(SubtitleSource.Assrt, "other");
        var deleted = physical.Library.Remove(title, physical.Files[1], true);
        Check(deleted.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(physical.Files),
            "Selecting any language returns all physical subtitle paths but excludes the archive from native track removal");
        Check(physical.Files.All(file => !File.Exists(file)) && !File.Exists(physical.Archive),
            "Confirmed removal deletes every season, language and the original archive");
        Check(File.Exists(physical.OtherId) && File.Exists(physical.OtherSource) && File.Exists(physical.OtherWork)
            && File.Exists(physical.OtherIdArchive) && File.Exists(physical.OtherSourceArchive)
            && physical.Library.Results(title).Count == 2, "Physical removal preserves different source, version and work packages");
        Check(File.ReadAllBytes(SelectionPath(physical.Library, title)).SequenceEqual(otherSelection)
            && Shelf(physical.Library, title, 1).PreferredPackage == otherPackageKey
            && physical.Library.Tracks(Media("Removal Fixture S01E01")).Single(track => track.Pinned).Path == physical.OtherId,
            "Removing a different package preserves the other package's pinned and remembered selection");

        var originals = Import(root, "originals");
        Check(originals.Copies.Length == 4, "Imported deletion fixture contains both languages and both seasons");
        var originalDeletion = originals.Library.Remove(originals.Title, originals.Selected, true);
        Check(originalDeletion.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(originals.Copies.Concat(originals.Originals))
            && originals.Copies.Concat(originals.Originals).All(file => !File.Exists(file))
            && originals.Library.Results(originals.Title).Count == 0,
            "Imported set removal deletes every verified original and managed copy across seasons");

        var shared = Create(root, "shared-managed");
        var sharingResult = Result(SubtitleSource.Assrt, "sharing-version", title);
        shared.Library.Save(new(title, 1, 1, ""), sharingResult,
            new(SubtitleSource.Assrt, sharingResult.Id, 0, Path.GetFileName(shared.Files[0]), ""), shared.Files[0], true);
        FailsUnchanged(shared.Root, () => shared.Library.Remove(title, shared.Files[0], true),
            "A managed file still referenced by another package in the same work");
        shared.Library.Remove(title, shared.Files[1], false);
        Check(shared.Library.Results(title).Any(result => result.Id == sharingResult.Id && result.Files.Any(file => file.LocalPath == shared.Files[0]))
            && shared.Files.All(File.Exists), "Record-only deletion removes only the selected package even when another package shares its path");

        var sharedOriginal = Import(root, "shared-original");
        var otherMedia = Media("Another Imported Work S01E01");
        string otherCopy = sharedOriginal.Library.Import(otherMedia, sharedOriginal.Originals[0]);
        FailsUnchanged(sharedOriginal.Root, () => sharedOriginal.Library.Remove(sharedOriginal.Title, sharedOriginal.Selected, true),
            "An external original still referenced by another work");
        sharedOriginal.Library.Remove(otherMedia.Title, otherCopy, false);
        var released = sharedOriginal.Library.Remove(sharedOriginal.Title, sharedOriginal.Selected, true);
        Check(released.Count == sharedOriginal.Copies.Length + sharedOriginal.Originals.Length && File.Exists(otherCopy),
            "After releasing another work's records, deleting the target set retains that unreferenced different-work copy");

        var changed = Import(root, "changed-original");
        File.AppendAllText(changed.Originals[^1], "user edit");
        FailsUnchanged(changed.Root, () => changed.Library.Remove(changed.Title, changed.Selected, true),
            "A later episode whose original hash changed");

        var readOnly = Import(root, "readonly-original");
        string protectedOriginal = readOnly.Originals[^1];
        File.SetAttributes(protectedOriginal, File.GetAttributes(protectedOriginal) | FileAttributes.ReadOnly);
        try
        {
            FailsUnchanged(readOnly.Root, () => readOnly.Library.Remove(readOnly.Title, readOnly.Selected, true),
                "A read-only original in the last episode of the set");
        }
        finally { File.SetAttributes(protectedOriginal, File.GetAttributes(protectedOriginal) & ~FileAttributes.ReadOnly); }

        var readOnlyArchive = Create(root, "readonly-archive");
        File.SetAttributes(readOnlyArchive.Archive, File.GetAttributes(readOnlyArchive.Archive) | FileAttributes.ReadOnly);
        try
        {
            FailsUnchanged(readOnlyArchive.Root, () => readOnlyArchive.Library.Remove(title, readOnlyArchive.Files[0], true),
                "A read-only original archive");
        }
        finally { File.SetAttributes(readOnlyArchive.Archive, File.GetAttributes(readOnlyArchive.Archive) & ~FileAttributes.ReadOnly); }

        var readOnlyIndex = Create(root, "readonly-index");
        string protectedIndex = Index(readOnlyIndex.Library, title, 2);
        File.SetAttributes(protectedIndex, File.GetAttributes(protectedIndex) | FileAttributes.ReadOnly);
        try
        {
            FailsUnchanged(readOnlyIndex.Root, () => readOnlyIndex.Library.Remove(title, readOnlyIndex.Files[0], true),
                "A read-only index in the last affected season before physical deletion");
            FailsUnchanged(readOnlyIndex.Root, () => readOnlyIndex.Library.Remove(title, readOnlyIndex.Files[0], false),
                "A read-only index in the last affected season before record-only deletion");
        }
        finally { File.SetAttributes(protectedIndex, File.GetAttributes(protectedIndex) & ~FileAttributes.ReadOnly); }

        var link = Create(root, "reparse");
        string junction = FixturePath(link.Root, Path.GetDirectoryName(link.Files[0])!);
        string junctionTarget = FixturePath(link.Root, Path.Combine(link.Root, "junction-target"));
        string targetFile = Path.Combine(junctionTarget, Path.GetFileName(link.Files[0]));
        string targetHash = Hash(link.Files[0]);
        if (Directory.Exists(junctionTarget)) throw new InvalidOperationException("A junction target must be a new fixture directory.");
        Directory.Move(junction, junctionTarget);
        try
        {
            Junction(junction, junctionTarget);
            Check((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "The path-protection fixture uses a real directory junction");
            FailsUnchanged(link.Root, () => link.Library.Remove(title, link.Files[0], true), "A managed package directory replaced with a junction");
            Check(File.Exists(targetFile) && File.Exists(link.Files[0]) && Hash(targetFile) == targetHash,
                "Reparse rejection preserves the junction and its target bytes");
        }
        finally
        {
            FixturePath(link.Root, junction);
            FixturePath(link.Root, junctionTarget);
            if (Directory.Exists(junction))
            {
                if ((File.GetAttributes(junction) & FileAttributes.ReparsePoint) == 0)
                    throw new IOException("The fixture junction changed before cleanup.");
                Directory.Delete(junction);
            }
            Directory.Move(junctionTarget, junction);
        }

        var corrupt = Create(root, "corrupt-index");
        File.WriteAllText(Index(corrupt.Library, "Other Removal Work", 1), "{invalid-index");
        FailsUnchanged(corrupt.Root, () => corrupt.Library.Remove(title, corrupt.Files[0], true),
            "An unreadable foreign index whose references cannot be verified");

        var corruptSeason = Create(root, "corrupt-target-season");
        corruptSeason.Library.SetPinned(title, corruptSeason.Files[0], true);
        File.WriteAllText(Index(corruptSeason.Library, title, 2), "{invalid-index");
        foreach (bool deleteLocal in new[] { false, true })
            FailsUnchanged(corruptSeason.Root, () => corruptSeason.Library.Remove(title, corruptSeason.Files[0], deleteLocal),
                "An unreadable second season in the target work with deleteLocal=" + deleteLocal);

        var corruptSelection = Create(root, "corrupt-target-selection");
        corruptSelection.Library.SetPinned(title, corruptSelection.Files[0], true);
        File.WriteAllText(SelectionPath(corruptSelection.Library, title), "{invalid-selection");
        foreach (bool deleteLocal in new[] { false, true })
            FailsUnchanged(corruptSelection.Root, () => corruptSelection.Library.Remove(title, corruptSelection.Files[0], deleteLocal),
                "An unreadable forced selection in the target work with deleteLocal=" + deleteLocal);

        var invalid = Create(root, "outside-path");
        string outside = Path.Combine(invalid.Root, "outside-library.ass");
        File.Copy(invalid.Files[0], outside);
        var invalidShelf = Shelf(invalid.Library, title, 1);
        invalidShelf = invalidShelf with { Packages = invalidShelf.Packages.Select(package => package.Source == invalid.Target.Source && package.Id == invalid.Target.Id
            ? package with { Files = package.Files.Select(file => file.Path == invalid.Files[0] ? file with { Path = outside } : file).ToArray() } : package).ToArray() };
        File.WriteAllBytes(Index(invalid.Library, title, 1), JsonSerializer.SerializeToUtf8Bytes(invalidShelf));
        FailsUnchanged(invalid.Root, () => invalid.Library.Remove(title, outside, true), "A target record pointing outside the subtitle library");

        var wrongWork = Create(root, "wrong-work-path");
        string unindexed = Path.Combine(Path.GetDirectoryName(wrongWork.OtherWork)!, "unindexed.ass");
        File.WriteAllText(unindexed, "another work's unindexed subtitle");
        var wrongWorkShelf = Shelf(wrongWork.Library, title, 1);
        wrongWorkShelf = wrongWorkShelf with { Packages = wrongWorkShelf.Packages.Select(package => package.Source == wrongWork.Target.Source && package.Id == wrongWork.Target.Id
            ? package with { Files = package.Files.Select(file => file.Path == wrongWork.Files[0] ? file with { Path = unindexed } : file).ToArray() } : package).ToArray() };
        File.WriteAllBytes(Index(wrongWork.Library, title, 1), JsonSerializer.SerializeToUtf8Bytes(wrongWorkShelf));
        FailsUnchanged(wrongWork.Root, () => wrongWork.Library.Remove(title, unindexed, true),
            "A target record pointing to another work's unindexed file inside the library");
        return checks;
    }
}
