using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TypedJint;

namespace TypedJint.Runtime;

public sealed class DomWindow : DomEventTarget
{
    public DomWindow(DomDocument document)
    {
        this.document = document;
        this.location = new DomLocation();
        this.navigator = new DomNavigator();
        this.localStorage = new DomStorage();
        this.sessionStorage = new DomStorage();
        this.performance = new DomPerformance();
    }

    public DomDocument document { get; }
    public DomLocation location { get; }
    public DomNavigator navigator { get; }
    public DomStorage localStorage { get; }
    public DomStorage sessionStorage { get; }
    public DomPerformance performance { get; }

    public double devicePixelRatio => 1.0;
    public double innerWidth => 800.0;
    public double innerHeight => 600.0;
    public CssStyleDeclaration getComputedStyle(DomElement element) => element.style;

    public double requestAnimationFrame(object callback) => JavaScriptTime.requestAnimationFrame(callback);
    public void cancelAnimationFrame(double id) => JavaScriptTime.cancelAnimationFrame(id);
    public DomMediaQueryList matchMedia(string query) => new DomMediaQueryList { media = query };
}

public sealed class DomMediaQueryList
{
    public bool matches => false;
    public string media { get; set; } = string.Empty;
    public void addListener(object? listener) {}
    public void removeListener(object? listener) {}
    public void addEventListener(string type, object? listener) {}
    public void removeEventListener(string type, object? listener) {}
}

public sealed class DomLocation
{
    public string href { get; set; } = "http://localhost/";
    public string search { get; set; } = string.Empty;
    public string hash { get; set; } = string.Empty;
    public string pathname { get; set; } = "/";
    public string origin { get; set; } = "http://localhost";
    public void reload() { }
}

public sealed class DomNavigator
{
    public string userAgent => "Mozilla/5.0 (Mock; TypedJint)";
    public string language => "en-US";
    public string[] languages => new[] { "en-US", "en" };
    public string platform => "mac";
    public bool onLine => true;
}

public sealed class DomStorage
{
    private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);
    public int length => _store.Count;
    public string? getItem(string key) => _store.TryGetValue(key, out var val) ? val : null;
    public void setItem(string key, string value) => _store[key] = value;
    public void removeItem(string key) => _store.Remove(key);
    public void clear() => _store.Clear();
    public string? key(int index) => _store.Keys.ElementAtOrDefault(index);
}

public sealed class DomPerformance
{
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
    public double now() => System.Diagnostics.Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
}

public class DomEvent
{
    public DomEvent(string type) => this.type = type;
    public string type { get; }
    public DomEventTarget? target { get; internal set; }
    public DomEventTarget? currentTarget { get; internal set; }
    public bool defaultPrevented { get; private set; }
    public void preventDefault() => defaultPrevented = true;
}

public interface IDomEventListener { void HandleEvent(DomEvent ev); }

public class DomEventTarget
{
    private readonly Dictionary<string, List<object>> _listeners = new(StringComparer.Ordinal);

    public virtual void addEventListener(string type, object listener) => addEventListener(type, listener, null);
    public virtual void addEventListener(string type, object listener, object? options)
    {
        if (!_listeners.TryGetValue(type, out var listeners))
        {
            listeners = new List<object>();
            _listeners[type] = listeners;
        }

        listeners.Add(listener);
    }

    public virtual void removeEventListener(string type, object listener) => removeEventListener(type, listener, null);
    public virtual void removeEventListener(string type, object listener, object? options)
    {
        if (_listeners.TryGetValue(type, out var listeners))
        {
            listeners.Remove(listener);
        }
    }

    public bool dispatchEvent(DomEvent ev)
    {
        ev.target = this;
        if (!_listeners.TryGetValue(ev.type, out var listeners))
        {
            return !ev.defaultPrevented;
        }

        foreach (var listener in listeners.ToArray())
        {
            if (listener is IDomEventListener typed) typed.HandleEvent(ev);
            if (listener is Action<DomEvent> action) action(ev);
        }

        return !ev.defaultPrevented;
    }
}

