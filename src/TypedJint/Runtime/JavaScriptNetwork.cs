using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace TypedJint.Runtime;

public sealed class JavaScriptResponse
{
    private readonly HttpResponseMessage _response;
    public JavaScriptResponse(HttpResponseMessage response) => _response = response;
    public bool ok => _response.IsSuccessStatusCode;
    public double status => (double)_response.StatusCode;
    public string text() => _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    public object? json() => JavaScriptJson.Instance.parse(text());
}

public sealed class JavaScriptNetwork
{
    public static readonly JavaScriptNetwork Instance = new();
    private static readonly HttpClient Http = new();

    private JavaScriptNetwork()
    {
    }

    public string getString(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUri(address);
        }

        return Http.GetStringAsync(address).GetAwaiter().GetResult();
    }

    public byte[] getBytes(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetBytes(DecodeDataUri(address));
        }

        return Http.GetByteArrayAsync(address).GetAwaiter().GetResult();
    }

    public string postString(string address, string content) => postString(address, content, "text/plain");

    public string postString(string address, string content, string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        using var httpContent = new StringContent(content, Encoding.UTF8, mediaType);
        using var response = Http.PostAsync(address, httpContent).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static string DecodeDataUri(string address)
    {
        var comma = address.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            throw new FormatException("Invalid data URI.");
        }

        var metadata = address[5..comma];
        var data = address[(comma + 1)..];
        return metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(data))
            : WebUtility.UrlDecode(data);
    }
}
