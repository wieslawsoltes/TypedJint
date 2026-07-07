using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Jint;

namespace TypedJint;

public enum TypedBackendKind { ExpressionTrees, CSharp, IL }
public enum TypedCompilationMode { Disabled, CompileAnnotatedFunctionsOnly, CompileSafeFunctionsOnly, CompileAggressively }
public enum TypedDiagnosticSeverity { Info, Warning, Error }

public sealed class TypedJintOptions
{
    public bool EnableCompilation { get; init; } = true;
    public bool ExecuteOriginalSourceInJint { get; init; } = true;
    public TypedCompilationMode CompilationMode { get; init; } = TypedCompilationMode.CompileAnnotatedFunctionsOnly;
    public TypedBackendKind Backend { get; init; } = TypedBackendKind.ExpressionTrees;
    public bool ThrowOnCompilationFailure { get; init; }
    public Action<TypedDiagnostic>? DiagnosticSink { get; init; }
}

public sealed record SourceSpan(int Start, int Length, int Line, int Column);
public sealed record TypedDiagnostic(string Code, TypedDiagnosticSeverity Severity, string Message, SourceSpan? Span = null);
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
    public DomWindow(DomDocument document) => this.document = document;
    public DomDocument document { get; }
}

public class DomEvent
{
    public DomEvent(string type) => this.type = type;
    public string type { get; }
    public DomEventTarget? target { get; internal set; }
    public bool defaultPrevented { get; private set; }
    public void preventDefault() => defaultPrevented = true;
}

public interface IDomEventListener { void HandleEvent(DomEvent ev); }

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
        set { _children.Clear(); _textContent = value; }
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
        if (_children.Remove(child)) child.parentNode = null;
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
    public void toggle(string token) { if (!_tokens.Remove(token)) _tokens.Add(token); }
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
    private void SetOrRemove(string name, string? value) { if (value is null) _values.Remove(name); else _values[name] = value; }
}

public class DomElement : DomNode
{
    private readonly Dictionary<string, string> _attributes = new(StringComparer.OrdinalIgnoreCase);
    public DomElement(string tagName) : base(tagName.ToUpperInvariant()) => this.tagName = tagName.ToUpperInvariant();
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

public enum JsStaticTypeKind { Void, Number, String, Boolean, Object, Clr }

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
        "Element" or "HTMLElement" or "HTMLButtonElement" or "DomElement" => Clr(typeof(DomElement)),
        "TextNode" or "DomTextNode" => Clr(typeof(DomTextNode)),
        "Event" or "DomEvent" => Clr(typeof(DomEvent)),
        _ => Object
    };
}

public sealed record FunctionAnnotation(IReadOnlyDictionary<string, JsStaticType> Parameters, JsStaticType ReturnType);
public sealed record JsFunctionDeclaration(string Name, IReadOnlyList<string> Parameters, FunctionAnnotation? Annotation, IReadOnlyList<JsStatement> Body, SourceSpan Span);

public abstract record JsStatement;
public sealed record JsBlockStatement(IReadOnlyList<JsStatement> Statements) : JsStatement;
public sealed record JsVariableStatement(string Name, JsExpression Initializer) : JsStatement;
public sealed record JsReturnStatement(JsExpression? Value) : JsStatement;
public sealed record JsExpressionStatement(JsExpression Expression) : JsStatement;
public sealed record JsAssignmentStatement(JsExpression Target, JsExpression Value) : JsStatement;
public sealed record JsIfStatement(JsExpression Test, JsStatement Consequent, JsStatement? Alternate) : JsStatement;
public sealed record JsWhileStatement(JsExpression Test, JsStatement Body) : JsStatement;
public sealed record JsForStatement(JsStatement? Init, JsExpression? Test, JsStatement? Update, JsStatement Body) : JsStatement;

