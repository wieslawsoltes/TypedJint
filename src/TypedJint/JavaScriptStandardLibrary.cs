using System.Net;
using System.Text;
using System.Text.Json;

namespace TypedJint;

public static class JavaScriptStandardLibraryExtensions
{
    public static TypedJintEngine RegisterStandardLibrary(this TypedJintEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.SetValue("Math", JavaScriptMath.Instance);
        engine.SetValue("console", JavaScriptConsole.Instance);
        engine.SetValue("net", JavaScriptNetwork.Instance);
        engine.SetValue("encoding", JavaScriptEncoding.Instance);
        engine.SetValue("json", JavaScriptJson.Instance);
        engine.SetValue("time", JavaScriptTime.Instance);
        return engine;
    }

    public static JavaScriptRuntimeEngine RegisterStandardLibrary(this JavaScriptRuntimeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.SetValue("console", JavaScriptConsole.Instance);
        engine.SetValue("net", JavaScriptNetwork.Instance);
        engine.SetValue("encoding", JavaScriptEncoding.Instance);
        engine.SetValue("json", JavaScriptJson.Instance);
        engine.SetValue("time", JavaScriptTime.Instance);
        return engine;
    }
}

public sealed class JavaScriptMath
{
    public static readonly JavaScriptMath Instance = new();

    private JavaScriptMath()
    {
    }

    public double PI => Math.PI;
    public double E => Math.E;
    public double abs(double value) => Math.Abs(value);
    public double sqrt(double value) => Math.Sqrt(value);
    public double pow(double x, double y) => Math.Pow(x, y);
    public double min(double x, double y) => Math.Min(x, y);
    public double max(double x, double y) => Math.Max(x, y);
    public double floor(double value) => Math.Floor(value);
    public double ceil(double value) => Math.Ceiling(value);
    public double round(double value) => Math.Round(value);
    public double sin(double value) => Math.Sin(value);
    public double cos(double value) => Math.Cos(value);
    public double tan(double value) => Math.Tan(value);
    public double log(double value) => Math.Log(value);
    public double exp(double value) => Math.Exp(value);
}

public sealed class JavaScriptConsole
{
    public static readonly JavaScriptConsole Instance = new();

    private JavaScriptConsole()
    {
    }

    public void log() => Console.WriteLine();
    public void log(object? value) => Console.WriteLine(Format(value));
    public void log(object? a, object? b) => Console.WriteLine(Join(a, b));
    public void log(object? a, object? b, object? c) => Console.WriteLine(Join(a, b, c));
    public void log(object? a, object? b, object? c, object? d) => Console.WriteLine(Join(a, b, c, d));

    public void info() => log();
    public void info(object? value) => log(value);
    public void info(object? a, object? b) => log(a, b);
    public void info(object? a, object? b, object? c) => log(a, b, c);
    public void info(object? a, object? b, object? c, object? d) => log(a, b, c, d);

    public void debug() => log();
    public void debug(object? value) => log(value);
    public void debug(object? a, object? b) => log(a, b);
    public void debug(object? a, object? b, object? c) => log(a, b, c);
    public void debug(object? a, object? b, object? c, object? d) => log(a, b, c, d);

    public void warn() => log();
    public void warn(object? value) => log(value);
    public void warn(object? a, object? b) => log(a, b);
    public void warn(object? a, object? b, object? c) => log(a, b, c);
    public void warn(object? a, object? b, object? c, object? d) => log(a, b, c, d);

    public void error() => Console.Error.WriteLine();
    public void error(object? value) => Console.Error.WriteLine(Format(value));
    public void error(object? a, object? b) => Console.Error.WriteLine(Join(a, b));
    public void error(object? a, object? b, object? c) => Console.Error.WriteLine(Join(a, b, c));
    public void error(object? a, object? b, object? c, object? d) => Console.Error.WriteLine(Join(a, b, c, d));

    public void write(object? value) => Console.Write(Format(value));
    public void writeLine(object? value) => Console.WriteLine(Format(value));
    public void clear() => Console.Clear();

    private static string Join(params object?[] values) => string.Join(" ", values.Select(Format));
    private static string Format(object? value) => value?.ToString() ?? "null";
}

public sealed class JavaScriptNetwork
{
    public static readonly JavaScriptNetwork Instance = new();
    private static readonly HttpClient Http = new();

    private JavaScriptNetwork()
    {
    }

    public string getString(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUri(uri);
        }

        return Http.GetStringAsync(uri).GetAwaiter().GetResult();
    }

    public byte[] getBytes(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetBytes(DecodeDataUri(uri));
        }

        return Http.GetByteArrayAsync(uri).GetAwaiter().GetResult();
    }

    public string postString(string uri, string content) => postString(uri, content, "text/plain");

    public string postString(string uri, string content, string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        using var httpContent = new StringContent(content, Encoding.UTF8, mediaType);
        using var response = Http.PostAsync(uri, httpContent).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static string DecodeDataUri(string uri)
    {
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            throw new FormatException("Invalid data URI.");
        }

        var metadata = uri[5..comma];
        var data = uri[(comma + 1)..];
        return metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(data))
            : WebUtility.UrlDecode(data);
    }
}

public sealed class JavaScriptEncoding
{
    public static readonly JavaScriptEncoding Instance = new();

    private JavaScriptEncoding()
    {
    }

    public string base64Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    public string base64Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    public string uriEncode(string value) => Uri.EscapeDataString(value);
    public string uriDecode(string value) => Uri.UnescapeDataString(value);
    public double utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);
}

public sealed class JavaScriptJson
{
    public static readonly JavaScriptJson Instance = new();

    private JavaScriptJson()
    {
    }

    public string stringify(object? value) => JsonSerializer.Serialize(value);
}

public sealed class JavaScriptTime
{
    public static readonly JavaScriptTime Instance = new();

    private JavaScriptTime()
    {
    }

    public double nowUnixMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string utcNowIsoString() => DateTimeOffset.UtcNow.ToString("O");
}
