using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Jint;

namespace TypedJint;

public enum TypedBackendKind
{
    ExpressionTrees,
    CSharp,
    IL
}

public enum TypedCompilationMode
{
    Disabled,
    CompileAnnotatedFunctionsOnly,
    CompileSafeFunctionsOnly,
    CompileAggressively
}

public sealed class TypedJintOptions
{
    public bool EnableCompilation { get; init; } = true;
    public bool ExecuteOriginalSourceInJint { get; init; } = true;
    public TypedCompilationMode CompilationMode { get; init; } = TypedCompilationMode.CompileAnnotatedFunctionsOnly;
    public TypedBackendKind Backend { get; init; } = TypedBackendKind.ExpressionTrees;
    public bool ThrowOnCompilationFailure { get; init; }
    public Action<TypedDiagnostic>? DiagnosticSink { get; init; }
}

public enum TypedDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record TypedDiagnostic(string Code, TypedDiagnosticSeverity Severity, string Message, SourceSpan? Span = null);
public sealed record SourceSpan(int Start, int Length, int Line, int Column);
public sealed record FallbackInfo(string FunctionName, string Reason, SourceSpan? Span = null);

public sealed class TypedCompilationResult
{
    public required IReadOnlyDictionary<string, ICompiledFunction> CompiledFunctions { get; init; }
    public required IReadOnlyDictionary<string, FallbackInfo> Fallbacks { get; init; }
    public required IReadOnlyList<TypedDiagnostic> Diagnostics { get; init; }
}

public interface ICompiledFunction
{
    string Name { get; }
    Delegate Delegate { get; }
    object? Invoke(params object?[] arguments);
}

public sealed class CompiledFunction : ICompiledFunction
{
    public required string Name { get; init; }
    public required Delegate Delegate { get; init; }

    public object? Invoke(params object?[] arguments) => Delegate.DynamicInvoke(arguments);
}

public sealed class TypedJintEngine
{
    private readonly TypedJintOptions _options;
    private readonly Engine _jint;
    private readonly ConcurrentDictionary<string, ICompiledFunction> _compiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _globals = new(StringComparer.Ordinal);

    public TypedJintEngine(TypedJintOptions? options = null)
    {
        _options = options ?? new TypedJintOptions();
        _jint = new Engine(cfg => cfg.AllowClr());
        Document = new DomDocument();
        RegisterDom(Document);
    }

    public Engine Jint => _jint;
    public DomDocument Document { get; }

    public TypedJintEngine SetValue(string name, object? value)
    {
        _globals[name] = value;
        _jint.SetValue(name, value);
        return this;
    }

    public TypedJintEngine RegisterHostObject(string name, object instance) => SetValue(name, instance);

    public TypedJintEngine RegisterDom(DomDocument document)
    {
        SetValue("document", document);
        SetValue("window", new DomWindow(document));
        return this;
    }

    public TypedCompilationResult Execute(string source)
    {
        var result = _options.EnableCompilation
            ? new TypedJsCompiler(_globals, _options).Compile(source)
            : new TypedCompilationResult
            {
                CompiledFunctions = new Dictionary<string, ICompiledFunction>(),
                Fallbacks = new Dictionary<string, FallbackInfo>(),
                Diagnostics = Array.Empty<TypedDiagnostic>()
            };

        foreach (var function in result.CompiledFunctions)
        {
            _compiled[function.Key] = function.Value;
        }

        if (_options.ExecuteOriginalSourceInJint)
        {
            _jint.Execute(source);
        }

        return result;
    }

    public object? Invoke(string functionName, params object?[] arguments)
    {
        if (_compiled.TryGetValue(functionName, out var compiled))
        {
            return compiled.Invoke(arguments);
        }

        return _jint.Invoke(functionName, arguments).ToObject();
    }
}

public sealed class DomWindow
{
    public DomWindow(DomDocument document) => document_ = document;
    public DomDocument document_ { get; }
}

public class DomEvent
{
    public DomEvent(string type) => this.type = type;
    public string type { get; }
    public DomEventTarget? target { get; internal set; }
    public bool defaultPrevented { get; private set; }
    public void preventDefault() => defaultPrevented = true;
}

