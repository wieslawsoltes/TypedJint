using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TypedJint;

public class TypeScriptTypeRegistry
{
    public Dictionary<string, string> Globals { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TypeScriptTypeInfo> Types { get; } = new(StringComparer.Ordinal);

    public void RegisterType(string name, TypeScriptTypeInfo typeInfo)
    {
        Types[name] = typeInfo;
    }

    public string? GetGlobalType(string name)
    {
        return Globals.TryGetValue(name, out var type) ? type : null;
    }

    public TypeScriptTypeInfo? GetTypeInfo(string name)
    {
        return Types.TryGetValue(name, out var typeInfo) ? typeInfo : null;
    }

    public void Merge(TypeScriptTypeRegistry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var kvp in other.Globals) Globals[kvp.Key] = kvp.Value;
        foreach (var kvp in other.Types) Types[kvp.Key] = kvp.Value;
    }
}

public class TypeScriptTypeInfo
{
    public string Name { get; }
    public Dictionary<string, TypeScriptMethodInfo> Methods { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

    public TypeScriptTypeInfo(string name) => Name = name;
}

public class TypeScriptMethodInfo
{
    public string Name { get; }
    public string ReturnType { get; }
    public List<string> ParameterTypes { get; } = new();

    public TypeScriptMethodInfo(string name, string returnType)
    {
        Name = name;
        ReturnType = returnType;
    }
}

public static class TypeScriptDefParser
{
    public static TypeScriptTypeRegistry Parse(string content)
    {
        var registry = new TypeScriptTypeRegistry();
        
        // Statically register known global variables to prevent modular/ESM export resolution issues
        registry.Globals["rough"] = "Rough";
        registry.Globals["THREE"] = "Three";
        registry.Globals["LightweightCharts"] = "LightweightCharts";
        registry.Globals["d3"] = "D3";
        registry.Globals["PIXI"] = "Pixi";

        if (string.IsNullOrWhiteSpace(content)) return registry;

        // 1. Remove single-line and multi-line comments
        content = Regex.Replace(content, @"/\*[\s\S]*?\*/|//.*", "");

        // 2. Scan interface declarations
        var interfaceRegex = new Regex(@"interface\s+(\w+)(?:\s+extends\s+[\w,\s]+)?\s*\{", RegexOptions.Compiled);
        var matches = interfaceRegex.Matches(content);
        foreach (Match match in matches)
        {
            var typeName = match.Groups[1].Value;
            var openBraceIndex = match.Index + match.Length - 1;
            
            var body = ExtractBraceBody(content, openBraceIndex);
            if (body != null)
            {
                var typeInfo = new TypeScriptTypeInfo(typeName);
                ParseInterfaceBody(body, typeInfo);
                registry.RegisterType(typeName, typeInfo);
            }
        }

        // 3. Scan class declarations
        var classRegex = new Regex(@"class\s+(\w+)(?:\s+extends\s+\w+)?\s*\{", RegexOptions.Compiled);
        matches = classRegex.Matches(content);
        foreach (Match match in matches)
        {
            var typeName = match.Groups[1].Value;
            var openBraceIndex = match.Index + match.Length - 1;
            var body = ExtractBraceBody(content, openBraceIndex);
            if (body != null)
            {
                var typeInfo = new TypeScriptTypeInfo(typeName);
                ParseInterfaceBody(body, typeInfo);
                registry.RegisterType(typeName, typeInfo);
            }
        }

        // 4. Scan global variables (like declare const document: Document)
        var globalVarRegex = new Regex(@"(?:declare\s+)?(?:var|const|let)\s+(\w+)\s*:\s*(\w+);", RegexOptions.Compiled);
        foreach (Match match in globalVarRegex.Matches(content))
        {
            var varName = match.Groups[1].Value;
            var varType = match.Groups[2].Value;
            registry.Globals[varName] = varType;
        }

        return registry;
    }

    private static string? ExtractBraceBody(string content, int openBraceIndex)
    {
        int braceCount = 0;
        for (int i = openBraceIndex; i < content.Length; i++)
        {
            if (content[i] == '{') braceCount++;
            else if (content[i] == '}')
            {
                braceCount--;
                if (braceCount == 0)
                {
                    return content.Substring(openBraceIndex + 1, i - openBraceIndex - 1);
                }
            }
        }
        return null;
    }

    private static void ParseInterfaceBody(string body, TypeScriptTypeInfo typeInfo)
    {
        // Parse methods: name(params): returnType;
        var methodRegex = new Regex(@"(\w+)\s*\(([^)]*)\)\s*:\s*([\w<>\[\]|]+);", RegexOptions.Compiled);
        foreach (Match match in methodRegex.Matches(body))
        {
            var methodName = match.Groups[1].Value;
            var paramsStr = match.Groups[2].Value;
            var returnType = match.Groups[3].Value.Trim();

            var methodInfo = new TypeScriptMethodInfo(methodName, returnType);
            if (!string.IsNullOrWhiteSpace(paramsStr))
            {
                var paramPairs = paramsStr.Split(',');
                foreach (var pair in paramPairs)
                {
                    var parts = pair.Split(':');
                    if (parts.Length == 2)
                    {
                        methodInfo.ParameterTypes.Add(parts[1].Trim());
                    }
                }
            }
            typeInfo.Methods[methodName] = methodInfo;
        }

        // Parse properties: name: type;
        var propRegex = new Regex(@"(\w+)\s*:\s*([\w<>\[\]|]+);", RegexOptions.Compiled);
        foreach (Match match in propRegex.Matches(body))
        {
            var propName = match.Groups[1].Value;
            var propType = match.Groups[2].Value.Trim();
            if (!typeInfo.Methods.ContainsKey(propName))
            {
                typeInfo.Properties[propName] = propType;
            }
        }
    }
}
