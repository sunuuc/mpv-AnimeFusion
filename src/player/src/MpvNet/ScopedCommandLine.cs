using System.Text;

namespace MpvNet;

// Preserve mpv --{ ... --} scopes; never interpolate media into shell commands.
public sealed class ScopedCommandLine
{
    public sealed record Option(string Name, string Value);
    public sealed record Entry(string Path, List<Option> Options);
    public List<Option> GlobalOptions { get; } = [];
    public List<Entry> Entries { get; } = [];
    public List<Option> PromotedOptions { get; } = [];
    // Options of each empty --{ ... --} group, kept per group in order. The
    // caller may send several such groups, each one a separate playback item.
    public List<List<Option>> EmptyGroupOptions { get; } = [];
    public bool HasGroups { get; private set; }
    public int EmptyGroupCount { get; private set; }
    public int EmptyGroupOptionsPromoted { get; private set; }
    public bool LegacyScriptHandoff => EmptyGroupOptionsPromoted > 0 &&
        GlobalOptions.Any(o => BaseName(CanonicalName(o.Name)) is "scripts" or "script-opts");
    public bool NeedsDedicatedProcess => HasGroups || Entries.Any(entry => entry.Path == "-") ||
        GlobalOptions.Any(o => BaseName(CanonicalName(o.Name)) is
            "input-ipc-server" or "input-ipc-client" or "input-commands" or
            "scripts" or "script-opts" or "include" or "config-dir" or
            "sub-files" or "audio-files" or "external-files" or
            "force-media-title" or "start" or "http-header-fields" or
            "referrer" or "user-agent" or "playlist" or "playlist-start");

    public static string BaseName(string name)
    {
        foreach (string suffix in new[] { "-append", "-remove", "-toggle", "-add", "-set", "-pre", "-clr" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        return name;
    }

    static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) {
        "sub-file", "sub-files", "audio-file", "audio-files", "external-file",
        "external-files", "force-media-title", "title", "start", "end", "length",
        "sid", "aid", "vid", "slang", "alang", "sub-delay", "audio-delay",
        "http-header-fields", "referrer", "user-agent", "input-ipc-server",
        "input-ipc-client", "input-commands", "include", "config-dir", "input-conf", "script", "scripts", "script-opt",
        "script-opts", "playlist", "playlist-start", "profile", "log-file", "o"
    };

    public static ScopedCommandLine Parse(IEnumerable<string> arguments)
    {
        var result = new ScopedCommandLine();
        var args = arguments.ToArray();
        List<string>? groupFiles = null;
        List<Option>? groupOptions = null;
        bool literal = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Contains('\0')) throw new ArgumentException("启动参数包含无效字符。");
            if (!literal && arg == "--{")
            {
                if (groupFiles != null) throw new ArgumentException("不支持嵌套的 --{ 参数组。");
                result.HasGroups = true;
                groupFiles = []; groupOptions = [];
                continue;
            }
            if (!literal && arg == "--}")
            {
                if (groupFiles == null) throw new ArgumentException("--} 没有对应的 --{。");
                if (groupFiles.Count == 0)
                {
                    // Player UI uses an otherwise empty mpv scope as a transport
                    // envelope. Preserve its option order, but retain provenance
                    // so diagnostics and tests can distinguish it from globals.
                    result.GlobalOptions.AddRange(groupOptions!);
                    result.PromotedOptions.AddRange(groupOptions!);
                    result.EmptyGroupOptions.Add(new List<Option>(groupOptions!));
                    result.EmptyGroupCount++;
                    result.EmptyGroupOptionsPromoted += groupOptions!.Count;
                }
                else
                {
                    foreach (var path in groupFiles)
                        result.Entries.Add(new Entry(path, new List<Option>(groupOptions!)));
                }
                groupFiles = null; groupOptions = null;
                continue;
            }
            if (!literal && arg == "--") { literal = true; continue; }
            if (!literal && arg.StartsWith("--", StringComparison.Ordinal))
            {
                string body = arg[2..];
                int eq = body.IndexOf('=');
                string name = eq < 0 ? body : body[..eq];
                string value;
                if (eq >= 0) value = body[(eq + 1)..];
                else if (name.StartsWith("no-", StringComparison.Ordinal))
                { name = name[3..]; value = "no"; }
                else if (ValueOptions.Contains(BaseName(name)) && !name.EndsWith("-clr", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"选项 --{name} 缺少值；请使用 --{name}=值。");
                    value = args[++i];
                }
                else value = "yes";
                if (value.Contains('\0')) throw new ArgumentException("启动参数包含无效字符。");
                if (name.Length == 0) throw new ArgumentException("空的启动选项名称。");
                (groupOptions ?? result.GlobalOptions).Add(new Option(name, value));
            }
            else if (!string.IsNullOrEmpty(arg))
            {
                if (!literal && arg.StartsWith("-", StringComparison.Ordinal) && arg != "-")
                    throw new ArgumentException("不支持短格式启动选项；请使用 --选项=值，或先用 -- 结束选项。");
                if (groupFiles != null) groupFiles.Add(arg);
                else result.Entries.Add(new Entry(arg, []));
            }
        }
        if (groupFiles != null) throw new ArgumentException("--{ 参数组未以 --} 结束。");
        return result;
    }

    public static string CanonicalName(string name) => name switch {
        "script" => "scripts-append", "script-opt" => "script-opts-append",
        "sub-file" => "sub-files-append", "audio-file" => "audio-files-append",
        "external-file" => "external-files-append", _ => name
    };

    // mpv key/value lists require UTF-8 BYTE lengths, not UTF-16 character lengths.
    public static string Quote(string value) => $"%{Encoding.UTF8.GetByteCount(value)}%{value}";

    public static string FileOptions(IEnumerable<Option> options)
    {
        var normalized = new List<Option>();
        foreach (var raw in options)
        {
            string name = CanonicalName(raw.Name);
            bool fileAppend = name is "sub-files-append" or "audio-files-append" or "external-files-append";
            if (fileAppend)
            {
                name = name[..^7] + "-add";
                string item = raw.Value.Replace(";", "\\;", StringComparison.Ordinal);
                int n = normalized.FindLastIndex(o => o.Name == name);
                if (n >= 0)
                {
                    var old = normalized[n];
                    normalized[n] = new Option(name, old.Value + ";" + item);
                }
                else normalized.Add(new Option(name, item));
            }
            else
            {
                if (name is "sub-files" or "audio-files" or "external-files")
                    normalized.RemoveAll(o => o.Name == name + "-add");
                normalized.RemoveAll(o => o.Name == name);
                normalized.Add(new Option(name, raw.Value));
            }
        }
        return string.Join(",", normalized.Select(o => Quote(o.Name) + "=" + Quote(o.Value)));
    }
}
