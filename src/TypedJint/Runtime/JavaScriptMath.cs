using System;
using System.Linq;

namespace TypedJint.Runtime;

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
