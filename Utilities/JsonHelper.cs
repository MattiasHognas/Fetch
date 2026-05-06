using System.Text.Json;

namespace Fetch.Utilities;

public static class JsonHelper
{
    public static bool TryParseObject(string text, out JsonDocument? doc, out string cleaned)
    {
        doc = null;
        cleaned = "";
        if (TryParse(text, out doc))
        {
            cleaned = text;
            return true;
        }
        return TryExtractJsonObject(text, out cleaned) && TryParse(cleaned, out doc);
    }

    private static bool TryParse(string text, out JsonDocument? doc)
    {
        doc = null;
        try
        {
            doc = JsonDocument.Parse(text);
            return true;
        }
        catch { return false; }
    }

    private static bool TryExtractJsonObject(string text, out string json)
    {
        json = "";
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        json = text[start..(end + 1)];
        return true;
    }
}
