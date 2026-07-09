using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ProGPU.Vector;
using ProGPU.Scene;
using TypedJint;

namespace TypedJint.Runtime;

public class HTMLImageElement : DomElement
{
    public HTMLImageElement() : base("img") { }
    public string src { get; set; } = string.Empty;
    public double width { get; set; } = 0;
    public double height { get; set; } = 0;
}

public class HTMLCanvasElement : DomElement
{
    private double _width = 300;
    private double _height = 150;
    private CanvasRenderingContext2D? _context2d;
    private WebGLRenderingContext? _contextWebGL;

    public HTMLCanvasElement() : base("canvas")
    {
    }

    public double width
    {
        get => _width;
        set { _width = value; style.width = value.ToString(CultureInfo.InvariantCulture) + "px"; }
    }

    public double height
    {
        get => _height;
        set { _height = value; style.height = value.ToString(CultureInfo.InvariantCulture) + "px"; }
    }

    public object? getContext(string contextId) => getContext(contextId, null);
    public object? getContext(string contextId, object? options)
    {
        if (string.Equals(contextId, "2d", StringComparison.OrdinalIgnoreCase))
        {
            return _context2d ??= new CanvasRenderingContext2D(this);
        }
        if (string.Equals(contextId, "webgl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contextId, "webgl2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contextId, "experimental-webgl", StringComparison.OrdinalIgnoreCase))
        {
            return _contextWebGL ??= new WebGLRenderingContext(this);
        }
        return null;
    }
}

public class TextMetrics
{
    public TextMetrics(double width) => this.width = width;
    public double width { get; }
}

public class CanvasRenderingContext2D
{
    private readonly HTMLCanvasElement _canvas;
    private readonly DrawingContext _drawingContext = new();
    private string _fillStyle = "black";
    private string _strokeStyle = "black";
    private double _lineWidth = 1.0;
    private string _font = "10px sans-serif";
    private string _lineCap = "butt";
    private string _lineJoin = "miter";
    private readonly List<Vector2> _currentPath = new();
    private readonly Stack<Matrix4x4> _transformStack = new();
    private Matrix4x4 _currentTransform = Matrix4x4.Identity;
    private Rect? _pendingRect;

    public CanvasRenderingContext2D(HTMLCanvasElement canvas)
    {
        _canvas = canvas;
    }

    public HTMLCanvasElement canvas => _canvas;
    public DrawingContext DrawingContext => _drawingContext;

    public string fillStyle { get => _fillStyle; set => _fillStyle = value; }
    public string strokeStyle { get => _strokeStyle; set => _strokeStyle = value; }
    public double lineWidth { get => _lineWidth; set => _lineWidth = value; }
    public string font { get => _font; set => _font = value; }
    public string lineCap { get => _lineCap; set => _lineCap = value; }
    public string lineJoin { get => _lineJoin; set => _lineJoin = value; }

    private double _globalAlpha = 1.0;
    private string _globalCompositeOperation = "source-over";
    private bool _imageSmoothingEnabled = true;
    private string _shadowColor = "transparent";
    private double _shadowBlur = 0.0;
    private double _shadowOffsetX = 0.0;
    private double _shadowOffsetY = 0.0;
    private double _lineDashOffset = 0.0;
    private double[] _lineDash = Array.Empty<double>();

    public double globalAlpha { get => _globalAlpha; set => _globalAlpha = value; }
    public string globalCompositeOperation { get => _globalCompositeOperation; set => _globalCompositeOperation = value; }
    public bool imageSmoothingEnabled { get => _imageSmoothingEnabled; set => _imageSmoothingEnabled = value; }
    public string shadowColor { get => _shadowColor; set => _shadowColor = value; }
    public double shadowBlur { get => _shadowBlur; set => _shadowBlur = value; }
    public double shadowOffsetX { get => _shadowOffsetX; set => _shadowOffsetX = value; }
    public double shadowOffsetY { get => _shadowOffsetY; set => _shadowOffsetY = value; }
    public double lineDashOffset { get => _lineDashOffset; set => _lineDashOffset = value; }
    public void setLineDash(object? segments) { }
    public double[] getLineDash() => _lineDash;

    private string _textAlign = "start";
    private string _textBaseline = "alphabetic";
    private string _direction = "inherit";
    private double _miterLimit = 10.0;
    private string _filter = "none";
    private string _imageSmoothingQuality = "low";

    public string textAlign { get => _textAlign; set => _textAlign = value; }
    public string textBaseline { get => _textBaseline; set => _textBaseline = value; }
    public string direction { get => _direction; set => _direction = value; }
    public double miterLimit { get => _miterLimit; set => _miterLimit = value; }
    public string filter { get => _filter; set => _filter = value; }
    public string imageSmoothingQuality { get => _imageSmoothingQuality; set => _imageSmoothingQuality = value; }

