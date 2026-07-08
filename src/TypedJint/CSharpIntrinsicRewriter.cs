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
        ("console.log(", "Console.WriteLine("),
        ("console.info(", "Console.WriteLine("),
        ("console.debug(", "Console.WriteLine("),
        ("console.warn(", "Console.WriteLine("),
        ("console.error(", "Console.Error.WriteLine("),
        ("console.write(", "Console.Write("),
        ("console.writeLine(", "Console.WriteLine("),
        ("net.getString(", "JavaScriptNetwork.Instance.getString("),
        ("net.getBytes(", "JavaScriptNetwork.Instance.getBytes("),
        ("net.postString(", "JavaScriptNetwork.Instance.postString("),
        ("encoding.base64Encode(", "JavaScriptEncoding.Instance.base64Encode("),
        ("encoding.base64Decode(", "JavaScriptEncoding.Instance.base64Decode("),
        ("encoding.uriEncode(", "JavaScriptEncoding.Instance.uriEncode("),
        ("encoding.uriDecode(", "JavaScriptEncoding.Instance.uriDecode("),
        ("encoding.utf8ByteCount(", "JavaScriptEncoding.Instance.utf8ByteCount("),
        ("json.stringify(", "JavaScriptJson.Instance.stringify("),
        ("time.nowUnixMilliseconds(", "JavaScriptTime.Instance.nowUnixMilliseconds("),
        ("time.utcNowIsoString(", "JavaScriptTime.Instance.utcNowIsoString("),
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
