using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace TypedJint.Runtime;

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

public sealed class ConsoleWrapper
{
    public void log(params object?[] args)
    {
        if (args.Length == 0) JavaScriptConsole.Instance.log();
        else if (args.Length == 1) JavaScriptConsole.Instance.log(args[0]);
        else if (args.Length == 2) JavaScriptConsole.Instance.log(args[0], args[1]);
        else if (args.Length == 3) JavaScriptConsole.Instance.log(args[0], args[1], args[2]);
        else if (args.Length == 4) JavaScriptConsole.Instance.log(args[0], args[1], args[2], args[3]);
        else JavaScriptConsole.Instance.log(string.Join(" ", args));
    }
    public void warn(params object?[] args)
    {
        if (args.Length == 0) JavaScriptConsole.Instance.warn();
        else if (args.Length == 1) JavaScriptConsole.Instance.warn(args[0]);
        else if (args.Length == 2) JavaScriptConsole.Instance.warn(args[0], args[1]);
        else if (args.Length == 3) JavaScriptConsole.Instance.warn(args[0], args[1], args[2]);
        else if (args.Length == 4) JavaScriptConsole.Instance.warn(args[0], args[1], args[2], args[3]);
        else JavaScriptConsole.Instance.warn(string.Join(" ", args));
    }
    public void error(params object?[] args)
    {
        if (args.Length == 0) JavaScriptConsole.Instance.error();
        else if (args.Length == 1) JavaScriptConsole.Instance.error(args[0]);
        else if (args.Length == 2) JavaScriptConsole.Instance.error(args[0], args[1]);
        else if (args.Length == 3) JavaScriptConsole.Instance.error(args[0], args[1], args[2]);
        else if (args.Length == 4) JavaScriptConsole.Instance.error(args[0], args[1], args[2], args[3]);
        else JavaScriptConsole.Instance.error(string.Join(" ", args));
    }
}