    private void Invalidate()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _canvas.AvaloniaControl?.InvalidateVisual());
    }

    public void fillRect(double x, double y, double w, double h)
    {
        var brush = CreateBrush(_fillStyle);
        lock (_drawingContext.Commands)
        {
            _drawingContext.DrawRectangle(brush, null, new Rect((float)x, (float)y, (float)w, (float)h));
        }
        Invalidate();
    }

    public void strokeRect(double x, double y, double w, double h)
    {
        var pen = CreatePen(_strokeStyle, _lineWidth);
        lock (_drawingContext.Commands)
        {
            _drawingContext.DrawRectangle(null, pen, new Rect((float)x, (float)y, (float)w, (float)h));
        }
        Invalidate();
    }

    public void clearRect(double x, double y, double w, double h)
    {
        lock (_drawingContext.Commands)
        {
            if (x == 0 && y == 0)
            {
                _drawingContext.Commands.Clear();
            }
            else
            {
                _drawingContext.DrawRectangle(new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f)), null, new Rect((float)x, (float)y, (float)w, (float)h));
            }
        }
        Invalidate();
    }

    public void beginPath()
    {
        _currentPath.Clear();
        _pendingRect = null;
    }

    public void moveTo(double x, double y)
    {
        _currentPath.Add(Vector2.Transform(new Vector2((float)x, (float)y), _currentTransform));
    }

    public void lineTo(double x, double y)
    {
        _currentPath.Add(Vector2.Transform(new Vector2((float)x, (float)y), _currentTransform));
    }

    public void closePath()
    {
        if (_currentPath.Count > 0)
        {
            _currentPath.Add(_currentPath[0]);
        }
    }

    public void stroke() => stroke(null);
    public void stroke(object? arg1)
    {
        lock (_drawingContext.Commands)
        {
            if (_pendingRect.HasValue)
            {
                var pen = CreatePen(_strokeStyle, _lineWidth);
                _drawingContext.DrawRectangle(null, pen, _pendingRect.Value);
                _pendingRect = null;
                Invalidate();
                return;
            }
            if (_currentPath.Count < 2) return;
            var penStyle = CreatePen(_strokeStyle, _lineWidth);
            var path = RenderCommandGeometryCache.CreatePolylinePath(_currentPath.ToArray(), false);
            _drawingContext.DrawPath(null, penStyle, path);
        }
        Invalidate();
    }

    public void fill() => fill(null, null);
    public void fill(object? arg1) => fill(arg1, null);
    public void fill(object? arg1, object? arg2)
    {
        lock (_drawingContext.Commands)
        {
            if (_pendingRect.HasValue)
            {
                var brush = CreateBrush(_fillStyle);
                _drawingContext.DrawRectangle(brush, null, _pendingRect.Value);
                _pendingRect = null;
                Invalidate();
                return;
            }
            if (_currentPath.Count < 3) return;
            var brushStyle = CreateBrush(_fillStyle);
            var path = RenderCommandGeometryCache.CreatePolylinePath(_currentPath.ToArray(), true);
            _drawingContext.DrawPath(brushStyle, null, path);
        }
        Invalidate();
    }

    public void arc(double x, double y, double radius, double startAngle, double endAngle, bool anticlockwise = false)
    {
        int segments = 32;
        double step = (endAngle - startAngle) / segments;
        for (int i = 0; i <= segments; i++)
        {
            double angle = startAngle + i * step;
            double px = x + Math.Cos(angle) * radius;
            double py = y + Math.Sin(angle) * radius;
            if (i == 0 && _currentPath.Count == 0)
            {
                moveTo(px, py);
            }
            else
            {
                lineTo(px, py);
            }
        }
    }

    public void rect(double x, double y, double w, double h)
    {
        _pendingRect = new Rect((float)x, (float)y, (float)w, (float)h);
        moveTo(x, y);
        lineTo(x + w, y);
        lineTo(x + w, y + h);
        lineTo(x, y + h);
        closePath();
    }

    public void bezierCurveTo(double cp1x, double cp1y, double cp2x, double cp2y, double x, double y)
    {
        if (_currentPath.Count == 0) moveTo(cp1x, cp1y);
        var p0 = _currentPath.Last();
        var p1 = new Vector2((float)cp1x, (float)cp1y);
        var p2 = new Vector2((float)cp2x, (float)cp2y);
        var p3 = new Vector2((float)x, (float)y);
        
        int segments = 16;
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            var pt = BezierMath.Cubic(p0, p1, p2, p3, t);
            lineTo(pt.X, pt.Y);
        }
    }

    public void quadraticCurveTo(double cpx, double cpy, double x, double y)
    {
        if (_currentPath.Count == 0) moveTo(cpx, cpy);
        var p0 = _currentPath.Last();
        var p1 = new Vector2((float)cpx, (float)cpy);
        var p2 = new Vector2((float)x, (float)y);

        int segments = 16;
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            var pt = BezierMath.Quadratic(p0, p1, p2, t);
            lineTo(pt.X, pt.Y);
        }
    }

    public void save()
    {
        _transformStack.Push(_currentTransform);
    }

    public void restore()
    {
        if (_transformStack.Count > 0)
        {
            _currentTransform = _transformStack.Pop();
        }
    }

    public void scale(double x, double y)
    {
        _currentTransform *= Matrix4x4.CreateScale((float)x, (float)y, 1f);
    }

    public void rotate(double angle)
    {
        _currentTransform *= Matrix4x4.CreateRotationZ((float)angle);
    }

    public void translate(double x, double y)
    {
        _currentTransform *= Matrix4x4.CreateTranslation((float)x, (float)y, 0f);
    }

    public void transform(double a, double b, double c, double d, double e, double f)
    {
        var m = new Matrix4x4(
            (float)a, (float)b, 0f, 0f,
            (float)c, (float)d, 0f, 0f,
            0f, 0f, 1f, 0f,
            (float)e, (float)f, 0f, 1f
        );
        _currentTransform *= m;
    }

    public void setTransform(double a, double b, double c, double d, double e, double f)
    {
        _currentTransform = new Matrix4x4(
            (float)a, (float)b, 0f, 0f,
            (float)c, (float)d, 0f, 0f,
            0f, 0f, 1f, 0f,
            (float)e, (float)f, 0f, 1f
        );
    }

    public void fillText(string text, double x, double y)
    {
        lock (_drawingContext.Commands)
        {
            _drawingContext.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawText,
                Text = text,
                Position = Vector2.Transform(new Vector2((float)x, (float)y), _currentTransform),
                Brush = CreateBrush(_fillStyle)
            });
        }
    }

    public void strokeText(string text, double x, double y)
    {
        lock (_drawingContext.Commands)
        {
            _drawingContext.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawText,
                Text = text,
                Position = Vector2.Transform(new Vector2((float)x, (float)y), _currentTransform),
                Brush = CreateBrush(_strokeStyle)
            });
        }
    }

    public TextMetrics measureText(string text)
    {
        return new TextMetrics(text.Length * 8.0);
    }

    public void drawImage(object image, double dx, double dy)
    {
        if (image is HTMLCanvasElement canvas)
        {
            DrawImageCore(image, 0, 0, canvas.width, canvas.height, dx, dy, canvas.width, canvas.height);
        }
    }

    public void drawImage(object image, double dx, double dy, double dw, double dh)
    {
        if (image is HTMLCanvasElement canvas)
        {
            DrawImageCore(image, 0, 0, canvas.width, canvas.height, dx, dy, dw, dh);
        }
    }

    public void drawImage(object image, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        DrawImageCore(image, sx, sy, sw, sh, dx, dy, dw, dh);
    }

    private void DrawImageCore(object image, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        if (image is HTMLCanvasElement srcCanvas)
        {
            var srcCtx = srcCanvas.getContext("2d") as CanvasRenderingContext2D;
            if (srcCtx != null)
            {
                lock (srcCtx._drawingContext.Commands)
                lock (_drawingContext.Commands)
                {
                    double scaleX = sw > 0 ? dw / sw : 1.0;
                    double scaleY = sh > 0 ? dh / sh : 1.0;

                    foreach (var cmd in srcCtx._drawingContext.Commands)
                    {
                        var copy = new RenderCommand
                        {
                            Type = cmd.Type,
                            Text = cmd.Text,
                            Brush = cmd.Brush,
                            Pen = cmd.Pen,
                            Path = cmd.Path
                        };

                        if (cmd.Type == RenderCommandType.DrawText)
                        {
                            float localX = cmd.Position.X - (float)sx;
                            float localY = cmd.Position.Y - (float)sy;
                            float tx = (float)(dx + localX * scaleX);
                            float ty = (float)(dy + localY * scaleY);
                            copy.Position = Vector2.Transform(new Vector2(tx, ty), _currentTransform);
                        }
                        else
                        {
                            float localX = cmd.Rect.X - (float)sx;
                            float localY = cmd.Rect.Y - (float)sy;
                            float rx = (float)(dx + localX * scaleX);
                            float ry = (float)(dy + localY * scaleY);
                            float rw = (float)(cmd.Rect.Width * scaleX);
                            float rh = (float)(cmd.Rect.Height * scaleY);
                            copy.Rect = new Rect(rx, ry, rw, rh);
                        }

                        _drawingContext.Commands.Add(copy);
                    }
                }
                Invalidate();
            }
        }
    }

    public void clip() => clip(null, null);
    public void clip(object? arg1) => clip(arg1, null);
    public void clip(object? arg1, object? arg2)
    {
    }

    public CanvasGradient createLinearGradient(double x0, double y0, double x1, double y1) => new CanvasGradient();
    public CanvasGradient createRadialGradient(double x0, double y0, double r0, double x1, double y1, double r1) => new CanvasGradient();
    public CanvasPattern createPattern(object image, string repetition) => new CanvasPattern();

    private static Brush CreateBrush(string style)
    {
        var color = ParseColor(style);
        return new SolidColorBrush(color);
    }

    private static Pen CreatePen(string style, double thickness)
    {
        var color = ParseColor(style);
        return new Pen(new SolidColorBrush(color), (float)thickness);
    }

    private static Vector4 ParseColor(string style)
    {
        if (string.IsNullOrWhiteSpace(style)) return new Vector4(0f, 0f, 0f, 1f);
        style = style.Trim();

        if (style.StartsWith('#'))
        {
            if (style.Length == 7)
            {
                var r = byte.Parse(style.Substring(1, 2), NumberStyles.HexNumber) / 255.0f;
                var g = byte.Parse(style.Substring(3, 2), NumberStyles.HexNumber) / 255.0f;
                var b = byte.Parse(style.Substring(5, 2), NumberStyles.HexNumber) / 255.0f;
                return new Vector4(r, g, b, 1f);
            }
            if (style.Length == 9)
            {
                var r = byte.Parse(style.Substring(1, 2), NumberStyles.HexNumber) / 255.0f;
                var g = byte.Parse(style.Substring(3, 2), NumberStyles.HexNumber) / 255.0f;
                var b = byte.Parse(style.Substring(5, 2), NumberStyles.HexNumber) / 255.0f;
                var a = byte.Parse(style.Substring(7, 2), NumberStyles.HexNumber) / 255.0f;
                return new Vector4(r, g, b, a);
            }
            if (style.Length == 4)
            {
                var rc = style[1];
                var gc = style[2];
                var bc = style[3];
                var r = byte.Parse($"{rc}{rc}", NumberStyles.HexNumber) / 255.0f;
                var g = byte.Parse($"{gc}{gc}", NumberStyles.HexNumber) / 255.0f;
                var b = byte.Parse($"{bc}{bc}", NumberStyles.HexNumber) / 255.0f;
                return new Vector4(r, g, b, 1f);
            }
        }

        if (style.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(style, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var r = float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 255.0f;
                var g = float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 255.0f;
                var b = float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) / 255.0f;
                var a = 1.0f;
                if (match.Groups[4].Success)
                {
                    a = float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                }
                return new Vector4(r, g, b, a);
            }
        }

        if (style.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(style, @"hsla?\(\s*(\d+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%\s*(?:,\s*([\d.]+)\s*)?\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var h = float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var s = float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0f;
                var l = float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) / 100.0f;
                var a = 1.0f;
                if (match.Groups[4].Success)
                {
                    a = float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                }
                return HslToRgb(h, s, l, a);
            }
        }

        if (style.Equals("red", StringComparison.OrdinalIgnoreCase)) return new Vector4(1f, 0f, 0f, 1f);
        if (style.Equals("green", StringComparison.OrdinalIgnoreCase)) return new Vector4(0f, 1f, 0f, 1f);
        if (style.Equals("blue", StringComparison.OrdinalIgnoreCase)) return new Vector4(0f, 0f, 1f, 1f);
        if (style.Equals("white", StringComparison.OrdinalIgnoreCase)) return new Vector4(1f, 1f, 1f, 1f);
        if (style.Equals("black", StringComparison.OrdinalIgnoreCase)) return new Vector4(0f, 0f, 0f, 1f);
        if (style.Equals("yellow", StringComparison.OrdinalIgnoreCase)) return new Vector4(1f, 1f, 0f, 1f);
        if (style.Equals("cyan", StringComparison.OrdinalIgnoreCase)) return new Vector4(0f, 1f, 1f, 1f);
        if (style.Equals("magenta", StringComparison.OrdinalIgnoreCase)) return new Vector4(1f, 0f, 1f, 1f);
        if (style.Equals("gray", StringComparison.OrdinalIgnoreCase) || style.Equals("grey", StringComparison.OrdinalIgnoreCase)) return new Vector4(0.5f, 0.5f, 0.5f, 1f);

        return new Vector4(0f, 0f, 0f, 1f);
    }

    private static Vector4 HslToRgb(float h, float s, float l, float a)
    {
        float r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5f ? l * (1f + s) : l + s - l * s;
            var p = 2f * l - q;
            r = HueToRgb(p, q, h / 360f + 1f / 3f);
            g = HueToRgb(p, q, h / 360f);
            b = HueToRgb(p, q, h / 360f - 1f / 3f);
        }
        return new Vector4(r, g, b, a);
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}