public abstract record JsExpression;
public sealed record JsLiteralExpression(object? Value) : JsExpression;
public sealed record JsIdentifierExpression(string Name) : JsExpression;
public sealed record JsMemberExpression(JsExpression Target, string Member) : JsExpression;
public sealed record JsCallExpression(JsExpression Target, IReadOnlyList<JsExpression> Arguments) : JsExpression;
public sealed record JsBinaryExpression(string Operator, JsExpression Left, JsExpression Right) : JsExpression;
public sealed record JsUnaryExpression(string Operator, JsExpression Operand) : JsExpression;
public sealed record JsUpdateExpression(JsExpression Target, string Operator, bool Prefix) : JsExpression;

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

        return new TypedCompilationResult { CompiledFunctions = _compiled, Fallbacks = _fallbacks, Diagnostics = _diagnostics };
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
            var name = match.Groups["name"].Value;
            var parameters = SplitComma(match.Groups["params"].Value).Where(x => x.Length > 0).ToArray();
            var annotation = ParseAnnotation(match.Groups["doc"].Success ? match.Groups["doc"].Value : null);
            var body = ParseStatements(source[bodyStart..bodyEnd]);
            result.Add(new JsFunctionDeclaration(name, parameters, annotation, body, ComputeSpan(source, match.Index, bodyEnd - match.Index + 1)));
            position = bodyEnd + 1;
        }
        return result;
    }

    private static FunctionAnnotation? ParseAnnotation(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc)) return null;
        var parameters = new Dictionary<string, JsStaticType>(StringComparer.Ordinal);
        foreach (Match match in ParamRegex.Matches(doc)) parameters[match.Groups["name"].Value] = JsStaticType.Parse(match.Groups["type"].Value);
        var returnMatch = ReturnRegex.Match(doc);
        var returnType = returnMatch.Success ? JsStaticType.Parse(returnMatch.Groups["type"].Value) : JsStaticType.Void;
        return new FunctionAnnotation(parameters, returnType);
    }

    private static IReadOnlyList<JsStatement> ParseStatements(string body) => new StatementParser(body).ParseStatements();
    internal static JsExpression ParseExpression(string expression) => new ExpressionParser(expression).Parse();
    private static IReadOnlyList<string> SplitComma(string text) => text.Split(',').Select(x => x.Trim()).ToArray();

    private static int FindMatchingBrace(string source, int openBrace)
    {
        var depth = 0;
        var inString = false;
        var quote = '\0';
        for (var i = openBrace; i < source.Length; i++)
        {
            var c = source[i];
            if (inString) { if (c == quote && (i == 0 || source[i - 1] != '\\')) inString = false; continue; }
            if (c is '\'' or '"') { inString = true; quote = c; continue; }
            if (c == '{') depth++;
            if (c == '}') depth--;
            if (depth == 0) return i;
        }
        throw new FormatException("Unterminated function body.");
    }

    internal static int FindTopLevelAssignment(string text)
    {
        var depth = 0; var inString = false; var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString) { if (c == quote && (i == 0 || text[i - 1] != '\\')) inString = false; continue; }
            if (c is '\'' or '"') { inString = true; quote = c; continue; }
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}') depth--;
            if (depth == 0 && c == '=')
            {
                var previous = i > 0 ? text[i - 1] : '\0';
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (previous is not ('!' or '<' or '>' or '=') && next != '=' && next != '>') return i;
            }
        }
        return -1;
    }

    internal static SourceSpan ComputeSpan(string source, int start, int length)
    {
        var line = 1; var column = 1;
        for (var i = 0; i < start; i++) { if (source[i] == '\n') { line++; column = 1; } else column++; }
        return new SourceSpan(start, length, line, column);
    }
}

internal sealed class StatementParser
{
    private static readonly Regex VariableRegex = new(@"^(let|const|var)\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?<expr>[\s\S]+)$", RegexOptions.Compiled);
    private readonly string _text;
    private int _position;

    public StatementParser(string text) => _text = text;