public class DomNode : DomEventTarget, IDictionary<string, object?>
{
    private readonly List<DomNode> _children = new();
    private string? _textContent;
    private readonly Dictionary<string, object?> _expando = new(StringComparer.Ordinal);

    // IDictionary<string, object?> implementation
    public object? this[string key] { get => _expando.TryGetValue(key, out var val) ? val : null; set => _expando[key] = value; }
    public ICollection<string> Keys => _expando.Keys;
    public ICollection<object?> Values => _expando.Values;
    public int Count => _expando.Count;
    public bool IsReadOnly => false;
    public void Add(string key, object? value) => _expando.Add(key, value);
    public void Add(KeyValuePair<string, object?> item) => ((IDictionary<string, object?>)_expando).Add(item);
    public void Clear() => _expando.Clear();
    public bool Contains(KeyValuePair<string, object?> item) => ((IDictionary<string, object?>)_expando).Contains(item);
    public bool ContainsKey(string key) => _expando.ContainsKey(key);
    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => ((IDictionary<string, object?>)_expando).CopyTo(array, arrayIndex);
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _expando.GetEnumerator();
    public bool Remove(string key) => _expando.Remove(key);
    public bool Remove(KeyValuePair<string, object?> item) => ((IDictionary<string, object?>)_expando).Remove(item);
    public bool TryGetValue(string key, out object? value) => _expando.TryGetValue(key, out value);
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_expando).GetEnumerator();

    public DomNode(string nodeName) => this.nodeName = nodeName;
    public string nodeName { get; }
    public DomNode? parentNode { get; private set; }
    public IReadOnlyList<DomNode> childNodes => _children;

    public Avalonia.Controls.Control? AvaloniaControl { get; set; }
    public DomDocument ownerDocument => JavaScriptRuntimeEngine.CurrentDocument;

    public virtual string? textContent
    {
        get => _children.Count == 0 ? _textContent : string.Concat(_children.Select(x => x.textContent));
        set
        {
            _children.Clear();
            _textContent = value;
            OnTextContentChanged(value);
        }
    }

    protected virtual void OnTextContentChanged(string? value)
    {
    }

    public DomNode appendChild(DomNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.parentNode = this;
        _children.Add(child);
        OnChildAdded(child);
        return child;
    }

    public DomNode insertBefore(DomNode newChild, DomNode? refChild)
    {
        ArgumentNullException.ThrowIfNull(newChild);
        if (refChild == null)
        {
            return appendChild(newChild);
        }
        int index = _children.IndexOf(refChild);
        if (index >= 0)
        {
            newChild.parentNode?.removeChild(newChild);
            _children.Insert(index, newChild);
            newChild.parentNode = this;
            OnChildInserted(newChild, index);
        }
        else
        {
            appendChild(newChild);
        }
        return newChild;
    }

    protected virtual void OnChildInserted(DomNode child, int index)
    {
        if (AvaloniaControl is null || child.AvaloniaControl is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (AvaloniaControl is Avalonia.Controls.Panel panel)
            {
                if (!panel.Children.Contains(child.AvaloniaControl))
                {
                    panel.Children.Insert(index, child.AvaloniaControl);
                }
            }
            else if (AvaloniaControl is Avalonia.Controls.Decorator decorator)
            {
                decorator.Child = child.AvaloniaControl;
            }
            else if (AvaloniaControl is Avalonia.Controls.ContentControl cc)
            {
                cc.Content = child.AvaloniaControl;
            }
        });
    }

    protected virtual void OnChildAdded(DomNode child)
    {
        if (AvaloniaControl is null || child.AvaloniaControl is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (AvaloniaControl is Avalonia.Controls.Panel panel)
            {
                if (!panel.Children.Contains(child.AvaloniaControl))
                {
                    panel.Children.Add(child.AvaloniaControl);
                }
            }
            else if (AvaloniaControl is Avalonia.Controls.Decorator decorator)
            {
                decorator.Child = child.AvaloniaControl;
            }
            else if (AvaloniaControl is Avalonia.Controls.ContentControl cc)
            {
                cc.Content = child.AvaloniaControl;
            }
        });
    }

    public DomNode removeChild(DomNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (_children.Remove(child))
        {
            child.parentNode = null;
            OnChildRemoved(child);
        }
        return child;
    }

    protected virtual void OnChildRemoved(DomNode child)
    {
        if (AvaloniaControl is null || child.AvaloniaControl is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (AvaloniaControl is Avalonia.Controls.Panel panel)
            {
                panel.Children.Remove(child.AvaloniaControl);
            }
            else if (AvaloniaControl is Avalonia.Controls.Decorator decorator)
            {
                if (ReferenceEquals(decorator.Child, child.AvaloniaControl)) decorator.Child = null;
            }
            else if (AvaloniaControl is Avalonia.Controls.ContentControl cc)
            {
                if (ReferenceEquals(cc.Content, child.AvaloniaControl)) cc.Content = null;
            }
        });
    }
}

