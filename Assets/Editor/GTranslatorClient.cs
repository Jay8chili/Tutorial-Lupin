using System;
using System.Text;
using UnityEngine.Networking;

/// <summary>
/// Free Google Translate access with no API key, ported from the approach used
/// by GTranslatorAPI (franck-gaspoz) to run natively on UnityWebRequest.
///
/// WHY A PORT RATHER THAN THE DLL: the NuGet package pulls in
/// Microsoft.AspNetCore.Mvc.WebApiCompatShim, Newtonsoft.Json and
/// System.Configuration.ConfigurationManager. Getting an ASP.NET Core MVC shim
/// to coexist with Unity's editor assemblies is a fight with no payoff - the
/// part that matters is one GET against translate_a/single, which is below.
///
/// This is Google's INTERNAL endpoint, the same one the library uses. No key,
/// no quota, but also no guarantees: it rate-limits on volume and can change
/// without notice. Fine for generating first drafts in the editor. Editor-only
/// code - nothing here ships in a build.
/// </summary>
public static class GTranslatorClient
{
    private const string ENDPOINT = "https://translate.googleapis.com/translate_a/single";

    /// <summary>
    /// Builds the request. client=gtx is the free web client; dt=t asks for
    /// plain translated segments rather than the full analysis payload.
    /// </summary>
    public static UnityWebRequest CreateRequest(string text, string from, string to, int timeoutSeconds = 20)
    {
        string url = ENDPOINT
                   + "?client=gtx"
                   + $"&sl={UnityWebRequest.EscapeURL(from)}"
                   + $"&tl={UnityWebRequest.EscapeURL(to)}"
                   + "&dt=t"
                   + $"&q={UnityWebRequest.EscapeURL(text)}";

        UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = timeoutSeconds;

        // The endpoint returns an error page to clients it does not recognise.
        request.SetRequestHeader("User-Agent", "Mozilla/5.0");

        return request;
    }

    /// <summary>
    /// Extracts the translation from the endpoint's nested-array response:
    ///
    ///   [[["Hola","Hello",null,null,10],["mundo"," world",null,null,3]],null,"en"]
    ///
    /// This is not an object, so JsonUtility cannot touch it, and long inputs
    /// are split across MULTIPLE segments that must be concatenated in order -
    /// taking only the first would silently truncate the sentence.
    ///
    /// Parsed by bracket depth rather than regex: the strings can contain
    /// commas, brackets and escaped quotes, which a pattern match gets wrong.
    /// </summary>
    public static string ParseResponse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        StringBuilder result = new StringBuilder();
        int depth = 0;
        int i = 0;
        bool started = false;

        while (i < json.Length)
        {
            char c = json[i];

            if (c == '[') { depth++; i++; continue; }

            if (c == ']')
            {
                depth--;
                i++;

                // The segment block has closed. Everything after it is metadata
                // - and the trailing [["en"],null,[1.0],["en"]] sits at depth 3
                // too, so without this the language codes get appended to the
                // translation ("Hola" becomes "Holaenen").
                if (started && depth <= 1) break;

                continue;
            }

            if (c == '"')
            {
                // Depth 3 is a segment array; its first string is the
                // translated text, the second is the original.
                string literal = ReadString(json, ref i);

                if (depth == 3)
                {
                    if (literal != null) result.Append(literal);
                    started = true;

                    // Skip the rest of the segment so the source text, which
                    // sits at the same depth, is not appended as well.
                    SkipToSegmentEnd(json, ref i, ref depth);

                    if (depth <= 1) break;
                }

                continue;
            }

            i++;
        }

        return result.Length > 0 ? result.ToString() : null;
    }

    /// <summary>Reads a JSON string literal starting at the opening quote.</summary>
    private static string ReadString(string json, ref int index)
    {
        index++;   // Opening quote.

        StringBuilder sb = new StringBuilder();

        while (index < json.Length)
        {
            char c = json[index];

            if (c == '\\' && index + 1 < json.Length)
            {
                char next = json[index + 1];
                switch (next)
                {
                    case 'n':  sb.Append('\n'); break;
                    case 'r':  sb.Append('\r'); break;
                    case 't':  sb.Append('\t'); break;
                    case '"':  sb.Append('"');  break;
                    case '\\': sb.Append('\\'); break;
                    case '/':  sb.Append('/');  break;

                    case 'u':
                        // \uXXXX - accented and non-Latin characters arrive escaped.
                        if (index + 5 < json.Length &&
                            ushort.TryParse(json.Substring(index + 2, 4),
                                            System.Globalization.NumberStyles.HexNumber,
                                            System.Globalization.CultureInfo.InvariantCulture,
                                            out ushort code))
                        {
                            sb.Append((char)code);
                            index += 6;
                            continue;
                        }
                        break;

                    default: sb.Append(next); break;
                }

                index += 2;
                continue;
            }

            if (c == '"') { index++; return sb.ToString(); }

            sb.Append(c);
            index++;
        }

        return sb.ToString();
    }

    /// <summary>Advances past the current segment array, leaving depth correct.</summary>
    private static void SkipToSegmentEnd(string json, ref int index, ref int depth)
    {
        while (index < json.Length)
        {
            char c = json[index];

            if (c == '"') { ReadString(json, ref index); continue; }   // Skip strings wholesale.

            if (c == '[') depth++;

            if (c == ']')
            {
                depth--;
                index++;
                return;
            }

            index++;
        }
    }
}
