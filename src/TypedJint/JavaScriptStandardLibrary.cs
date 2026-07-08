using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        engine.SetValue("JSON", JavaScriptJson.Instance);
        engine.SetValue("time", JavaScriptTime.Instance);
        
        // Register Global Functions
        engine.SetValue("fetch", new Func<string, JavaScriptResponse>(JavaScriptStandardLibrary.Fetch));
        engine.SetValue("setTimeout", new Func<Action, double, double>(JavaScriptStandardLibrary.setTimeout));
        engine.SetValue("clearTimeout", new Action<double>(JavaScriptStandardLibrary.clearTimeout));
        engine.SetValue("setInterval", new Func<Action, double, double>(JavaScriptStandardLibrary.setInterval));
        engine.SetValue("clearInterval", new Action<double>(JavaScriptStandardLibrary.clearInterval));

        return engine;
    }

    public static JavaScriptRuntimeEngine RegisterStandardLibrary(this JavaScriptRuntimeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.SetValue("console", JavaScriptConsole.Instance);
        engine.SetValue("net", JavaScriptNetwork.Instance);
        engine.SetValue("encoding", JavaScriptEncoding.Instance);
        engine.SetValue("json", JavaScriptJson.Instance);
        engine.SetValue("JSON", JavaScriptJson.Instance);
        engine.SetValue("time", JavaScriptTime.Instance);

        // Register Global Functions
        engine.SetValue("fetch", new Func<string, JavaScriptResponse>(JavaScriptStandardLibrary.Fetch));
        engine.SetValue("setTimeout", new Func<Action, double, double>(JavaScriptStandardLibrary.setTimeout));
        engine.SetValue("clearTimeout", new Action<double>(JavaScriptStandardLibrary.clearTimeout));
        engine.SetValue("setInterval", new Func<Action, double, double>(JavaScriptStandardLibrary.setInterval));
        engine.SetValue("clearInterval", new Action<double>(JavaScriptStandardLibrary.clearInterval));

        return engine;
    }
}

public static class JavaScriptStandardLibrary
{
    private static readonly HttpClient Http = new();
    private static int _nextTimerId = 1;
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> ActiveTimers = new();

    public static JavaScriptResponse Fetch(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                throw new FormatException("Invalid data URI.");
            }

            var metadata = url[5..comma];
            var data = url[(comma + 1)..];
            var contentStr = metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(data))
                : WebUtility.UrlDecode(data);

            var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(contentStr)
            };
            return new JavaScriptResponse(mockResponse);
        }

        var response = Http.GetAsync(url).GetAwaiter().GetResult();
        return new JavaScriptResponse(response);
    }

    public static double setTimeout(Action callback, double delay)
    {
        var id = Interlocked.Increment(ref _nextTimerId);
        var cts = new CancellationTokenSource();
        ActiveTimers[id] = cts;

        Task.Delay((int)delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try
                {
                    callback();
                }
                catch
                {
                    // ignore callback exceptions
                }
            }
            ActiveTimers.TryRemove(id, out _);
        }, TaskScheduler.Default);

        return id;
    }

    public static void clearTimeout(double id)
    {
        if (ActiveTimers.TryRemove((int)id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public static double setInterval(Action callback, double delay)
    {
        var id = Interlocked.Increment(ref _nextTimerId);
        var cts = new CancellationTokenSource();
        ActiveTimers[id] = cts;

        Task.Run(async () =>
        {
            var token = cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay((int)delay, token);
                    if (!token.IsCancellationRequested)
                    {
                        callback();
                    }
                }
                catch
                {
                    break;
                }
            }
            ActiveTimers.TryRemove(id, out _);
        });

        return id;
    }

    public static void clearInterval(double id) => clearTimeout(id);
}

public sealed class JavaScriptResponse
{
    private readonly HttpResponseMessage _response;
    public JavaScriptResponse(HttpResponseMessage response) => _response = response;
    public bool ok => _response.IsSuccessStatusCode;
    public double status => (double)_response.StatusCode;
    public string text() => _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    public object? json() => JavaScriptJson.Instance.parse(text());
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