public sealed class DomTextNode : DomNode
{
    public DomTextNode(string text) : base("#text") => textContent = text;
}

public sealed class DomTokenList
{
    private readonly SortedSet<string> _tokens = new(StringComparer.Ordinal);
    public int length => _tokens.Count;
    public void add(string token) => _tokens.Add(token);
    public void remove(string token) => _tokens.Remove(token);
    public bool contains(string token) => _tokens.Contains(token);
    public void toggle(string token) { if (!_tokens.Remove(token)) _tokens.Add(token); }
    public override string ToString() => string.Join(" ", _tokens);
}

public sealed class CssStyleDeclaration
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public DomElement? Owner { get; set; }

    public string? width { get => getPropertyValue("width"); set => SetOrRemove("width", value); }
    public string? height { get => getPropertyValue("height"); set => SetOrRemove("height", value); }
    public string? color { get => getPropertyValue("color"); set => SetOrRemove("color", value); }
    public string? backgroundColor { get => getPropertyValue("background-color"); set => SetOrRemove("background-color", value); }
    public string? display { get => getPropertyValue("display"); set => SetOrRemove("display", value); }
    public string? fontSize { get => getPropertyValue("font-size"); set => SetOrRemove("font-size", value); }
    public string? fontFamily { get => getPropertyValue("font-family"); set => SetOrRemove("font-family", value); }
    public string? lineHeight { get => getPropertyValue("line-height"); set => SetOrRemove("line-height", value); }

    public string? this[string name]
    {
        get => getPropertyValue(name);
        set => SetOrRemove(name, value);
    }

    public string? getPropertyValue(string name)
    {
        if (_values.TryGetValue(name, out var value))
        {
            if (string.Equals(name, "color", StringComparison.OrdinalIgnoreCase) || 
                string.Equals(name, "background-color", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeColorToRgb(value);
            }
            return value;
        }

        var normName = name.ToLowerInvariant();
        switch (normName)
        {
            case "font-size":
            case "fontsize":
                return "12px";
            case "font-family":
            case "fontfamily":
                return "sans-serif";
            case "line-height":
            case "lineheight":
                return "14px";
            case "padding":
            case "padding-top":
            case "padding-bottom":
            case "padding-left":
            case "padding-right":
                return "0px";
            case "margin":
            case "margin-top":
            case "margin-bottom":
            case "margin-left":
            case "margin-right":
                return "0px";
            case "border-width":
            case "border-top-width":
            case "border-bottom-width":
            case "border-left-width":
            case "border-right-width":
                return "0px";
            case "box-sizing":
            case "boxsizing":
                return "content-box";
        }
        return null;
    }

    private static string NormalizeColorToRgb(string color)
    {
        if (string.IsNullOrWhiteSpace(color)) return "rgb(0, 0, 0)";
        color = color.Trim().ToLowerInvariant();
        
        if (color.StartsWith("#"))
        {
            var hex = color.Substring(1);
            try
            {
                if (hex.Length == 3)
                {
                    int r = Convert.ToInt32(new string(hex[0], 2), 16);
                    int g = Convert.ToInt32(new string(hex[1], 2), 16);
                    int b = Convert.ToInt32(new string(hex[2], 2), 16);
                    return $"rgb({r}, {g}, {b})";
                }
                if (hex.Length == 6)
                {
                    int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                    return $"rgb({r}, {g}, {b})";
                }
                if (hex.Length == 8)
                {
                    int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                    double a = Math.Round(Convert.ToInt32(hex.Substring(6, 2), 16) / 255.0, 2);
                    return $"rgba({r}, {g}, {b}, {a})";
                }
            }
            catch
            {
                // ignore and fallback
            }
        }
        
        return color;
    }
    
    public void setProperty(string name, string value) => setProperty(name, value, null);
    public void setProperty(string name, string value, string? priority)
    {
        _values[name] = value;
        Owner?.OnStyleChanged(name, value);
    }
    
    public void removeProperty(string name)
    {
        _values.Remove(name);
        Owner?.OnStyleChanged(name, null);
    }
    
    private void SetOrRemove(string name, string? value)
    {
        if (value is null) _values.Remove(name); else _values[name] = value;
        Owner?.OnStyleChanged(name, value);
    }
}

