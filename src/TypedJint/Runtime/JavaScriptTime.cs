using System;

namespace TypedJint.Runtime;

public sealed class JavaScriptTime
{
    public static readonly JavaScriptTime Instance = new();

    private JavaScriptTime()
    {
    }

    public double nowUnixMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string utcNowIsoString() => DateTimeOffset.UtcNow.ToString("O");

    // Helper methods for animation frames
    public static double requestAnimationFrame(object callback) => JavaScriptStandardLibrary.requestAnimationFrame(callback);
    public static void cancelAnimationFrame(double id) => JavaScriptStandardLibrary.cancelAnimationFrame(id);
}