public interface IDomEventListener
{
    void HandleEvent(DomEvent ev);
}

public class DomEventTarget
{
    private readonly Dictionary<string, List<object>> _listeners = new(StringComparer.Ordinal);

    public void addEventListener(string type, object listener)
    {
        if (!_listeners.TryGetValue(type, out var listeners))
        {
            listeners = new List<object>();
            _listeners[type] = listeners;
        }

        listeners.Add(listener);
    }

    public void removeEventListener(string type, object listener)
    {
        if (_listeners.TryGetValue(type, out var listeners))
        {
            _ = listeners.Remove(listener);
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
            if (listener is IDomEventListener typed)
            {
                typed.HandleEvent(ev);
            }
            else if (listener is Action<DomEvent> action)
            {
                action(ev);
            }
        }

        return !ev.defaultPrevented;
    }
}

public class DomNode : DomEventTarget
{
    private readonly List<DomNode> _children = new();
    private string? _textContent;

    public DomNode(string nodeName) => this.nodeName = nodeName;

    public string nodeName { get; }
    public DomNode? parentNode { get; private set; }
    public IReadOnlyList<DomNode> childNodes => _children;

    public virtual string? textContent
    {
        get => _children.Count == 0 ? _textContent : string.Concat(_children.Select(x => x.textContent));
        set
        {
            _children.Clear();
            _textContent = value;
        }
    }

    public DomNode appendChild(DomNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.parentNode = this;
        _children.Add(child);
        return child;
    }

    public DomNode removeChild(DomNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (_children.Remove(child))
        {
            child.parentNode = null;
        }
        return child;
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
    public void toggle(string token)
    {
        if (!_tokens.Remove(token))
        {
            _tokens.Add(token);
        }
    }
    public override string ToString() => string.Join(" ", _tokens);
}

public sealed class CssStyleDeclaration
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? width { get => getPropertyValue("width"); set => SetOrRemove("width", value); }
    public string? height { get => getPropertyValue("height"); set => SetOrRemove("height", value); }
    public string? color { get => getPropertyValue("color"); set => SetOrRemove("color", value); }
    public string? backgroundColor { get => getPropertyValue("background-color"); set => SetOrRemove("background-color", value); }
    public string? display { get => getPropertyValue("display"); set => SetOrRemove("display", value); }

    public string? getPropertyValue(string name) => _values.TryGetValue(name, out var value) ? value : null;
    public void setProperty(string name, string value) => _values[name] = value;
    public void removeProperty(string name) => _values.Remove(name);

    private void SetOrRemove(string name, string? value)
    {
        if (value is null)
        {
            _values.Remove(name);
        }
        else
        {
            _values[name] = value;
        }
    }
}

public class DomElement : DomNode
{
    private readonly Dictionary<string, string> _attributes = new(StringComparer.OrdinalIgnoreCase);

    public DomElement(string tagName) : base(tagName.ToUpperInvariant())
    {
        this.tagName = tagName.ToUpperInvariant();
    }

    public string tagName { get; }
    public string id { get => getAttribute("id") ?? string.Empty; set => setAttribute("id", value); }
    public DomTokenList classList { get; } = new();
    public CssStyleDeclaration style { get; } = new();

    public string? getAttribute(string name) => _attributes.TryGetValue(name, out var value) ? value : null;
    public bool hasAttribute(string name) => _attributes.ContainsKey(name);
    public void setAttribute(string name, string value) => _attributes[name] = value;
    public void removeAttribute(string name) => _attributes.Remove(name);

    public DomElement? querySelector(string selector) => QuerySelectorAllCore(this, selector).FirstOrDefault();
    public IReadOnlyList<DomElement> querySelectorAll(string selector) => QuerySelectorAllCore(this, selector).ToArray();