public class DomElement : DomNode
{
    private readonly Dictionary<string, string> _attributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _wiredEvents = new(StringComparer.OrdinalIgnoreCase);

    public DomElement(string tagName) : base(tagName.ToUpperInvariant())
    {
        this.tagName = tagName.ToUpperInvariant();
        style.Owner = this;
    }
    public string tagName { get; }
    public string id { get => getAttribute("id") ?? string.Empty; set => setAttribute("id", value); }
    public DomTokenList classList { get; } = new();
    public CssStyleDeclaration style { get; } = new();
    
    public string? getAttribute(string name) => _attributes.TryGetValue(name, out var value) ? value : null;
    public bool hasAttribute(string name) => _attributes.ContainsKey(name);
    public void setAttribute(string name, string value) => _attributes[name] = value;
    public void removeAttribute(string name) => _attributes.Remove(name);
    public bool toggleAttribute(string qualifiedName) => toggleAttribute(qualifiedName, null);
    public bool toggleAttribute(string qualifiedName, bool? force)
    {
        bool hasAttr = hasAttribute(qualifiedName);
        bool shouldHave = force ?? !hasAttr;
        if (shouldHave)
        {
            setAttribute(qualifiedName, string.Empty);
            return true;
        }
        else
        {
            removeAttribute(qualifiedName);
            return false;
        }
    }
    public DomElement? querySelector(string selector) => QuerySelectorAllCore(this, selector).FirstOrDefault();
    public IReadOnlyList<DomElement> querySelectorAll(string selector) => QuerySelectorAllCore(this, selector).ToArray();

    public double clientWidth
    {
        get
        {
            if (AvaloniaControl != null)
            {
                if (AvaloniaControl.Bounds.Width > 0)
                {
                    return AvaloniaControl.Bounds.Width;
                }
                if (style != null)
                {
                    var w = style.getPropertyValue("width") ?? style.width;
                    if (!string.IsNullOrWhiteSpace(w))
                    {
                        var norm = w.Replace("px", "").Trim();
                        if (double.TryParse(norm, CultureInfo.InvariantCulture, out var parsed))
                        {
                            return parsed;
                        }
                    }
                }
                if (this is HTMLCanvasElement canvas)
                {
                    return canvas.width;
                }
                return 0.0;
            }
            return 800.0;
        }
    }

