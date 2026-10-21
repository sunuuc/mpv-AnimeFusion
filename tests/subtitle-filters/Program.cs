using MpvNet.Windows.Subtitles;
using System.IO;

static class Checks
{
    static int _checks;
    static void Check(bool success, string name)
    {
        if (!success) throw new Exception(name);
        _checks++;
    }

    static SubtitleResult Result(string language = "") => new(SubtitleSource.Assrt, "fixture", "Series", "", "", [])
        { Season = 2, LanguageCode = language };
    static SubtitleFile File(string name) => new(SubtitleSource.Assrt, "fixture", 0, name, "");

    static void Main()
    {
        var unknownChinese = Result("zh").Languages;
        Check(unknownChinese.Count == 1 && unknownChinese[0] == new SubtitleLanguage("zh", "中文"),
            "An unspecified Chinese source does not invent simplified or traditional metadata.");
        Check(Result("chi,zho,zh").Languages.Count == 1, "Equivalent unspecified Chinese codes form one filter.");
        Check(Result("zh-Hans,zh-Hant,zh-CN,zh-TW").Languages.Select(language => language.Code)
            .SequenceEqual(new[] { "zh-Hans", "zh-Hant" }), "Chinese scripts remain distinct while regional duplicates collapse.");
        Check(Result("ja,en,fr,zh,zh-Hant,zh-Hans,unrecognized").Languages.OrderBy(language => language.SortOrder)
            .Select(language => language.Code).SequenceEqual(new[] { "zh-Hans", "zh-Hant", "en", "ja", "zh", "fr", "other" }),
            "Available languages have the requested priority without adding absent languages.");
        Check(Result("en").Languages.Single().Code == "en", "English-only results do not add Chinese or Japanese filters.");
        foreach (var (regional, neutral) in new[] { ("ar-SA", "ar"), ("es-419", "es"), ("es-ES", "es"), ("fr-FR", "fr"), ("pt-BR", "pt"), ("en-US", "en"), ("ja-JP", "ja") })
            Check(SubtitleLanguage.Find(regional) == SubtitleLanguage.Find(neutral),
                $"The actual regional language {regional} retains its neutral language filter {neutral}.");
        Check(Result("es-419,es-ES,es").Languages.Single().Code == "es", "Regional versions share one language filter without becoming unknown.");
        Check(Result("ar-invalid,invalid-code,unrecognized").Languages.Single().Code == "other",
            "Unsupported or malformed language tags stay unknown rather than being guessed from their prefix.");
        Check(Result("zh-Hans,zh-Hant,zh-CN,zh-TW").Languages.Select(language => language.Code)
            .SequenceEqual(new[] { "zh-Hans", "zh-Hant" }), "Regional normalization preserves the Chinese script distinction.");
        Check(Result("zh-Hans").MatchesFilter(null, "zh-Hans", 2)
            && !Result("zh-Hans").MatchesFilter(null, "zh-Hant", 2), "Script filters select only their own results.");
        Check(!Result("zh").MatchesFilter(null, "zh-Hans", 2), "An unknown script does not match the simplified filter.");

        var single = Result("zh-Hans") with { Individual = true, Episode = 8 };
        Check(single.EpisodesFor(2).SequenceEqual(new[] { 8 }) && single.EpisodesFor(1).Count == 0,
            "Single-episode metadata is scoped to its actual season.");
        Check(single.MatchesFilter(SubtitleSource.Assrt, "zh-Hans", 2, 8)
            && !single.MatchesFilter(SubtitleSource.Assrt, "zh-Hans", 2, 7), "Episode filters retain only the selected episode.");
        Check(!single.MatchesFilter(SubtitleSource.Amenzb, "", null, 8), "Episode filtering preserves source filtering.");
        Check(single.EpisodesFor(2, "zh-Hans").SequenceEqual(new[] { 8 }) && single.EpisodesFor(2, "en").Count == 0,
            "A directly reported episode is available only for the result's advertised language.");

        var collection = Result() with
        {
            Collection = true, Season = null,
            Files = [File("Season 1/Series 01.chs.ass"), File("Season 1/Series 02.chs.ass"), File("Season 2/Series 01.chs.ass")],
            PackageNames = ["Season 1/Series 01.chs.ass", "Season 1/Series 02.chs.ass", "Season 2/Series 01.chs.ass"]
        };
        Check(collection.EpisodesFor(null).SequenceEqual(new[] { 1, 2 }), "All episodes are unique and ordered across a pack.");
        Check(collection.EpisodesFor(1).SequenceEqual(new[] { 1, 2 }) && collection.EpisodesFor(2).SequenceEqual(new[] { 1 }),
            "Pack episodes use their own folders' seasons.");
        Check(collection.MatchesFilter(null, "", 1, 2) && !collection.MatchesFilter(null, "", 2, 2),
            "The same episode number in another season cannot satisfy the selected season.");
        Check((Result() with { Files = [File("Series S02E03.chs.ass")], PackageNames = ["Series S02E04.cht.ass"] })
            .EpisodesFor(2).SequenceEqual(new[] { 3, 4 }), "Both supplied file records and package file lists contribute episodes.");
        Check((Result() with { Files = [File("Season 1/Series S02E03.chs.ass")] }).EpisodesFor(null).Count == 0,
            "Conflicting folder and filename seasons are not guessed.");
        Check(!new SubtitleQuery("Series", 2, 3, "").MatchesFile("Season 1/Series S02E03.chs.ass")
            && SubtitleLibrary.FileQuery(new("Series", 2, 3, ""), Result(), "Season 1/Series S02E03.chs.ass").Season == -1,
            "Season conflicts cannot be automatically loaded or assigned to a library season.");
        Check(SubtitleLibrary.FileQuery(new("Series", 1, 3, ""), Result(), "Season 1/Specials/Series S00E03.chs.ass").Season == 0,
            "An explicitly numbered special remains separate inside a regular season's archive wrapper.");
        Check((Result() with { Files = [File("Series 01-02.chs.ass"), File("Series S02E12.5.chs.ass")] }).EpisodesFor(2).Count == 0,
            "Ambiguous ranges and fractional specials are not invented as regular episode numbers.");
        Check((Result() with { PackageNames = ["Font S02E08.ttf", "Series S02E09.zip"] }).EpisodesFor(2).Count == 0,
            "Font and archive attachment names cannot create episode filters.");
        Check(Result().MatchesFilter(null, "", null) && !Result().MatchesFilter(null, "", null, 1),
            "All preserves results with unknown episodes while a specific episode requires evidence.");
        Check((single with { Season = null }).EpisodesFor(null).SequenceEqual(new[] { 8 })
            && (single with { Season = null }).EpisodesFor(2).Count == 0, "An unknown season stays unknown.");
        var differentLanguages = Result("zh-Hans,zh-Hant,en") with
        {
            Collection = true,
            Files = [File("Series S02E01.chs.ass"), File("Series S02E02.en.ass"), File("Series S02E03.cht.ass"), File("Series S02E04.ass")]
        };
        Check(differentLanguages.EpisodesFor(2, "zh-Hans").SequenceEqual(new[] { 1 })
            && differentLanguages.EpisodesFor(2, "en").SequenceEqual(new[] { 2 })
            && differentLanguages.EpisodesFor(2, "zh-Hant").SequenceEqual(new[] { 3 }),
            "A multilingual package exposes only episodes with files in the selected language.");
        Check(differentLanguages.EpisodesFor(2).SequenceEqual(new[] { 1, 2, 3, 4 })
            && !differentLanguages.MatchesFilter(null, "zh-Hans", 2, 2)
            && differentLanguages.MatchesFilter(null, "en", 2, 2),
            "All retains unclassified files while a language-and-episode filter cannot borrow another language's episode.");
        Check((Result("en") with { PackageNames = ["Series S02E05.ass", "Series S02E06.chs.ass"] })
            .EpisodesFor(2, "en").SequenceEqual(new[] { 5 }),
            "Unmarked files may use one confirmed package language, while explicit file language wins.");
        Check((Result("zh-Hans,en") with { PackageNames = ["Series S02E05.ass"] }).EpisodesFor(2, "en").Count == 0,
            "A multilingual package does not guess the language of an unmarked episode file.");

        string root = Path.Combine(Path.GetTempPath(), "animefusion-subtitle-filters-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new SubtitleLibrary(root);
            var query = new SubtitleQuery("Series", 2, 1, "");
            var multilingual = Result("zh-Hans,zh-Hant,en");
            var files = new[] { File("Series S02E01.chs.ass"), File("Series S02E01.cht.ass"), File("Series S02E01.en.ass") };
            foreach (var file in files)
            {
                string path = library.FilePath(query, multilingual, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, "[Script Info]");
                library.Save(query, multilingual, file, path, true);
            }
            var saved = library.Results(query.Title).Single();
            Check(saved.Languages.Select(language => language.Code).ToHashSet().SetEquals(new[] { "zh-Hans", "zh-Hant", "en" }),
                "Saved file language markers preserve separate Chinese filters.");
            Check(saved.EpisodesFor(2).SequenceEqual(new[] { 1 }) && saved.EpisodesFor(1).Count == 0,
                "Local subtitle results expose their stored season and episode.");
            Check(saved.EpisodesFor(2, "zh-Hans").SequenceEqual(new[] { 1 })
                && saved.EpisodesFor(2, "en").SequenceEqual(new[] { 1 }) && saved.EpisodesFor(2, "ja").Count == 0,
                "Local episode filters use the actual saved file languages.");
            Check(library.CurrentFile(query with { Language = "zh-Hans" }, saved, saved.Files)?.Name.EndsWith(".chs.ass") == true,
                "A simplified selection loads the simplified file from a bilingual package.");
            Check(library.CurrentFile(query with { Language = "zh-Hant" }, saved, saved.Files)?.Name.EndsWith(".cht.ass") == true,
                "A traditional selection loads the traditional file from a bilingual package.");
            Check(library.CurrentFile(query with { Language = "en" }, saved, saved.Files)?.Name.EndsWith(".en.ass") == true,
                "English selection remains intact after Chinese script classification.");
            Check(library.CurrentFile(query with { Language = "zh" }, saved, saved.Files)?.Name.EndsWith(".chs.ass") == true,
                "The generic Chinese download preference can select a known Chinese script.");

            var unnamed = Result("zh") with { Id = "unspecified", Individual = true, Episode = 1 };
            var anonymousFile = File("subtitle.ass") with { ResultId = unnamed.Id };
            Check(library.CurrentFile(query with { Language = "zh-Hans" }, unnamed, [anonymousFile]) == null,
                "A file with unspecified Chinese metadata cannot pretend to satisfy a script preference.");
            Check(library.CurrentFile(query with { Language = "zh" }, unnamed, [anonymousFile]) == anonymousFile,
                "An unspecified Chinese file remains usable with its own language category.");
            string unnamedPath = library.FilePath(query, unnamed, anonymousFile.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(unnamedPath)!);
            System.IO.File.WriteAllText(unnamedPath, "[Script Info]");
            library.Save(query, unnamed, anonymousFile, unnamedPath, true);
            var savedUnnamed = library.Results(query.Title).Single(result => result.Id == unnamed.Id);
            Check(savedUnnamed.EpisodesFor(2).SequenceEqual(new[] { 1 }),
                "Stored episode metadata remains available even when a filename contains no episode.");

            var firstSeason = multilingual with { Id = "multi-season", Collection = true, Season = null, PackageNames = ["Series S01E01.chs.ass", "Series S02E02.chs.ass"] };
            foreach (var (season, episode) in new[] { (1, 1), (2, 2) })
            {
                var file = File($"Series S{season:00}E{episode:00}.chs.ass") with { ResultId = firstSeason.Id };
                var position = query with { Season = season, Episode = episode };
                string path = library.FilePath(position, firstSeason, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, "[Script Info]");
                library.Save(position, firstSeason, file, path, true);
            }
            var savedCollection = library.Results(query.Title).Single(result => result.Id == firstSeason.Id);
            Check(savedCollection.EpisodesFor(1).SequenceEqual(new[] { 1 }) && savedCollection.EpisodesFor(2).SequenceEqual(new[] { 2 }),
                "Grouping local shelves keeps episode positions attached to the original season.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine($"Subtitle language/episode checks passed: {_checks}; no network or desktop interaction.");
    }
}