    internal static IEnumerable<DomElement> QuerySelectorAllCore(DomNode root, string selector)
    {
        foreach (var child in root.childNodes)
        {
            if (child is DomElement element)
            {
                if (Matches(element, selector))
                {
                    yield return element;
                }

                foreach (var descendant in QuerySelectorAllCore(element, selector))
                {
                    yield return descendant;
                }
            }
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
        body = new DomElement("body");
        appendChild(documentElement);
        documentElement.appendChild(body);
    }

    public DomElement documentElement { get; }
    public DomElement body { get; }

    public DomElement createElement(string tagName) => new(tagName);
    public DomTextNode createTextNode(string text) => new(text);
    public DomElement? getElementById(string id) => DomElement.QuerySelectorAllCore(this, "#" + id).FirstOrDefault();
    public DomElement? querySelector(string selector) => DomElement.QuerySelectorAllCore(this, selector).FirstOrDefault();
    public IReadOnlyList<DomElement> querySelectorAll(string selector) => DomElement.QuerySelectorAllCore(this, selector).ToArray();
}

public sealed class TypedJsCompiler
{
    private readonly IReadOnlyDictionary<string, object?> _globals;
    private readonly TypedJintOptions _options;
    private readonly List<TypedDiagnostic> _diagnostics = new();
    private readonly Dictionary<string, ICompiledFunction> _compiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FallbackInfo> _fallbacks = new(StringComparer.Ordinal);

    public TypedJsCompiler(IReadOnlyDictionary<string, object?> globals, TypedJintOptions options)
    {
        _globals = globals;
        _options = options;
    }

    public TypedCompilationResult Compile(string source)
    {
        foreach (var fn in SimpleJsParser.ParseFunctions(source))
        {
            if (_options.CompilationMode == TypedCompilationMode.CompileAnnotatedFunctionsOnly && fn.Annotation is null)
            {
                AddFallback(fn.Name, "Function has no JSDoc type annotation.", fn.Span);
                continue;
            }

            if (fn.Annotation is null)
            {
                AddFallback(fn.Name, "Only annotated functions are supported in phase one.", fn.Span);
                continue;
            }

            try
            {
                var del = new ExpressionTreeBackend(_globals).Compile(fn);
                _compiled[fn.Name] = new CompiledFunction { Name = fn.Name, Delegate = del };
                AddDiagnostic("TJ0400", TypedDiagnosticSeverity.Info, $"Compiled function '{fn.Name}'.", fn.Span);
            }
            catch (Exception ex) when (!_options.ThrowOnCompilationFailure)
            {
                AddFallback(fn.Name, ex.Message, fn.Span);
            }
        }

        return new TypedCompilationResult
        {
            CompiledFunctions = _compiled,
            Fallbacks = _fallbacks,
            Diagnostics = _diagnostics
        };
    }

    private void AddFallback(string functionName, string reason, SourceSpan? span)
    {
        _fallbacks[functionName] = new FallbackInfo(functionName, reason, span);
        AddDiagnostic("TJ0500", TypedDiagnosticSeverity.Warning, $"Function '{functionName}' uses Jint fallback: {reason}", span);
    }

    private void AddDiagnostic(string code, TypedDiagnosticSeverity severity, string message, SourceSpan? span)
    {
        var diagnostic = new TypedDiagnostic(code, severity, message, span);
        _diagnostics.Add(diagnostic);
        _options.DiagnosticSink?.Invoke(diagnostic);
    }
}

public enum JsStaticTypeKind
{
    Void,
    Number,
    String,
    Boolean,
    Object,
    Clr
}

public sealed record JsStaticType(JsStaticTypeKind Kind, Type ClrType)
{
    public static readonly JsStaticType Void = new(JsStaticTypeKind.Void, typeof(void));
    public static readonly JsStaticType Number = new(JsStaticTypeKind.Number, typeof(double));
    public static readonly JsStaticType String = new(JsStaticTypeKind.String, typeof(string));
    public static readonly JsStaticType Boolean = new(JsStaticTypeKind.Boolean, typeof(bool));
    public static readonly JsStaticType Object = new(JsStaticTypeKind.Object, typeof(object));
    public static JsStaticType Clr(Type type) => new(JsStaticTypeKind.Clr, type);

