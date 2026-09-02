namespace PappaETStats.Server.Util;

public static class EtColorCodes
{
    // Source: https://etconfig.net/et-color-codes/et-color-codes/
    // ET uses 32 color codes. The engine effectively maps the color code character
    // to an index 0..31 using: (codeChar - '0') & 31.
    private static readonly string[] Palette =
    [
        "#000000", // 0
        "#ff0000", // 1
        "#00ff00", // 2
        "#ffff00", // 3
        "#0000ff", // 4
        "#00ffff", // 5
        "#ff00ff", // 6
        "#ffffff", // 7
        "#ff7f00", // 8
        "#7f7f7f", // 9
        "#bfbfbf", // 10
        "#bfbfbf", // 11
        "#007f00", // 12
        "#7f7f00", // 13
        "#00007f", // 14
        "#7f0000", // 15
        "#7f3f00", // 16
        "#ff9919", // 17
        "#007f7f", // 18
        "#7f007f", // 19
        "#007fff", // 20
        "#7f00ff", // 21
        "#3399cc", // 22
        "#ccffcc", // 23
        "#006633", // 24
        "#ff0033", // 25
        "#b21919", // 26
        "#993300", // 27
        "#cc9933", // 28
        "#999933", // 29
        "#ffffbf", // 30
        "#ffff7f", // 31
    ];

    public sealed record Segment(string CssColor, string Text);

    /// <summary>
    /// Parses ET color-coded text (e.g. "^1Red ^7Normal") into segments.
    /// Razor will HTML-encode <see cref="Segment.Text"/> automatically, so this stays safe.
    /// </summary>
    public static IReadOnlyList<Segment> Parse(string? text, string? defaultCssColor = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<Segment>();
        }

        // Use MudBlazor theme variable so "normal" (^7) stays readable in light+dark themes.
        var themeText = "var(--mud-palette-text-primary)";
        var currentColor = string.IsNullOrWhiteSpace(defaultCssColor) ? themeText : defaultCssColor!;

        var segments = new List<Segment>();
        var buffer = new System.Text.StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            segments.Add(new Segment(currentColor, buffer.ToString()));
            buffer.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '^')
            {
                if (i + 1 >= text.Length)
                {
                    buffer.Append('^');
                    continue;
                }

                var code = text[i + 1];

                // "^^" is a literal caret.
                if (code == '^')
                {
                    buffer.Append('^');
                    i++;
                    continue;
                }

                // Interpret any next char as a color code.
                Flush();
                currentColor = CssColorForEtCode(code, themeText);
                i++; // skip the code char
                continue;
            }

            buffer.Append(c);
        }

        Flush();
        return segments;
    }

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '^')
            {
                if (i + 1 >= text.Length)
                {
                    buffer.Append('^');
                    continue;
                }

                var code = text[i + 1];
                if (code == '^')
                {
                    buffer.Append('^');
                }

                // Skip color code.
                i++;
                continue;
            }

            buffer.Append(c);
        }

        return buffer.ToString();
    }

    private static string CssColorForEtCode(char codeChar, string themeText)
    {
        // Index = (codeChar - '0') & 31  (per ET/Q3-style engine behavior).
        var idx = (((int)codeChar) - '0') & 31;

        // Treat ^7 (white) as "normal text" so it stays readable on Mud light theme.
        if (idx == 7)
        {
            return themeText;
        }

        return Palette[idx];
    }
}