    public double clientHeight
    {
        get
        {
            if (AvaloniaControl != null)
            {
                if (AvaloniaControl.Bounds.Height > 0)
                {
                    return AvaloniaControl.Bounds.Height;
                }
                if (style != null)
                {
                    var h = style.getPropertyValue("height") ?? style.height;
                    if (!string.IsNullOrWhiteSpace(h))
                    {
                        var norm = h.Replace("px", "").Trim();
                        if (double.TryParse(norm, CultureInfo.InvariantCulture, out var parsed))
                        {
                            return parsed;
                        }
                    }
                }
                if (this is HTMLCanvasElement canvas)
                {
                    return canvas.height;
                }
                return 0.0;
            }
            return 600.0;
        }
    }

    public DomRect getBoundingClientRect()
    {
        var w = clientWidth;
        var h = clientHeight;
        if (AvaloniaControl != null)
        {
            var bounds = AvaloniaControl.Bounds;
            return new DomRect(bounds.X, bounds.Y, w, h);
        }
        return new DomRect(0, 0, w, h);
    }

    public DomRect[] getClientRects()
    {
        return new[] { getBoundingClientRect() };
    }

    public override void addEventListener(string type, object listener)
    {
        base.addEventListener(type, listener);
        WireAvaloniaEvent(type);
    }