    public IReadOnlyList<JsStatement> ParseStatements()
    {
        var result = new List<JsStatement>();
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (IsEnd || Current == '}') break;
            result.Add(ParseStatement());
        }
        return result;
    }

    private JsStatement ParseStatement()
    {
        SkipWhiteSpaceAndComments();
        if (Match('{')) return ParseBlockAfterOpenBrace();
        if (MatchKeyword("if")) return ParseIf();
        if (MatchKeyword("while")) return ParseWhile();
        if (MatchKeyword("for")) return ParseFor();

        var simple = ReadUntilStatementEnd();
        return ParseSimpleStatement(simple);
    }

    private JsBlockStatement ParseBlockAfterOpenBrace()
    {
        var statements = new List<JsStatement>();
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (IsEnd) throw new FormatException("Unterminated block statement.");
            if (Match('}')) break;
            statements.Add(ParseStatement());
        }
        return new JsBlockStatement(statements);
    }

    private JsIfStatement ParseIf()
    {
        var test = SimpleJsParser.ParseExpression(ReadParenthesized());
        var consequent = ParseStatement();
        SkipWhiteSpaceAndComments();
        var alternate = MatchKeyword("else") ? ParseStatement() : null;
        return new JsIfStatement(test, consequent, alternate);
    }

    private JsWhileStatement ParseWhile()
    {
        var test = SimpleJsParser.ParseExpression(ReadParenthesized());
        var body = ParseStatement();
        return new JsWhileStatement(test, body);
    }

    private JsForStatement ParseFor()
    {
        var header = ReadParenthesized();
        var parts = SplitTopLevel(header, ';').ToArray();
        if (parts.Length != 3) throw new FormatException("for statement requires init; test; update header.");
        var init = string.IsNullOrWhiteSpace(parts[0]) ? null : ParseSimpleStatement(parts[0]);
        var test = string.IsNullOrWhiteSpace(parts[1]) ? null : SimpleJsParser.ParseExpression(parts[1]);
        var update = string.IsNullOrWhiteSpace(parts[2]) ? null : ParseSimpleStatement(parts[2]);
        var body = ParseStatement();
        return new JsForStatement(init, test, update, body);
    }

    private static JsStatement ParseSimpleStatement(string text)
    {
        var statement = text.Trim();
        if (statement.EndsWith(';')) statement = statement[..^1].TrimEnd();
        if (statement.Length == 0) return new JsBlockStatement(Array.Empty<JsStatement>());

        if (IsKeywordStatement(statement, "return"))
        {
            var rest = statement.Length == 6 ? string.Empty : statement[6..].Trim();
            return new JsReturnStatement(rest.Length == 0 ? null : SimpleJsParser.ParseExpression(rest));
        }

        var variable = VariableRegex.Match(statement);
        if (variable.Success)
        {
            return new JsVariableStatement(variable.Groups["name"].Value, SimpleJsParser.ParseExpression(variable.Groups["expr"].Value));
        }

        var assignmentIndex = SimpleJsParser.FindTopLevelAssignment(statement);
        return assignmentIndex >= 0
            ? new JsAssignmentStatement(SimpleJsParser.ParseExpression(statement[..assignmentIndex]), SimpleJsParser.ParseExpression(statement[(assignmentIndex + 1)..]))
            : new JsExpressionStatement(SimpleJsParser.ParseExpression(statement));
    }

    private string ReadUntilStatementEnd()
    {
        var start = _position;
        var depth = 0;
        var inString = false;
        var quote = '\0';

        while (!IsEnd)
        {
            var c = Current;
            if (inString)
            {
                if (c == quote && (_position == 0 || _text[_position - 1] != '\\')) inString = false;
                _position++;
                continue;
            }

            if (c is '\'' or '"') { inString = true; quote = c; _position++; continue; }
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--;
            }

            if (c == ';' && depth == 0)
            {
                _position++;
                break;
            }

            _position++;
        }

        return _text[start.._position];
    }

    private string ReadParenthesized()
    {
        SkipWhiteSpaceAndComments();
        if (!Match('(')) throw new FormatException("Expected '('.");
        var start = _position;
        var depth = 1;
        var inString = false;
        var quote = '\0';

        while (!IsEnd)
        {
            var c = Current;
            if (inString)
            {
                if (c == quote && (_position == 0 || _text[_position - 1] != '\\')) inString = false;
                _position++;
                continue;
            }

            if (c is '\'' or '"') { inString = true; quote = c; _position++; continue; }
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (depth == 0)
            {
                var value = _text[start.._position];
                _position++;
                return value;
            }

            _position++;
        }

        throw new FormatException("Unterminated parenthesized expression.");
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var start = 0;
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
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}') depth--;
            if (c == separator && depth == 0)
            {
                yield return text[start..i].Trim();
                start = i + 1;
            }
        }

        yield return text[start..].Trim();
    }

    private void SkipWhiteSpaceAndComments()
    {
        while (!IsEnd)
        {
            if (char.IsWhiteSpace(Current)) { _position++; continue; }
            if (Current == '/' && Peek(1) == '/')
            {
                _position += 2;
                while (!IsEnd && Current != '\n') _position++;
                continue;
            }

            if (Current == '/' && Peek(1) == '*')
            {
                _position += 2;
                while (!IsEnd && !(Current == '*' && Peek(1) == '/')) _position++;
                if (!IsEnd) _position += 2;
                continue;
            }

            break;
        }
    }

    private bool MatchKeyword(string keyword)
    {
        SkipWhiteSpaceAndComments();
        if (!_text.AsSpan(_position).StartsWith(keyword, StringComparison.Ordinal)) return false;
        var end = _position + keyword.Length;
        if (end < _text.Length && IsIdentifierPart(_text[end])) return false;
        if (_position > 0 && IsIdentifierPart(_text[_position - 1])) return false;
        _position = end;
        return true;
    }

    private bool Match(char c)
    {
        SkipWhiteSpaceAndComments();
        if (IsEnd || Current != c) return false;
        _position++;
        return true;
    }

    private static bool IsKeywordStatement(string text, string keyword) =>
        text == keyword || text.StartsWith(keyword + " ", StringComparison.Ordinal) || text.StartsWith(keyword + "\t", StringComparison.Ordinal) || text.StartsWith(keyword + "\r", StringComparison.Ordinal) || text.StartsWith(keyword + "\n", StringComparison.Ordinal);

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
    private char Current => _text[_position];
    private char Peek(int offset) => _position + offset < _text.Length ? _text[_position + offset] : '\0';
    private bool IsEnd => _position >= _text.Length;
}

