using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Acornima;
using Acornima.Ast;

namespace TypedJint;

public static class FullJsToCSharpTranspiler
{
    [ThreadStatic]
    private static int _nestedClassDepth;

    [ThreadStatic]
    private static Stack<HashSet<string>>? _localFunctionsStack;

    private static Stack<HashSet<string>> LocalFunctionsStack => _localFunctionsStack ??= new Stack<HashSet<string>>();

    [ThreadStatic]
    private static Stack<string>? _argumentsNames;

    private static Stack<string> ArgumentsNames => _argumentsNames ??= new Stack<string>();

    [ThreadStatic]
    private static HashSet<string>? _topLevelFunctions;

    private static HashSet<string> TopLevelFunctions => _topLevelFunctions ??= new HashSet<string>(StringComparer.Ordinal);

    [ThreadStatic]
    private static Stack<string>? _enclosingConstructs;
    private static Stack<string> EnclosingConstructs => _enclosingConstructs ??= new Stack<string>();

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while"
    };

    private static readonly HashSet<string> TemplateGlobals = new(StringComparer.Ordinal)
    {
        "Date", "Map", "Set", "navigator", "performance", "JSON", "NaN", "Number",
        "setTimeout", "clearTimeout", "setInterval", "clearInterval",
        "RangeError", "TypeError", "ReferenceError", "Error",
        "parseInt", "parseFloat", "isNaN", "isFinite", "console",
        "document", "window", "Math", "Array", "Boolean", "String", "Object", "Function"
    };

    [ThreadStatic]
    private static List<ClassDeclaration>? _currentCollectedClasses;

    [ThreadStatic]
    private static Dictionary<string, HashSet<string>>? _currentPrototypeProperties;

    [ThreadStatic]
    private static Dictionary<string, HashSet<string>>? _currentStaticProperties;

    [ThreadStatic]
    private static bool _isStaticScope;

    private sealed class ClassCollector : AstVisitor
    {
        public List<ClassDeclaration> Classes { get; } = new();
        protected override object? VisitClassDeclaration(ClassDeclaration node)
        {
            Classes.Add(node);
            return base.VisitClassDeclaration(node);
        }
    }

    private sealed class ClassFinder : AstVisitor
    {
        public bool Found { get; private set; }
        protected override object? VisitClassDeclaration(ClassDeclaration node)
        {
            Found = true;
            return base.VisitClassDeclaration(node);
        }
        
        public static bool ContainsClass(Node node)
        {
            if (_currentCollectedClasses == null) return false;
            var start = node.Range.Start;
            var end = node.Range.End;
            for (int i = 0; i < _currentCollectedClasses.Count; i++)
            {
                var cls = _currentCollectedClasses[i];
                if (cls.Range.Start >= start && cls.Range.End <= end)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private sealed class EnclosingVariableCollector : AstVisitor
    {
        public HashSet<string> PromotedFields { get; } = new(StringComparer.Ordinal);
        
        protected override object? VisitFunctionExpression(FunctionExpression node)
        {
            CheckAndPromote(node.Body, node.Params);
            return base.VisitFunctionExpression(node);
        }

        protected override object? VisitFunctionDeclaration(FunctionDeclaration node)
        {
            CheckAndPromote(node.Body, node.Params);
            return base.VisitFunctionDeclaration(node);
        }

        protected override object? VisitArrowFunctionExpression(ArrowFunctionExpression node)
        {
            if (node.Body is FunctionBody body)
            {
                CheckAndPromote(body, node.Params);
            }
            return base.VisitArrowFunctionExpression(node);
        }

        private void CheckAndPromote(FunctionBody body, NodeList<Node> parameters)
        {
            if (ClassFinder.ContainsClass(body))
            {
                var vars = new HashSet<string>(StringComparer.Ordinal);
                ScanDeclaredVariables(body, vars);
                foreach (var v in vars)
                {
                    PromotedFields.Add(v);
                }
                foreach (var param in parameters)
                {
                    var paramName = FormatParameterName(param, new List<ClassDeclaration>(), new HashSet<string>(), "Dummy");
                    if (!string.IsNullOrEmpty(paramName))
                    {
                        PromotedFields.Add(paramName);
                    }
                }
            }
        }
    }

    public static string Transpile(string source, string className, Dictionary<string, JsFunctionDeclaration>? safeFunctions = null, bool emitRuntimeFallback = true)
    {
        _nestedClassDepth = 0;
        ArgumentsNames.Clear();
        LocalFunctionsStack.Clear();
        EnclosingConstructs.Clear();
        var parser = new Parser();
        var program = parser.ParseScript(source);

        var collector = new ClassCollector();
        collector.Visit(program);
        var collectedClasses = collector.Classes;

        var classPropVisitor = new ClassPropertyVisitor();
        classPropVisitor.Visit(program);
        _currentPrototypeProperties = classPropVisitor.PrototypeProperties;
        _currentStaticProperties = classPropVisitor.StaticProperties;

        _currentCollectedClasses = collectedClasses;
        try
        {
            var variableCollector = new EnclosingVariableCollector();
        variableCollector.Visit(program);
        var promotedFields = variableCollector.PromotedFields;

        // Also treat top-level script variables as promoted fields
        var topLevelVars = new HashSet<string>(StringComparer.Ordinal);
        ScanDeclaredVariables(program, topLevelVars);
        foreach (var v in topLevelVars)
        {
            promotedFields.Add(v);
        }

        TopLevelFunctions.Clear();
        foreach (var stmt in program.Body)
        {
            if (stmt is FunctionDeclaration funcDecl && funcDecl.Id is Identifier id)
            {
                TopLevelFunctions.Add(id.Name);
            }
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        var classes = new List<string>();
        var methods = new List<string>();
        var constructorStatements = new List<string>();

        var topLevelScope = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in promotedFields)
        {
            fields.Add(field);
        }

        // Transpile all classes
        foreach (var classDecl in collectedClasses)
        {
            classes.Add(TranspileClass(classDecl, collectedClasses, className));
        }

        // Collect and sort all top-level statements
        foreach (var stmt in program.Body)
        {
            if (stmt is ClassDeclaration)
            {
                continue;
            }
            else if (stmt is FunctionDeclaration funcDecl)
            {
                if (safeFunctions != null && funcDecl.Id != null && safeFunctions.TryGetValue(funcDecl.Id.Name, out var jsFn))
                {
                    var nativeC = "    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n    " + 
                        TypedJintTranspiler.TranspileFunctionToCSharp(jsFn)
                        .Replace("public static ", "public ", StringComparison.Ordinal);
                    methods.Add(nativeC);
                }
                else
                {
                    methods.Add(TranspileFunction(funcDecl, collectedClasses, promotedFields, className));
                }
            }
            else if (stmt is VariableDeclaration varDecl)
            {
                foreach (var decl in varDecl.Declarations)
                {
                    FunctionBody? iifeBody = null;
                    if (decl.Init is CallExpression call && call.Arguments.Count == 0)
                    {
                        if (call.Callee is FunctionExpression funcExpr)
                        {
                            iifeBody = funcExpr.Body;
                        }
                        else if (call.Callee is ArrowFunctionExpression arrowExpr && arrowExpr.Body is FunctionBody arrowBody)
                        {
                            iifeBody = arrowBody;
                        }
                    }

                    if (iifeBody != null)
                    {
                        // Inline IIFE body directly into constructorStatements to avoid lambda complexity
                        var name = decl.Id is Identifier id ? id.Name : (decl.Id?.ToString() ?? "unnamed");
                        fields.Add(name);

                        var iifeScope = new HashSet<string>(topLevelScope, StringComparer.Ordinal);
                        var declared = new HashSet<string>(StringComparer.Ordinal);
                        ScanDeclaredVariables(iifeBody, declared);
                        foreach (var v in declared)
                        {
                            iifeScope.Add(v);
                        }

                        foreach (var v in declared)
                        {
                            if (!promotedFields.Contains(v))
                            {
                                constructorStatements.Add($"dynamic? {SanitizeIdentifier(v, className)} = null;");
                            }
                        }

                        foreach (var bodyStmt in iifeBody.Body)
                        {
                            if (bodyStmt is FunctionDeclaration fd)
                            {
                                methods.Add(TranspileFunction(fd, collectedClasses, promotedFields, className));
                            }
                            else if (bodyStmt is ReturnStatement ret)
                            {
                                var retVal = ret.Argument != null ? TranspileExpression(ret.Argument, collectedClasses, iifeScope, promotedFields, className) : "null";
                                constructorStatements.Add($"{ResolveIdentifier(name, topLevelScope, promotedFields, className)} = {retVal};");
                            }
                            else
                            {
                                var transpiled = TranspileStatement(bodyStmt, 2, collectedClasses, iifeScope, promotedFields, className);
                                if (!string.IsNullOrWhiteSpace(transpiled))
                                {
                                    constructorStatements.Add(transpiled.TrimStart());
                                }
                            }
                        }
                    }
                    else if (decl.Id is ArrayPattern || decl.Id is ObjectPattern)
                    {
                        var ids = new List<string>();
                        CollectIdentifiers(decl.Id, ids);
                        foreach (var varId in ids)
                        {
                            fields.Add(varId);
                        }
                        var tempValName = $"_destruct_{Guid.NewGuid().ToString("N")}";
                        var initVal = decl.Init != null ? TranspileExpression(decl.Init, collectedClasses, topLevelScope, promotedFields, className) : "null";
                        constructorStatements.Add($"dynamic? {tempValName} = {initVal}; {TranspileDestructuring(decl.Id, tempValName, collectedClasses, topLevelScope, promotedFields, className)}");
                    }
                    else
                    {
                        var name = decl.Id is Identifier id ? id.Name : (decl.Id?.ToString() ?? "");
                        fields.Add(name);
                        if (decl.Init != null)
                        {
                            var initializer = TranspileExpression(decl.Init, collectedClasses, topLevelScope, promotedFields, className);
                            constructorStatements.Add($"{ResolveIdentifier(name, topLevelScope, promotedFields, className)} = {initializer};");
                        }
                    }
                }
            }
            else
            {
                var transpiled = TranspileStatement(stmt, 2, collectedClasses, topLevelScope, promotedFields, className);
                if (!string.IsNullOrWhiteSpace(transpiled))
                {
                    constructorStatements.Add(transpiled.TrimStart());
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#nullable disable warnings");
        sb.AppendLine("#pragma warning disable CS8321");
        sb.AppendLine("#pragma warning disable CS0168");
        sb.AppendLine("#pragma warning disable CS0219");
        sb.AppendLine("#pragma warning disable CS0162");
        sb.AppendLine("#pragma warning disable CS0164");
        sb.AppendLine("#pragma warning disable CS1718");
        sb.AppendLine("#pragma warning disable CS0649");
        sb.AppendLine("#pragma warning disable CS0169");
        sb.AppendLine("#pragma warning disable CS0108");
        sb.AppendLine("#pragma warning disable CS0109");
        sb.AppendLine("#pragma warning disable CS0114");
        sb.AppendLine("#pragma warning disable CS0628");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using TypedJint;");
        sb.AppendLine("using TypedJint.Runtime;");
        sb.AppendLine("using static TypedJint.Runtime.JavaScriptRuntime;");
        sb.AppendLine("using static TypedJint.JavaScriptRuntimeEngine;");
        sb.AppendLine();
        sb.AppendLine($"public sealed class {className}");
        sb.AppendLine("{");
        sb.AppendLine("    public object? Invoke(string functionName, params object?[] arguments)");
        sb.AppendLine("    {");
        sb.AppendLine("        var method = this.GetType().GetMethod(functionName);");
        sb.AppendLine("        if (method != null)");
        sb.AppendLine("        {");
        sb.AppendLine("            var parameters = method.GetParameters();");
        sb.AppendLine("            var invokeArgs = new object?[parameters.Length];");
        sb.AppendLine("            for (int i = 0; i < invokeArgs.Length; i++)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (i < arguments.Length) invokeArgs[i] = arguments[i];");
        sb.AppendLine("                else invokeArgs[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;");
        sb.AppendLine("            }");
        sb.AppendLine("            return method.Invoke(this, invokeArgs);");
        sb.AppendLine("        }");
        sb.AppendLine("        throw new MissingMethodException(this.GetType().FullName, functionName);");
        sb.AppendLine("    }");

        // 1. Declare standard DOM globals and math helpers as static properties
        sb.AppendLine("    public static dynamic document => JavaScriptRuntimeEngine.CurrentDocument;");
        sb.AppendLine("    public static dynamic window => JavaScriptRuntimeEngine.CurrentWindow;");
        sb.AppendLine("    public static dynamic console { get; set; } = new ConsoleWrapper();");
        sb.AppendLine("    public static dynamic _math { get; set; } = new JsMath();");
        sb.AppendLine("    public static dynamic Date { get; set; } = typeof(JsDate);");
        sb.AppendLine("    public static dynamic Map { get; set; } = typeof(JsMap);");
        sb.AppendLine("    public static dynamic Set { get; set; } = typeof(JsSet);");
        sb.AppendLine("    public static dynamic navigator { get; set; } = new JsNavigator();");
        sb.AppendLine("    public static dynamic performance { get; set; } = new JsPerformance();");
        sb.AppendLine("    public static dynamic requestAnimationFrame { get; set; } = new Func<dynamic?, dynamic>(callback => JavaScriptStandardLibrary.requestAnimationFrame(callback));");
        sb.AppendLine("    public static dynamic JSON { get; set; } = typeof(JsJson);");
        sb.AppendLine("    public static dynamic NaN { get; set; } = double.NaN;");
        sb.AppendLine("    public static dynamic Number { get; set; } = typeof(JsNumber);");
        sb.AppendLine("    public static dynamic setTimeout { get; set; } = new Func<dynamic?, dynamic?, dynamic>((callback, delay) => JavaScriptStandardLibrary.setTimeout(callback, delay));");
        sb.AppendLine("    public static dynamic clearTimeout { get; set; } = new Action<dynamic?>(id => JavaScriptStandardLibrary.clearTimeout(id));");
        sb.AppendLine("    public static dynamic setInterval { get; set; } = new Func<dynamic?, dynamic?, dynamic>((callback, delay) => JavaScriptStandardLibrary.setInterval(callback, delay));");
        sb.AppendLine("    public static dynamic clearInterval { get; set; } = new Action<dynamic?>(id => JavaScriptStandardLibrary.clearInterval(id));");
        sb.AppendLine("    public static dynamic Array { get; set; } = typeof(JsArray);");
        if (emitRuntimeFallback)
        {
            sb.AppendLine("    private readonly JavaScriptRuntimeEngine _runtime;");
            sb.AppendLine("    private const string Source = \"\";");
            sb.AppendLine();
        }

        // 2. Declare top-level variables as static C# class fields
        foreach (var field in fields)
        {
            sb.AppendLine($"    public static dynamic? {SanitizeIdentifier(field, className)} {{ get; set; }}");
        }
        sb.AppendLine();

        // 3. Constructor
        if (constructorStatements.Count > 0 || emitRuntimeFallback)
        {
            sb.AppendLine($"    public {className}()");
            sb.AppendLine("    {");
            sb.AppendLine("        dynamic? self = this;");
            if (emitRuntimeFallback)
            {
                sb.AppendLine("        if (false) _runtime.Execute(Source);");
            }
            foreach (var cStmt in constructorStatements)
            {
                sb.AppendLine("        " + cStmt);
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // 4. Methods (static)
        foreach (var method in methods)
        {
            sb.AppendLine(method);
            sb.AppendLine();
        }

        // 5. Nested Classes
        foreach (var cls in classes)
        {
            sb.AppendLine(cls);
            sb.AppendLine();
        }
        sb.AppendLine("}");

        return sb.ToString();
        }
        finally
        {
            _currentCollectedClasses = null;
            _currentPrototypeProperties = null;
            _currentStaticProperties = null;
        }
    }

    private static string TranspileClass(ClassDeclaration classDecl, List<ClassDeclaration> collectedClasses, string fileClassName)
    {
        _nestedClassDepth++;
        try
        {
            var classIdName = classDecl.Id is Identifier id ? id.Name : "AnonymousClass";
            var className = classIdName;
            var explicitClassMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in classDecl.Body.Body)
            {
                if (element is MethodDefinition methodDef)
                {
                    if (methodDef.Kind == PropertyKind.Get || methodDef.Kind == PropertyKind.Set)
                    {
                        if (methodDef.Key is Identifier memberId)
                        {
                            explicitClassMembers.Add(memberId.Name);
                        }
                    }
                }
                else if (element is PropertyDefinition propDef)
                {
                    if (propDef.Key is Identifier memberId)
                    {
                        explicitClassMembers.Add(memberId.Name);
                    }
                }
            }

            var properties = FindThisProperties(classDecl);
            if (_currentPrototypeProperties != null && _currentPrototypeProperties.TryGetValue(classIdName, out var protoProps))
            {
                foreach (var prop in protoProps)
                {
                    properties.Add(prop);
                }
            }
            properties.RemoveWhere(prop => explicitClassMembers.Contains(prop));

            var sb = new StringBuilder();
            sb.Append("    public class ").Append(SanitizeIdentifier(classIdName, fileClassName));
            if (classDecl.SuperClass != null && classDecl.SuperClass is Identifier headerSuperId && IsClassName(headerSuperId.Name, collectedClasses, new HashSet<string>()))
            {
                var superStr = SanitizeIdentifier(headerSuperId.Name, fileClassName);
                sb.Append(" : ").Append(superStr);
            }
            sb.AppendLine();
            sb.AppendLine("    {");

            foreach (var prop in properties)
            {
                sb.AppendLine($"        public dynamic? {SanitizeIdentifier(prop, className)} {{ get; set; }}");
            }
            if (properties.Count > 0) sb.AppendLine();

            var thisStaticProps = FindThisStaticProperties(classDecl);
            var staticPropsSet = new HashSet<string>(StringComparer.Ordinal);
            if (_currentStaticProperties != null && _currentStaticProperties.TryGetValue(classIdName, out var staticProps))
            {
                foreach (var prop in staticProps) staticPropsSet.Add(prop);
            }
            foreach (var prop in thisStaticProps) staticPropsSet.Add(prop);
            staticPropsSet.RemoveWhere(prop => explicitClassMembers.Contains(prop));

            if (staticPropsSet.Count > 0)
            {
                foreach (var prop in staticPropsSet)
                {
                    if (properties.Contains(prop)) continue;
                    sb.AppendLine($"        public static dynamic? {SanitizeIdentifier(prop, className)} {{ get; set; }}");
                }
                sb.AppendLine();
            }

            // Replicate JS constructor inheritance in C#
            var constructor = classDecl.Body.Body
                .OfType<MethodDefinition>()
                .FirstOrDefault(m => m.Kind == PropertyKind.Constructor);

            if (constructor == null)
            {
                var needsDefaultConst = false;
                var parentParamsSig = "";
                var baseCallStr = "";
                if (classDecl.SuperClass is Identifier inheritSuperId)
                {
                    needsDefaultConst = true;
                    var parentClass = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == inheritSuperId.Name);
                    if (parentClass != null)
                    {
                        var parentConstructor = parentClass.Body.Body
                            .OfType<MethodDefinition>()
                            .FirstOrDefault(m => m.Kind == PropertyKind.Constructor);
                        
                        if (parentConstructor != null && parentConstructor.Value != null)
                        {
                            var parentParamsList = parentConstructor.Value.Params.Select(p => FormatParameterName(p, collectedClasses, new HashSet<string>(), className)).ToList();
                            parentParamsSig = string.Join(", ", parentParamsList.Select(p => "dynamic? " + SanitizeIdentifier(p, className) + " = null"));
                            var parentParamsCall = string.Join(", ", parentParamsList.Select(p => $"(object?){SanitizeIdentifier(p, className)}"));
                            baseCallStr = $" : base({parentParamsCall})";
                        }
                    }
                }
                else
                {
                    var hasShadowed = classDecl.Body.Body
                        .OfType<MethodDefinition>()
                        .Where(m => m.Kind != PropertyKind.Constructor && m.Kind != PropertyKind.Get && m.Kind != PropertyKind.Set)
                        .Select(m => m.Key is Identifier idKey ? idKey.Name : null)
                        .Any(n => n != null && properties.Contains(n));
                    if (hasShadowed)
                    {
                        needsDefaultConst = true;
                    }
                }

                if (needsDefaultConst)
                {
                    sb.Append("        public ").Append(SanitizeIdentifier(classIdName, fileClassName))
                      .Append("(").Append(parentParamsSig).Append(")").Append(baseCallStr).AppendLine()
                      .AppendLine("        {");
                    sb.Append(GenerateShadowedMethodsInitialization(className, collectedClasses, "            "));
                    sb.AppendLine("        }");
                }
            }

            var getters = new Dictionary<(string Name, bool IsStatic), MethodDefinition>();
            var setters = new Dictionary<(string Name, bool IsStatic), MethodDefinition>();
            var propKeys = new List<(string Name, bool IsStatic)>();

            foreach (var element in classDecl.Body.Body)
            {
                if (element is MethodDefinition method && (method.Kind == PropertyKind.Get || method.Kind == PropertyKind.Set))
                {
                    var name = method.Key is Identifier propId ? propId.Name : method.Key.ToString();
                    var key = (name ?? "", method.Static);
                    if (method.Kind == PropertyKind.Get) getters[key] = method;
                    else setters[key] = method;
                    if (!propKeys.Contains(key)) propKeys.Add(key);
                }
            }

            foreach (var element in classDecl.Body.Body)
            {
                if (element is MethodDefinition method)
                {
                    var methodValue = method.Value;
                    if (methodValue == null) continue;

                    var name = method.Key is Identifier idKey ? idKey.Name : TranspileExpression((Expression?)method.Key, collectedClasses, new HashSet<string>(), new HashSet<string>(), className);
                    var parametersList = methodValue.Params.Select(p => FormatParameterName(p, collectedClasses, new HashSet<string>(), className)).ToList();
                    var parameters = parametersList.Select(p => "dynamic? " + SanitizeIdentifier(p, className) + " = null").ToArray();

                    if (method.Kind != PropertyKind.Constructor && method.Kind != PropertyKind.Get && method.Kind != PropertyKind.Set)
                    {
                        var allMethodsOfName = collectedClasses
                            .SelectMany(c => c.Body.Body.OfType<MethodDefinition>())
                            .Where(m => m.Key is Identifier mId && mId.Name == name && m.Kind != PropertyKind.Get && m.Kind != PropertyKind.Set && m.Kind != PropertyKind.Constructor)
                            .ToList();
                        var maxParamCount = allMethodsOfName.Count > 0 ? allMethodsOfName.Max(m => m.Value != null ? m.Value.Params.Count : 0) : methodValue.Params.Count;
                        if (maxParamCount > parametersList.Count)
                        {
                            var paddedParams = new List<string>();
                            for (int i = 0; i < maxParamCount; i++)
                            {
                                if (i < parametersList.Count)
                                {
                                    paddedParams.Add("dynamic? " + SanitizeIdentifier(parametersList[i], className) + " = null");
                                }
                                else
                                {
                                    paddedParams.Add($"dynamic? _unused_{i} = null");
                                }
                            }
                            parameters = paddedParams.ToArray();
                        }
                    }

                    if (method.Kind == PropertyKind.Get || method.Kind == PropertyKind.Set)
                    {
                        var key = (name ?? "", method.Static);
                        if (propKeys.Contains(key))
                        {
                            propKeys.Remove(key);

                            var isStatic = method.Static;
                            var propName = SanitizeIdentifier(name, className);

                            sb.Append(isStatic ? "        public static dynamic? " : "        public dynamic? ")
                              .AppendLine(propName)
                              .AppendLine("        {");

                            if (getters.TryGetValue(key, out var getter))
                            {
                                var getterValue = getter.Value;
                                if (getterValue != null)
                                {
                                    var returnedExpr = ExtractReturnedExpression(getterValue.Body, collectedClasses, new HashSet<string>(), new HashSet<string>(), className);
                                    if (returnedExpr != null)
                                    {
                                        sb.AppendLine($"            get => {returnedExpr};");
                                    }
                                    else
                                    {
                                        sb.AppendLine("            get");
                                        var getterParamsList = getterValue.Params.Select(p => FormatParameterName(p, collectedClasses, new HashSet<string>(), className)).ToList();
                                        var bodyStr = TranspileFunctionBody(getterValue.Body, 3, collectedClasses, new HashSet<string>(), getterValue.Params, getterParamsList, new HashSet<string>(), className);
                                        sb.AppendLine(bodyStr);
                                    }
                                }
                            }

                            if (setters.TryGetValue(key, out var setter))
                            {
                                var setterValue = setter.Value;
                                if (setterValue != null)
                                {
                                    sb.AppendLine("            set");
                                    sb.AppendLine("            {");
                                    
                                    var setterParamsList = setterValue.Params.Select(p => FormatParameterName(p, collectedClasses, new HashSet<string>(), className)).ToList();
                                    if (setterValue.Params.Count > 0)
                                    {
                                        var paramName = setterParamsList[0];
                                        if (paramName != "value")
                                        {
                                            sb.AppendLine($"                dynamic? {SanitizeIdentifier(paramName, className)} = value;");
                                        }
                                    }
                                    
                                    var bodyStr = TranspileFunctionBody(setterValue.Body, 4, collectedClasses, new HashSet<string>(), setterValue.Params, setterParamsList, new HashSet<string>(), className, isSetter: true);
                                    var bodyLines = bodyStr.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                                    if (bodyLines.Length > 2 && bodyLines[0].Trim() == "{")
                                    {
                                        int endTake = bodyLines.Length - 1;
                                        while (endTake >= 0 && string.IsNullOrWhiteSpace(bodyLines[endTake])) endTake--;
                                        if (endTake >= 0 && bodyLines[endTake].Trim() == "}")
                                        {
                                            bodyStr = string.Join(Environment.NewLine, bodyLines.Skip(1).Take(endTake - 1));
                                        }
                                    }
                                    sb.AppendLine(bodyStr);
                                    sb.AppendLine("            }");
                                }
                            }

                            sb.AppendLine("        }");
                            sb.AppendLine();
                        }
                    }
                    else if (method.Kind == PropertyKind.Constructor)
                    {
                        var baseCallStr = "";
                        if (classDecl.SuperClass != null && classDecl.SuperClass is Identifier constSuperId && IsClassName(constSuperId.Name, collectedClasses, new HashSet<string>()))
                        {
                            var superCall = FindSuperCall(methodValue.Body);
                            if (superCall != null)
                            {
                                var baseArgs = string.Join(", ", superCall.Arguments.Select(e => $"(object?)({TranspileExpression(e, collectedClasses, new HashSet<string>(), new HashSet<string>(), className)})"));
                                baseCallStr = $" : base({baseArgs})";
                            }
                            else
                            {
                                baseCallStr = " : base()";
                            }
                        }
                        sb.Append("        public ").Append(SanitizeIdentifier(classIdName, fileClassName)).Append("(").Append(string.Join(", ", parameters)).Append(")").AppendLine(baseCallStr);
                        sb.AppendLine(TranspileFunctionBody(methodValue.Body, 2, collectedClasses, new HashSet<string>(), methodValue.Params, parametersList, new HashSet<string>(), className, isConstructor: true));
                    }
                    else
                    {
                        var hasStaticAndInstanceCollision = false;
                        if (name != null)
                        {
                            var hasStatic = classDecl.Body.Body.OfType<MethodDefinition>().Any(m => m.Static && m.Key is Identifier idKey && idKey.Name == name);
                            var hasInstance = classDecl.Body.Body.OfType<MethodDefinition>().Any(m => !m.Static && m.Key is Identifier idKey && idKey.Name == name);
                            hasStaticAndInstanceCollision = hasStatic && hasInstance;
                        }

                        var isShadowed = name != null && properties.Contains(name);
                        var actualMethodName = (hasStaticAndInstanceCollision && method.Static) ? name + "_static" : (isShadowed ? name + "_method" : name);

                        sb.Append(method.Static ? "        public static dynamic? " : "        public dynamic? ")
                          .Append(SanitizeIdentifier(actualMethodName, className))
                          .Append("(")
                          .Append(string.Join(", ", parameters))
                          .AppendLine(")");
                        var oldStatic = _isStaticScope;
                        _isStaticScope = method.Static;
                        try
                        {
                            sb.AppendLine(TranspileFunctionBody(methodValue.Body, 2, collectedClasses, new HashSet<string>(), methodValue.Params, parametersList, new HashSet<string>(), className));
                        }
                        finally
                        {
                            _isStaticScope = oldStatic;
                        }
                    }
                    sb.AppendLine();
                }
                else if (element is PropertyDefinition field)
                {
                    var name = field.Key is Identifier idField ? idField.Name : field.Key.ToString();
                    sb.Append(field.Static ? "        public static dynamic? " : "        public dynamic? ")
                      .Append(SanitizeIdentifier(name, className))
                      .AppendLine(" { get; set; }");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("    }");
            return sb.ToString();
        }
        finally
        {
            _nestedClassDepth--;
        }
    }

    private static string TranspileFunction(FunctionDeclaration funcDecl, List<ClassDeclaration> collectedClasses, HashSet<string> promotedFields, string className)
    {
        var name = funcDecl.Id is Identifier id ? id.Name : "AnonymousFunc";
        var parametersList = funcDecl.Params.Select(p => FormatParameterName(p, collectedClasses, new HashSet<string>(), className)).ToList();
        
        string parameters;
        if (ClassFinder.ContainsClass(funcDecl.Body))
        {
            parameters = string.Join(", ", parametersList.Select(p => "dynamic? " + SanitizeIdentifier(p, className) + "Param = null"));
        }
        else
        {
            parameters = string.Join(", ", parametersList.Select(p => "dynamic? " + SanitizeIdentifier(p, className) + " = null"));
        }

        var sb = new StringBuilder();
        sb.Append("    public dynamic? ").Append(SanitizeIdentifier(name, className)).Append("(").Append(parameters).AppendLine(")");
        sb.AppendLine(TranspileFunctionBody(funcDecl.Body, 1, collectedClasses, new HashSet<string>(), funcDecl.Params, parametersList, promotedFields, className));
        return sb.ToString();
    }

    private static string TranspileFunctionBody(FunctionBody body, int indent, List<ClassDeclaration> collectedClasses, HashSet<string>? parentScope, IReadOnlyList<Node>? parameterNodes, IEnumerable<string>? parameters, HashSet<string> promotedFields, string className, bool isConstructor = false, bool isArrow = false, bool isSetter = false)
    {
        var pad = new string(' ', indent * 4);
        var localScope = parentScope != null ? new HashSet<string>(parentScope, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        bool pushedArgs = false;
        string uniqueArgsName = "";

        if (parameterNodes != null)
        {
            foreach (var paramNode in parameterNodes)
            {
                if (paramNode is ArrayPattern || paramNode is ObjectPattern)
                {
                    var ids = new List<string>();
                    CollectIdentifiers(paramNode, ids);
                    foreach (var id in ids)
                    {
                        localScope.Add(id);
                    }
                }
            }
        }
        
        var declared = new HashSet<string>(StringComparer.Ordinal);
        ScanDeclaredVariables(body, declared);

        var currentSet = LocalFunctionsStack.Count > 0 ? new HashSet<string>(LocalFunctionsStack.Peek(), StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        if (parameters != null)
        {
            foreach (var p in parameters) currentSet.Remove(p);
        }
        foreach (var v in declared) currentSet.Remove(v);
        
        var localFuncsToScan = body.Body.OfType<FunctionDeclaration>().ToList();
        foreach (var lf in localFuncsToScan)
        {
            var name = lf.Id is Identifier id ? id.Name : "AnonymousLocalFunc";
            currentSet.Add(name);
        }
        LocalFunctionsStack.Push(currentSet);
        
        bool isEnclosing = ClassFinder.ContainsClass(body);

        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                if (!isEnclosing || !promotedFields.Contains(p))
                {
                    localScope.Add(p);
                }
            }
        }
        
        foreach (var v in declared)
        {
            if (!isEnclosing || !promotedFields.Contains(v))
            {
                localScope.Add(v);
            }
        }

        var sb = new StringBuilder();
        sb.Append(pad).AppendLine("{");

        if (parameterNodes != null)
        {
            foreach (var paramNode in parameterNodes)
            {
                if (paramNode is ArrayPattern || paramNode is ObjectPattern)
                {
                    var ids = new List<string>();
                    CollectIdentifiers(paramNode, ids);
                    foreach (var id in ids)
                    {
                        if (!isEnclosing || !promotedFields.Contains(id))
                        {
                            sb.Append(pad).AppendLine($"    dynamic? {ResolveIdentifier(id, localScope, promotedFields, className)} = null;");
                        }
                    }
                }
            }
        }

        if (parameterNodes != null && parameters != null)
        {
            var pList = parameters.ToList();
            for (int i = 0; i < parameterNodes.Count; i++)
            {
                var paramNode = parameterNodes[i];
                var paramName = pList[i];
                if (paramNode is ArrayPattern || paramNode is ObjectPattern)
                {
                    var destrCode = TranspileDestructuring(paramNode, paramName, collectedClasses, localScope, promotedFields, className);
                    if (!string.IsNullOrEmpty(destrCode))
                    {
                        sb.Append(pad).AppendLine("    " + destrCode);
                    }
                }
            }
        }

        if (parameters != null && isEnclosing)
        {
            foreach (var p in parameters)
            {
                if (promotedFields.Contains(p))
                {
                    sb.Append(pad).AppendLine($"    {ResolveIdentifier(p, localScope, promotedFields, className)} = {SanitizeIdentifier(p, className)}Param;");
                }
            }
        }

        foreach (var v in declared)
        {
            if (parameters == null || !parameters.Contains(v))
            {
                if (!isEnclosing || !promotedFields.Contains(v))
                {
                    sb.Append(pad).AppendLine($"    dynamic? {SanitizeIdentifier(v, className)} = null;");
                }
            }
        }

        if (!isArrow && !isConstructor && (parameters == null || !parameters.Contains("arguments")))
        {
            uniqueArgsName = "arguments_" + Guid.NewGuid().ToString("N");
            ArgumentsNames.Push(uniqueArgsName);
            pushedArgs = true;
            sb.Append(pad).AppendLine($"    dynamic? {uniqueArgsName} = new List<dynamic?> {{ {(parameters == null ? "" : string.Join(", ", parameters.Select(p => SanitizeIdentifier(p, className))))} }};");
            localScope.Add("arguments");
        }

        var localFuncs = body.Body.OfType<FunctionDeclaration>().ToList();
         foreach (var lf in localFuncs)
         {
             var name = lf.Id is Identifier id ? id.Name : "AnonymousLocalFunc";
             localScope.Add(name);
             
             var sanitized = SanitizeIdentifier(name, className);
             sb.Append(pad).AppendLine($"    dynamic? {sanitized} = null;");
         }
         foreach (var lf in localFuncs)
         {
             var name = lf.Id is Identifier id ? id.Name : "AnonymousLocalFunc";
             var sanitized = SanitizeIdentifier(name, className);
             int paramCount = Math.Max(10, lf.Params.Count);
             string delegateType = "Func<" + string.Join(", ", Enumerable.Repeat("dynamic?", paramCount + 1)) + ">";
             sb.Append(pad).AppendLine($"    {sanitized} = new {delegateType}({sanitized}_local);");
         }
        foreach (var stmt in body.Body)
        {
            var transpiled = TranspileStatement(stmt, indent + 1, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter);
            if (!string.IsNullOrWhiteSpace(transpiled))
            {
                sb.AppendLine(transpiled);
            }
        }
        if (!isConstructor && !isSetter && (body.Body.Count == 0 || !EndsWithControlFlow(body.Body.Last())))
        {
            sb.Append(pad).AppendLine("    return null;");
        }
        if (isConstructor)
        {
            sb.Append(GenerateShadowedMethodsInitialization(className, collectedClasses, pad));
        }
        if (pushedArgs)
        {
            ArgumentsNames.Pop();
        }
        LocalFunctionsStack.Pop();
        sb.Append(pad).Append("}");
        return sb.ToString();
    }

    private static string TranspileStatement(Statement? stmt, int indent, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className, bool isConstructor = false, bool isSetter = false)
    {
        if (stmt == null) return "";
        var pad = new string(' ', indent * 4);

        switch (stmt)
        {
            case ClassDeclaration:
                return "";

            case BlockStatement block:
                var blockBuilder = new StringBuilder();
                blockBuilder.Append(pad).AppendLine("{");
                foreach (var child in block.Body)
                {
                    var transpiled = TranspileStatement(child, indent + 1, collectedClasses, localScope, promotedFields, className, isConstructor);
                    if (!string.IsNullOrWhiteSpace(transpiled))
                    {
                        blockBuilder.AppendLine(transpiled);
                    }
                }
                blockBuilder.Append(pad).Append("}");
                return blockBuilder.ToString();

            case EmptyStatement:
                return "";

            case ExpressionStatement exprStmt:
                if (exprStmt.Expression is CallExpression callSuper && callSuper.Callee is Super)
                {
                    return "";
                }
                if (IsValidCSharpStatementExpression(exprStmt.Expression))
                {
                    var exprStr = TranspileExpression(exprStmt.Expression, collectedClasses, localScope, promotedFields, className, isStatement: true);
                    return pad + exprStr + ";";
                }
                else
                {
                    var exprStr = TranspileExpression(exprStmt.Expression, collectedClasses, localScope, promotedFields, className, isStatement: false);
                    return pad + $"JavaScriptRuntime.Discard(" + exprStr + ");";
                }

            case VariableDeclaration varDecl:
                var varBuilder = new StringBuilder();
                foreach (var decl in varDecl.Declarations)
                {
                    if (decl.Id is ArrayPattern || decl.Id is ObjectPattern)
                    {
                        var tempValName = $"_destruct_{Guid.NewGuid().ToString("N")}";
                        var initVal = decl.Init != null ? TranspileExpression(decl.Init, collectedClasses, localScope, promotedFields, className) : "null";
                        varBuilder.Append(pad).AppendLine($"dynamic? {tempValName} = {initVal}; {TranspileDestructuring(decl.Id, tempValName, collectedClasses, localScope, promotedFields, className)}");
                    }
                    else
                    {
                        var name = decl.Id is Identifier id ? id.Name : (decl.Id.ToString() ?? "");
                        var initializer = decl.Init != null ? TranspileExpression(decl.Init, collectedClasses, localScope, promotedFields, className) : "null";
                        varBuilder.Append(pad).AppendLine($"{ResolveIdentifier(name, localScope, promotedFields, className)} = {initializer};");
                    }
                }
                return varBuilder.ToString().TrimEnd();

            case ReturnStatement ret:
                if (isConstructor || isSetter)
                {
                    return pad + "return;";
                }
                var val = ret.Argument != null ? TranspileExpression(ret.Argument, collectedClasses, localScope, promotedFields, className) : "null";
                return pad + $"return {val};";

            case IfStatement ifStmt:
                var ifBuilder = new StringBuilder();
                ifBuilder.Append(pad).AppendLine($"if (JavaScriptRuntime.ToBool({TranspileExpression(ifStmt.Test, collectedClasses, localScope, promotedFields, className)}))");
                ifBuilder.AppendLine(TranspileStatementBody(ifStmt.Consequent, indent, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter));
                if (ifStmt.Alternate != null)
                {
                    ifBuilder.Append(pad).AppendLine("else");
                    ifBuilder.AppendLine(TranspileStatementBody(ifStmt.Alternate, indent, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter));
                }
                return ifBuilder.ToString().TrimEnd();

            case WhileStatement whileStmt:
                EnclosingConstructs.Push("loop");
                try
                {
                    var whileBuilder = new StringBuilder();
                    whileBuilder.Append(pad).AppendLine($"while (JavaScriptRuntime.ToBool({TranspileExpression(whileStmt.Test, collectedClasses, localScope, promotedFields, className)}))");
                    whileBuilder.AppendLine(TranspileStatementBody(whileStmt.Body, indent, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter));
                    return whileBuilder.ToString().TrimEnd();
                }
                finally
                {
                    EnclosingConstructs.Pop();
                }

            case DoWhileStatement doStmt:
                EnclosingConstructs.Push("loop");
                try
                {
                    var doBuilder = new StringBuilder();
                    doBuilder.Append(pad).AppendLine("do");
                    doBuilder.AppendLine(TranspileStatementBody(doStmt.Body, indent, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter));
                    doBuilder.Append(pad).AppendLine($"while (JavaScriptRuntime.ToBool({TranspileExpression(doStmt.Test, collectedClasses, localScope, promotedFields, className)}));");
                    return doBuilder.ToString().TrimEnd();
                }
                finally
                {
                    EnclosingConstructs.Pop();
                }

            case ForStatement forStmt:
                EnclosingConstructs.Push("loop");
                try
                {
                    string forInit = "";
                    if (forStmt.Init != null)
                    {
                        if (forStmt.Init is VariableDeclaration vd)
                        {
                            forInit = string.Join(", ", vd.Declarations.Select(d => ResolveIdentifier(d.Id is Identifier id ? id.Name : d.Id.ToString(), localScope, promotedFields, className) + " = " + (d.Init != null ? TranspileExpression(d.Init, collectedClasses, localScope, promotedFields, className) : "null")));
                        }
                        else
                        {
                            forInit = TranspileExpression((Expression)forStmt.Init, collectedClasses, localScope, promotedFields, className, isStatement: true);
                        }
                    }
                    var test = forStmt.Test != null ? $"JavaScriptRuntime.ToBool({TranspileExpression(forStmt.Test, collectedClasses, localScope, promotedFields, className)})" : "true";
                    var update = forStmt.Update != null ? TranspileExpression(forStmt.Update, collectedClasses, localScope, promotedFields, className, isStatement: true) : "";
                    var forBuilder = new StringBuilder();
                    forBuilder.Append(pad).AppendLine($"for ({forInit}; {test}; {update})");
                    forBuilder.AppendLine(TranspileStatementBody(forStmt.Body, indent, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter));
                    return forBuilder.ToString().TrimEnd();
                }
                finally
                {
                    EnclosingConstructs.Pop();
                }

            case BreakStatement:
                if (EnclosingConstructs.Count > 0 && EnclosingConstructs.Peek() == "switch")
                {
                    return "";
                }
                return pad + "break;";

            case ContinueStatement:
                return pad + "continue;";

            case ThrowStatement throwStmt:
                return pad + $"throw new Exception(Convert.ToString({TranspileExpression(throwStmt.Argument, collectedClasses, localScope, promotedFields, className)}));";

            case TryStatement tryStmt:
                var tryBuilder = new StringBuilder();
                tryBuilder.Append(pad).AppendLine("try");
                tryBuilder.AppendLine(TranspileStatement(tryStmt.Block, indent, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter));
                if (tryStmt.Handler != null)
                {
                    var paramName = tryStmt.Handler.Param != null ? SanitizeIdentifier(FormatParameterName(tryStmt.Handler.Param, collectedClasses, localScope, className), className) : "ex";
                    var handlerScope = new HashSet<string>(localScope, StringComparer.Ordinal);
                    if (tryStmt.Handler.Param != null)
                    {
                        if (tryStmt.Handler.Param is Identifier id)
                        {
                            handlerScope.Add(id.Name);
                        }
                        else
                        {
                            var ids = new List<string>();
                            CollectIdentifiers(tryStmt.Handler.Param, ids);
                            foreach (var varId in ids)
                            {
                                handlerScope.Add(varId);
                            }
                        }
                    }
                    tryBuilder.Append(pad).AppendLine($"catch (Exception {paramName})");
                    var handlerBody = TranspileStatement(tryStmt.Handler.Body, indent, collectedClasses, handlerScope, promotedFields, className, isConstructor, isSetter);
                    if (tryStmt.Handler.Param != null && (tryStmt.Handler.Param is ArrayPattern || tryStmt.Handler.Param is ObjectPattern))
                    {
                        var destrCode = TranspileDestructuring(tryStmt.Handler.Param, paramName, collectedClasses, handlerScope, promotedFields, className);
                        var catchBodyPad = new string(' ', (indent + 1) * 4);
                        handlerBody = handlerBody.Insert(handlerBody.IndexOf('{') + 1, Environment.NewLine + catchBodyPad + destrCode + ";");
                    }
                    tryBuilder.AppendLine(handlerBody);
                }
                if (tryStmt.Finalizer != null)
                {
                    tryBuilder.Append(pad).AppendLine("finally");
                    tryBuilder.AppendLine(TranspileStatement(tryStmt.Finalizer, indent, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter));
                }
                return tryBuilder.ToString().TrimEnd();

            case SwitchStatement switchStmt:
                EnclosingConstructs.Push("switch");
                try
                {
                    var swBuilder = new StringBuilder();
                    var tempVar = $"_sw_{Guid.NewGuid().ToString("N")}";
                    swBuilder.Append(pad).AppendLine($"dynamic? {tempVar} = {TranspileExpression(switchStmt.Discriminant, collectedClasses, localScope, promotedFields, className)};");
                    
                    bool first = true;
                    var currentConditions = new List<string>();
                    bool hasDefault = false;
                    
                    for (int caseIdx = 0; caseIdx < switchStmt.Cases.Count; caseIdx++)
                    {
                        var c = switchStmt.Cases[caseIdx];
                        if (c.Test != null)
                        {
                            currentConditions.Add($"({tempVar} == {TranspileExpression(c.Test, collectedClasses, localScope, promotedFields, className)})");
                        }
                        else
                        {
                            hasDefault = true;
                        }
                        
                        if (c.Consequent.Count > 0 || caseIdx == switchStmt.Cases.Count - 1)
                        {
                            if (first)
                            {
                                swBuilder.Append(pad);
                                first = false;
                            }
                            else
                            {
                                swBuilder.Append(" else ");
                            }
                            
                            if (currentConditions.Count > 0)
                            {
                                swBuilder.AppendLine($"if ({string.Join(" || ", currentConditions)})");
                            }
                            else if (hasDefault)
                            {
                                swBuilder.AppendLine();
                            }
                            else
                            {
                                break;
                            }
                            
                            swBuilder.Append(pad).AppendLine("{");
                            foreach (var s in c.Consequent)
                            {
                                if (s is BreakStatement) continue;
                                var transpiled = TranspileStatement(s, indent + 1, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter);
                                if (!string.IsNullOrWhiteSpace(transpiled))
                                {
                                    swBuilder.AppendLine(transpiled);
                                }
                            }
                            swBuilder.Append(pad).AppendLine("}");
                            currentConditions.Clear();
                            hasDefault = false;
                        }
                    }
                    return swBuilder.ToString().TrimEnd();
                }
                finally
                {
                    EnclosingConstructs.Pop();
                }

            case FunctionDeclaration nestedFunc:
                var nfName = nestedFunc.Id is Identifier nfid ? nfid.Name : "AnonymousLocalFunc";
                var nfParamsList = nestedFunc.Params.Select(p => FormatParameterName(p, collectedClasses, localScope, className)).ToList();
                
                var nfBuilder = new StringBuilder();
                var paramCount = Math.Max(10, nestedFunc.Params.Count);
                var lambdaParams = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"dynamic? _p{i} = null"));
                
                nfBuilder.Append(pad).AppendLine($"dynamic? {SanitizeIdentifier(nfName, className)}_local({lambdaParams})");
                nfBuilder.Append(pad).AppendLine("{");
                
                var bodyPad = pad + "    ";
                for (int i = 0; i < nfParamsList.Count; i++)
                {
                    nfBuilder.Append(bodyPad).AppendLine($"dynamic? {SanitizeIdentifier(nfParamsList[i], className)} = _p{i};");
                }
                var argsStr = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"_p{i}"));
                nfBuilder.Append(bodyPad).AppendLine($"dynamic? arguments = new List<dynamic?> {{ {argsStr} }};");
                
                var bodyContent = TranspileFunctionBody(nestedFunc.Body, indent + 1, collectedClasses, localScope, nestedFunc.Params, nfParamsList, promotedFields, className);
                var bodyLines = bodyContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                if (bodyLines.Length > 2 && bodyLines[0].Trim() == "{")
                {
                    int endTake = bodyLines.Length - 1;
                    while (endTake >= 0 && string.IsNullOrWhiteSpace(bodyLines[endTake]))
                    {
                        endTake--;
                    }
                    if (endTake >= 0 && bodyLines[endTake].Trim() == "}")
                    {
                        bodyContent = string.Join(Environment.NewLine, bodyLines.Skip(1).Take(endTake - 1));
                    }
                }
                
                nfBuilder.AppendLine(bodyContent);
                nfBuilder.Append(pad).Append("}");
                return nfBuilder.ToString();

            default:
                return pad + $"// Unsupported statement type: {stmt.Type}";
        }
    }

    private static bool EndsWithControlFlow(Statement stmt)
    {
        if (stmt is BreakStatement || stmt is ReturnStatement || stmt is ThrowStatement || stmt is ContinueStatement)
            return true;
        if (stmt is BlockStatement block && block.Body.Count > 0)
            return EndsWithControlFlow(block.Body.Last());
        if (stmt is IfStatement ifStmt)
            return ifStmt.Alternate != null && EndsWithControlFlow(ifStmt.Consequent) && EndsWithControlFlow(ifStmt.Alternate);
        return false;
    }

    private static string TranspileStatementBody(Statement body, int indent, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className, bool isConstructor, bool isSetter = false)
    {
        if (body is BlockStatement)
        {
            return TranspileStatement(body, indent, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter);
        }
        var pad = new string(' ', indent * 4);
        var inner = TranspileStatement(body, indent + 1, collectedClasses, localScope, promotedFields, className, isConstructor, isSetter);
        return $"{pad}{{\n{inner}\n{pad}}}";
    }

    private static string TranspileExpression(Expression? expr, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className, bool isStatement = false, bool isCallee = false)
    {
        if (expr is null) return "null";
        var result = expr switch
        {
            Literal lit => FormatLiteral(lit),
            Identifier id => IsClassName(id.Name, collectedClasses, localScope) ? $"((dynamic)JavaScriptRuntimeEngine.CurrentEngine.Jint.GetValue(\"{id.Name}\"))" : ResolveIdentifier(id.Name, localScope, promotedFields, className),
            ThisExpression => _isStaticScope ? $"((dynamic)JavaScriptRuntimeEngine.CurrentEngine.Jint.GetValue(\"{className}\"))" : (_nestedClassDepth > 0 ? "this" : "self"),
            Super => (HasStaticSuperClass(className, collectedClasses) ? "base" : "this"),
            MemberExpression mem => FormatMemberExpression(mem, collectedClasses, localScope, promotedFields, className, isCallee),
            AssignmentExpression assign => FormatAssignmentExpression(assign, collectedClasses, localScope, promotedFields, className, isStatement),
            CallExpression call => FormatCallExpression(call, collectedClasses, localScope, promotedFields, className),
            LogicalExpression log => log.Operator switch
            {
                Operator.LogicalAnd => $"LogicalAnd({TranspileExpression(log.Left, collectedClasses, localScope, promotedFields, className)}, {TranspileExpression(log.Right, collectedClasses, localScope, promotedFields, className)})",
                Operator.LogicalOr => $"LogicalOr({TranspileExpression(log.Left, collectedClasses, localScope, promotedFields, className)}, {TranspileExpression(log.Right, collectedClasses, localScope, promotedFields, className)})",
                _ => $"({TranspileExpression(log.Left, collectedClasses, localScope, promotedFields, className)} {MapOperator(log.Operator)} {TranspileExpression(log.Right, collectedClasses, localScope, promotedFields, className)})"
            },
            BinaryExpression bin => bin.Operator switch
            {
                Operator.Division => $"((double)({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}) / (double)({TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)}))",
                Operator.In => $"JavaScriptRuntime.In({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}, {TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})",
                Operator.InstanceOf => $"JavaScriptRuntime.InstanceOf({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}, {TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})",
                Operator.Exponentiation => $"JavaScriptRuntime._math.pow({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}, {TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})",
                Operator.UnsignedRightShift => $"JavaScriptRuntime.UnsignedRightShift({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}, {TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})",
                Operator.BitwiseAnd => $"unchecked(((int)({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}) & (int)({TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})))",
                Operator.BitwiseOr => $"unchecked(((int)({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}) | (int)({TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})))",
                Operator.BitwiseXor => $"unchecked(((int)({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}) ^ (int)({TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})))",
                Operator.LeftShift => $"unchecked(((int)({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}) << (int)({TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})))",
                Operator.RightShift => $"unchecked(((int)({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)}) >> (int)({TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})))",
                Operator.Equality or Operator.StrictEquality or Operator.Inequality or Operator.StrictInequality =>
                    $"(((dynamic)({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)})) {MapOperator(bin.Operator)} ((dynamic)({TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})))",
                _ => $"({TranspileExpression(bin.Left, collectedClasses, localScope, promotedFields, className)} {MapOperator(bin.Operator)} {TranspileExpression(bin.Right, collectedClasses, localScope, promotedFields, className)})"
            },
            UpdateExpression upd => FormatUpdateExpression(upd, collectedClasses, localScope, promotedFields, className),
            UnaryExpression un => FormatUnaryExpression(un, collectedClasses, localScope, promotedFields, className),
            NewExpression n => FormatNewExpression(n, collectedClasses, localScope, promotedFields, className),
            ConditionalExpression cond => (isStatement
                ? $"if (JavaScriptRuntime.ToBool({TranspileExpression(cond.Test, collectedClasses, localScope, promotedFields, className)}))\n{{\n    {(IsValidCSharpStatementExpression(cond.Consequent) ? TranspileExpression(cond.Consequent, collectedClasses, localScope, promotedFields, className, isStatement: true) : "JavaScriptRuntime.Discard(" + TranspileExpression(cond.Consequent, collectedClasses, localScope, promotedFields, className, isStatement: false) + ")")};\n}}\nelse\n{{\n    {(IsValidCSharpStatementExpression(cond.Alternate) ? TranspileExpression(cond.Alternate, collectedClasses, localScope, promotedFields, className, isStatement: true) : "JavaScriptRuntime.Discard(" + TranspileExpression(cond.Alternate, collectedClasses, localScope, promotedFields, className, isStatement: false) + ")")};\n}}"
                : $"(JavaScriptRuntime.ToBool({TranspileExpression(cond.Test, collectedClasses, localScope, promotedFields, className)}) ? (dynamic)({TranspileExpression(cond.Consequent, collectedClasses, localScope, promotedFields, className)}) : (dynamic)({TranspileExpression(cond.Alternate, collectedClasses, localScope, promotedFields, className)}))"),
            ArrayExpression arr => $"new JsArray {{ {string.Join(", ", arr.Elements.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)))} }}",
            TemplateLiteral t => FormatTemplateLiteral(t, collectedClasses, localScope, promotedFields, className),
            ObjectExpression obj => FormatObjectExpression(obj, collectedClasses, localScope, promotedFields, className),
            ArrowFunctionExpression arrow => FormatArrowFunction(arrow, collectedClasses, localScope, promotedFields, className),
            FunctionExpression func => FormatFunctionExpression(func, collectedClasses, localScope, promotedFields, className),
            SequenceExpression seq => $"JavaScriptRuntime.InvokeSequence(() => {{ {string.Join(" ", seq.Expressions.SkipLast(1).Select(e => (IsValidCSharpStatementExpression(e) ? TranspileExpression(e, collectedClasses, localScope, promotedFields, className, isStatement: true) : "JavaScriptRuntime.Discard(" + TranspileExpression(e, collectedClasses, localScope, promotedFields, className, isStatement: false) + ")") + ";"))} return {TranspileExpression(seq.Expressions.Last(), collectedClasses, localScope, promotedFields, className, isStatement: false)}; }})",
            _ => "null"
        };
        if (isStatement && !IsValidCSharpStatementExpression(expr))
        {
            return $"JavaScriptRuntime.Discard({result})";
        }
        return result;
    }

    private static string FormatNewExpression(NewExpression n, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        var errorTypes = new[] { "Error", "RangeError", "TypeError", "ReferenceError" };
        if (n.Callee is Identifier idCall && errorTypes.Contains(idCall.Name))
        {
            return $"new {idCall.Name}({string.Join(", ", n.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)))})";
        }
        var calleeStr = TranspileExpression(n.Callee, collectedClasses, localScope, promotedFields, className);
        if (calleeStr == "Array")
        {
            return "new JsArray()";
        }
        if (n.Callee is ThisExpression)
        {
            var targetClass = SanitizeIdentifier(className, className);
            return $"new {targetClass}({string.Join(", ", n.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)))})";
        }
        if (n.Callee is Identifier id && IsClassName(id.Name, collectedClasses, localScope))
        {
            calleeStr = SanitizeIdentifier(id.Name, className);
            return $"new {calleeStr}({string.Join(", ", n.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)))})";
        }
        return $"JavaScriptRuntime.CreateNewInstance({calleeStr}{(n.Arguments.Count > 0 ? ", " + string.Join(", ", n.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className))) : "")})";
    }

    private static string FormatCallExpression(CallExpression call, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        if (call.Callee is Super)
        {
            return "null";
        }
        if (call.Callee is Identifier calleeIdStr && calleeIdStr.Name == "String")
        {
            var argStr = call.Arguments.Count > 0 ? TranspileExpression(call.Arguments[0], collectedClasses, localScope, promotedFields, className) : "\"\"";
            return $"Convert.ToString({argStr})";
        }
        if (call.Callee is Identifier calleeIdErr && (calleeIdErr.Name == "Error" || calleeIdErr.Name == "RangeError" || calleeIdErr.Name == "TypeError" || calleeIdErr.Name == "ReferenceError"))
        {
            return $"new {calleeIdErr.Name}({string.Join(", ", call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)))})";
        }
        if (call.Callee is Identifier calleeIdLocal && LocalFunctionsStack.Count > 0 && LocalFunctionsStack.Peek().Contains(calleeIdLocal.Name))
        {
            var localCalleeStr = SanitizeIdentifier(calleeIdLocal.Name, className) + "_local";
            return $"{localCalleeStr}({string.Join(", ", call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)))})";
        }
        if (call.Callee is MemberExpression memMath && memMath.Object is Identifier idMath && idMath.Name == "Math" && !memMath.Computed && memMath.Property is Identifier propIdMath)
        {
            var mathMethod = propIdMath.Name switch
            {
                "abs" => "Abs",
                "acos" => "Acos",
                "asin" => "Asin",
                "atan" => "Atan",
                "atan2" => "Atan2",
                "ceil" => "Ceiling",
                "cos" => "Cos",
                "exp" => "Exp",
                "floor" => "Floor",
                "log" => "Log",
                "max" => "Max",
                "min" => "Min",
                "pow" => "Pow",
                "random" => "Random",
                "round" => "Round",
                "sin" => "Sin",
                "sqrt" => "Sqrt",
                "tan" => "Tan",
                _ => null
            };
            if (mathMethod != null)
            {
                var argsStrMath = string.Join(", ", call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)));
                if (mathMethod == "Random") return "Random.Shared.NextDouble()";
                if (mathMethod == "Max" || mathMethod == "Min")
                {
                    if (call.Arguments.Count == 2)
                    {
                        return $"Math.{mathMethod}({argsStrMath})";
                    }
                    return $"JavaScriptRuntime.{mathMethod}({argsStrMath})";
                }
                return $"Math.{mathMethod}({argsStrMath})";
            }
        }
        if (call.Callee is MemberExpression mem)
        {
            var objStr = TranspileExpression(mem.Object, collectedClasses, localScope, promotedFields, className);
            var argsStr = string.Join(", ", call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)));
            if (!mem.Computed && mem.Property is Identifier propId)
            {
                var methodName = propId.Name;
                if (methodName == "apply")
                {
                    var argsList = call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)).ToList();
                    var thisArg = argsList.Count > 0 ? argsList[0] : "null";
                    var applyArgs = argsList.Count > 1 ? argsList[1] : "null";
                    return $"JavaScriptRuntime.apply({objStr}, {thisArg}, {applyArgs})";
                }
                if (methodName == "call")
                {
                    if (objStr.StartsWith("base."))
                    {
                        var baseArgsStr = string.Join(", ", call.Arguments.Skip(1).Select(e => $"(object?)({TranspileExpression(e, collectedClasses, localScope, promotedFields, className)})"));
                        return $"{objStr}({baseArgsStr})";
                    }
                    var argsList = call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)).ToList();
                    var thisArg = argsList.Count > 0 ? argsList[0] : "null";
                    var callArgs = argsList.Skip(1).ToList();
                    return $"JavaScriptRuntime.call({objStr}, {thisArg}{(callArgs.Count > 0 ? ", " + string.Join(", ", callArgs) : "")})";
                }
                if (methodName == "test")
                {
                    var argStr = call.Arguments.Count > 0 ? TranspileExpression(call.Arguments[0], collectedClasses, localScope, promotedFields, className) : "null";
                    return $"JsRegExpExtensions.test({objStr}, {argStr})";
                }
                var arrayMethods = new[] { "push", "concat", "join", "indexOf", "forEach", "map", "filter", "slice", "splice" };
                if (arrayMethods.Contains(methodName))
                {
                    return $"JsArrayExtensions.{methodName}({objStr}{(string.IsNullOrEmpty(argsStr) ? "" : ", " + argsStr)})";
                }

                if (objStr == "base")
                {
                    var baseArgsStr = string.Join(", ", call.Arguments.Select(e => $"(object?)({TranspileExpression(e, collectedClasses, localScope, promotedFields, className)})"));
                    return $"base.{SanitizeIdentifier(methodName, className)}({baseArgsStr})";
                }
                if (objStr == "this" || objStr == "self")
                {
                    if (HasMethodWithParamCount(className, methodName, call.Arguments.Count, collectedClasses))
                    {
                        return $"{objStr}.{SanitizeIdentifier(methodName, className)}({argsStr})";
                    }
                    var arrayDeclThis = string.IsNullOrEmpty(argsStr) ? "System.Array.Empty<object?>()" : $"new object?[] {{ {argsStr} }}";
                    return $"JavaScriptRuntimeEngine.InvokeMethod({objStr}, \"{methodName}\", {arrayDeclThis})";
                }
                else if (IsClassName(objStr, collectedClasses, localScope) || objStr.StartsWith(className))
                {
                    if (HasStaticPropertyOrField(objStr, methodName, collectedClasses))
                    {
                        var actualMethodName = HasStaticAndInstanceCollision(objStr, methodName, collectedClasses) ? methodName + "_static" : methodName;
                        return $"{objStr}.{SanitizeIdentifier(actualMethodName, className)}({argsStr})";
                    }
                    else
                    {
                        var jintObj = $"((dynamic)JavaScriptRuntimeEngine.CurrentEngine.Jint.GetValue(\"{objStr}\"))";
                        var arrayDeclClassName = string.IsNullOrEmpty(argsStr) ? "System.Array.Empty<object?>()" : $"new object?[] {{ {argsStr} }}";
                        return $"JavaScriptRuntimeEngine.InvokeMethod({jintObj}, \"{methodName}\", {arrayDeclClassName})";
                    }
                }
                var arrayDecl = string.IsNullOrEmpty(argsStr) ? "System.Array.Empty<object?>()" : $"new object?[] {{ {argsStr} }}";
                return $"JavaScriptRuntimeEngine.InvokeMethod({objStr}, \"{methodName}\", {arrayDecl})";
            }
            else
            {
                var propStr = TranspileExpression(mem.Property, collectedClasses, localScope, promotedFields, className);
                var arrayDecl = string.IsNullOrEmpty(argsStr) ? "System.Array.Empty<object?>()" : $"new object?[] {{ {argsStr} }}";
                return $"JavaScriptRuntimeEngine.InvokeMethod({objStr}, Convert.ToString({propStr}), {arrayDecl})";
            }
        }

        var calleeStr = TranspileExpression(call.Callee, collectedClasses, localScope, promotedFields, className, isStatement: false, isCallee: true);
        if (call.Callee is FunctionExpression func)
        {
            var delType = GetFuncDelegateType(func.Params.Count);
            var args = call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)).ToList();
            while (args.Count < func.Params.Count)
            {
                args.Add("null");
            }
            return $"{calleeStr}.Invoke({string.Join(", ", args)})";
        }
        if (call.Callee is ArrowFunctionExpression arrow)
        {
            var delType = GetFuncDelegateType(arrow.Params.Count);
            var args = call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)).ToList();
            while (args.Count < arrow.Params.Count)
            {
                args.Add("null");
            }
            return $"{calleeStr}.Invoke({string.Join(", ", args)})";
        }
        return $"{calleeStr}({string.Join(", ", call.Arguments.Select(e => TranspileExpression(e, collectedClasses, localScope, promotedFields, className)))})";
    }

    private static string FormatAssignmentExpression(AssignmentExpression assign, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className, bool isStatement)
    {
        if (assign.Left is ArrayPattern || assign.Left is ObjectPattern)
        {
            var tempValName = $"_destruct_{Guid.NewGuid().ToString("N")}";
            var seq = $"JavaScriptRuntime.InvokeSequence(() => {{ dynamic? {tempValName} = {TranspileExpression(assign.Right, collectedClasses, localScope, promotedFields, className, false)}; {TranspileDestructuring(assign.Left, tempValName, collectedClasses, localScope, promotedFields, className)} return {tempValName}; }})";
            return isStatement ? seq : $"({seq})";
        }

        var valStr = TranspileExpression(assign.Right, collectedClasses, localScope, promotedFields, className, false);

        if (assign.Left is MemberExpression memLen && !memLen.Computed && memLen.Property is Identifier propLen && propLen.Name == "length")
        {
            var objStr = TranspileExpression(memLen.Object, collectedClasses, localScope, promotedFields, className, false);
            var resultLen = $"JavaScriptRuntime.SetListLength({objStr}, {valStr})";
            return isStatement ? resultLen : $"({resultLen})";
        }

        string result;
        if (assign.Left is MemberExpression mem)
        {
            var objStr = TranspileExpression(mem.Object, collectedClasses, localScope, promotedFields, className, false);
            if (mem.Computed)
            {
                var propStr = TranspileExpression(mem.Property, collectedClasses, localScope, promotedFields, className, false);
                if (assign.Operator == Operator.Assignment)
                {
                    result = $"JavaScriptRuntime.SetComputedProperty({objStr}, {propStr}, {valStr})";
                }
                else if (assign.Operator == Operator.UnsignedRightShiftAssignment)
                {
                    result = $"JavaScriptRuntime.SetComputedProperty({objStr}, {propStr}, JavaScriptRuntime.UnsignedRightShift(JavaScriptRuntime.GetComputedProperty({objStr}, {propStr}), {valStr}))";
                }
                else
                {
                    var binOp = assign.Operator switch
                    {
                        Operator.AdditionAssignment => "+",
                        Operator.SubtractionAssignment => "-",
                        Operator.MultiplicationAssignment => "*",
                        Operator.DivisionAssignment => "/",
                        Operator.RemainderAssignment => "%",
                        Operator.BitwiseAndAssignment => "&",
                        Operator.BitwiseOrAssignment => "|",
                        Operator.BitwiseXorAssignment => "^",
                        Operator.LeftShiftAssignment => "<<",
                        Operator.RightShiftAssignment => ">>",
                        _ => "="
                    };
                    result = $"JavaScriptRuntime.SetComputedProperty({objStr}, {propStr}, (dynamic)JavaScriptRuntime.GetComputedProperty({objStr}, {propStr}) {binOp} {valStr})";
                }
            }
            else
            {
                var rawPropName = mem.Property is Identifier id ? id.Name : TranspileExpression(mem.Property, collectedClasses, localScope, promotedFields, className, false);
                bool isLValue = false;
                if (objStr == "this" || objStr == "self")
                {
                    isLValue = HasInstancePropertyOrField(className, rawPropName, collectedClasses);
                }
                else if (IsClassName(objStr, collectedClasses, localScope) || objStr.StartsWith(className))
                {
                    isLValue = true;
                }

                if (isLValue)
                {
                    var sanitizedProp = mem.Property is Identifier id3 ? SanitizeIdentifier(id3.Name, className) : rawPropName;
                    if (assign.Operator == Operator.UnsignedRightShiftAssignment)
                    {
                        result = $"{objStr}.{sanitizedProp} = JavaScriptRuntime.UnsignedRightShift({objStr}.{sanitizedProp}, {valStr})";
                    }
                    else
                    {
                        result = $"{objStr}.{sanitizedProp} {MapOperator(assign.Operator)} {valStr}";
                    }
                }
                else
                {
                    if (assign.Operator == Operator.Assignment)
                    {
                        result = $"JavaScriptRuntimeEngine.SetProperty({objStr}, \"{rawPropName}\", {valStr})";
                    }
                    else if (assign.Operator == Operator.UnsignedRightShiftAssignment)
                    {
                        result = $"JavaScriptRuntimeEngine.SetProperty({objStr}, \"{rawPropName}\", JavaScriptRuntime.UnsignedRightShift(JavaScriptRuntimeEngine.GetProperty({objStr}, \"{rawPropName}\"), {valStr}))";
                    }
                    else
                    {
                        var binOp = assign.Operator switch
                        {
                            Operator.AdditionAssignment => "+",
                            Operator.SubtractionAssignment => "-",
                            Operator.MultiplicationAssignment => "*",
                            Operator.DivisionAssignment => "/",
                            Operator.RemainderAssignment => "%",
                            Operator.BitwiseAndAssignment => "&",
                            Operator.BitwiseOrAssignment => "|",
                            Operator.BitwiseXorAssignment => "^",
                            Operator.LeftShiftAssignment => "<<",
                            Operator.RightShiftAssignment => ">>",
                            _ => "="
                        };
                        result = $"JavaScriptRuntimeEngine.SetProperty({objStr}, \"{rawPropName}\", (dynamic)JavaScriptRuntimeEngine.GetProperty({objStr}, \"{rawPropName}\") {binOp} {valStr})";
                    }
                }
            }
        }
        else if (assign.Left is Identifier id)
        {
            if (localScope.Contains(id.Name) || promotedFields.Contains(id.Name))
            {
                var targetStr = ResolveIdentifier(id.Name, localScope, promotedFields, className);
                if (assign.Operator == Operator.UnsignedRightShiftAssignment)
                {
                    result = $"{targetStr} = JavaScriptRuntime.UnsignedRightShift({targetStr}, {valStr})";
                }
                else
                {
                    result = $"{targetStr} {MapOperator(assign.Operator)} {valStr}";
                }
            }
            else
            {
                if (assign.Operator == Operator.Assignment)
                {
                    result = $"JavaScriptRuntimeEngine.CurrentEngine.Jint.SetValue(\"{id.Name}\", {valStr})";
                }
                else if (assign.Operator == Operator.UnsignedRightShiftAssignment)
                {
                    result = $"JavaScriptRuntimeEngine.CurrentEngine.Jint.SetValue(\"{id.Name}\", JavaScriptRuntime.UnsignedRightShift(JavaScriptRuntimeEngine.CurrentEngine.Jint.GetValue(\"{id.Name}\"), {valStr}))";
                }
                else
                {
                    var binOp = assign.Operator switch
                    {
                        Operator.AdditionAssignment => "+",
                        Operator.SubtractionAssignment => "-",
                        Operator.MultiplicationAssignment => "*",
                        Operator.DivisionAssignment => "/",
                        Operator.RemainderAssignment => "%",
                        Operator.BitwiseAndAssignment => "&",
                        Operator.BitwiseOrAssignment => "|",
                        Operator.BitwiseXorAssignment => "^",
                        Operator.LeftShiftAssignment => "<<",
                        Operator.RightShiftAssignment => ">>",
                        _ => "="
                    };
                    result = $"JavaScriptRuntimeEngine.CurrentEngine.Jint.SetValue(\"{id.Name}\", (dynamic)JavaScriptRuntimeEngine.CurrentEngine.Jint.GetValue(\"{id.Name}\") {binOp} {valStr})";
                }
            }
        }
        else
        {
            result = $"{TranspileExpression((Expression?)assign.Left, collectedClasses, localScope, promotedFields, className, false)} {MapOperator(assign.Operator)} {valStr}";
        }
        return isStatement ? result : $"({result})";
    }

    private static string FormatUnaryExpression(UnaryExpression un, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        if (un.Operator == Operator.Delete)
        {
            if (un.Argument is MemberExpression mem)
            {
                var objStr = TranspileExpression(mem.Object, collectedClasses, localScope, promotedFields, className);
                var propStr = mem.Computed ? TranspileExpression(mem.Property, collectedClasses, localScope, promotedFields, className) : "@\"" + (mem.Property is Identifier id ? id.Name : (mem.Property.ToString() ?? "")) + "\"";
                return $"JavaScriptRuntime.DeleteProperty({objStr}, {propStr})";
            }
            return "true";
        }
        if (un.Operator == Operator.TypeOf)
        {
            return $"JavaScriptRuntime.GetTypeString({TranspileExpression(un.Argument, collectedClasses, localScope, promotedFields, className)})";
        }
        if (un.Operator == Operator.LogicalNot)
        {
            return $"(!JavaScriptRuntime.ToBool({TranspileExpression(un.Argument, collectedClasses, localScope, promotedFields, className)}))";
        }
        if (un.Operator == Operator.Void)
        {
            return "null";
        }
        if (un.Operator == Operator.BitwiseNot)
        {
            return $"unchecked((~(int)({TranspileExpression(un.Argument, collectedClasses, localScope, promotedFields, className)})))";
        }
        return $"({MapUnaryOperator(un.Operator)} {TranspileExpression(un.Argument, collectedClasses, localScope, promotedFields, className)})";
    }

    private static string FormatMemberExpression(MemberExpression mem, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className, bool isCallee = false)
    {
        if (!mem.Computed && mem.Property is Identifier idProto && idProto.Name == "prototype" && mem.Object is Identifier idClass)
        {
            return $"JavaScriptRuntime.GetPrototype(\"{idClass.Name}\")";
        }
        var obj = mem.Object switch
        {
            Identifier id => IsClassName(id.Name, collectedClasses, localScope) ? SanitizeIdentifier(id.Name, className) : ResolveIdentifier(id.Name, localScope, promotedFields, className),
            MemberExpression nested => FormatMemberExpression(nested, collectedClasses, localScope, promotedFields, className),
            ThisExpression => _isStaticScope ? className : (_nestedClassDepth > 0 ? "this" : "self"),
            _ => TranspileExpression(mem.Object, collectedClasses, localScope, promotedFields, className)
        };
        if (!mem.Computed && mem.Property is Identifier propConstructor && propConstructor.Name == "constructor")
        {
            return $"JavaScriptRuntime.GetConstructor({obj})";
        }
        if (!isCallee && !mem.Computed && mem.Property is Identifier propIdMatch)
        {
            var propName = propIdMatch.Name;
            if (obj == "this" || obj == "self")
            {
                var cls = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == className);
                if (cls != null)
                {
                    var method = cls.Body.Body
                        .OfType<MethodDefinition>()
                        .FirstOrDefault(m => m.Key is Identifier idKey && idKey.Name == propName);
                    if (method != null && method.Value != null && method.Kind != PropertyKind.Get && method.Kind != PropertyKind.Set && method.Kind != PropertyKind.Constructor)
                    {
                        var properties = FindThisProperties(cls);
                        var actualMethodName = properties.Contains(propName) ? propName + "_method" : propName;
                        var paramCount = method.Value.Params.Count;
                        var types = string.Join(", ", Enumerable.Repeat("dynamic?", paramCount + 1));
                        var lambdaParams = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"x{i}"));
                        var lambdaArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"x{i}"));
                        return $"new Func<{types}>({(paramCount > 0 ? $"({lambdaParams}) => " : "() => ")}{obj}.{SanitizeIdentifier(actualMethodName, className)}({lambdaArgs}))";
                    }
                }
            }
            else if (IsClassName(obj, collectedClasses, localScope))
            {
                var cls = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == obj);
                if (cls != null)
                {
                    var method = cls.Body.Body
                        .OfType<MethodDefinition>()
                        .FirstOrDefault(m => m.Static && m.Key is Identifier idKey && idKey.Name == propName);
                    if (method != null && method.Value != null && method.Kind != PropertyKind.Get && method.Kind != PropertyKind.Set && method.Kind != PropertyKind.Constructor)
                    {
                        var paramCount = method.Value.Params.Count;
                        var types = string.Join(", ", Enumerable.Repeat("dynamic?", paramCount + 1));
                        var actualMethodName = HasStaticAndInstanceCollision(obj, propName, collectedClasses) ? propName + "_static" : propName;
                        var lambdaParams = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"x{i}"));
                        var lambdaArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"x{i}"));
                        return $"new Func<{types}>({(paramCount > 0 ? $"({lambdaParams}) => " : "() => ")}{obj}.{SanitizeIdentifier(actualMethodName, className)}({lambdaArgs}))";
                    }
                }
            }
        }
        if (!mem.Computed && mem.Property is Identifier propId && propId.Name == "length")
        {
            return $"((dynamic){obj}).Length";
        }
        if (mem.Computed)
        {
            var prop = TranspileExpression(mem.Property, collectedClasses, localScope, promotedFields, className);
            return $"JavaScriptRuntime.GetComputedProperty({obj}, {prop})";
        }
        else
        {
            var rawPropName = mem.Property is Identifier propId2 ? propId2.Name : TranspileExpression(mem.Property, collectedClasses, localScope, promotedFields, className);
            if (obj == "base")
            {
                var sanitizedProp = mem.Property is Identifier propId3 ? SanitizeIdentifier(propId3.Name, className) : rawPropName;
                return $"base.{sanitizedProp}";
            }
            if (obj == "this" || obj == "self")
            {
                if (HasInstancePropertyOrField(className, rawPropName, collectedClasses))
                {
                    var sanitizedProp = mem.Property is Identifier propId3 ? SanitizeIdentifier(propId3.Name, className) : rawPropName;
                    return $"{obj}.{sanitizedProp}";
                }
                return $"JavaScriptRuntimeEngine.GetProperty({obj}, \"{rawPropName}\")";
            }
            if (IsClassName(obj, collectedClasses, localScope) || obj.StartsWith(className))
            {
                if (HasStaticPropertyOrField(obj, rawPropName, collectedClasses))
                {
                    var sanitizedProp = mem.Property is Identifier propId3 ? SanitizeIdentifier(propId3.Name, className) : rawPropName;
                    return $"{obj}.{sanitizedProp}";
                }
                return $"JavaScriptRuntimeEngine.GetProperty(((dynamic)JavaScriptRuntimeEngine.CurrentEngine.Jint.GetValue(\"{obj}\")), \"{rawPropName}\")";
            }
            return $"JavaScriptRuntimeEngine.GetProperty({obj}, \"{rawPropName}\")";
        }
    }

    private static string FormatUpdateExpression(UpdateExpression upd, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        bool isLValue = false;
        if (upd.Argument is Identifier id)
        {
            if (localScope.Contains(id.Name) || promotedFields.Contains(id.Name))
            {
                isLValue = true;
            }
        }
        else if (upd.Argument is MemberExpression mem)
        {
            var obj = mem.Object switch
            {
                Identifier idObj => IsClassName(idObj.Name, collectedClasses, localScope) ? SanitizeIdentifier(idObj.Name, className) : ResolveIdentifier(idObj.Name, localScope, promotedFields, className),
                MemberExpression nested => FormatMemberExpression(nested, collectedClasses, localScope, promotedFields, className),
                ThisExpression => (_nestedClassDepth > 0 ? "this" : "self"),
                _ => TranspileExpression(mem.Object, collectedClasses, localScope, promotedFields, className)
            };
            var rawPropName = mem.Property is Identifier propId2 ? propId2.Name : TranspileExpression(mem.Property, collectedClasses, localScope, promotedFields, className);
            if (obj == "this" || obj == "self")
            {
                isLValue = HasInstancePropertyOrField(className, rawPropName, collectedClasses);
            }
            else if (IsClassName(obj, collectedClasses, localScope) || obj.StartsWith(className))
            {
                isLValue = true;
            }
        }

        if (!isLValue)
        {
            if (upd.Argument is Identifier idGlobal)
            {
                var op = upd.Operator == Operator.Increment ? "Increment" : "Decrement";
                var prefix = upd.Prefix ? "Pre" : "Post";
                return $"JavaScriptRuntime.{prefix}{op}Global(\"{idGlobal.Name}\")";
            }
            else if (upd.Argument is MemberExpression memExpr)
            {
                var obj = memExpr.Object switch
                {
                    Identifier idObj => IsClassName(idObj.Name, collectedClasses, localScope) ? SanitizeIdentifier(idObj.Name, className) : ResolveIdentifier(idObj.Name, localScope, promotedFields, className),
                    MemberExpression nested => FormatMemberExpression(nested, collectedClasses, localScope, promotedFields, className),
                    ThisExpression => (_nestedClassDepth > 0 ? "this" : "self"),
                    _ => TranspileExpression(memExpr.Object, collectedClasses, localScope, promotedFields, className)
                };
                var rawPropName = memExpr.Property is Identifier propId2 ? propId2.Name : TranspileExpression(memExpr.Property, collectedClasses, localScope, promotedFields, className);
                var op = upd.Operator == Operator.Increment ? "Increment" : "Decrement";
                var prefix = upd.Prefix ? "Pre" : "Post";
                var propExpr = memExpr.Computed ? TranspileExpression(memExpr.Property, collectedClasses, localScope, promotedFields, className) : $"\"{rawPropName}\"";
                return $"JavaScriptRuntime.{prefix}{op}Property({obj}, {propExpr})";
            }
        }

        return upd.Prefix ? $"{MapUpdateOperator(upd.Operator)}{TranspileExpression(upd.Argument, collectedClasses, localScope, promotedFields, className)}" : $"{TranspileExpression(upd.Argument, collectedClasses, localScope, promotedFields, className)}{MapUpdateOperator(upd.Operator)}";
    }

    private static string FormatObjectExpression(ObjectExpression obj, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        return $"JavaScriptRuntime.CreateObject(" + string.Join(", ", obj.Properties.Select(p =>
        {
            if (p is Property prop)
            {
                var key = prop.Key is Identifier id ? $"\"{id.Name}\"" : (prop.Key is Literal litKey ? $"\"{litKey.Value}\"" : TranspileExpression(prop.Key, collectedClasses, localScope, promotedFields, className));
                var valStr = TranspileExpression((Expression?)prop.Value, collectedClasses, localScope, promotedFields, className);
                return $"({key}, {valStr})";
            }
            return "null";
        })) + ")";
    }

    private static string FormatArrowFunction(ArrowFunctionExpression arrow, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        var parametersList = arrow.Params.Select(p => FormatParameterName(p, collectedClasses, localScope, className)).ToList();
        var innerScope = new HashSet<string>(localScope, StringComparer.Ordinal);
        foreach (var p in parametersList) innerScope.Add(p);
        
        string parameters;
        if (arrow.Body is FunctionBody bodyBlock && ClassFinder.ContainsClass(bodyBlock))
        {
            parameters = string.Join(", ", parametersList.Select(p => SanitizeIdentifier(p, className) + "Param"));
        }
        else
        {
            parameters = string.Join(", ", parametersList.Select(p => SanitizeIdentifier(p, className)));
        }

        var body = arrow.Body switch
        {
            Expression expr => TranspileExpression(expr, collectedClasses, innerScope, promotedFields, className),
            FunctionBody block => TranspileFunctionBody(block, 0, collectedClasses, innerScope, arrow.Params, parametersList, promotedFields, className, isArrow: true),
            _ => "null"
        };
        var delType = GetFuncDelegateType(arrow.Params.Count);
        return $"(new {delType}(({parameters}) => {body}))";
    }

    private static string FormatFunctionExpression(FunctionExpression func, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        var parametersList = func.Params.Select(p => FormatParameterName(p, collectedClasses, localScope, className)).ToList();
        var innerScope = new HashSet<string>(localScope, StringComparer.Ordinal);
        foreach (var p in parametersList) innerScope.Add(p);
        
        string parameters;
        if (ClassFinder.ContainsClass(func.Body))
        {
            parameters = string.Join(", ", parametersList.Select(p => SanitizeIdentifier(p, className) + "Param"));
        }
        else
        {
            parameters = string.Join(", ", parametersList.Select(p => SanitizeIdentifier(p, className)));
        }

        var body = TranspileFunctionBody(func.Body, 0, collectedClasses, innerScope, func.Params, parametersList, promotedFields, className);
        var delType = GetFuncDelegateType(func.Params.Count);
        return $"(new {delType}(({parameters}) => {body}))";
    }

    private static string FormatParameterName(Node param, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, string className)
    {
        return param switch
        {
            Identifier id => id.Name,
            ArrayPattern => $"__param_{param.Range.Start}",
            ObjectPattern => $"__param_{param.Range.Start}",
            AssignmentPattern assign => assign.Left is Identifier idLeft ? idLeft.Name : (assign.Left is ArrayPattern || assign.Left is ObjectPattern ? $"__param_{assign.Left.Range.Start}" : TranspileExpression((Expression?)assign.Left, collectedClasses, localScope, new HashSet<string>(), className)),
            RestElement rest => rest.Argument is Identifier idRest ? idRest.Name : (rest.Argument is ArrayPattern || rest.Argument is ObjectPattern ? $"__param_{rest.Argument.Range.Start}" : TranspileExpression((Expression?)rest.Argument, collectedClasses, localScope, new HashSet<string>(), className)),
            _ => TranspileExpression((Expression?)param, collectedClasses, localScope, new HashSet<string>(), className)
        };
    }

    private static string FormatLiteral(Literal lit)
    {
        if (lit is NullLiteral) return "null";
        if (lit is BooleanLiteral b) return b.Value ? "true" : "false";
        if (lit is StringLiteral s) return "@\"" + s.Value.Replace("\"", "\"\"") + "\"";
        if (lit is NumericLiteral n) return n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (lit is RegExpLiteral r) return $"new System.Text.RegularExpressions.Regex(@\"{r.RegExp.Pattern.Replace("\"", "\"\"")}\")";
        return lit.Value?.ToString() ?? "null";
    }

    private static string FormatTemplateLiteral(TemplateLiteral t, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        var builder = new StringBuilder();
        builder.Append("$@\"");
        for (int i = 0; i < t.Quasis.Count; i++)
        {
            var quasi = t.Quasis[i];
            var val = quasi.Value.Cooked ?? quasi.Value.Raw;
            var escaped = val
                .Replace("\"", "\"\"")
                .Replace("{", "{{")
                .Replace("}", "}}");
            builder.Append(escaped);
            if (i < t.Expressions.Count)
            {
                builder.Append("{").Append(TranspileExpression(t.Expressions[i], collectedClasses, localScope, promotedFields, className)).Append("}");
            }
        }
        builder.Append("\"");
        return builder.ToString();
    }

    private static string MapOperator(Operator op) => op switch
    {
        Operator.StrictEquality or Operator.Equality => "==",
        Operator.StrictInequality or Operator.Inequality => "!=",
        Operator.Addition => "+",
        Operator.Subtraction => "-",
        Operator.Multiplication => "*",
        Operator.Division => "/",
        Operator.Remainder => "%",
        Operator.LessThan => "<",
        Operator.LessThanOrEqual => "<=",
        Operator.GreaterThan => ">",
        Operator.GreaterThanOrEqual => ">=",
        Operator.LogicalAnd => "&&",
        Operator.LogicalOr => "||",
        Operator.Assignment => "=",
        Operator.AdditionAssignment => "+=",
        Operator.SubtractionAssignment => "-=",
        Operator.MultiplicationAssignment => "*=",
        Operator.DivisionAssignment => "/=",
        Operator.RemainderAssignment => "%=",
        Operator.BitwiseAnd => "&",
        Operator.BitwiseOr => "|",
        Operator.BitwiseXor => "^",
        Operator.LeftShift => "<<",
        Operator.RightShift => ">>",
        Operator.UnsignedRightShift => ">>>",
        Operator.BitwiseAndAssignment => "&=",
        Operator.BitwiseOrAssignment => "|=",
        Operator.BitwiseXorAssignment => "^=",
        Operator.LeftShiftAssignment => "<<=",
        Operator.RightShiftAssignment => ">>=",
        Operator.UnsignedRightShiftAssignment => ">>>=",
        _ => op.ToString()
    };

    private static string MapUnaryOperator(Operator op) => op switch
    {
        Operator.UnaryPlus => "+",
        Operator.UnaryNegation => "-",
        Operator.LogicalNot => "!",
        Operator.BitwiseNot => "~",
        _ => op.ToString()
    };

    private static string MapUpdateOperator(Operator op) => op switch
    {
        Operator.Increment => "++",
        Operator.Decrement => "--",
        _ => op.ToString()
    };

    private static string? ExtractReturnedExpression(FunctionBody? body, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        if (body == null) return null;
        foreach (var stmt in body.Body)
        {
            if (stmt is ReturnStatement ret)
            {
                return ret.Argument != null ? TranspileExpression(ret.Argument, collectedClasses, localScope, promotedFields, className) : "null";
            }
        }
        return null;
    }

    private static HashSet<string> FindThisProperties(ClassDeclaration classDecl)
    {
        var visitor = new ThisPropertyVisitor();
        visitor.Visit(classDecl);
        return visitor.Properties;
    }

    private static HashSet<string> FindThisStaticProperties(ClassDeclaration classDecl)
    {
        var visitor = new ThisPropertyVisitor();
        visitor.Visit(classDecl);
        return visitor.StaticProperties;
    }

    private static bool HasInstancePropertyOrField(string className, string propName, List<ClassDeclaration> collectedClasses)
    {
        var current = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == className);
        while (current != null)
        {
            var properties = FindThisProperties(current);
            if (properties.Contains(propName)) return true;

            var method = current.Body.Body
                .OfType<MethodDefinition>()
                .Any(m => !m.Static && m.Key is Identifier mId && mId.Name == propName);
            if (method) return true;

            if (current.SuperClass != null && current.SuperClass is Identifier superId)
            {
                current = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == superId.Name);
            }
            else
            {
                break;
            }
        }
        return false;
    }

    private static bool HasStaticPropertyOrField(string className, string propName, List<ClassDeclaration> collectedClasses)
    {
        var current = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == className);
        while (current != null)
        {
            var staticProps = FindThisStaticProperties(current);
            if (staticProps.Contains(propName)) return true;

            var declaredStaticProps = _currentStaticProperties != null && _currentStaticProperties.TryGetValue(current.Id?.Name ?? "", out var sp) ? sp : new HashSet<string>();
            if (declaredStaticProps.Contains(propName)) return true;

            var method = current.Body.Body
                .OfType<MethodDefinition>()
                .Any(m => m.Static && m.Key is Identifier mId && mId.Name == propName);
            if (method) return true;

            if (current.SuperClass != null && current.SuperClass is Identifier superId)
            {
                current = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == superId.Name);
            }
            else
            {
                break;
            }
        }
        return false;
    }

    private static string SanitizeIdentifier(string? value, string className)
    {
        if (value == null) return string.Empty;
        if (value == "Math") return $"JavaScriptRuntime._math";
        if (value == "Object") return "JsObject";
        var sanitized = value.Replace("$", "_dollar_", StringComparison.Ordinal);
        if (string.Equals(sanitized, className, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsClassName(value, _currentCollectedClasses ?? new List<ClassDeclaration>(), new HashSet<string>()))
            {
                sanitized = sanitized + "_var";
            }
        }
        if (CSharpKeywords.Contains(sanitized))
        {
            return "@" + sanitized;
        }
        return sanitized;
    }

    private static string ResolveIdentifier(string? value, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        if (value == null) return string.Empty;
        if (IsClassName(value, _currentCollectedClasses ?? new List<ClassDeclaration>(), localScope))
        {
            return $"((dynamic)JavaScriptRuntimeEngine.CurrentEngine.Jint.GetValue(\"{value}\"))";
        }
        if (value == "arguments" && ArgumentsNames.Count > 0) return ArgumentsNames.Peek();
        if (value == "undefined") return "null";
        if (value == "Math") return $"JavaScriptRuntime._math";
        if (value == "Array") return "_Array";
        if (value == "Boolean") return "_Boolean";
        if (value == "String") return "_String";
        if (value == "Object") return "_Object";
        if (value == "Number") return "_Number";
        if (value == "Function") return "_Function";
        if (localScope.Contains(value))
        {
            return SanitizeIdentifier(value, className);
        }
        if (TopLevelFunctions.Contains(value))
        {
            return $"this.{SanitizeIdentifier(value, className)}";
        }
        if (promotedFields.Contains(value) && !localScope.Contains(value))
        {
            return $"{className}.{SanitizeIdentifier(value, className)}";
        }
        if (TemplateGlobals.Contains(value))
        {
            return value;
        }
        return $"((dynamic)JavaScriptRuntimeEngine.CurrentEngine.Jint.GetValue(\"{value}\"))";
    }

    private static void ScanDeclaredVariables(Node node, HashSet<string> vars)
    {
        if (node is FunctionDeclaration) return;

        if (node is Program prog)
        {
            foreach (var stmt in prog.Body) ScanDeclaredVariables(stmt, vars);
        }
        else if (node is VariableDeclaration varDecl)
        {
            foreach (var decl in varDecl.Declarations)
            {
                if (decl.Id is Identifier id)
                {
                    vars.Add(id.Name);
                }
                else
                {
                    var ids = new List<string>();
                    CollectIdentifiers(decl.Id, ids);
                    foreach (var varId in ids)
                    {
                        vars.Add(varId);
                    }
                }
            }
        }
        else if (node is BlockStatement block)
        {
            foreach (var stmt in block.Body) ScanDeclaredVariables(stmt, vars);
        }
        else if (node is FunctionBody funcBody)
        {
            foreach (var stmt in funcBody.Body) ScanDeclaredVariables(stmt, vars);
        }
        else if (node is IfStatement ifStmt)
        {
            ScanDeclaredVariables(ifStmt.Consequent, vars);
            if (ifStmt.Alternate != null) ScanDeclaredVariables(ifStmt.Alternate, vars);
        }
        else if (node is WhileStatement whileStmt)
        {
            ScanDeclaredVariables(whileStmt.Body, vars);
        }
        else if (node is DoWhileStatement doStmt)
        {
            ScanDeclaredVariables(doStmt.Body, vars);
        }
        else if (node is ForStatement forStmt)
        {
            if (forStmt.Init != null) ScanDeclaredVariables(forStmt.Init, vars);
            ScanDeclaredVariables(forStmt.Body, vars);
        }
        else if (node is ForInStatement forIn)
        {
            ScanDeclaredVariables(forIn.Left, vars);
            ScanDeclaredVariables(forIn.Body, vars);
        }
        else if (node is TryStatement tryStmt)
        {
            ScanDeclaredVariables(tryStmt.Block, vars);
            if (tryStmt.Handler != null) ScanDeclaredVariables(tryStmt.Handler.Body, vars);
            if (tryStmt.Finalizer != null) ScanDeclaredVariables(tryStmt.Finalizer, vars);
        }
        else if (node is SwitchStatement sw)
        {
            foreach (var c in sw.Cases)
            {
                foreach (var s in c.Consequent) ScanDeclaredVariables(s, vars);
            }
        }
    }

    private static bool IsClassName(string name, List<ClassDeclaration> classes, HashSet<string> localScope)
    {
        return classes.Any(c => c.Id != null && c.Id.Name == name);
    }

    private static string TranspileDestructuring(Node pattern, string rightHandSideVar, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        var assignments = new List<string>();
        TranspileDestructuringRecursiveInternal(pattern, rightHandSideVar, assignments, 0, collectedClasses, localScope, promotedFields, className);
        return string.Join(" ", assignments);
    }

    private static void TranspileDestructuringRecursiveInternal(Node pattern, string sourceExpr, List<string> assignments, int index, List<ClassDeclaration> collectedClasses, HashSet<string> localScope, HashSet<string> promotedFields, string className)
    {
        if (pattern is Identifier id)
        {
            if (localScope.Contains(id.Name) || promotedFields.Contains(id.Name))
            {
                assignments.Add($"{ResolveIdentifier(id.Name, localScope, promotedFields, className)} = {sourceExpr};");
            }
            else
            {
                assignments.Add($"JavaScriptRuntimeEngine.CurrentEngine.Jint.SetValue(\"{id.Name}\", {sourceExpr});");
            }
        }
        else if (pattern is ArrayPattern arrPat)
        {
            for (int i = 0; i < arrPat.Elements.Count; i++)
            {
                var elem = arrPat.Elements[i];
                if (elem == null) continue;
                if (elem is RestElement rest)
                {
                    if (rest.Argument is Identifier idRest)
                    {
                        if (localScope.Contains(idRest.Name) || promotedFields.Contains(idRest.Name))
                        {
                            assignments.Add($"{ResolveIdentifier(idRest.Name, localScope, promotedFields, className)} = JavaScriptRuntime.SliceList({sourceExpr}, {i});");
                        }
                        else
                        {
                            assignments.Add($"JavaScriptRuntimeEngine.CurrentEngine.Jint.SetValue(\"{idRest.Name}\", JavaScriptRuntime.SliceList({sourceExpr}, {i}));");
                        }
                    }
                }
                else if (elem is AssignmentPattern assignPat)
                {
                    if (assignPat.Left is Identifier idLeft)
                    {
                        var defaultVal = TranspileExpression(assignPat.Right, collectedClasses, localScope, promotedFields, className);
                        if (localScope.Contains(idLeft.Name) || promotedFields.Contains(idLeft.Name))
                        {
                            assignments.Add($"{ResolveIdentifier(idLeft.Name, localScope, promotedFields, className)} = JavaScriptRuntime.GetComputedProperty({sourceExpr}, {i}.0) ?? {defaultVal};");
                        }
                        else
                        {
                            assignments.Add($"JavaScriptRuntimeEngine.CurrentEngine.Jint.SetValue(\"{idLeft.Name}\", JavaScriptRuntime.GetComputedProperty({sourceExpr}, {i}.0) ?? {defaultVal});");
                        }
                    }
                }
                else
                {
                    var tempVar = $"_temp_{Guid.NewGuid().ToString("N")}";
                    assignments.Add($"dynamic? {tempVar} = JavaScriptRuntime.GetComputedProperty({sourceExpr}, {i}.0);");
                    TranspileDestructuringRecursiveInternal(elem, tempVar, assignments, 0, collectedClasses, localScope, promotedFields, className);
                }
            }
        }
        else if (pattern is ObjectPattern objPat)
        {
            foreach (var prop in objPat.Properties)
            {
                if (prop is Property p)
                {
                    var key = p.Key is Identifier idKey ? idKey.Name : p.Key.ToString();
                    var valueNode = p.Value;
                    var tempVar = $"_temp_{Guid.NewGuid().ToString("N")}";
                    assignments.Add($"dynamic? {tempVar} = JavaScriptRuntime.GetComputedProperty({sourceExpr}, \"{key}\");");
                    TranspileDestructuringRecursiveInternal(valueNode, tempVar, assignments, 0, collectedClasses, localScope, promotedFields, className);
                }
            }
        }
        else if (pattern is AssignmentPattern assignPat)
        {
            var leftName = assignPat.Left is Identifier idLeft ? ResolveIdentifier(idLeft.Name, localScope, promotedFields, className) : "null";
            var defaultVal = TranspileExpression(assignPat.Right, collectedClasses, localScope, promotedFields, className);
            assignments.Add($"{leftName} = {sourceExpr} ?? {defaultVal};");
        }
    }

    private static void CollectIdentifiers(Node pattern, List<string> ids)
    {
        if (pattern is Identifier id)
        {
            ids.Add(id.Name);
        }
        else if (pattern is ArrayPattern arrPat)
        {
            foreach (var elem in arrPat.Elements)
            {
                if (elem != null) CollectIdentifiers(elem, ids);
            }
        }
        else if (pattern is ObjectPattern objPat)
        {
            foreach (var prop in objPat.Properties)
            {
                if (prop is Property p)
                {
                    CollectIdentifiers(p.Value, ids);
                }
            }
        }
        else if (pattern is AssignmentPattern assignPat)
        {
            CollectIdentifiers(assignPat.Left, ids);
        }
        else if (pattern is RestElement rest)
        {
            CollectIdentifiers(rest.Argument, ids);
        }
    }

    private static string GetFuncDelegateType(int paramCount)
    {
        if (paramCount == 0) return "Func<dynamic?>";
        return $"Func<{string.Join(", ", Enumerable.Repeat("dynamic?", paramCount + 1))}>";
    }

    private sealed class ClassPropertyVisitor : AstVisitor
    {
        public Dictionary<string, HashSet<string>> PrototypeProperties { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> StaticProperties { get; } = new(StringComparer.Ordinal);

        protected override object? VisitCallExpression(CallExpression node)
        {
            if (node.Callee is MemberExpression mem && mem.Object is Identifier idObj && idObj.Name == "Object" && !mem.Computed && mem.Property is Identifier idProp && idProp.Name == "defineProperty" && node.Arguments.Count >= 2)
            {
                var target = node.Arguments[0];
                var secondArg = node.Arguments[1];
                if (secondArg is Literal lit && lit.Value is string propName)
                {
                    if (target is MemberExpression protoMem && 
                        !protoMem.Computed && 
                        protoMem.Property is Identifier idProto && 
                        idProto.Name == "prototype" &&
                        protoMem.Object is Identifier idClass1)
                    {
                        var className = idClass1.Name;
                        if (!PrototypeProperties.TryGetValue(className, out var props))
                        {
                            props = new HashSet<string>(StringComparer.Ordinal);
                            PrototypeProperties[className] = props;
                        }
                        props.Add(propName);
                    }
                    else if (target is Identifier idClass2)
                    {
                        var className = idClass2.Name;
                        if (!StaticProperties.TryGetValue(className, out var props))
                        {
                            props = new HashSet<string>(StringComparer.Ordinal);
                            StaticProperties[className] = props;
                        }
                        props.Add(propName);
                    }
                }
            }
            return base.VisitCallExpression(node);
        }

        protected override object? VisitAssignmentExpression(AssignmentExpression node)
        {
            if (node.Left is MemberExpression mem && !mem.Computed && mem.Property is Identifier idProp)
            {
                if (mem.Object is MemberExpression protoMem && 
                    !protoMem.Computed && 
                    protoMem.Property is Identifier idProto && 
                    idProto.Name == "prototype" &&
                    protoMem.Object is Identifier idClass1)
                {
                    var className = idClass1.Name;
                    var propName = idProp.Name;
                    if (!PrototypeProperties.TryGetValue(className, out var props))
                    {
                        props = new HashSet<string>(StringComparer.Ordinal);
                        PrototypeProperties[className] = props;
                    }
                    props.Add(propName);
                }
                else if (mem.Object is Identifier idClass2)
                {
                    var className = idClass2.Name;
                    var propName = idProp.Name;
                    if (propName != "prototype")
                    {
                        if (!StaticProperties.TryGetValue(className, out var props))
                        {
                            props = new HashSet<string>(StringComparer.Ordinal);
                            StaticProperties[className] = props;
                        }
                        props.Add(propName);
                    }
                }
            }
            return base.VisitAssignmentExpression(node);
        }
    }

    private sealed class ThisPropertyVisitor : AstVisitor
    {
        public HashSet<string> Properties { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StaticProperties { get; } = new(StringComparer.Ordinal);

        private bool _isStatic;

        protected override object? VisitMethodDefinition(MethodDefinition node)
        {
            var oldStatic = _isStatic;
            _isStatic = node.Static;
            try
            {
                return base.VisitMethodDefinition(node);
            }
            finally
            {
                _isStatic = oldStatic;
            }
        }

        protected override object? VisitPropertyDefinition(PropertyDefinition node)
        {
            var oldStatic = _isStatic;
            _isStatic = node.Static;
            try
            {
                return base.VisitPropertyDefinition(node);
            }
            finally
            {
                _isStatic = oldStatic;
            }
        }

        protected override object? VisitAssignmentExpression(AssignmentExpression node)
        {
            if (node.Left is MemberExpression mem && mem.Object is ThisExpression && mem.Property is Identifier id)
            {
                if (_isStatic)
                {
                    StaticProperties.Add(id.Name);
                }
                else
                {
                    Properties.Add(id.Name);
                }
            }
            return base.VisitAssignmentExpression(node);
        }

        protected override object? VisitCallExpression(CallExpression node)
        {
            if (node.Callee is MemberExpression mem && mem.Object is Identifier idObj && idObj.Name == "Object" && !mem.Computed && mem.Property is Identifier idProp)
            {
                if (idProp.Name == "defineProperty" && node.Arguments.Count >= 2 && node.Arguments[0] is ThisExpression)
                {
                    var secondArg = node.Arguments[1];
                    if (secondArg is Literal lit && lit.Value is string propStr)
                    {
                        if (_isStatic)
                        {
                            StaticProperties.Add(propStr);
                        }
                        else
                        {
                            Properties.Add(propStr);
                        }
                    }
                }
                else if (idProp.Name == "defineProperties" && node.Arguments.Count >= 2 && node.Arguments[0] is ThisExpression)
                {
                    var secondArg = node.Arguments[1];
                    if (secondArg is ObjectExpression objExpr)
                    {
                        foreach (var prop in objExpr.Properties)
                        {
                            if (prop is Property p && !p.Computed && p.Key is Identifier idKey)
                            {
                                if (_isStatic) StaticProperties.Add(idKey.Name);
                                else Properties.Add(idKey.Name);
                            }
                            else if (prop is Property p2 && !p2.Computed && p2.Key is Literal litKey && litKey.Value is string propStr)
                            {
                                if (_isStatic) StaticProperties.Add(propStr);
                                else Properties.Add(propStr);
                            }
                        }
                    }
                }
            }
            return base.VisitCallExpression(node);
        }
    }

    private static bool IsValidCSharpStatementExpression(Expression expr)
    {
        if (expr is CallExpression call && call.Callee is Super)
        {
            return false;
        }
        return expr switch
        {
            CallExpression => true,
            AssignmentExpression => true,
            UpdateExpression => true,
            NewExpression => true,
            _ => false
        };
    }

    private static CallExpression? FindSuperCall(Node node)
    {
        if (node is ExpressionStatement exprStmt && exprStmt.Expression is CallExpression call && call.Callee is Super)
        {
            return call;
        }
        if (node is BlockStatement block)
        {
            foreach (var stmt in block.Body)
            {
                var found = FindSuperCall(stmt);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static bool HasMethodWithParamCount(string className, string methodName, int paramCount, List<ClassDeclaration> collectedClasses)
    {
        var current = className;
        while (current != null)
        {
            var cls = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == current);
            if (cls == null) break;
            
            var method = cls.Body.Body
                .OfType<MethodDefinition>()
                .FirstOrDefault(m => m.Key is Identifier id && id.Name == methodName && m.Kind != PropertyKind.Get && m.Kind != PropertyKind.Set);
            if (method != null)
            {
                if (method.Value != null && paramCount <= method.Value.Params.Count)
                {
                    return true;
                }
                return false;
            }
            
            if (cls.SuperClass is Identifier superId && IsClassName(superId.Name, collectedClasses, new HashSet<string>()))
            {
                current = superId.Name;
            }
            else
            {
                break;
            }
        }
        return false;
    }

    private static bool HasStaticAndInstanceCollision(string className, string methodName, List<ClassDeclaration> collectedClasses)
    {
        var cls = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == className);
        if (cls != null)
        {
            var hasStatic = cls.Body.Body.OfType<MethodDefinition>().Any(m => m.Static && m.Key is Identifier idKey && idKey.Name == methodName && m.Kind != PropertyKind.Get && m.Kind != PropertyKind.Set && m.Kind != PropertyKind.Constructor);
            var hasInstance = cls.Body.Body.OfType<MethodDefinition>().Any(m => !m.Static && m.Key is Identifier idKey && idKey.Name == methodName && m.Kind != PropertyKind.Get && m.Kind != PropertyKind.Set && m.Kind != PropertyKind.Constructor);
            return hasStatic && hasInstance;
        }
        return false;
    }

    private static bool HasStaticSuperClass(string className, List<ClassDeclaration> collectedClasses)
    {
        var cls = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == className);
        if (cls != null && cls.SuperClass is Identifier superId && IsClassName(superId.Name, collectedClasses, new HashSet<string>()))
        {
            return true;
        }
        return false;
    }

    private static string GenerateShadowedMethodsInitialization(string className, List<ClassDeclaration> collectedClasses, string pad)
    {
        var sb = new StringBuilder();
        var cls = collectedClasses.FirstOrDefault(c => c.Id != null && c.Id.Name == className);
        if (cls != null)
        {
            var properties = FindThisProperties(cls);
            if (_currentPrototypeProperties != null && _currentPrototypeProperties.TryGetValue(className, out var protoProps))
            {
                foreach (var prop in protoProps)
                {
                    properties.Add(prop);
                }
            }
            
            var shadowedMethods = cls.Body.Body
                .OfType<MethodDefinition>()
                .Where(m => m.Kind != PropertyKind.Constructor && m.Kind != PropertyKind.Get && m.Kind != PropertyKind.Set)
                .Select(m => m.Key is Identifier idKey ? idKey.Name : null)
                .Where(n => n != null && properties.Contains(n))
                .ToList();
            foreach (var sm in shadowedMethods)
            {
                var methodDef = cls.Body.Body
                    .OfType<MethodDefinition>()
                    .First(m => m.Key is Identifier idKey && idKey.Name == sm);
                var paramCount = methodDef.Value?.Params.Count ?? 0;
                var types = string.Join(", ", Enumerable.Repeat("dynamic?", paramCount + 1));
                var lambdaParams = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"x{i}"));
                var lambdaArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"x{i}"));
                sb.Append(pad).AppendLine($"    {SanitizeIdentifier(sm, className)} = new Func<{types}>({(paramCount > 0 ? $"({lambdaParams}) => " : "() => ")}{SanitizeIdentifier(sm + "_method", className)}({lambdaArgs}));");
            }
        }
        return sb.ToString();
    }
}
