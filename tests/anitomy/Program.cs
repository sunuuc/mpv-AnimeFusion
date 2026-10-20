using System.Text.Json;
using AnitomySharp;
using AnimeParser = AnitomySharp.AnitomySharp;
using Category = AnitomySharp.Element.ElementCategory;

var failures = new List<Failure>();
var checks = 0;
var inputs = new HashSet<string>(StringComparer.Ordinal);

void Check(bool condition, string name, string input, string detail = "")
{
    checks++;
    if (!condition)
        failures.Add(new(name, input, detail));
}

void Tokenize(string input)
{
    try
    {
        var tokens = new List<Token>();
        new Tokenizer(input, new List<Element>(), new Options(), tokens).Tokenize();
        Check(tokens.All(token => !string.IsNullOrEmpty(token.Content)),
            "nonempty-token", input,
            string.Join(", ", tokens.Select(token => $"{token.Category}:{JsonSerializer.Serialize(token.Content)}")));
    }
    catch (Exception exception)
    {
        Check(false, "tokenize-exception", input, exception.ToString());
    }
}

List<Element> Parse(string input)
{
    try
    {
        var result = AnimeParser.Parse(input).ToList();
        Check(true, "parse-no-exception", input);
        return result;
    }
    catch (Exception exception)
    {
        Check(false, "parse-exception", input, exception.ToString());
        return null;
    }
}

void Exercise(string input)
{
    if (!inputs.Add(input))
        return;
    Tokenize(input);
    Parse(input);
}

void Preserve(string input, string title, string episode)
{
    Exercise(input);
    var parsed = Parse(input);
    if (parsed == null)
        return;
    Check(parsed.Any(element => element.Category == Category.ElementAnimeTitle && element.Value == title),
        "anime-title", input, string.Join("; ", parsed.Select(element => $"{element.Category}={element.Value}")));
    Check(parsed.Any(element => element.Category == Category.ElementEpisodeNumber && element.Value == episode),
        "episode-number", input, string.Join("; ", parsed.Select(element => $"{element.Category}={element.Value}")));
}

Preserve("[Group] Anime 01.ass", "Anime", "01");
Preserve("[Group] Anime 01_.ass", "Anime", "01");
Preserve("[Group] Anime 01 .ass", "Anime", "01");
Preserve("[Group] Anime 01 [1080p].ass", "Anime", "01");
Preserve("[Group] Anime - 02 - Episode Title.ass", "Anime", "02");
Preserve("[字幕组] 草莓棉花糖 02 [1080p].ass", "草莓棉花糖", "02");
foreach (var bracket in new[] { "[]", "()", "{}", "【】", "（）", "[", "(", "【" })
    Preserve("[Group] Anime 01 " + bracket + ".ass", "Anime", "01");

string[] suffixes = ["", " ", "_", ".", "&", "+", ",", "|", " _", "_.", "  ", "__"];
string[] prefixes = ["", "[Group] ", "(Group) ", "{Group} ", "【字幕组】 ", "（字幕组） "];
string[] bracketTails = ["", "[]", "()", "{}", "「」", "【】", "（）", "[", "(", "{", "「", "【", "（", "]", ")", "}", "」", "】", "）", "[1080p]", "[1080p", "[[]]", "[(]", "[] Anime"];

foreach (var prefix in prefixes)
foreach (var tail in bracketTails)
foreach (var suffix in suffixes)
{
    var filename = prefix + "Anime 01" + tail + suffix;
    Exercise(filename);
    Exercise(filename + ".ass");
}

foreach (var suffix in suffixes)
{
    if (suffix.Length <= 1)
        Preserve("[Group] Anime 01" + suffix + ".ass", "Anime", "01");
    Exercise("[Group] Anime 01 - Episode Title" + suffix + ".ass");
}

foreach (var input in new[] { "", " ", "_", "[]", "[", "]", "()", "[Group] ", "[Group]_", "[Group]_.ass", "[Group] Anime 01_[1080p]_", "[Group] Anime 01_[1080p]_.ass", ".A Fixture 01.ass", "_A Fixture 01.ass", " .A Fixture 01.ass", "_.A Fixture 01.ass" })
    Exercise(input);

var report = new
{
    Inputs = inputs.Count,
    Checks = checks,
    Failures = failures.Count,
    Exceptions = failures.Count(failure => failure.Name.EndsWith("exception", StringComparison.Ordinal)),
    Details = failures
};
if (args.Length > 0)
{
    var destination = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllText(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"Parser checks: {checks}; inputs: {inputs.Count}; failures: {failures.Count}; exceptions: {report.Exceptions}");
foreach (var failure in failures.Take(10))
    Console.WriteLine($"{failure.Name}: {JsonSerializer.Serialize(failure.Input)}: {failure.Detail.Split(Environment.NewLine)[0]}");
return failures.Count == 0 ? 0 : 1;

record Failure(string Name, string Input, string Detail);
