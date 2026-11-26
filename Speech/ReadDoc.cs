using System.Text.RegularExpressions;

namespace pxdSpeak.Speech;

/// <summary>
/// Text laid out for the in-app reader: paragraphs (blocks) made of pieces, where a piece is either a word that is read
/// and highlighted or decoration that is only shown (a bullet, a "#", a lone dash). Line breaks inside a paragraph that
/// only wrap the text are joined; short lines such as headings and list items stay blocks of their own, the same way
/// <see cref="TextPrep.Clean"/> treats them when reading aloud.
/// </summary>
public sealed partial class ReadDoc
{
    /// <param name="Text">As shown.</param>
    /// <param name="Word">Index into <see cref="Words"/>, or -1 for decoration.</param>
    public readonly record struct Piece(string Text, int Word);

    public List<List<Piece>> Blocks { get; } = new();

    /// <summary>Blocks that were markdown headings ("# …"), shown bold.</summary>
    public List<bool> Headings { get; } = new();

    /// <summary>The words as they are spoken (markdown marks trimmed off).</summary>
    public List<string> Words { get; } = new();

    /// <summary>True where a block ends after that word.</summary>
    public List<bool> BlockEnds { get; } = new();

    /// <summary>Which block each word is in.</summary>
    public List<int> BlockOf { get; } = new();

    /// <summary>Sentences as word ranges [First, Last], counted from the first word.</summary>
    public List<(int First, int Last)> Sentences { get; private set; } = new();

    public int WordCount => Words.Count;

    public static ReadDoc From(string text)
    {
        var doc = new ReadDoc();
        var s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var para in Regex.Split(s, @"\n[ \t]*\n"))
        {
            var lines = para.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var block = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (block.Count > 0 && StartsOwnLine(line))
                    doc.AddBlock(block);
                block.Add(line);
                var next = i + 1 < lines.Count ? lines[i + 1] : null;
                // Ends here: a heading or list item, or a short line followed by one that starts a new thought.
                if (next is null || StartsOwnLine(line) || line.Length < 60 && char.IsUpper(next[0]) && !EndsMidSentence(line))
                    doc.AddBlock(block);
            }

            doc.AddBlock(block);
        }

        doc.Sentences = Tracking.Sentences(doc.Tracked(0));
        return doc;
    }

    private static bool StartsOwnLine(string line) => Bullet().IsMatch(line) || line.StartsWith('#') || line.StartsWith('>');

    private static bool EndsMidSentence(string line) => line.EndsWith(',') || line.EndsWith('-') || line.EndsWith('—');

    private void AddBlock(List<string> lines)
    {
        if (lines.Count == 0)
            return;
        var pieces = new List<Piece>();
        var first = Words.Count;
        var joined = string.Join(" ", lines);
        var heading = joined.StartsWith('#');
        foreach (Match m in Token().Matches(joined))
        {
            var spoken = Marks().Replace(m.Value, "");
            if (!spoken.Any(char.IsLetterOrDigit))
            {
                // "#" and ">" marks are left out; bullets and dashes are shown but not read.
                if (spoken.Length > 0)
                    pieces.Add(new Piece(spoken, -1));
                continue;
            }

            pieces.Add(new Piece(spoken, Words.Count));
            Words.Add(spoken);
            BlockEnds.Add(false);
            BlockOf.Add(Blocks.Count);
        }

        lines.Clear();
        if (Words.Count == first && pieces.Count == 0)
            return;
        if (Words.Count > first)
            BlockEnds[^1] = true;
        Blocks.Add(pieces);
        Headings.Add(heading);
    }

    /// <summary>The words to hand the reader, starting at <paramref name="start"/>.</summary>
    public TrackedText Tracked(int start, Action<int, int, int>? onWord = null) =>
        new() { Words = Words, BlockEnds = BlockEnds, Start = Math.Clamp(start, 0, Math.Max(0, Words.Count - 1)), OnWord = onWord };

    /// <summary>Which sentence (0-based) a word is in.</summary>
    public int SentenceOf(int word)
    {
        int lo = 0, hi = Sentences.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (word < Sentences[mid].First)
                hi = mid - 1;
            else if (word > Sentences[mid].Last)
                lo = mid + 1;
            else
                return mid;
        }

        return Math.Clamp(lo, 0, Math.Max(0, Sentences.Count - 1));
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex Token();

    [GeneratedRegex(@"\*\*|__|~~|`+|^#+|^>")]
    private static partial Regex Marks();

    [GeneratedRegex(@"^(?:[-*•▪◦]|\d+[.)])\s+")]
    private static partial Regex Bullet();
}
