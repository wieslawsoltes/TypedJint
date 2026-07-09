using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TypedJint.Runtime;

public sealed class JavaScriptJson
{
    public static readonly JavaScriptJson Instance = new();

    private JavaScriptJson()
    {
    }

    public string stringify(object? value) => JsonSerializer.Serialize(value);

    public object? parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return ToElement(doc.RootElement);
    }

    private static object? ToElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ToElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static System.Dynamic.ExpandoObject ToDictionary(JsonElement element)
    {
        var expando = new System.Dynamic.ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ToElement(prop.Value);
        }
        return expando;
    }
}