    public static JsStaticType Parse(string text) => text.Trim() switch
    {
        "number" or "double" => Number,
        "string" => String,
        "boolean" or "bool" => Boolean,
        "void" => Void,
        "Document" or "DomDocument" => Clr(typeof(DomDocument)),
        "Element" or "HTMLElement" or "DomElement" => Clr(typeof(DomElement)),
        "Event" or "DomEvent" => Clr(typeof(DomEvent)),
        _ => Object
    };
}

public sealed record FunctionAnnotation(IReadOnlyDictionary<string, JsStaticType> Parameters, JsStaticType ReturnType);
public sealed record JsFunctionDeclaration(string Name, IReadOnlyList<string> Parameters, FunctionAnnotation? Annotation, IReadOnlyList<JsStatement> Body, SourceSpan Span);

public abstract record JsStatement;
public sealed record JsVariableStatement(string Name, JsExpression Initializer) : JsStatement;
public sealed record JsReturnStatement(JsExpression? Value) : JsStatement;
public sealed record JsExpressionStatement(JsExpression Expression) : JsStatement;
public sealed record JsAssignmentStatement(JsExpression Target, JsExpression Value) : JsStatement;

public abstract record JsExpression;
public sealed record JsLiteralExpression(object? Value) : JsExpression;
public sealed record JsIdentifierExpression(string Name) : JsExpression;
public sealed record JsMemberExpression(JsExpression Target, string Member) : JsExpression;
public sealed record JsCallExpression(JsExpression Target, IReadOnlyList<JsExpression> Arguments) : JsExpression;
public sealed record JsBinaryExpression(string Operator, JsExpression Left, JsExpression Right) : JsExpression;
public sealed record JsUnaryExpression(string Operator, JsExpression Operand) : JsExpression;

public static class SimpleJsParser
{
    private static readonly Regex FunctionHeaderRegex = new(@"(?<doc>/\*\*[\s\S]*?\*/\s*)?function\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*\((?<params>[^)]*)\)\s*\{", RegexOptions.Compiled);
    private static readonly Regex ParamRegex = new(@"@param\s*\{\s*(?<type>[^}]+)\s*\}\s*(?<name>[A-Za-z_$][A-Za-z0-9_$]*)", RegexOptions.Compiled);
    private static readonly Regex ReturnRegex = new(@"@returns?\s*\{\s*(?<type>[^}]+)\s*\}", RegexOptions.Compiled);

    public static IReadOnlyList<JsFunctionDeclaration> ParseFunctions(string source)
    {
        var result = new List<JsFunctionDeclaration>();
        var position = 0;

        while (position < source.Length)
        {
            var match = FunctionHeaderRegex.Match(source, position);
            if (!match.Success) break;

            var bodyStart = match.Index + match.Length;
            var bodyEnd = FindMatchingBrace(source, bodyStart - 1);
            var bodyText = source[bodyStart..bodyEnd];
            var name = match.Groups["name"].Value;
            var parameters = SplitComma(match.Groups["params"].Value).Where(x => x.Length > 0).ToArray();
            var annotation = ParseAnnotation(match.Groups["doc"].Success ? match.Groups["doc"].Value : null);
            var body = ParseStatements(bodyText);
            var span = ComputeSpan(source, match.Index, bodyEnd - match.Index + 1);

            result.Add(new JsFunctionDeclaration(name, parameters, annotation, body, span));
            position = bodyEnd + 1;
        }

        return result;
    }

    private static FunctionAnnotation? ParseAnnotation(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc)) return null;

        var parameters = new Dictionary<string, JsStaticType>(StringComparer.Ordinal);
        foreach (Match match in ParamRegex.Matches(doc))
        {
            parameters[match.Groups["name"].Value] = JsStaticType.Parse(match.Groups["type"].Value);
        }

