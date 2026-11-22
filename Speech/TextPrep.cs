using System.Text;
using System.Text.RegularExpressions;

namespace pxdSpeak.Speech;

/// <summary>Cleans copied text so it reads well aloud: no link soup, markdown marks or broken-up lines.</summary>
public static partial class TextPrep
{
    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Url().Replace(s, m => LinkWord(m.Value));
        s = MarkdownLink().Replace(s, "$1");                 // [words](link) → words
        s = Emphasis().Replace(s, "");                       // ** __ ~~ ` and leading # / > / bullets
        s = Bullet().Replace(s, "");
        // A line break inside a sentence (a PDF or e-mail wrapped it) is just a space; a blank line ends a paragraph,
        // and a line without closing punctuation (a heading, a list item) gets a pause after it.
        var paragraphs = Regex.Split(s, @"\n\s*\n");
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            var lines = p.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0)
                continue;
            var para = new StringBuilder();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                para.Append(line);
                var shortLine = line.Length < 60 && i + 1 < lines.Count && lines.Count > 1 && char.IsUpper(lines[i + 1][0]);
                if (shortLine && !EndsSentence(line))
                    para.Append('.');
                para.Append(' ');
            }

            var text2 = para.ToString().Trim();
            if (!EndsSentence(text2))
                text2 += ".";
            sb.Append(text2).Append(' ');
        }

        s = Spaces().Replace(sb.ToString(), " ").Trim();
        return s;
    }

    private static bool EndsSentence(string s) => s.Length > 0 && ".!?:;…\"'”’)".Contains(s[^1]);

    private static string LinkWord(string url)
    {
        try
        {
            var host = new Uri(url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url).Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host[4..];
            return "link to " + host;
        }
        catch
        {
            return "link";
        }
    }

    /// <summary>Splits text into pieces of at most about <paramref name="max"/> characters, at sentence ends where possible.</summary>
    public static IEnumerable<string> Pieces(string text, int max)
    {
        var sentences = Sentence().Split(text).Where(x => x.Trim().Length > 0);
        var sb = new StringBuilder();
        foreach (var raw in sentences)
        {
            var sentence = raw.Trim();
            if (sb.Length > 0 && sb.Length + sentence.Length + 1 > max)
            {
                yield return sb.ToString();
                sb.Clear();
            }

            while (sentence.Length > max)
            {
                var cut = sentence.LastIndexOf(' ', max);
                if (cut < max / 2)
                    cut = max;
                yield return sentence[..cut];
                sentence = sentence[cut..].Trim();
            }

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(sentence);
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>()\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\[([^\]]+)\]\((?:link to [^)]*|link)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(\*\*|__|~~|`+|^#{1,6}\s+|^>\s+)", RegexOptions.Multiline)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"^\s*(?:[-*•▪◦]|\d+[.)])\s+", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex Sentence();
}