public class CanvasGradient
{
    public void addColorStop(double offset, string color) { }
}

public class CanvasPattern
{
    public void setTransform(object? transform = null) { }
}

internal static class BezierMath
{
    public static Vector2 Quadratic(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }

    public static Vector2 Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
    }
}

public class WebGLShader { }
public class WebGLProgram { }
public class WebGLBuffer { }
public class WebGLTexture { }
public class WebGLFramebuffer { }
public class WebGLRenderbuffer { }
public class WebGLUniformLocation { }
public class WebGLActiveInfo
{
    public WebGLActiveInfo(string name, int size, uint type)
    {
        this.name = name;
        this.size = size;
        this.type = type;
    }
    public string name { get; }
    public int size { get; }
    public uint type { get; }
}

public class WebGLRenderingContext
{
    private readonly HTMLCanvasElement _canvas;

    public WebGLRenderingContext(HTMLCanvasElement canvas)
    {
        _canvas = canvas;
    }

    public HTMLCanvasElement canvas => _canvas;

    public uint MAX_TEXTURE_SIZE => 0x0D33;
    public uint MAX_VERTEX_ATTRIBS => 0x8869;
    public uint VERSION => 0x1F02;
    public uint SHADING_LANGUAGE_VERSION => 0x8B8C;
    public uint VENDOR => 0x1F00;
    public uint RENDERER => 0x1F01;
    public uint DEPTH_BUFFER_BIT => 0x00000100;
    public uint STENCIL_BUFFER_BIT => 0x00000400;
    public uint COLOR_BUFFER_BIT => 0x00004000;
    public uint POINTS => 0x0000;
    public uint LINES => 0x0001;
    public uint LINE_LOOP => 0x0002;
    public uint LINE_STRIP => 0x0003;
    public uint TRIANGLES => 0x0004;
    public uint TRIANGLE_STRIP => 0x0005;
    public uint TRIANGLE_FAN => 0x0006;
    public uint NEVER => 0x0200;
    public uint LESS => 0x0201;
    public uint EQUAL => 0x0202;
    public uint LEQUAL => 0x0203;
    public uint GREATER => 0x0204;
    public uint NOTEQUAL => 0x0205;
    public uint GEQUAL => 0x0206;
    public uint ALWAYS => 0x0207;
    public uint SRC_COLOR => 0x0300;
    public uint ONE_MINUS_SRC_COLOR => 0x0301;
    public uint SRC_ALPHA => 0x0302;
    public uint ONE_MINUS_SRC_ALPHA => 0x0303;
    public uint DST_ALPHA => 0x0304;
    public uint ONE_MINUS_DST_ALPHA => 0x0305;
    public uint CULL_FACE => 0x0B44;
    public uint DEPTH_TEST => 0x0B71;
    public uint BLEND => 0x0BE2;
    public uint DITHER => 0x0BD0;
    public uint SCISSOR_TEST => 0x0C11;
    public uint TEXTURE_2D => 0x0DE1;
    public uint BYTE => 0x1400;
    public uint UNSIGNED_BYTE => 0x1401;
    public uint SHORT => 0x1402;
    public uint UNSIGNED_SHORT => 0x1403;
    public uint INT => 0x1404;
    public uint UNSIGNED_INT => 0x1405;
    public uint FLOAT => 0x1406;
    public uint COMPILE_STATUS => 0x8B81;
    public uint LINK_STATUS => 0x8B82;
    public uint VALIDATE_STATUS => 0x8B83;
    public uint VERTEX_SHADER => 0x8B31;
    public uint FRAGMENT_SHADER => 0x8B30;
    public uint MAX_TEXTURE_IMAGE_UNITS => 0x8872;
    public uint ARRAY_BUFFER => 0x8892;
    public uint ELEMENT_ARRAY_BUFFER => 0x8893;
    public uint STATIC_DRAW => 0x88E4;
    public uint DYNAMIC_DRAW => 0x88E8;
    public uint STREAM_DRAW => 0x88E0;
    public uint TEXTURE_WIDTH => 0x1000;
    public uint TEXTURE_HEIGHT => 0x1001;
    public uint TEXTURE_MAG_FILTER => 0x2800;
    public uint TEXTURE_MIN_FILTER => 0x2801;
    public uint TEXTURE_WRAP_S => 0x2802;
    public uint TEXTURE_WRAP_T => 0x2803;
    public uint NEAREST => 0x2600;
    public uint LINEAR => 0x2601;
    public uint CLAMP_TO_EDGE => 0x812F;
    public uint REPEAT => 0x2901;
    public uint RGBA => 0x1908;
    public uint RGB => 0x1907;