        var returnMatch = ReturnRegex.Match(doc);
        var returnType = returnMatch.Success ? JsStaticType.Parse(returnMatch.Groups["type"].Value) : JsStaticType.Void;
        return new FunctionAnnotation(parameters, returnType);
    }

    private static IReadOnlyList<JsStatement> ParseStatements(string body)
    {
        var result = new List<JsStatement>();
        foreach (var text in SplitStatements(body))
        {
            var statement = text.Trim();
            if (statement.Length == 0) continue;

            if (statement.StartsWith("return", StringComparison.Ordinal))
            {
                var expr = statement.Length == 6 ? null : ParseExpression(statement[6..].Trim());
                result.Add(new JsReturnStatement(expr));
                continue;
            }

            var variable = Regex.Match(statement, @"^(let|const|var)\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?<expr>[\s\S]+)$");
            if (variable.Success)
            {
                result.Add(new JsVariableStatement(variable.Groups["name"].Value, ParseExpression(variable.Groups["expr"].Value)));
                continue;
            }

            var assignmentIndex = FindTopLevelAssignment(statement);
            if (assignmentIndex >= 0)
            {
                result.Add(new JsAssignmentStatement(ParseExpression(statement[..assignmentIndex]), ParseExpression(statement[(assignmentIndex + 1)..])));
                continue;
            }

            result.Add(new JsExpressionStatement(ParseExpression(statement)));
        }

        return result;
    }

    private static int FindTopLevelAssignment(string text)
    {
        var depth = 0;
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == quote && (i == 0 || text[i - 1] != '\\')) inString = false;
                continue;
            }
            if (c is '\'' or '"') { inString = true; quote = c; continue; }
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (depth == 0 && c == '=' && (i + 1 >= text.Length || text[i + 1] != '=') && (i == 0 || text[i - 1] != '!' && text[i - 1] != '<' && text[i - 1] != '>')) return i;
        }
        return -1;
    }

    private static JsExpression ParseExpression(string expression) => new ExpressionParser(expression).Parse();

    private static int FindMatchingBrace(string source, int openBrace)
    {
        var depth = 0;
        var inString = false;
        var quote = '\0';
        for (var i = openBrace; i < source.Length; i++)
        {
            var c = source[i];
            if (inString)
            {
                if (c == quote && source[i - 1] != '\\') inString = false;
                continue;
            }

            if (c is '\'' or '"') { inString = true; quote = c; continue; }
            if (c == '{') depth++;
            if (c == '}') depth--;
            if (depth == 0) return i;
        }
        throw new FormatException("Unterminated function body.");
    }

    private static IReadOnlyList<string> SplitComma(string text) => text.Split(',').Select(x => x.Trim()).ToArray();

    private static IEnumerable<string> SplitStatements(string body)
    {
        var sb = new StringBuilder();
        var depth = 0;
        var inString = false;
        var quote = '\0';
        foreach (var c in body)
        {
            if (inString)
            {
                sb.Append(c);
                if (c == quote) inString = false;
                continue;
            }
            if (c is '\'' or '"') { inString = true; quote = c; sb.Append(c); continue; }
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == ';' && depth == 0)
            {
                yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static SourceSpan ComputeSpan(string source, int start, int length)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < start; i++)
        {
            if (source[i] == '\n') { line++; column = 1; } else { column++; }
        }
        return new SourceSpan(start, length, line, column);
    }
}

internal sealed class ExpressionParser
{
    private readonly List<Token> _tokens;
    private int _position;

    public ExpressionParser(string text) => _tokens = Tokenize(text).ToList();

    public JsExpression Parse()
    {
        var expr = ParseBinary(0);
        Expect(TokenKind.End);
        return expr;
    }

    private JsExpression ParseBinary(int parentPrecedence)
    {
        JsExpression left;
        var unary = Current.UnaryPrecedence;
        if (unary != 0 && unary >= parentPrecedence)
        {
            var op = Next().Text;
            var operand = ParseBinary(unary);
            left = new JsUnaryExpression(op, operand);
        }
        else
        {
            left = ParsePostfix(ParsePrimary());
        }

        while (true)
        {
            var precedence = Current.BinaryPrecedence;
            if (precedence == 0 || precedence <= parentPrecedence) break;
            var op = Next().Text;
            var right = ParseBinary(precedence);
            left = new JsBinaryExpression(op, left, right);
        }

        return left;
    }

