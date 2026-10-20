using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MpvNet;
using MpvNet.Native;
using static MpvNet.Native.LibMpv;

static class Checks
{
    static int checks;

    static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        checks++;
    }

    static (string Output, string Error) Capture(Action action)
    {
        TextWriter output = Console.Out, error = Console.Error;
        using var capturedOutput = new StringWriter();
        using var capturedError = new StringWriter();
        try
        {
            Console.SetOut(capturedOutput);
            Console.SetError(capturedError);
            action();
            return (capturedOutput.ToString(), capturedError.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    static void Silent(Action action, string message)
    {
        var captured = Capture(action);
        Check(captured.Output == "" && captured.Error == "", message);
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text + '\0');

    static void Main()
    {
        string native = Environment.GetEnvironmentVariable("ANIMEFUSION_NATIVE_DIRECTORY")
            ?? throw new Exception("ANIMEFUSION_NATIVE_DIRECTORY must identify the player native runtime.");
        Check(Path.IsPathFullyQualified(native) && File.Exists(Path.Combine(native, "libmpv-2.dll")),
            "The test uses an explicitly selected real player native runtime.");
        NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, (name, _, _) =>
            File.Exists(Path.Combine(native, name)) ? NativeLibrary.Load(Path.Combine(native, name)) : IntPtr.Zero);
        string temporary = Path.Combine(Path.GetTempPath(), "AnimeFusion-NativeProperties-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("ANIMEVE_HOME", temporary);
        Environment.SetEnvironmentVariable("_started_from_console", null);
        nint core = 0;
        try
        {
            Check(Path.GetFullPath(Global.Player.ConfigFolder).TrimEnd(Path.DirectorySeparatorChar) == temporary,
                "Configuration and diagnostics are isolated from the installed player.");
            Silent(Global.App.Init, "Initializing the app produces no launcher notifications.");
            Check(!Global.App.DebugMode && !Global.App.IsTerminalAttached,
                "Diagnostic builds do not enable console debugging for a launcher-started player.");
            Check(Global.App.ProcessProperty("debug-mode", "yes") && Global.App.DebugMode,
                "The explicit debug option remains available.");
            Check(Global.App.ProcessProperty("debug-mode", "no") && !Global.App.DebugMode,
                "The explicit debug option can be disabled independently of diagnostics.");
            core = mpv_create();
            Check(core != 0, "The real libmpv core was created.");
            foreach (var option in new Dictionary<string, string>
            {
                ["config"] = "no", ["load-scripts"] = "no", ["terminal"] = "no", ["input-terminal"] = "no",
                ["vo"] = "null", ["ao"] = "null", ["idle"] = "yes", ["force-window"] = "no"
            })
                Check(mpv_set_option_string(core, Bytes(option.Key), Bytes(option.Value)) >= 0,
                    "An isolated, windowless native option was accepted: " + option.Key);
            Check(mpv_initialize(core) == mpv_error.MPV_ERROR_SUCCESS,
                "The real native core initializes without opening media or a desktop window.");
            var client = new MpvClient { Handle = core };
            const string unknown = "animefusion-test-property-does-not-exist";
            var failures = new List<(string Name, mpv_format Format, mpv_error Error)>();

            mpv_error NativeError(string name, mpv_format format)
            {
                mpv_error error = mpv_get_property(core, Bytes(name), format, out nint value);
                Check(error < 0, "The native fixture must produce an actual read failure: " + name);
                failures.Add((name, format, error));
                return error;
            }

            Check(NativeError(unknown, mpv_format.MPV_FORMAT_STRING) == mpv_error.MPV_ERROR_PROPERTY_NOT_FOUND,
                "An unknown native property remains distinct from a temporarily unavailable property.");
            foreach (var format in new[] { mpv_format.MPV_FORMAT_FLAG, mpv_format.MPV_FORMAT_INT64,
                mpv_format.MPV_FORMAT_DOUBLE, mpv_format.MPV_FORMAT_NODE, mpv_format.MPV_FORMAT_OSD_STRING })
                Check(NativeError(unknown, format) == mpv_error.MPV_ERROR_PROPERTY_NOT_FOUND,
                    "The native missing-property result is retained for each supported getter format.");
            Check(NativeError("time-pos", mpv_format.MPV_FORMAT_DOUBLE) == mpv_error.MPV_ERROR_PROPERTY_UNAVAILABLE,
                "An idle core reports an unavailable playback position rather than a media failure.");
            Check(NativeError("mpv-version", mpv_format.MPV_FORMAT_INT64) == mpv_error.MPV_ERROR_PROPERTY_FORMAT,
                "An incompatible native property type retains the actual format error.");

            Silent(() =>
            {
                Check(client.GetPropertyString(unknown) == "", "A missing string returns the empty value.");
                Check(client.GetPropertyDouble("time-pos") == 0, "An unavailable playback position returns zero.");
                Check(client.GetPropertyInt("mpv-version") == 0, "An invalid integer read returns zero.");
                Check(!client.GetPropertyBool(unknown), "A missing flag returns false.");
                Check(client.GetPropertyInt(unknown) == 0, "A missing integer returns zero.");
                Check(client.GetPropertyLong(unknown) == 0, "A missing long integer returns zero.");
                Check(client.GetPropertyDouble(unknown) == 0, "A missing floating-point property returns zero.");
                Check(client.GetPropertyStringArray(unknown).Length == 0, "A missing array returns an empty array.");
                Check(client.GetPropertyOsdString(unknown) == "", "A missing OSD string returns an empty value.");
                Terminal.WriteError("native-property-terminal-error");
                Terminal.Write("native-property-terminal-message");
            }, "Normal reads and diagnostic messages never write to launcher stdout or stderr.");

            Global.App.DebugMode = true;
            Silent(() =>
            {
                foreach (string name in new[]
                {
                    "metadata/by-key/tmdb_id", "metadata/by-key/tvdb_id", "metadata/by-key/bangumi_id",
                    "metadata/by-key/mal_id", "metadata/by-key/anilist_id", "metadata/by-key/imdb_id",
                    "current-tracks/sub/external-filename"
                })
                {
                    NativeError(name, mpv_format.MPV_FORMAT_STRING);
                    Check(client.GetPropertyString(name, false) == "", "Optional metadata is empty when not provided.");
                }
                Check(client.GetPropertyDouble("time-pos", false) == 0,
                    "Optional reads can suppress unavailable playback positions in explicit debug mode.");
            }, "Optional metadata and track reads remain silent even with explicit console debugging.");

            var required = Capture(() => client.GetPropertyString(unknown));
            var wrongType = Capture(() => client.GetPropertyInt("mpv-version"));
            var terminal = Capture(() =>
            {
                Terminal.WriteError("explicit-debug-error");
                Terminal.Write("explicit-debug-message");
            });
            if (Diagnostics.Enabled)
            {
                Check(required.Error.Contains(unknown) && required.Error.Contains(
                    Marshal.PtrToStringUTF8(mpv_error_string(mpv_error.MPV_ERROR_PROPERTY_NOT_FOUND))!),
                    "Explicit debugging retains a required unknown-property error on stderr.");
                Check(wrongType.Error.Contains("mpv-version") && wrongType.Error.Contains(
                    Marshal.PtrToStringUTF8(mpv_error_string(mpv_error.MPV_ERROR_PROPERTY_FORMAT))!),
                    "Explicit debugging retains a required incompatible-format error on stderr.");
                Check(terminal.Error.Contains("explicit-debug-error") && terminal.Output.Contains("explicit-debug-message"),
                    "Explicit debugging preserves intentional stdout and stderr output.");
                string log = Directory.GetFiles(Path.Combine(temporary, "logs"), "*.jsonl").Single();
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var events = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                foreach (var failure in failures)
                    Check(events.Any(entry => entry.GetProperty("operation").GetString() == "player.property"
                        && entry.GetProperty("event").GetString() == "read-failed"
                        && entry.GetProperty("data").GetProperty("name").GetString() == failure.Name
                        && entry.GetProperty("data").GetProperty("error").GetInt32() == (int)failure.Error
                        && entry.GetProperty("data").GetProperty("format").GetString() == failure.Format.ToString()),
                        "Silent native failures retain the real property, format and native error in diagnostics.");
                Check(events.Any(entry => entry.GetProperty("operation").GetString() == "terminal"
                    && entry.GetProperty("data").TryGetProperty("text", out var text)
                    && text.GetString() == "native-property-terminal-error"),
                    "Quiet normal terminal errors remain in the detailed test log.");
                string? artifact = Environment.GetEnvironmentVariable("ANIMEFUSION_TEST_ARTIFACT_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(artifact))
                {
                    Check(Path.IsPathFullyQualified(artifact), "The diagnostic artifact path must be explicit.");
                    Directory.CreateDirectory(artifact);
                    File.Copy(log, Path.Combine(artifact, "native-property-diagnostics.jsonl"), true);
                }
            }
            else
            {
                Check(required.Output == "" && required.Error == "" && wrongType.Output == "" && wrongType.Error == ""
                    && terminal.Output == "" && terminal.Error == "",
                    "A production build emits no diagnostic console messages even with debug requested.");
                Check(!Directory.Exists(Path.Combine(temporary, "logs")),
                    "A production build creates no diagnostic logs or log directory.");
            }
            Check(client.GetPropertyString("mpv-version").Length > 0,
                "Normal native string reads remain successful.");
            Check(client.GetPropertyStringArray("property-list").Contains("time-pos"),
                "Normal native arrays retain the property list.");
            client.SetPropertyBool("pause", true);
            Check(client.GetPropertyBool("pause"), "Normal native flags retain their value.");
            Check(client.GetPropertyInt("volume") == 100 && client.GetPropertyLong("volume") == 100
                && client.GetPropertyDouble("volume") == 100 && client.GetPropertyOsdString("volume").Length > 0,
                "Normal native numeric and display reads remain successful.");
            Check(Global.Player.Handle == 0 && Global.Player.MainHandle == 0,
                "The formal player and its UI were never initialized.");
            Console.WriteLine($"PASS {checks} native property and launcher-output checks; diagnostics={Diagnostics.Enabled}; no UI, media or network.");
        }
        finally
        {
            Global.App.DebugMode = false;
            if (core != 0) mpv_destroy(core);
            // The unique directory was created by this test; it contains no user files.
            Directory.Delete(temporary, true);
        }
    }
}
