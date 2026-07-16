namespace CurlHttp.Diagnostics;

internal static class LogRedaction
{
    private static readonly string[] SensitiveHeaderPrefixes =
    [
        "Authorization:",
        "Proxy-Authorization:",
        "Cookie:",
        "Set-Cookie:",
        "X-Api-Key:",
    ];

    /// <summary>Redacts the value of credential-bearing header lines in
    /// verbose native output. Bodies never reach this path (dropped in the
    /// native bridge).</summary>
    public static string RedactHeaderLine(string line)
    {
        foreach (string prefix in SensitiveHeaderPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(line.AsSpan(0, prefix.Length), " <redacted>");
            }
        }
        // Free-form libcurl TEXT lines (kind 0) are not header-prefixed but can
        // still name a credential, e.g. "Proxy auth using Basic with user
        // 'alice'". Scrub the quoted user name.
        return RedactQuotedUser(line);
    }

    private static string RedactQuotedUser(string line)
    {
        const string marker = "user '";
        int start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return line;
        }
        int valueStart = start + marker.Length;
        int end = line.IndexOf('\'', valueStart);
        if (end < 0)
        {
            return line;
        }
        return string.Concat(line.AsSpan(0, valueStart), "<redacted>", line.AsSpan(end));
    }

    /// <summary>Optionally strips query strings from URLs before logging
    /// (query values frequently carry tokens).</summary>
    public static string RedactUrl(Uri uri, bool redactQuery)
    {
        if (!redactQuery || string.IsNullOrEmpty(uri.Query))
        {
            return uri.AbsoluteUri;
        }
        return uri.GetLeftPart(UriPartial.Path) + "?<redacted>";
    }

    public static string RedactUrl(string url, bool redactQuery)
    {
        if (!redactQuery)
        {
            return url;
        }
        int query = url.IndexOf('?');
        return query < 0 ? url : url[..query] + "?<redacted>";
    }
}
