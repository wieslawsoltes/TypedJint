namespace TypedJint;

public static class JavaScriptStandardLibraryExtensions
{
    public static TypedJintEngine RegisterStandardLibrary(this TypedJintEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.SetValue("Math", JavaScriptMath.Instance);
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
