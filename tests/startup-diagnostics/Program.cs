using System.Runtime.CompilerServices;
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
        try
        {
            Check(Path.GetFullPath(Global.Player.ConfigFolder).TrimEnd(Path.DirectorySeparatorChar) == temporary,
                "All diagnostic writes must use the isolated test folder.");
            StartupDiagnostics.Begin();
            Exception original = Original();
            StartupDiagnostics.Failed(original);
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
            // The target is the unique temporary folder created by this test.
            Directory.Delete(temporary, true);
        }
    }
}