    private JsExpression ParsePrimary()
    {
        var token = Next();
        return token.Kind switch
        {
            TokenKind.Number => new JsLiteralExpression(double.Parse(token.Text, CultureInfo.InvariantCulture)),
            TokenKind.String => new JsLiteralExpression(token.Text),
            TokenKind.Identifier when token.Text == "true" => new JsLiteralExpression(true),
            TokenKind.Identifier when token.Text == "false" => new JsLiteralExpression(false),
            TokenKind.Identifier when token.Text == "null" => new JsLiteralExpression(null),
            TokenKind.Identifier => new JsIdentifierExpression(token.Text),
            TokenKind.OpenParen => ParseParenthesized(),
            _ => throw new NotSupportedException($"Unexpected token '{token.Text}'.")
        };
    }

    private JsExpression ParseParenthesized()
    {
        var expr = ParseBinary(0);
        Expect(TokenKind.CloseParen);
        return expr;
    }

    private JsExpression ParsePostfix(JsExpression expression)
    {
        while (true)
        {
            if (Match(TokenKind.Dot))
            {
                var name = Expect(TokenKind.Identifier).Text;
                expression = new JsMemberExpression(expression, name);
                continue;
            }

            if (Match(TokenKind.OpenParen))
            {
                var args = new List<JsExpression>();
                if (!Match(TokenKind.CloseParen))
                {
                    do
                    {
                        args.Add(ParseBinary(0));
                    } while (Match(TokenKind.Comma));
                    Expect(TokenKind.CloseParen);
                }
                expression = new JsCallExpression(expression, args);
                continue;
            }

            return expression;
        }
    }

    private Token Current => _tokens[_position];
    private Token Next() => _tokens[_position++];
    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind) return false;
        _position++;
        return true;
    }
    private Token Expect(TokenKind kind)
    {
        if (Current.Kind != kind) throw new NotSupportedException($"Expected {kind}, got '{Current.Text}'.");
        return Next();
    }

    private static IEnumerable<Token> Tokenize(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                yield return new Token(TokenKind.Number, text[start..i]);
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;
                yield return new Token(TokenKind.Identifier, text[start..i]);
                continue;
            }
            if (c is '\'' or '"')
            {
                var quote = c;
                var start = ++i;
                while (i < text.Length && text[i] != quote) i++;
                yield return new Token(TokenKind.String, text[start..i]);
                i++;
                continue;
            }
            var two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
            if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||")
            {
                yield return new Token(TokenKind.Operator, two);
                i += 2;
                continue;
            }
            yield return c switch
            {
                '+' or '-' or '*' or '/' or '<' or '>' or '!' => new Token(TokenKind.Operator, c.ToString(CultureInfo.InvariantCulture)),
                '.' => new Token(TokenKind.Dot, "."),
                '(' => new Token(TokenKind.OpenParen, "("),
                ')' => new Token(TokenKind.CloseParen, ")"),
                ',' => new Token(TokenKind.Comma, ","),
                _ => throw new NotSupportedException($"Unsupported character '{c}'.")
            };
            i++;
        }
        yield return new Token(TokenKind.End, string.Empty);
    }

    private enum TokenKind { Identifier, Number, String, Operator, Dot, OpenParen, CloseParen, Comma, End }
    private sealed record Token(TokenKind Kind, string Text)
    {
        public int UnaryPrecedence => Kind == TokenKind.Operator && Text is "+" or "-" or "!" ? 7 : 0;
        public int BinaryPrecedence => Kind != TokenKind.Operator ? 0 : Text switch
        {
            "*" or "/" => 6,
            "+" or "-" => 5,
            "<" or "<=" or ">" or ">=" => 4,
            "==" or "!=" => 3,
            "&&" => 2,
            "||" => 1,
            _ => 0
        };
    }
}

public sealed class ExpressionTreeBackend
{
    private readonly IReadOnlyDictionary<string, object?> _globals;
    private readonly Dictionary<string, Expression> _symbols = new(StringComparer.Ordinal);
    private readonly List<ParameterExpression> _locals = new();

