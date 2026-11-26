namespace AnimeJaNai.Components;

public static class ComponentSize
{
    private static (long Divisor, string Unit) DisplayUnit(long bytes) => bytes switch
    {
        < 1024 => (1, "B"),
        < 1024 * 1024 => (1024, "KB"),
        _ => (1024 * 1024, "MB")
    };

    public static string Format(long bytes)
    {
        var (divisor, unit) = DisplayUnit(bytes);
        return $"{bytes / divisor:N0} {unit}";
    }

    public static string FormatProgress(long received, long total)
    {
        var (divisor, unit) = DisplayUnit(total);
        if (received > 0 && received < divisor)
            return $"{Format(received)} / {Format(total)}";

        return $"{received / divisor:N0} / {total / divisor:N0} {unit}";
    }
}