    public void viewport(object? x, object? y, object? width, object? height) { }
    public void clearColor(object? r, object? g, object? b, object? a) { }
    public void clear(object? mask) { }

    public WebGLShader createShader(uint type) => new WebGLShader();
    public void shaderSource(WebGLShader shader, string source) { }
    public void compileShader(WebGLShader shader) { }
    public object getShaderParameter(WebGLShader shader, object? pnameObj)
    {
        uint pname = 0;
        if (pnameObj != null)
        {
            try { pname = Convert.ToUInt32(pnameObj); } catch {}
        }
        if (pname == COMPILE_STATUS) return true;
        return 0;
    }
    public string getShaderInfoLog(WebGLShader shader) => string.Empty;

    public WebGLProgram createProgram() => new WebGLProgram();
    public void attachShader(WebGLProgram program, WebGLShader shader) { }
    public void linkProgram(WebGLProgram program) { }
    public object getProgramParameter(WebGLProgram program, object? pnameObj)
    {
        uint pname = 0;
        if (pnameObj != null)
        {
            try { pname = Convert.ToUInt32(pnameObj); } catch {}
        }
        if (pname == LINK_STATUS || pname == VALIDATE_STATUS) return true;
        return 0;
    }
    public string getProgramInfoLog(WebGLProgram program) => string.Empty;
    public void useProgram(WebGLProgram program) { }