    public ExpressionTreeBackend(IReadOnlyDictionary<string, object?> globals) => _globals = globals;

    public Delegate Compile(JsFunctionDeclaration fn)
    {
        if (fn.Annotation is null) throw new InvalidOperationException("Function must be annotated.");

        var parameters = new List<ParameterExpression>();
        foreach (var name in fn.Parameters)
        {
            if (!fn.Annotation.Parameters.TryGetValue(name, out var type)) throw new NotSupportedException($"Parameter '{name}' has no JSDoc type.");
            var parameter = Expression.Parameter(type.ClrType, name);
            parameters.Add(parameter);
            _symbols[name] = parameter;
        }

        foreach (var global in _globals)
        {
            if (global.Value is not null)
            {
                _symbols[global.Key] = Expression.Constant(global.Value, global.Value.GetType());
            }
        }

        var expressions = new List<Expression>();
        var returnTarget = Expression.Label(fn.Annotation.ReturnType.ClrType, "return");
        foreach (var statement in fn.Body)
        {
            expressions.Add(CompileStatement(statement, returnTarget));
        }
        expressions.Add(Expression.Label(returnTarget, Default(fn.Annotation.ReturnType.ClrType)));

        var body = Expression.Block(_locals, expressions);
        var delegateType = fn.Annotation.ReturnType.ClrType == typeof(void)
            ? Expression.GetActionType(parameters.Select(x => x.Type).ToArray())
            : Expression.GetFuncType(parameters.Select(x => x.Type).Append(fn.Annotation.ReturnType.ClrType).ToArray());

        return Expression.Lambda(delegateType, body, fn.Name, parameters).Compile();
    }

    private Expression CompileStatement(JsStatement statement, LabelTarget returnTarget) => statement switch
    {
        JsVariableStatement variable => CompileVariable(variable),
        JsReturnStatement ret => Expression.Return(returnTarget, ret.Value is null ? Default(returnTarget.Type) : ConvertTo(CompileExpression(ret.Value), returnTarget.Type)),
        JsExpressionStatement expr => CompileExpression(expr.Expression),
        JsAssignmentStatement assignment => CompileAssignment(assignment),
        _ => throw new NotSupportedException($"Unsupported statement '{statement.GetType().Name}'.")
    };

    private Expression CompileVariable(JsVariableStatement variable)
    {
        var value = CompileExpression(variable.Initializer);
        var local = Expression.Variable(value.Type, variable.Name);
        _locals.Add(local);
        _symbols[variable.Name] = local;
        return Expression.Assign(local, value);
    }

    private Expression CompileAssignment(JsAssignmentStatement assignment)
    {
        var target = CompileAssignable(assignment.Target);
        var value = CompileExpression(assignment.Value);
        return Expression.Assign(target, ConvertTo(value, target.Type));
    }

    private Expression CompileExpression(JsExpression expression) => expression switch
    {
        JsLiteralExpression literal => CompileLiteral(literal.Value),
        JsIdentifierExpression identifier => _symbols.TryGetValue(identifier.Name, out var symbol) ? symbol : throw new NotSupportedException($"Unknown identifier '{identifier.Name}'."),
        JsMemberExpression member => CompileMember(member),
        JsCallExpression call => CompileCall(call),
        JsBinaryExpression binary => CompileBinary(binary),
        JsUnaryExpression unary => CompileUnary(unary),
        _ => throw new NotSupportedException($"Unsupported expression '{expression.GetType().Name}'.")
    };

    private static Expression CompileLiteral(object? value) => value switch
    {
        null => Expression.Constant(null),
        double d => Expression.Constant(d),
        string s => Expression.Constant(s),
        bool b => Expression.Constant(b),
        _ => Expression.Constant(value)
    };

    private Expression CompileMember(JsMemberExpression member)
    {
        var target = CompileExpression(member.Target);
        return BindMember(target, member.Member);
    }