internal sealed class ExpressionParser
{
    private readonly List<Token> _tokens;
    private int _position;
    public ExpressionParser(string text) => _tokens = Tokenize(text).ToList();
    public JsExpression Parse() { var expr = ParseBinary(0); Expect(TokenKind.End); return expr; }

    private JsExpression ParseBinary(int parentPrecedence)
    {
        JsExpression left;
        var unary = Current.UnaryPrecedence;
        if (Current.Kind == TokenKind.Operator && Current.Text is "++" or "--")
        {
            var op = Next().Text;
            left = new JsUpdateExpression(ParsePostfix(ParsePrimary()), op, Prefix: true);
        }
        else if (unary != 0 && unary >= parentPrecedence)
        {
            var op = Next().Text;
            left = new JsUnaryExpression(op, ParseBinary(unary));
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
            left = new JsBinaryExpression(op, left, ParseBinary(precedence));
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

    private JsExpression ParseParenthesized() { var expr = ParseBinary(0); Expect(TokenKind.CloseParen); return expr; }

    private JsExpression ParsePostfix(JsExpression expression)
    {
        while (true)
        {
            if (Match(TokenKind.Dot)) { expression = new JsMemberExpression(expression, Expect(TokenKind.Identifier).Text); continue; }
            if (Match(TokenKind.OpenParen))
            {
                var args = new List<JsExpression>();
                if (!Match(TokenKind.CloseParen))
                {
                    do { args.Add(ParseBinary(0)); } while (Match(TokenKind.Comma));
                    Expect(TokenKind.CloseParen);
                }
                expression = new JsCallExpression(expression, args);
                continue;
            }
            if (Current.Kind == TokenKind.Operator && Current.Text is "++" or "--")
            {
                expression = new JsUpdateExpression(expression, Next().Text, Prefix: false);
                continue;
            }
            return expression;
        }
    }

    private Token Current => _tokens[_position];
    private Token Next() => _tokens[_position++];
    private bool Match(TokenKind kind) { if (Current.Kind != kind) return false; _position++; return true; }
    private Token Expect(TokenKind kind) { if (Current.Kind != kind) throw new NotSupportedException($"Expected {kind}, got '{Current.Text}'."); return Next(); }

    private static IEnumerable<Token> Tokenize(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c)) { var start = i; while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++; yield return new Token(TokenKind.Number, text[start..i]); continue; }
            if (char.IsLetter(c) || c is '_' or '$') { var start = i; while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++; yield return new Token(TokenKind.Identifier, text[start..i]); continue; }
            if (c is '\'' or '"')
            {
                var quote = c;
                var builder = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        builder.Append(text[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '\'' => '\'', '"' => '"', var escaped => escaped });
                        i++;
                        continue;
                    }
                    builder.Append(text[i]);
                    i++;
                }
                if (i >= text.Length) throw new NotSupportedException("Unterminated string literal.");
                i++;
                yield return new Token(TokenKind.String, builder.ToString());
                continue;
            }
            var three = i + 2 < text.Length ? text.Substring(i, 3) : string.Empty;
            if (three is "===" or "!==") { yield return new Token(TokenKind.Operator, three); i += 3; continue; }
            var two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
            if (two is "++" or "--" or "==" or "!=" or "<=" or ">=" or "&&" or "||") { yield return new Token(TokenKind.Operator, two); i += 2; continue; }
            yield return c switch
            {
                '+' or '-' or '*' or '/' or '%' or '<' or '>' or '!' => new Token(TokenKind.Operator, c.ToString(CultureInfo.InvariantCulture)),
                '.' => new Token(TokenKind.Dot, "."), '(' => new Token(TokenKind.OpenParen, "("), ')' => new Token(TokenKind.CloseParen, ")"), ',' => new Token(TokenKind.Comma, ","),
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
        public int BinaryPrecedence => Kind != TokenKind.Operator ? 0 : Text switch { "*" or "/" or "%" => 6, "+" or "-" => 5, "<" or "<=" or ">" or ">=" => 4, "==" or "!=" or "===" or "!==" => 3, "&&" => 2, "||" => 1, _ => 0 };
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
        foreach (var global in _globals.Where(x => x.Value is not null)) _symbols[global.Key] = Expression.Constant(global.Value!, global.Value!.GetType());

        var expressions = new List<Expression>();
        var returnTarget = Expression.Label(fn.Annotation.ReturnType.ClrType, "return");
        foreach (var statement in fn.Body) expressions.Add(CompileStatement(statement, returnTarget));
        expressions.Add(Expression.Label(returnTarget, Default(fn.Annotation.ReturnType.ClrType)));
        var body = Expression.Block(_locals, expressions);
        var delegateType = fn.Annotation.ReturnType.ClrType == typeof(void) ? Expression.GetActionType(parameters.Select(x => x.Type).ToArray()) : Expression.GetFuncType(parameters.Select(x => x.Type).Append(fn.Annotation.ReturnType.ClrType).ToArray());
        return Expression.Lambda(delegateType, body, fn.Name, parameters).Compile();
    }

    private Expression CompileStatement(JsStatement statement, LabelTarget returnTarget) => statement switch
    {
        JsBlockStatement block => Expression.Block(block.Statements.Select(x => CompileStatement(x, returnTarget))),
        JsVariableStatement variable => CompileVariable(variable),
        JsReturnStatement ret => Expression.Return(returnTarget, ret.Value is null ? Default(returnTarget.Type) : ConvertTo(CompileExpression(ret.Value), returnTarget.Type)),
        JsExpressionStatement expr => CompileExpression(expr.Expression),
        JsAssignmentStatement assignment => CompileAssignment(assignment),
        JsIfStatement ifStatement => CompileIf(ifStatement, returnTarget),
        JsWhileStatement whileStatement => CompileWhile(whileStatement, returnTarget),
        JsForStatement forStatement => CompileFor(forStatement, returnTarget),
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
        return Expression.Assign(target, ConvertTo(CompileExpression(assignment.Value), target.Type));
    }

    private Expression CompileIf(JsIfStatement statement, LabelTarget returnTarget)
    {
        var test = ConvertTo(CompileExpression(statement.Test), typeof(bool));
        var consequent = CompileStatement(statement.Consequent, returnTarget);
        var alternate = statement.Alternate is null ? Expression.Empty() : CompileStatement(statement.Alternate, returnTarget);
        return Expression.IfThenElse(test, consequent, alternate);
    }

    private Expression CompileWhile(JsWhileStatement statement, LabelTarget returnTarget)
    {
        var breakTarget = Expression.Label("while_break");
        return Expression.Loop(
            Expression.IfThenElse(
                ConvertTo(CompileExpression(statement.Test), typeof(bool)),
                CompileStatement(statement.Body, returnTarget),
                Expression.Break(breakTarget)),
            breakTarget);
    }

    private Expression CompileFor(JsForStatement statement, LabelTarget returnTarget)
    {
        var breakTarget = Expression.Label("for_break");
        var expressions = new List<Expression>();
        if (statement.Init is not null) expressions.Add(CompileStatement(statement.Init, returnTarget));

        var loopExpressions = new List<Expression>();
        if (statement.Test is not null)
        {
            loopExpressions.Add(Expression.IfThen(Expression.Not(ConvertTo(CompileExpression(statement.Test), typeof(bool))), Expression.Break(breakTarget)));
        }

        loopExpressions.Add(CompileStatement(statement.Body, returnTarget));
        if (statement.Update is not null) loopExpressions.Add(CompileStatement(statement.Update, returnTarget));
        expressions.Add(Expression.Loop(Expression.Block(loopExpressions), breakTarget));
        return Expression.Block(expressions);
    }

    private Expression CompileExpression(JsExpression expression) => expression switch
    {
        JsLiteralExpression literal => CompileLiteral(literal.Value),
        JsIdentifierExpression identifier => _symbols.TryGetValue(identifier.Name, out var symbol) ? symbol : throw new NotSupportedException($"Unknown identifier '{identifier.Name}'."),
        JsMemberExpression member => CompileMember(member),
        JsCallExpression call => CompileCall(call),
        JsBinaryExpression binary => CompileBinary(binary),
        JsUnaryExpression unary => CompileUnary(unary),
        JsUpdateExpression update => CompileUpdate(update),
        _ => throw new NotSupportedException($"Unsupported expression '{expression.GetType().Name}'.")
    };

    private static Expression CompileLiteral(object? value) => value switch { null => Expression.Constant(null), double d => Expression.Constant(d), string s => Expression.Constant(s), bool b => Expression.Constant(b), _ => Expression.Constant(value) };
    private Expression CompileMember(JsMemberExpression member) => BindMember(CompileExpression(member.Target), member.Member);

    private Expression CompileAssignable(JsExpression expression) => expression switch
    {
        JsIdentifierExpression id when _symbols.TryGetValue(id.Name, out var symbol) => symbol,
        JsMemberExpression member => CompileMember(member),
        _ => throw new NotSupportedException("Unsupported assignment target.")
    };

    private Expression CompileCall(JsCallExpression call)
    {
        if (call.Target is not JsMemberExpression member) throw new NotSupportedException("Only method calls are supported in phase one.");
        var instance = CompileExpression(member.Target);
        var args = call.Arguments.Select(CompileExpression).ToArray();
        var method = ResolveMethod(instance.Type, member.Member, args.Select(x => x.Type).ToArray());
        var converted = method.GetParameters().Select((p, i) => ConvertTo(args[i], p.ParameterType));
        return Expression.Call(instance, method, converted);
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
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase).Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetParameters().Length == argumentTypes.Count))
        {
            var parameters = method.GetParameters();
            var ok = true;
            for (var i = 0; i < parameters.Length; i++) if (!CanConvert(argumentTypes[i], parameters[i].ParameterType)) { ok = false; break; }
            if (ok) return method;
        }
        throw new NotSupportedException($"Method '{name}' with {argumentTypes.Count} argument(s) not found on '{type.Name}'.");
    }

    private Expression CompileUnary(JsUnaryExpression unary)
    {
        var operand = CompileExpression(unary.Operand);
        return unary.Operator switch
        {
            "+" => ConvertTo(operand, typeof(double)),
            "-" => Expression.Negate(ConvertTo(operand, typeof(double))),
            "!" => Expression.Not(ConvertTo(operand, typeof(bool))),
            _ => throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported.")
        };
    }

    private Expression CompileUpdate(JsUpdateExpression update)
    {
        var target = CompileAssignable(update.Target);
        return update.Operator switch
        {
            "++" => update.Prefix ? Expression.PreIncrementAssign(target) : Expression.PostIncrementAssign(target),
            "--" => update.Prefix ? Expression.PreDecrementAssign(target) : Expression.PostDecrementAssign(target),
            _ => throw new NotSupportedException($"Update operator '{update.Operator}' is not supported.")
        };
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
            "%" => Expression.Modulo(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "<" => Expression.LessThan(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "<=" => Expression.LessThanOrEqual(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            ">" => Expression.GreaterThan(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            ">=" => Expression.GreaterThanOrEqual(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "==" or "===" => Expression.Equal(ConvertTo(left, right.Type), right),
            "!=" or "!==" => Expression.NotEqual(ConvertTo(left, right.Type), right),
            "&&" => Expression.AndAlso(ConvertTo(left, typeof(bool)), ConvertTo(right, typeof(bool))),
            "||" => Expression.OrElse(ConvertTo(left, typeof(bool)), ConvertTo(right, typeof(bool))),
            _ => throw new NotSupportedException($"Operator '{binary.Operator}' is not supported.")
        };
    }

    private static bool CanConvert(Type source, Type target) => target.IsAssignableFrom(source) || source == typeof(double) && target == typeof(int) || source == typeof(int) && target == typeof(double) || target == typeof(object);
    private static Expression ConvertTo(Expression expression, Type targetType) => expression.Type == targetType ? expression : Expression.Convert(expression, targetType);
    private static Expression Default(Type type) => type == typeof(void) ? Expression.Empty() : Expression.Default(type);
}

public static class TypedJintTranspiler
{
    public static string TranspileToCSharp(string source, string className = "ScriptModule")
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using TypedJint;");
        builder.AppendLine();
        builder.Append("public static class ").Append(SanitizeIdentifier(className)).AppendLine();
        builder.AppendLine("{");
        foreach (var fn in SimpleJsParser.ParseFunctions(source))
        {
            EmitFunction(builder, fn, 1);
            builder.AppendLine();
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    public static string TranspileFunctionToCSharp(JsFunctionDeclaration function)
    {
        var builder = new StringBuilder();
        EmitFunction(builder, function, 0);
        return builder.ToString();
    }

    private static void EmitFunction(StringBuilder builder, JsFunctionDeclaration function, int indent)
    {
        var pad = Pad(indent);
        var returnType = function.Annotation is null ? "object?" : ToCSharpType(function.Annotation.ReturnType);
        var parameters = function.Parameters.Select(parameter =>
        {
            var type = function.Annotation is not null && function.Annotation.Parameters.TryGetValue(parameter, out var staticType) ? ToCSharpType(staticType) : "object?";
            return type + " " + SanitizeIdentifier(parameter);
        });

        builder.Append(pad).Append("public static ").Append(returnType).Append(' ').Append(SanitizeIdentifier(function.Name)).Append('(').Append(string.Join(", ", parameters)).AppendLine(")");
        builder.Append(pad).AppendLine("{");
        foreach (var statement in function.Body)
        {
            EmitStatement(builder, statement, indent + 1);
        }
        builder.Append(pad).AppendLine("}");
    }

    private static void EmitStatement(StringBuilder builder, JsStatement statement, int indent)
    {
        var pad = Pad(indent);
        switch (statement)
        {
            case JsBlockStatement block:
                builder.Append(pad).AppendLine("{");
                foreach (var child in block.Statements) EmitStatement(builder, child, indent + 1);
                builder.Append(pad).AppendLine("}");
                break;
            case JsVariableStatement variable:
                builder.Append(pad).Append("var ").Append(SanitizeIdentifier(variable.Name)).Append(" = ").Append(EmitExpression(variable.Initializer)).AppendLine(";");
                break;
            case JsReturnStatement ret:
                builder.Append(pad).Append("return");
                if (ret.Value is not null) builder.Append(' ').Append(EmitExpression(ret.Value));
                builder.AppendLine(";");
                break;
            case JsExpressionStatement expression:
                builder.Append(pad).Append(EmitExpression(expression.Expression)).AppendLine(";");
                break;
            case JsAssignmentStatement assignment:
                builder.Append(pad).Append(EmitExpression(assignment.Target)).Append(" = ").Append(EmitExpression(assignment.Value)).AppendLine(";");
                break;
            case JsIfStatement ifStatement:
                builder.Append(pad).Append("if (").Append(EmitExpression(ifStatement.Test)).AppendLine(")");
                EmitEmbeddedStatement(builder, ifStatement.Consequent, indent);
                if (ifStatement.Alternate is not null)
                {
                    builder.Append(pad).AppendLine("else");
                    EmitEmbeddedStatement(builder, ifStatement.Alternate, indent);
                }
                break;
            case JsWhileStatement whileStatement:
                builder.Append(pad).Append("while (").Append(EmitExpression(whileStatement.Test)).AppendLine(")");
                EmitEmbeddedStatement(builder, whileStatement.Body, indent);
                break;
            case JsForStatement forStatement:
                builder.Append(pad).Append("for (").Append(EmitForPart(forStatement.Init)).Append("; ").Append(forStatement.Test is null ? string.Empty : EmitExpression(forStatement.Test)).Append("; ").Append(EmitForPart(forStatement.Update)).AppendLine(")");
                EmitEmbeddedStatement(builder, forStatement.Body, indent);
                break;
            default:
                builder.Append(pad).Append("// unsupported: ").AppendLine(statement.GetType().Name);
                break;
        }
    }

    private static void EmitEmbeddedStatement(StringBuilder builder, JsStatement statement, int indent)
    {
        if (statement is JsBlockStatement)
        {
            EmitStatement(builder, statement, indent);
            return;
        }

        builder.Append(Pad(indent)).AppendLine("{");
        EmitStatement(builder, statement, indent + 1);
        builder.Append(Pad(indent)).AppendLine("}");
    }

    private static string EmitForPart(JsStatement? statement)
    {
        return statement switch
        {
            null => string.Empty,
            JsVariableStatement variable => "var " + SanitizeIdentifier(variable.Name) + " = " + EmitExpression(variable.Initializer),
            JsAssignmentStatement assignment => EmitExpression(assignment.Target) + " = " + EmitExpression(assignment.Value),
            JsExpressionStatement expression => EmitExpression(expression.Expression),
            _ => statement.GetType().Name
        };
    }

    private static string EmitExpression(JsExpression expression)
    {
        return expression switch
        {
            JsLiteralExpression { Value: null } => "null",
            JsLiteralExpression { Value: string text } => FormatStringLiteral(text),
            JsLiteralExpression { Value: bool value } => value ? "true" : "false",
            JsLiteralExpression { Value: double value } => value.ToString("R", CultureInfo.InvariantCulture),
            JsLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? string.Empty,
            JsIdentifierExpression identifier => SanitizeIdentifier(identifier.Name),
            JsMemberExpression member => EmitExpression(member.Target) + "." + member.Member,
            JsCallExpression call => EmitExpression(call.Target) + "(" + string.Join(", ", call.Arguments.Select(EmitExpression)) + ")",
            JsBinaryExpression binary => "(" + EmitExpression(binary.Left) + " " + MapOperator(binary.Operator) + " " + EmitExpression(binary.Right) + ")",
            JsUnaryExpression unary => "(" + unary.Operator + EmitExpression(unary.Operand) + ")",
            JsUpdateExpression update => update.Prefix ? update.Operator + EmitExpression(update.Target) : EmitExpression(update.Target) + update.Operator,
            _ => expression.GetType().Name
        };
    }

    private static string MapOperator(string op) => op switch { "===" => "==", "!==" => "!=", _ => op };
    private static string ToCSharpType(JsStaticType type) => type.Kind switch
    {
        JsStaticTypeKind.Void => "void",
        JsStaticTypeKind.Number => "double",
        JsStaticTypeKind.String => "string",
        JsStaticTypeKind.Boolean => "bool",
        JsStaticTypeKind.Clr => type.ClrType.Name,
        _ => "object?"
    };

    private static string FormatStringLiteral(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
    private static string SanitizeIdentifier(string value) => value switch { "class" or "namespace" or "public" or "private" or "protected" or "internal" or "static" or "void" or "double" or "string" or "bool" or "object" or "return" => "@" + value, _ => value };
    private static string Pad(int indent) => new(' ', indent * 4);
}