    private readonly List<float[]> _verticesList = new();
    private object? _boundBuffer;

    public WebGLBuffer createBuffer() => new WebGLBuffer();
    public void bindBuffer(uint target, WebGLBuffer? buffer)
    {
        _boundBuffer = buffer;
    }

    public void bufferData(uint target, object data, uint usage)
    {
        if (data is System.Collections.IEnumerable enumerable && data is not string)
        {
            var floats = new List<float>();
            foreach (var item in enumerable)
            {
                if (item != null)
                {
                    try
                    {
                        floats.Add(Convert.ToSingle(item, CultureInfo.InvariantCulture));
                    }
                    catch {}
                }
            }
            _verticesList.Clear();
            for (int i = 0; i < floats.Count; i += 3)
            {
                if (i + 2 < floats.Count)
                {
                    _verticesList.Add(new[] { floats[i], floats[i+1], floats[i+2] });
                }
            }
        }
    }

    public void enableVertexAttribArray(uint index) { }
    public void disableVertexAttribArray(uint index) { }
    public void vertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, long offset) { }
    
    public void drawArrays(uint mode, int first, int count)
    {
        var ctx2d = _canvas.getContext("2d") as CanvasRenderingContext2D;
        if (ctx2d != null && _verticesList.Count >= 3)
        {
            lock (ctx2d.DrawingContext.Commands)
            {
                ctx2d.beginPath();
                ctx2d.fillStyle = "rgba(0, 150, 255, 0.7)";
                ctx2d.strokeStyle = "white";

                float time = (float)(DateTime.Now.TimeOfDay.TotalSeconds);
                float cos = (float)Math.Cos(time);
                float sin = (float)Math.Sin(time);

                for (int i = 0; i < count && i < _verticesList.Count; i++)
                {
                    var v = _verticesList[i];
                    float x = v[0];
                    float y = v[1];
                    float z = v[2];

                    float x1 = x * cos - z * sin;
                    float z1 = x * sin + z * cos;
                    float y2 = y * cos - z1 * sin;

                    float scale = 120f;
                    float px = (float)(_canvas.width / 2.0 + x1 * scale);
                    float py = (float)(_canvas.height / 2.0 - y2 * scale);

                    if (i == 0) ctx2d.moveTo(px, py);
                    else ctx2d.lineTo(px, py);
                }
                ctx2d.closePath();
                ctx2d.fill();
                ctx2d.stroke();
            }
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _canvas.AvaloniaControl?.InvalidateVisual());
        }
    }

    public void drawElements(uint mode, int count, uint type, long offset) { }

    public WebGLUniformLocation getUniformLocation(WebGLProgram program, string name) => new WebGLUniformLocation();
    public int getAttribLocation(WebGLProgram program, string name) => 0;

    public void uniform1f(WebGLUniformLocation? loc, float x) { }
    public void uniform2f(WebGLUniformLocation? loc, float x, float y) { }
    public void uniform3f(WebGLUniformLocation? loc, float x, float y, float z) { }
    public void uniform4f(WebGLUniformLocation? loc, float x, float y, float z, float w) { }
    public void uniform1i(WebGLUniformLocation? loc, int x) { }
    public void uniform2i(WebGLUniformLocation? loc, int x, int y) { }
    public void uniform3i(WebGLUniformLocation? loc, int x, int y, int z) { }
    public void uniform4i(WebGLUniformLocation? loc, int x, int y, int z, int w) { }

    public void uniformMatrix4fv(WebGLUniformLocation? loc, bool transpose, object value) { }
    public void uniformMatrix3fv(WebGLUniformLocation? loc, bool transpose, object value) { }
    public void uniformMatrix2fv(WebGLUniformLocation? loc, bool transpose, object value) { }

    public WebGLTexture createTexture() => new WebGLTexture();
    public void bindTexture(object? target, object? texture) { }
    public void texImage2D(object? target, object? level, object? internalformat, object? width, object? height, object? border, object? format, object? type, object? pixels) { }
    public void texImage2D(object? target, object? level, object? internalformat, object? format, object? type, object? source) { }
    public void texParameteri(object? target, object? pname, object? param) { }

    public WebGLActiveInfo getActiveUniform(WebGLProgram program, uint index) => new WebGLActiveInfo("uniform", 1, FLOAT);
    public WebGLActiveInfo getActiveAttrib(WebGLProgram program, uint index) => new WebGLActiveInfo("attribute", 1, FLOAT);

    public object? getInternalformatParameter(object? target, object? internalformat, object? pname) => null;

    public object? getParameter(object? pnameObj)
    {
        uint pname = 0;
        if (pnameObj != null)
        {
            try
            {
                pname = Convert.ToUInt32(pnameObj);
            }
            catch {}
        }

        const uint MAX_TEXTURE_SIZE = 0x0D33;
        const uint MAX_VERTEX_ATTRIBS = 0x8869;
        const uint VERSION = 0x1F02;
        const uint SHADING_LANGUAGE_VERSION = 0x8B8C;
        const uint VENDOR = 0x1F00;
        const uint RENDERER = 0x1F01;
        const uint MAX_TEXTURE_IMAGE_UNITS = 0x8872;

        if (pname == MAX_TEXTURE_SIZE) return 4096;
        if (pname == MAX_VERTEX_ATTRIBS) return 16;
        if (pname == VERSION) return "WebGL 1.0";
        if (pname == SHADING_LANGUAGE_VERSION) return "WebGL GLSL ES 1.0";
        if (pname == VENDOR) return "Mock Vendor";
        if (pname == RENDERER) return "Mock Renderer";
        if (pname == MAX_TEXTURE_IMAGE_UNITS) return 16;
        return null;
    }
    
    public object? getContextAttributes()
    {
        return new Dictionary<string, object>
        {
            { "alpha", true },
            { "depth", true },
            { "stencil", true },
            { "antialias", true },
            { "premultipliedAlpha", true },
            { "preserveDrawingBuffer", false },
            { "failIfMajorPerformanceCaveat", false }
        };
    }

    public object? getExtension(string name)
    {
        return null;
    }
    public void activeTexture(object? texture) { }
    public void deleteTexture(object? texture) { }
    public void deleteFramebuffer(object? framebuffer) { }
    public void deleteRenderbuffer(object? renderbuffer) { }
    public void deleteProgram(object? program) { }
    public void deleteShader(object? shader) { }
    public void deleteBuffer(object? buffer) { }
    public void pixelStorei(object? pname, object? param) { }
    public void depthFunc(object? func) { }
    public void enable(object? cap) { }
    public void disable(object? cap) { }
    public void blendFunc(object? sfactor, object? dfactor) { }
    public void frontFace(object? mode) { }
    public void cullFace(object? mode) { }
    public void depthMask(object? flag) { }
    public void colorMask(object? r, object? g, object? b, object? a) { }
    public void stencilMask(object? mask) { }
    public void scissor(object? x, object? y, object? width, object? height) { }
    public void clearDepth(object? depth) { }
    public void clearStencil(object? stencil) { }
    public void stencilFunc(object? func, object? refVal, object? mask) { }
    public void stencilOp(object? fail, object? zfail, object? zpass) { }
    public void blendEquation(object? mode) { }
    public void blendEquationSeparate(object? modeRGB, object? modeAlpha) { }
    public void blendFuncSeparate(object? srcRGB, object? dstRGB, object? srcAlpha, object? dstAlpha) { }

    public WebGLFramebuffer createFramebuffer() => new WebGLFramebuffer();
    public void bindFramebuffer(object? target, object? framebuffer) { }
    public void framebufferTexture2D(object? target, object? attachment, object? textarget, object? texture, object? level) { }
 
    public WebGLRenderbuffer createRenderbuffer() => new WebGLRenderbuffer();
    public void bindRenderbuffer(object? target, object? renderbuffer) { }
    public void renderbufferStorage(object? target, object? internalformat, object? width, object? height) { }
}

