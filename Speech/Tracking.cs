using System.Text.RegularExpressions;

namespace pxdSpeak.Speech;

/// <summary>
/// Text read word by word for a highlighter (the browser extension): the words as the page shows them, where a block
/// (paragraph, heading, list item) ends, and a callback for each word as it becomes audible.
/// </summary>
public sealed class TrackedText
{
    public required IReadOnlyList<string> Words { get; init; }

    /// <summary>True where a paragraph/block ends after that word (a sentence always ends there too).</summary>
    public required IReadOnlyList<bool> BlockEnds { get; init; }

    /// <summary>Start reading at this word.</summary>
    public int Start { get; init; }

    /// <summary>(word, first word of its sentence, last word of its sentence), on a worker thread, as it is heard.</summary>
    public Action<int, int, int>? OnWord { get; init; }
}

public static partial class Tracking
{
    /// <summary>Word ranges [first, last] that are read as one piece: sentences, never longer than about 40 words.</summary>
    public static List<(int First, int Last)> Sentences(TrackedText t)
    {
        var list = new List<(int, int)>();
        var first = t.Start;
        for (var i = t.Start; i < t.Words.Count; i++)
        {
            var w = t.Words[i];
            var end = t.BlockEnds[i] || i == t.Words.Count - 1 || EndsSentence(w) || i - first >= 40
                      || (i - first >= 25 && (w.EndsWith(',') || w.EndsWith(';')));
            if (!end)
                continue;
            list.Add((first, i));
            first = i + 1;
        }

        return list;
    }

    private static bool EndsSentence(string w)
    {
        var s = w.TrimEnd('"', '\'', '”', '’', ')', ']');
        if (s.Length == 0 || !".!?…".Contains(s[^1]))
            return false;
        // "Mr." "e.g." "U.S." are not sentence ends.
        return !Abbrev().IsMatch(s);
    }

    [GeneratedRegex(@"^(Mr|Mrs|Ms|Dr|St|vs|etc|e\.g|i\.e|No|Fig|[A-Z])\.$|^([A-Za-z]\.){2,}$")]
    private static partial Regex Abbrev();

    /// <summary>What a word is spoken as: links read as "link", everything else as written.</summary>
    public static string Spoken(string word) =>
        word.StartsWith("http", StringComparison.OrdinalIgnoreCase) || word.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "link" : word;

    /// <summary>
    /// Estimated start (seconds) of each word in a sentence's audio when the voice can't say: the audio between the
    /// first and last sound is shared out by word length, with extra room after commas and full stops (pauses).
    /// </summary>
    public static double[] Estimate(float[] samples, int rate, IReadOnlyList<string> words)
    {
        var (from, to) = SpeechSpan(samples);
        var weights = words.Select(w =>
        {
            var x = Math.Max(1, w.Count(char.IsLetterOrDigit)) + 1.5;
            if (w.EndsWith(',') || w.EndsWith(';') || w.EndsWith(':') || w.EndsWith('—'))
                x += 3;
            else if (w.Length > 0 && ".!?…".Contains(w[^1]))
                x += 4;
            return x;
        }).ToArray();
        var total = weights.Sum();
        var starts = new double[words.Count];
        double acc = 0;
        for (var i = 0; i < words.Count; i++)
        {
            starts[i] = (from + (to - from) * (acc / total)) / rate;
            acc += weights[i];
        }

        return starts;
    }

    /// <summary>First and last sample that are clearly speech (not the voice's lead-in/out silence).</summary>
    public static (int From, int To) SpeechSpan(float[] s)
    {
        const int win = 240;
        const float threshold = 0.015f;
        int from = 0, to = s.Length;
        for (var i = 0; i + win < s.Length; i += win / 2)
            if (Rms(s, i, win) > threshold)
            {
                from = i;
                break;
            }

        for (var i = s.Length - win; i > from; i -= win / 2)
            if (Rms(s, i, win) > threshold)
            {
                to = i + win;
                break;
            }

        return (from, Math.Max(from + 1, to));
    }

    private static float Rms(float[] s, int at, int n)
    {
        double sum = 0;
        for (var i = at; i < at + n && i < s.Length; i++)
            sum += s[i] * s[i];
        return (float)Math.Sqrt(sum / n);
    }
}