    private void WireAvaloniaEvent(string type)
    {
        if (AvaloniaControl is null) return;
        if (!_wiredEvents.Add(type)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (string.Equals(type, "click", StringComparison.OrdinalIgnoreCase))
            {
                if (AvaloniaControl is Avalonia.Controls.Button btn)
                {
                    btn.Click += (s, e) =>
                    {
                        var ev = new DomEvent("click") { target = this, currentTarget = this };
                        dispatchEvent(ev);
                    };
                }
            }
            else if (string.Equals(type, "pointerdown", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "mousedown", StringComparison.OrdinalIgnoreCase))
            {
                AvaloniaControl.PointerPressed += (s, e) =>
                {
                    var pt = e.GetPosition(AvaloniaControl);
                    var btnCode = e.GetCurrentPoint(AvaloniaControl).Properties.IsLeftButtonPressed ? 0 : 2;
                    var ev = new DomPointerEvent(type, pt.X, pt.Y, btnCode)
                    {
                        target = this,
                        currentTarget = this
                    };
                    dispatchEvent(ev);
                };
            }
            else if (string.Equals(type, "pointerup", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "mouseup", StringComparison.OrdinalIgnoreCase))
            {
                AvaloniaControl.PointerReleased += (s, e) =>
                {
                    var pt = e.GetPosition(AvaloniaControl);
                    var btnCode = e.GetCurrentPoint(AvaloniaControl).Properties.PointerUpdateKind == Avalonia.Input.PointerUpdateKind.LeftButtonReleased ? 0 : 2;
                    var ev = new DomPointerEvent(type, pt.X, pt.Y, btnCode)
                    {
                        target = this,
                        currentTarget = this
                    };
                    dispatchEvent(ev);
                };
            }
            else if (string.Equals(type, "pointermove", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "mousemove", StringComparison.OrdinalIgnoreCase))
            {
                AvaloniaControl.PointerMoved += (s, e) =>
                {
                    var pt = e.GetPosition(AvaloniaControl);
                    var ev = new DomPointerEvent(type, pt.X, pt.Y, 0)
                    {
                        target = this,
                        currentTarget = this
                    };
                    dispatchEvent(ev);
                };
            }
        });
    }

    public virtual void OnStyleChanged(string name, string? value)
    {
        if (AvaloniaControl is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (string.Equals(name, "width", StringComparison.OrdinalIgnoreCase))
            {
                var norm = value?.Replace("px", "").Trim();
                if (double.TryParse(norm, CultureInfo.InvariantCulture, out var w)) AvaloniaControl.Width = w;
            }
            else if (string.Equals(name, "height", StringComparison.OrdinalIgnoreCase))
            {
                var norm = value?.Replace("px", "").Trim();
                if (double.TryParse(norm, CultureInfo.InvariantCulture, out var h)) AvaloniaControl.Height = h;
            }
            else if (string.Equals(name, "background-color", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "backgroundColor", StringComparison.OrdinalIgnoreCase))
            {
                if (TryCreateBrush(value, out var brush))
                {
                    if (AvaloniaControl is Avalonia.Controls.Primitives.TemplatedControl tc) tc.Background = brush;
                    else if (AvaloniaControl is Avalonia.Controls.Panel panel) panel.Background = brush;
                }
            }
            else if (string.Equals(name, "color", StringComparison.OrdinalIgnoreCase))
            {
                if (TryCreateBrush(value, out var brush))
                {
                    if (AvaloniaControl is Avalonia.Controls.TextBlock tb) tb.Foreground = brush;
                    else if (AvaloniaControl is Avalonia.Controls.Button btn) btn.Foreground = brush;
                    else if (AvaloniaControl is Avalonia.Controls.Panel panel)
                    {
                        foreach (var childTb in panel.Children.OfType<Avalonia.Controls.TextBlock>())
                        {
                            childTb.Foreground = brush;
                        }
                    }
                }
            }
            else if (string.Equals(name, "display", StringComparison.OrdinalIgnoreCase))
            {
                if (AvaloniaControl is Avalonia.Controls.StackPanel sp)
                {
                    var isFlex = value?.Contains("flex", StringComparison.OrdinalIgnoreCase) ?? false;
                    var isInline = value?.Contains("inline", StringComparison.OrdinalIgnoreCase) ?? false;
                    
                    if (isFlex || isInline)
                    {
                        var dir = style.getPropertyValue("flex-direction") ?? style.getPropertyValue("flexDirection");
                        var isCol = dir?.Contains("column", StringComparison.OrdinalIgnoreCase) ?? false;
                        sp.Orientation = isCol ? Avalonia.Layout.Orientation.Vertical : Avalonia.Layout.Orientation.Horizontal;
                    }
                    else
                    {
                        // table elements, tr, etc., can be handled specifically, but default tr is horizontal
                        if (!string.Equals(tagName, "tr", StringComparison.OrdinalIgnoreCase))
                        {
                            sp.Orientation = Avalonia.Layout.Orientation.Vertical;
                        }
                    }
                }
            }
            else if (string.Equals(name, "flex-direction", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "flexDirection", StringComparison.OrdinalIgnoreCase))
            {
                if (AvaloniaControl is Avalonia.Controls.StackPanel sp)
                {
                    var isCol = value?.Contains("column", StringComparison.OrdinalIgnoreCase) ?? false;
                    sp.Orientation = isCol ? Avalonia.Layout.Orientation.Vertical : Avalonia.Layout.Orientation.Horizontal;
                }
            }
        });
    }

    protected override void OnTextContentChanged(string? value)
    {
        if (AvaloniaControl is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (AvaloniaControl is Avalonia.Controls.TextBlock tb)
            {
                tb.Text = value ?? string.Empty;
            }
            else if (AvaloniaControl is Avalonia.Controls.Button btn)
            {
                btn.Content = value;
            }
            else if (AvaloniaControl is Avalonia.Controls.Panel panel)
            {
                var textBlocks = panel.Children.OfType<Avalonia.Controls.TextBlock>().ToList();
                foreach (var oldTb in textBlocks)
                {
                    panel.Children.Remove(oldTb);
                }
                if (!string.IsNullOrEmpty(value))
                {
                    var newTb = new Avalonia.Controls.TextBlock { Text = value };
                    if (TryCreateBrush(style.color, out var brush))
                    {
                        newTb.Foreground = brush;
                    }
                    panel.Children.Add(newTb);
                }
            }
        });
    }

    private static bool TryCreateBrush(string? value, out Avalonia.Media.IBrush? brush)
    {
        brush = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            brush = Avalonia.Media.Brush.Parse(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static IEnumerable<DomElement> QuerySelectorAllCore(DomNode root, string selector)
    {
        foreach (var child in root.childNodes)
        {
            if (child is not DomElement element) continue;
            if (Matches(element, selector)) yield return element;
            foreach (var descendant in QuerySelectorAllCore(element, selector)) yield return descendant;
        }
    }

    private static bool Matches(DomElement element, string selector)
    {
        selector = selector.Trim();
        if (selector.StartsWith('#')) return string.Equals(element.id, selector[1..], StringComparison.Ordinal);
        if (selector.StartsWith('.')) return element.classList.contains(selector[1..]);
        return string.Equals(element.tagName, selector.ToUpperInvariant(), StringComparison.Ordinal);
    }
}

public sealed class DomDocument : DomNode
{
    public DomDocument() : base("#document")
    {
        documentElement = new DomElement("html");
        body = new DomElement("body")
        {
            AvaloniaControl = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical }
        };
        body.style.Owner = body;
        appendChild(documentElement);
        documentElement.appendChild(body);
    }

    public DomElement documentElement { get; }
    public DomElement body { get; }
    public DomWindow defaultView => JavaScriptRuntimeEngine.CurrentWindow;
    public DomElement createElement(string tagName)
    {
        DomElement element;
        if (string.Equals(tagName, "canvas", StringComparison.OrdinalIgnoreCase))
        {
            var canvas = new HTMLCanvasElement();
            canvas.AvaloniaControl = new AvaloniaCanvasHost(canvas);
            element = canvas;
        }
        else if (string.Equals(tagName, "button", StringComparison.OrdinalIgnoreCase))
        {
            element = new DomElement(tagName)
            {
                AvaloniaControl = new Avalonia.Controls.Button()
            };
        }
        else if (string.Equals(tagName, "input", StringComparison.OrdinalIgnoreCase))
        {
            var tb = new Avalonia.Controls.TextBox();
            element = new DomElement(tagName)
            {
                AvaloniaControl = tb
            };
            tb.TextChanged += (s, e) =>
            {
                element.setAttribute("value", tb.Text ?? string.Empty);
            };
        }
        else if (string.Equals(tagName, "span", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tagName, "p", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tagName, "h1", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tagName, "h2", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tagName, "h3", StringComparison.OrdinalIgnoreCase))
        {
            element = new DomElement(tagName)
            {
                AvaloniaControl = new Avalonia.Controls.TextBlock()
            };
        }
        else if (string.Equals(tagName, "tr", StringComparison.OrdinalIgnoreCase))
        {
            element = new DomElement(tagName)
            {
                AvaloniaControl = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal }
            };
        }
        else
        {
            element = new DomElement(tagName)
            {
                AvaloniaControl = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical }
            };
        }

        element.style.Owner = element;
        return element;
    }

    public DomElement createElementNS(string? namespaceURI, string qualifiedName)
    {
        return createElement(qualifiedName);
    }
    public DomTextNode createTextNode(string text) => new(text);
    public DomElement? getElementById(string id) => DomElement.QuerySelectorAllCore(this, "#" + id).FirstOrDefault();
    public DomElement? querySelector(string selector) => DomElement.QuerySelectorAllCore(this, selector).FirstOrDefault();
    public IReadOnlyList<DomElement> querySelectorAll(string selector) => DomElement.QuerySelectorAllCore(this, selector).ToArray();
}

public sealed class DomRect
{
    public DomRect(double x, double y, double width, double height)
    {
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
    }
    public double x { get; }
    public double y { get; }
    public double width { get; }
    public double height { get; }
    public double top => y;
    public double left => x;
    public double right => x + width;
    public double bottom => y + height;
}