public class DomMouseEvent : DomEvent
{
    public DomMouseEvent(string type, double clientX, double clientY, int button) : base(type)
    {
        this.clientX = clientX;
        this.clientY = clientY;
        this.button = button;
    }
    public double clientX { get; }
    public double clientY { get; }
    public int button { get; }
}

public class DomPointerEvent : DomMouseEvent
{
    public DomPointerEvent(string type, double clientX, double clientY, int button) : base(type, clientX, clientY, button)
    {
    }
}

public class DomKeyboardEvent : DomEvent
{
    public DomKeyboardEvent(string type, string key, int keyCode) : base(type)
    {
        this.key = key;
        this.keyCode = keyCode;
        this.which = keyCode;
    }
    public string key { get; }
    public int keyCode { get; }
    public int which { get; }
}

public static class AvaloniaInputEventRouter
{
    public static void Attach(Avalonia.Controls.Control control, HTMLCanvasElement canvas)
    {
        control.PointerPressed += (sender, e) =>
        {
            var pt = e.GetPosition(control);
            var button = 0;
            var point = e.GetCurrentPoint(control);
            if (point.Properties.IsLeftButtonPressed) button = 0;
            else if (point.Properties.IsMiddleButtonPressed) button = 1;
            else if (point.Properties.IsRightButtonPressed) button = 2;

            var domEvent = new DomPointerEvent("pointerdown", pt.X, pt.Y, button);
            canvas.dispatchEvent(domEvent);
            if (domEvent.defaultPrevented) e.Handled = true;
            
            var mouseEvent = new DomMouseEvent("mousedown", pt.X, pt.Y, button);
            canvas.dispatchEvent(mouseEvent);
        };

        control.PointerReleased += (sender, e) =>
        {
            var pt = e.GetPosition(control);
            var button = 0;
            var point = e.GetCurrentPoint(control);
            if (point.Properties.PointerUpdateKind == Avalonia.Input.PointerUpdateKind.LeftButtonReleased) button = 0;
            else if (point.Properties.PointerUpdateKind == Avalonia.Input.PointerUpdateKind.MiddleButtonReleased) button = 1;
            else if (point.Properties.PointerUpdateKind == Avalonia.Input.PointerUpdateKind.RightButtonReleased) button = 2;

            var domEvent = new DomPointerEvent("pointerup", pt.X, pt.Y, button);
            canvas.dispatchEvent(domEvent);
            if (domEvent.defaultPrevented) e.Handled = true;

            var mouseEvent = new DomMouseEvent("mouseup", pt.X, pt.Y, button);
            canvas.dispatchEvent(mouseEvent);
        };

        control.PointerMoved += (sender, e) =>
        {
            var pt = e.GetPosition(control);
            var domEvent = new DomPointerEvent("pointermove", pt.X, pt.Y, 0);
            canvas.dispatchEvent(domEvent);
            if (domEvent.defaultPrevented) e.Handled = true;

            var mouseEvent = new DomMouseEvent("mousemove", pt.X, pt.Y, 0);
            canvas.dispatchEvent(mouseEvent);
        };

        control.KeyDown += (sender, e) =>
        {
            var keyStr = e.Key.ToString();
            var keyCode = (int)e.Key;
            var domEvent = new DomKeyboardEvent("keydown", keyStr, keyCode);
            canvas.dispatchEvent(domEvent);
            if (domEvent.defaultPrevented) e.Handled = true;
        };

        control.KeyUp += (sender, e) =>
        {
            var keyStr = e.Key.ToString();
            var keyCode = (int)e.Key;
            var domEvent = new DomKeyboardEvent("keyup", keyStr, keyCode);
            canvas.dispatchEvent(domEvent);
            if (domEvent.defaultPrevented) e.Handled = true;
        };
    }
}

