using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ProGPU.Vector;
using ProGPU.Scene;

namespace TypedJint;

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

    public object? getContext(string contextId)
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

    public void fillRect(double x, double y, double w, double h)
    {
        var brush = CreateBrush(_fillStyle);
        _drawingContext.DrawRectangle(brush, null, new Rect((float)x, (float)y, (float)w, (float)h));
    }

    public void strokeRect(double x, double y, double w, double h)
    {
        var pen = CreatePen(_strokeStyle, _lineWidth);
        _drawingContext.DrawRectangle(null, pen, new Rect((float)x, (float)y, (float)w, (float)h));
    }

    public void clearRect(double x, double y, double w, double h)
    {
        // Mock clearRect as drawing transparent rectangle
        _drawingContext.DrawRectangle(new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f)), null, new Rect((float)x, (float)y, (float)w, (float)h));
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

    public void stroke()
    {
        if (_pendingRect.HasValue)
        {
            var pen = CreatePen(_strokeStyle, _lineWidth);
            _drawingContext.DrawRectangle(null, pen, _pendingRect.Value);
            _pendingRect = null;
            return;
        }
        if (_currentPath.Count < 2) return;
        var penStyle = CreatePen(_strokeStyle, _lineWidth);
        var path = RenderCommandGeometryCache.CreatePolylinePath(_currentPath.ToArray(), false);
        _drawingContext.DrawPath(null, penStyle, path);
    }

    public void fill()
    {
        if (_pendingRect.HasValue)
        {
            var brush = CreateBrush(_fillStyle);
            _drawingContext.DrawRectangle(brush, null, _pendingRect.Value);
            _pendingRect = null;
            return;
        }
        if (_currentPath.Count < 3) return;
        var brushStyle = CreateBrush(_fillStyle);
        var path = RenderCommandGeometryCache.CreatePolylinePath(_currentPath.ToArray(), true);
        _drawingContext.DrawPath(brushStyle, null, path);
    }

    public void arc(double x, double y, double radius, double startAngle, double endAngle, bool anticlockwise = false)
    {
        // Simple arc approximation using points
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
        // Approximate cubic Bezier curve
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
        // Approximate quadratic Bezier curve
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
        // Mock text rendering to drawingContext Commands
        _drawingContext.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = text,
            Position = Vector2.Transform(new Vector2((float)x, (float)y), _currentTransform),
            Brush = CreateBrush(_fillStyle)
        });
    }

    public void strokeText(string text, double x, double y)
    {
        // Mock text stroke
        _drawingContext.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = text,
            Position = Vector2.Transform(new Vector2((float)x, (float)y), _currentTransform),
            Brush = CreateBrush(_strokeStyle)
        });
    }

    public TextMetrics measureText(string text)
    {
        // Estimate width (approx 8 pixels per char)
        return new TextMetrics(text.Length * 8.0);
    }

    public void drawImage(object image, double dx, double dy)
    {
        // Mock drawImage
    }

    public void drawImage(object image, double dx, double dy, double dw, double dh)
    {
        // Mock drawImage
    }

    public void drawImage(object image, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        // Mock drawImage
    }

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
        if (style.Equals("red", StringComparison.OrdinalIgnoreCase)) return new Vector4(1f, 0f, 0f, 1f);
        if (style.Equals("green", StringComparison.OrdinalIgnoreCase)) return new Vector4(0f, 1f, 0f, 1f);
        if (style.Equals("blue", StringComparison.OrdinalIgnoreCase)) return new Vector4(0f, 0f, 1f, 1f);
        if (style.Equals("white", StringComparison.OrdinalIgnoreCase)) return new Vector4(1f, 1f, 1f, 1f);
        if (style.Equals("black", StringComparison.OrdinalIgnoreCase)) return new Vector4(0f, 0f, 0f, 1f);
        if (style.StartsWith('#') && style.Length == 7)
        {
            var r = byte.Parse(style.Substring(1, 2), NumberStyles.HexNumber) / 255.0f;
            var g = byte.Parse(style.Substring(3, 2), NumberStyles.HexNumber) / 255.0f;
            var b = byte.Parse(style.Substring(5, 2), NumberStyles.HexNumber) / 255.0f;
            return new Vector4(r, g, b, 1f);
        }
        return new Vector4(0f, 0f, 0f, 1f);
    }
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

// WebGL context elements
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

    // Standard WebGL Constants as instance properties
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

    public void viewport(int x, int y, int width, int height) { }
    public void clearColor(float r, float g, float b, float a) { }
    public void clear(uint mask) { }

    public WebGLShader createShader(uint type) => new WebGLShader();
    public void shaderSource(WebGLShader shader, string source) { }
    public void compileShader(WebGLShader shader) { }
    public object getShaderParameter(WebGLShader shader, uint pname)
    {
        if (pname == COMPILE_STATUS) return true;
        return 0;
    }
    public string getShaderInfoLog(WebGLShader shader) => string.Empty;

    public WebGLProgram createProgram() => new WebGLProgram();
    public void attachShader(WebGLProgram program, WebGLShader shader) { }
    public void linkProgram(WebGLProgram program) { }
    public object getProgramParameter(WebGLProgram program, uint pname)
    {
        if (pname == LINK_STATUS || pname == VALIDATE_STATUS) return true;
        return 0;
    }
    public string getProgramInfoLog(WebGLProgram program) => string.Empty;
    public void useProgram(WebGLProgram program) { }

    public WebGLBuffer createBuffer() => new WebGLBuffer();
    public void bindBuffer(uint target, WebGLBuffer? buffer) { }
    public void bufferData(uint target, object data, uint usage) { }

    public void enableVertexAttribArray(uint index) { }
    public void disableVertexAttribArray(uint index) { }
    public void vertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, long offset) { }
    public void drawArrays(uint mode, int first, int count) { }
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
    public void bindTexture(uint target, WebGLTexture? texture) { }
    public void texImage2D(uint target, int level, int internalformat, int width, int height, int border, uint format, uint type, object pixels) { }
    public void texImage2D(uint target, int level, int internalformat, uint format, uint type, object source) { }
    public void texParameteri(uint target, uint pname, int param) { }

    public WebGLActiveInfo getActiveUniform(WebGLProgram program, uint index) => new WebGLActiveInfo("uniform", 1, FLOAT);
    public WebGLActiveInfo getActiveAttrib(WebGLProgram program, uint index) => new WebGLActiveInfo("attribute", 1, FLOAT);

    public object? getParameter(uint pname) => null;
    public object? getExtension(string name) => null;
    public void pixelStorei(uint pname, int param) { }
    public void depthFunc(uint func) { }
    public void enable(uint cap) { }
    public void disable(uint cap) { }
    public void blendFunc(uint sfactor, uint dfactor) { }

    public WebGLFramebuffer createFramebuffer() => new WebGLFramebuffer();
    public void bindFramebuffer(uint target, WebGLFramebuffer? framebuffer) { }
    public void framebufferTexture2D(uint target, uint attachment, uint textarget, WebGLTexture? texture, int level) { }

    public WebGLRenderbuffer createRenderbuffer() => new WebGLRenderbuffer();
    public void bindRenderbuffer(uint target, WebGLRenderbuffer? renderbuffer) { }
    public void renderbufferStorage(uint target, uint internalformat, int width, int height) { }
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
