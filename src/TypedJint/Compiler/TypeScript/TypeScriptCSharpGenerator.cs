using System;
using System.Collections.Generic;
using System.Text;

namespace TypedJint;

public static class TypeScriptCSharpGenerator
{
    public static string Generate(TypeScriptTypeRegistry registry)
    {
        if (registry == null) return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("// ===========================================================================");
        builder.AppendLine("// Compiled TypeScript Library Definitions to C# Facades / Interfaces");
        builder.AppendLine("// ===========================================================================");
        builder.AppendLine();

        // 1. Global variables mapping
        if (registry.Globals.Count > 0)
        {
            builder.AppendLine("// Global Variables Mapping:");
            foreach (var global in registry.Globals)
            {
                builder.AppendLine($"// globalThis.{global.Key} => {global.Value}");
            }
            builder.AppendLine();
        }

        // 2. Types
        foreach (var typeName in registry.Types.Keys)
        {
            var typeInfo = registry.Types[typeName];
            builder.AppendLine($"public interface {typeName}");
            builder.AppendLine("{");

            // Properties
            foreach (var prop in typeInfo.Properties)
            {
                var csharpType = MapTypeToCSharp(prop.Value);
                builder.AppendLine($"    {csharpType} {prop.Key} {{ get; set; }}");
            }

            if (typeInfo.Properties.Count > 0 && typeInfo.Methods.Count > 0)
            {
                builder.AppendLine();
            }

            // Methods
            foreach (var method in typeInfo.Methods.Values)
            {
                var returnType = MapTypeToCSharp(method.ReturnType);
                var parameters = new List<string>();
                for (int i = 0; i < method.ParameterTypes.Count; i++)
                {
                    parameters.Add($"{MapTypeToCSharp(method.ParameterTypes[i])} p{i}");
                }
                var parametersStr = string.Join(", ", parameters);
                builder.AppendLine($"    {returnType} {method.Name}({parametersStr});");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string MapTypeToCSharp(string type)
    {
        if (string.IsNullOrEmpty(type)) return "object?";

        var clean = type.Trim();

        // Handle array types
        if (clean.EndsWith("[]", StringComparison.Ordinal))
        {
            var element = clean.Substring(0, clean.Length - 2);
            return MapTypeToCSharp(element) + "[]";
        }

        return clean switch
        {
            "number" => "double",
            "boolean" => "bool",
            "string" => "string",
            "void" => "void",
            "any" => "object?",
            _ => clean
        };
    }
}
