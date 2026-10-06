namespace IISMonitor.Core.Presentation;

public enum SeriesLinePattern
{
    Solid,
    Dashed,
    Dotted,
    DenselyDashed,
}

public sealed record SeriesStyle(string ColorHex, SeriesLinePattern Pattern);

/// <summary>
/// Colors for chart series. Eight hues in a fixed order, checked for color-blind separation between
/// neighbours. Slots past eight reuse the hues with a different line pattern rather than inventing
/// new, hard-to-tell-apart colors.
/// </summary>
public static class SeriesStyles
{
    public static readonly IReadOnlyList<string> Palette =
    [
        "#2a78d6", // blue
        "#eb6834", // orange
        "#1baf7a", // aqua
        "#eda100", // yellow
        "#e87ba4", // magenta
        "#008300", // green
        "#4a3aa7", // violet
        "#e34948", // red
    ];

    private static readonly SeriesLinePattern[] Patterns =
    [
        SeriesLinePattern.Solid,
        SeriesLinePattern.Dashed,
        SeriesLinePattern.Dotted,
        SeriesLinePattern.DenselyDashed,
    ];

    public static SeriesStyle ForSlot(int slot)
    {
        if (slot < 0)
            slot = 0;
        return new SeriesStyle(Palette[slot % Palette.Count], Patterns[slot / Palette.Count % Patterns.Length]);
    }
}