public class AvaloniaCanvasHost : Avalonia.Controls.Control
{
    private readonly HTMLCanvasElement _canvas;

    public AvaloniaCanvasHost(HTMLCanvasElement canvas)
    {
        _canvas = canvas;
        ClipToBounds = true;
        AvaloniaInputEventRouter.Attach(this, _canvas);
    }

    public override void Render(Avalonia.Media.DrawingContext context)
    {
        context.DrawRectangle(Avalonia.Media.Brushes.Transparent, null, new Avalonia.Rect(0, 0, Bounds.Width, Bounds.Height));

        var ctx2d = _canvas.getContext("2d") as CanvasRenderingContext2D;
        if (ctx2d != null)
        {
            lock (ctx2d.DrawingContext.Commands)
            {
                foreach (var cmd in ctx2d.DrawingContext.Commands)
                {
                    RenderCommandToAvalonia(context, cmd);
                }
            }
        }
    }

    private void RenderCommandToAvalonia(Avalonia.Media.DrawingContext context, ProGPU.Scene.RenderCommand cmd)
    {
        switch (cmd.Type)
        {
            case ProGPU.Scene.RenderCommandType.DrawRect:
                {
                    var brush = MapBrush(cmd.Brush);
                    var pen = MapPen(cmd.Pen);
                    context.DrawRectangle(brush, pen, new Avalonia.Rect(cmd.Rect.X, cmd.Rect.Y, cmd.Rect.Width, cmd.Rect.Height));
                }
                break;

            case ProGPU.Scene.RenderCommandType.DrawPath:
                {
                    var brush = MapBrush(cmd.Brush);
                    var pen = MapPen(cmd.Pen);
                    var geometry = MapGeometry(cmd.Path);
                    if (geometry != null)
                    {
                        context.DrawGeometry(brush, pen, geometry);
                    }
                }
                break;

            case ProGPU.Scene.RenderCommandType.DrawLine:
                {
                    var pen = MapPen(cmd.Pen) ?? new Avalonia.Media.Pen(Avalonia.Media.Brushes.Black, 1.0);
                    context.DrawLine(pen, new Avalonia.Point(cmd.Rect.X, cmd.Rect.Y), new Avalonia.Point(cmd.Rect.Width, cmd.Rect.Height));
                }
                break;

            case ProGPU.Scene.RenderCommandType.DrawText:
                {
                    if (!string.IsNullOrEmpty(cmd.Text))
                    {
                        var foreground = MapBrush(cmd.Brush) ?? Avalonia.Media.Brushes.White;
                        var typeface = new Avalonia.Media.Typeface(Avalonia.Media.FontFamily.Default);
                        var formattedText = new Avalonia.Media.FormattedText(
                            cmd.Text,
                            System.Globalization.CultureInfo.InvariantCulture,
                            Avalonia.Media.FlowDirection.LeftToRight,
                            typeface,
                            12.0,
                            foreground
                        );
                        context.DrawText(formattedText, new Avalonia.Point(cmd.Position.X, cmd.Position.Y));
                    }
                }
                break;
        }
    }

