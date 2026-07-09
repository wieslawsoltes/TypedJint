using System;
using System.Text;

namespace TypedJint.Runtime;

public sealed class JavaScriptEncoding
{
    public static readonly JavaScriptEncoding Instance = new();

    private JavaScriptEncoding()
    {
    }

    public string base64Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    public string base64Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    public string uriEncode(string value) => Uri.EscapeDataString(value);
    public string uriDecode(string value) => Uri.UnescapeDataString(value);
    public double utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);
}