    // ES6 Math Methods
    public double sign(double value) => double.IsNaN(value) ? double.NaN : value == 0 ? value : Math.Sign(value);
    public double trunc(double value) => Math.Truncate(value);
    public double cbrt(double value) => Math.Cbrt(value);
    public double clz32(double value) => System.Numerics.BitOperations.LeadingZeroCount((uint)(int)value);
    public double log2(double value) => Math.Log2(value);
    public double log10(double value) => Math.Log10(value);
    public double log1p(double value) => Math.Log(1 + value);
    public double expm1(double value) => Math.Exp(value) - 1;
    public double sinh(double value) => Math.Sinh(value);
    public double cosh(double value) => Math.Cosh(value);
    public double tanh(double value) => Math.Tanh(value);
    public double asinh(double value) => Math.Asinh(value);
    public double acosh(double value) => Math.Acosh(value);
    public double atanh(double value) => Math.Atanh(value);
    public double hypot(double x, double y) => Math.Sqrt(x * x + y * y);
    public double fround(double value) => (float)value;
    public double imul(double x, double y) => (int)((uint)(int)x * (uint)(int)y);
}

public sealed class JavaScriptConsole
{
    public static readonly JavaScriptConsole Instance = new();

    private static readonly AsyncLocal<TextWriter?> CapturedOut = new();
    private static readonly AsyncLocal<TextWriter?> CapturedError = new();

    private JavaScriptConsole()
    {
    }

    public static JavaScriptConsoleCapture Capture()
    {
        return new JavaScriptConsoleCapture(CapturedOut, CapturedError);
    }

    public void log() => Out.WriteLine();
    public void log(object? value) => Out.WriteLine(Format(value));
    public void log(object? a, object? b) => Out.WriteLine(Join(a, b));
    public void log(object? a, object? b, object? c) => Out.WriteLine(Join(a, b, c));
    public void log(object? a, object? b, object? c, object? d) => Out.WriteLine(Join(a, b, c, d));

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

    public void error() => Error.WriteLine();
    public void error(object? value) => Error.WriteLine(Format(value));
    public void error(object? a, object? b) => Error.WriteLine(Join(a, b));
    public void error(object? a, object? b, object? c) => Error.WriteLine(Join(a, b, c));
    public void error(object? a, object? b, object? c, object? d) => Error.WriteLine(Join(a, b, c, d));

    public void write(object? value) => Out.Write(Format(value));
    public void writeLine(object? value) => Out.WriteLine(Format(value));
    public void clear()
    {
    }

    private static TextWriter Out => CapturedOut.Value ?? Console.Out;
    private static TextWriter Error => CapturedError.Value ?? Console.Error;
    private static string Join(params object?[] values) => string.Join(" ", values.Select(Format));
    private static string Format(object? value) => value?.ToString() ?? "null";
}

public sealed class JavaScriptConsoleCapture : IDisposable
{
    private readonly AsyncLocal<TextWriter?> _outSlot;
    private readonly AsyncLocal<TextWriter?> _errorSlot;
    private readonly TextWriter? _previousOut;
    private readonly TextWriter? _previousError;
    private bool _disposed;

    internal JavaScriptConsoleCapture(AsyncLocal<TextWriter?> outSlot, AsyncLocal<TextWriter?> errorSlot)
    {
        _outSlot = outSlot;
        _errorSlot = errorSlot;
        _previousOut = outSlot.Value;
        _previousError = errorSlot.Value;
        Output = new StringWriter(CultureInfo.InvariantCulture);
        Error = new StringWriter(CultureInfo.InvariantCulture);
        _outSlot.Value = Output;
        _errorSlot.Value = Error;
    }

    public StringWriter Output { get; }
    public StringWriter Error { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _outSlot.Value = _previousOut;
        _errorSlot.Value = _previousError;
        _disposed = true;
    }
}

public sealed class JavaScriptNetwork
{
    public static readonly JavaScriptNetwork Instance = new();
    private static readonly HttpClient Http = new();

    private JavaScriptNetwork()
    {
    }

    public string getString(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUri(address);
        }

        return Http.GetStringAsync(address).GetAwaiter().GetResult();
    }

    public byte[] getBytes(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetBytes(DecodeDataUri(address));
        }

        return Http.GetByteArrayAsync(address).GetAwaiter().GetResult();
    }

    public string postString(string address, string content) => postString(address, content, "text/plain");

    public string postString(string address, string content, string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        using var httpContent = new StringContent(content, Encoding.UTF8, mediaType);
        using var response = Http.PostAsync(address, httpContent).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static string DecodeDataUri(string address)
    {
        var comma = address.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            throw new FormatException("Invalid data URI.");
        }

        var metadata = address[5..comma];
        var data = address[(comma + 1)..];
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

    public object? parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return ToElement(doc.RootElement);
    }

    private static object? ToElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ToElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static System.Dynamic.ExpandoObject ToDictionary(JsonElement element)
    {
        var expando = new System.Dynamic.ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ToElement(prop.Value);
        }
        return expando;
    }
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