    private Avalonia.Media.IBrush? MapBrush(ProGPU.Vector.Brush? brush)
    {
        if (brush is ProGPU.Vector.SolidColorBrush solid)
        {
            var col = solid.Color;
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(
                (byte)(col.W * 255), (byte)(col.X * 255), (byte)(col.Y * 255), (byte)(col.Z * 255)));
        }
        return null;
    }

    private Avalonia.Media.IPen? MapPen(ProGPU.Vector.Pen? pen)
    {
        if (pen == null) return null;
        var brush = MapBrush(pen.Brush);
        return new Avalonia.Media.Pen(brush, pen.Thickness);
    }

    private Avalonia.Media.Geometry? MapGeometry(ProGPU.Vector.PathGeometry? pathGeom)
    {
        if (pathGeom == null) return null;

        var streamGeom = new Avalonia.Media.StreamGeometry();
        using (var ctx = streamGeom.Open())
        {
            foreach (var figure in pathGeom.Figures)
            {
                ctx.BeginFigure(new Avalonia.Point(figure.StartPoint.X, figure.StartPoint.Y), figure.IsFilled);
                foreach (var segment in figure.Segments)
                {
                    if (segment is ProGPU.Vector.LineSegment line)
                    {
                        ctx.LineTo(new Avalonia.Point(line.Point.X, line.Point.Y));
                    }
                    else if (segment is ProGPU.Vector.QuadraticBezierSegment quad)
                    {
                        ctx.QuadraticBezierTo(
                            new Avalonia.Point(quad.ControlPoint.X, quad.ControlPoint.Y),
                            new Avalonia.Point(quad.Point.X, quad.Point.Y)
                        );
                    }
                    else if (segment is ProGPU.Vector.CubicBezierSegment cubic)
                    {
                        ctx.CubicBezierTo(
                            new Avalonia.Point(cubic.ControlPoint1.X, cubic.ControlPoint1.Y),
                            new Avalonia.Point(cubic.ControlPoint2.X, cubic.ControlPoint2.Y),
                            new Avalonia.Point(cubic.Point.X, cubic.Point.Y)
                        );
                    }
                    else if (segment is ProGPU.Vector.ArcSegment arc)
                    {
                        var sweep = arc.SweepDirection == ProGPU.Vector.SweepDirection.Clockwise
                            ? Avalonia.Media.SweepDirection.Clockwise
                            : Avalonia.Media.SweepDirection.CounterClockwise;
                        ctx.ArcTo(
                            new Avalonia.Point(arc.Point.X, arc.Point.Y),
                            new Avalonia.Size(arc.Size.X, arc.Size.Y),
                            arc.RotationAngle,
                            arc.IsLargeArc,
                            sweep
                        );
                    }
                }
                ctx.EndFigure(figure.IsClosed);
            }
        }
        return streamGeom;
    }
}