    private Expression CompileAssignable(JsExpression expression)
    {
        if (expression is JsIdentifierExpression id && _symbols.TryGetValue(id.Name, out var symbol)) return symbol;
        if (expression is JsMemberExpression member) return CompileMember(member);
        throw new NotSupportedException("Unsupported assignment target.");
    }

    private Expression CompileCall(JsCallExpression call)
    {
        if (call.Target is JsMemberExpression member)
        {
            var instance = CompileExpression(member.Target);
            var args = call.Arguments.Select(CompileExpression).ToArray();
            var method = ResolveMethod(instance.Type, member.Member, args.Select(x => x.Type).ToArray());
            var converted = method.GetParameters().Select((p, i) => ConvertTo(args[i], p.ParameterType));
            return Expression.Call(instance, method, converted);
        }

        throw new NotSupportedException("Only method calls are supported in phase one.");
    }

    private static Expression BindMember(Expression target, string member)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var property = target.Type.GetProperty(member, flags);
        if (property is not null) return Expression.Property(target, property);
        var field = target.Type.GetField(member, flags);
        if (field is not null) return Expression.Field(target, field);
        throw new NotSupportedException($"Member '{member}' not found on '{target.Type.Name}'.");
    }

    private static MethodInfo ResolveMethod(Type type, string name, IReadOnlyList<Type> argumentTypes)
    {
        var candidates = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
            .Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.GetParameters().Length == argumentTypes.Count)
            .ToArray();

        foreach (var method in candidates)
        {
            var parameters = method.GetParameters();
            var ok = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!CanConvert(argumentTypes[i], parameters[i].ParameterType)) { ok = false; break; }
            }
            if (ok) return method;
        }

        throw new NotSupportedException($"Method '{name}' with {argumentTypes.Count} argument(s) not found on '{type.Name}'.");
    }

    private static Expression CompileUnary(JsUnaryExpression unary)
    {
        var operand = unary.Operand is null ? throw new InvalidOperationException() : throw new NotSupportedException($"Unary operator '{unary.Operator}' is not yet supported.");
    }

    private Expression CompileBinary(JsBinaryExpression binary)
    {
        var left = CompileExpression(binary.Left);
        var right = CompileExpression(binary.Right);
        return binary.Operator switch
        {
            "+" when left.Type == typeof(string) || right.Type == typeof(string) => Expression.Call(typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(object), typeof(object) })!, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object))),
            "+" => Expression.Add(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "-" => Expression.Subtract(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "*" => Expression.Multiply(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "/" => Expression.Divide(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "<" => Expression.LessThan(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "<=" => Expression.LessThanOrEqual(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            ">" => Expression.GreaterThan(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            ">=" => Expression.GreaterThanOrEqual(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "==" => Expression.Equal(ConvertTo(left, right.Type), right),
            "!=" => Expression.NotEqual(ConvertTo(left, right.Type), right),
            "&&" => Expression.AndAlso(ConvertTo(left, typeof(bool)), ConvertTo(right, typeof(bool))),
            "||" => Expression.OrElse(ConvertTo(left, typeof(bool)), ConvertTo(right, typeof(bool))),
            _ => throw new NotSupportedException($"Operator '{binary.Operator}' is not supported.")
        };
    }

    private static bool CanConvert(Type source, Type target) => target.IsAssignableFrom(source) || source == typeof(double) && target == typeof(int) || source == typeof(int) && target == typeof(double) || target == typeof(object);

    private static Expression ConvertTo(Expression expression, Type targetType)
    {
        if (expression.Type == targetType) return expression;
        if (targetType.IsAssignableFrom(expression.Type)) return Expression.Convert(expression, targetType);
        if (expression.Type == typeof(double) && targetType == typeof(int)) return Expression.Convert(expression, targetType);
        if (expression.Type == typeof(int) && targetType == typeof(double)) return Expression.Convert(expression, targetType);
        if (targetType == typeof(object)) return Expression.Convert(expression, targetType);
        return Expression.Convert(expression, targetType);
    }

    private static Expression Default(Type type) => type == typeof(void) ? Expression.Empty() : Expression.Default(type);
}
