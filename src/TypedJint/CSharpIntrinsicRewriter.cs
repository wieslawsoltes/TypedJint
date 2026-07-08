namespace TypedJint;

public static class CSharpIntrinsicRewriter
{
    private static readonly (string Source, string Target)[] Replacements =
    [
        ("Math.abs(", "Math.Abs("),
        ("Math.sqrt(", "Math.Sqrt("),
        ("Math.pow(", "Math.Pow("),
        ("Math.min(", "Math.Min("),
        ("Math.max(", "Math.Max("),
        ("Math.floor(", "Math.Floor("),
        ("Math.ceil(", "Math.Ceiling("),
        ("Math.round(", "Math.Round("),
        ("Math.sin(", "Math.Sin("),
        ("Math.cos(", "Math.Cos("),
        ("Math.tan(", "Math.Tan("),
        ("Math.log(", "Math.Log("),
        ("Math.exp(", "Math.Exp("),
        (".length", ".Length")
    ];

    public static string Rewrite(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = source;
        foreach (var replacement in Replacements)
        {
            result = result.Replace(replacement.Source, replacement.Target, StringComparison.Ordinal);
        }

        return result;
    }
}
