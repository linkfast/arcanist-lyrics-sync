using System.Text;
using System.Text.RegularExpressions;
using Arcanist.Core.Data;

namespace Arcanist.LyricsSync;

/// <summary>The lyrics with the time of each line, and how much of them was heard.</summary>
/// <param name="Matched">The share of the lyrics' words found among those heard (0 to 1).</param>
public sealed record AlignedLyrics(IReadOnlyList<(int TimeMs, string Text)> Lines, double Matched);

/// <summary>
/// Matches the words heard in a song to its known lyrics and gives each line the time its first word is sung.
/// Both sides are normalized (case, accents, punctuation) and aligned as sequences (Needleman-Wunsch), so missed,
/// extra and misheard words do not shift the rest; words found are anchors, the others are placed between them.
/// Languages written without spaces (Japanese, Chinese, Thai…) are compared character by character, with katakana
/// and hiragana as the same: a line of them would otherwise be one long "word" nothing heard is like.
/// </summary>
public static partial class LyricsAligner
{
    /// <summary>Changes when the method changes: songs given up on are tried again with a new one.</summary>
    public const int Version = 2;

    /// <summary>Below this share of the lyrics' words heard, the lyrics are not this song's (or nothing was understood).</summary>
    public const double MinimumMatched = 0.5;

    /// <summary>How similar two words must be to count as the same one (1 is identical).</summary>
    private const double SameWord = 0.7;

    /// <summary>The time a sung character takes, to place words with no anchor near them.</summary>
    private const int MsPerCharacter = 70;

    private const double Gap = -0.4;
    private const double Mismatch = -1;

    /// <summary>The lines with their times, or null when too little of the lyrics was heard.</summary>
    public static AlignedLyrics? Align(string plain, IReadOnlyList<HeardWord> heard)
    {
        var lines = Lines(plain);
        var lyricWords = new List<(string Word, int Line)>();
        for (var i = 0; i < lines.Count; i++)
            lyricWords.AddRange(lines[i].Words.Select(w => (w, i)));
        if (lyricWords.Count == 0)
            return null;

        var heardWords = HeardTokens(heard);
        var anchors = Anchors(lyricWords.Select(w => w.Word).ToList(), heardWords.Select(h => h.Word).ToList());
        var matched = (double)anchors.Count(a => a >= 0) / lyricWords.Count;
        if (matched < MinimumMatched)
            return null;

        // Each lyric word's start: its anchor's, or placed between the anchors around it.
        var starts = new int[lyricWords.Count];
        var known = new bool[lyricWords.Count];
        for (var i = 0; i < anchors.Length; i++)
        {
            if (anchors[i] >= 0)
            {
                starts[i] = heardWords[anchors[i]].StartMs;
                known[i] = true;
            }
        }

        Place(lyricWords, starts, known, anchors.Select(a => a >= 0 ? heardWords[a].EndMs : 0).ToArray());

        var result = new List<(int TimeMs, string Text)>();
        var last = -1;
        for (var line = 0; line < lines.Count; line++)
        {
            var first = lyricWords.FindIndex(w => w.Line == line);
            if (first < 0)
                continue;
            // Never back in time, and never two lines at the same moment.
            var time = Math.Max(starts[first], last + 10);
            result.Add((time, lines[line].Text));
            last = time;
        }

        return new AlignedLyrics(result, matched);
    }

    /// <summary>
    /// The parts of the song (from the end of the last word heard before to the start of the next one heard after)
    /// where whole lines of the lyrics were not heard, to listen to them again; only those of two seconds or more.
    /// </summary>
    public static IReadOnlyList<(int FromMs, int ToMs)> Unheard(string plain, IReadOnlyList<HeardWord> heard, int durationMs)
    {
        var lines = Lines(plain);
        var lyricWords = new List<(string Word, int Line)>();
        for (var i = 0; i < lines.Count; i++)
            lyricWords.AddRange(lines[i].Words.Select(w => (w, i)));
        var heardWords = HeardTokens(heard);
        if (lyricWords.Count == 0 || heardWords.Count == 0)
            return [];

        var anchors = Anchors(lyricWords.Select(w => w.Word).ToList(), heardWords.Select(h => h.Word).ToList());
        var lineHeard = new bool[lines.Count];
        for (var i = 0; i < anchors.Length; i++)
        {
            if (anchors[i] >= 0)
                lineHeard[lyricWords[i].Line] = true;
        }

        var regions = new List<(int, int)>();
        var line = 0;
        while (line < lines.Count)
        {
            if (lineHeard[line])
            {
                line++;
                continue;
            }

            var runEnd = line;
            while (runEnd < lines.Count && !lineHeard[runEnd])
                runEnd++;
            // The heard words around the run: the last of the line before, the first of the line after.
            var before = Enumerable.Range(0, anchors.Length).LastOrDefault(i => anchors[i] >= 0 && lyricWords[i].Line < line, -1);
            var after = Enumerable.Range(0, anchors.Length).FirstOrDefault(i => anchors[i] >= 0 && lyricWords[i].Line >= runEnd, -1);
            var from = before >= 0 ? heardWords[anchors[before]].EndMs : 0;
            var to = after >= 0 ? heardWords[anchors[after]].StartMs : durationMs;
            if (to - from >= 2000)
                regions.Add((from, to));
            line = runEnd;
        }

        return regions;
    }

    /// <summary>
    /// The lines to time: the lyrics' lines with words, without notes such as "[Chorus]" or "(x2)" on a line of their
    /// own (they are not sung).
    /// </summary>
    internal static List<(string Text, List<string> Words)> Lines(string plain) =>
        plain.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !NoteLine().IsMatch(l))
            .Select(l => (Text: l, Words: Words(l)))
            .Where(l => l.Words.Count > 0)
            .ToList();

    internal static List<string> Words(string text) => Tokens(Normalize(text));

    /// <summary>
    /// The words heard, as the units compared: a word in a language written without spaces is split into its
    /// characters, each with its share of the word's time.
    /// </summary>
    internal static List<(string Word, int StartMs, int EndMs)> HeardTokens(IReadOnlyList<HeardWord> heard)
    {
        var tokens = new List<(string, int, int)>();
        foreach (var word in heard)
        {
            var parts = Tokens(Normalize(word.Text));
            var span = Math.Max(0, word.EndMs - word.StartMs);
            for (var i = 0; i < parts.Count; i++)
                tokens.Add((parts[i], word.StartMs + span * i / parts.Count, word.StartMs + span * (i + 1) / parts.Count));
        }

        return tokens;
    }

    /// <summary>The units of normalized text: its words, and each character of a script written without spaces.</summary>
    internal static List<string> Tokens(string normalized)
    {
        var tokens = new List<string>();
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var run = new StringBuilder();
            foreach (var c in word)
            {
                if (Unspaced(c))
                {
                    if (run.Length > 0)
                        tokens.Add(run.ToString());
                    run.Clear();
                    tokens.Add(c.ToString());
                }
                else
                    run.Append(c);
            }

            if (run.Length > 0)
                tokens.Add(run.ToString());
        }

        return tokens;
    }

    /// <summary>A letter of a script written without spaces between words: Han, kana, Thai, Lao, Khmer, Myanmar.</summary>
    internal static bool Unspaced(char c) =>
        c is >= '\u3040' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF' or >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF' or >= '\u0E00' and <= '\u0EFF' or >= '\u1780' and <= '\u17FF' or >= '\u1000' and <= '\u109F';

    /// <summary>Lower case, without accents, apostrophes or punctuation, katakana as hiragana: what is compared.</summary>
    public static string Normalize(string text)
    {
        // Full-width letters and half-width katakana as their usual forms first.
        var folded = TextNormalizer.Normalize(text.Normalize(NormalizationForm.FormKC).Replace("'", "").Replace("’", ""));
        var builder = new StringBuilder(folded.Length);
        foreach (var c in folded)
            builder.Append(c is >= '\u30A1' and <= '\u30F6' ? (char)(c - 0x60) : char.IsLetterOrDigit(c) || c == '\u30FC' ? c : ' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>For each lyric word, the index of the heard word it is (-1 when none): a global alignment.</summary>
    internal static int[] Anchors(List<string> lyrics, List<string> heard)
    {
        int n = lyrics.Count, m = heard.Count;
        var score = new double[n + 1, m + 1];
        var from = new byte[n + 1, m + 1]; // 0 diagonal, 1 skip a lyric word, 2 skip a heard word
        for (var i = 1; i <= n; i++)
        {
            score[i, 0] = i * Gap;
            from[i, 0] = 1;
        }

        // What is heard before the singing starts (an intro, a hallucination) costs nothing.
        for (var j = 1; j <= m; j++)
            from[0, j] = 2;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var similarity = Similarity(lyrics[i - 1], heard[j - 1]);
                var diagonal = score[i - 1, j - 1] + (similarity >= SameWord ? 1 + similarity : Mismatch);
                var up = score[i - 1, j] + Gap;
                var left = score[i, j - 1] + Gap;
                if (diagonal >= up && diagonal >= left)
                {
                    score[i, j] = diagonal;
                    from[i, j] = 0;
                }
                else if (up >= left)
                {
                    score[i, j] = up;
                    from[i, j] = 1;
                }
                else
                {
                    score[i, j] = left;
                    from[i, j] = 2;
                }
            }
        }

        // What is heard after the last line (an outro) costs nothing either: the best end on the last row.
        var end = 0;
        for (var j = 1; j <= m; j++)
        {
            if (score[n, j] > score[n, end])
                end = j;
        }

        var anchors = Enumerable.Repeat(-1, n).ToArray();
        int a = n, b = end;
        while (a > 0 && b > 0)
        {
            switch (from[a, b])
            {
                case 0:
                    if (Similarity(lyrics[a - 1], heard[b - 1]) >= SameWord)
                        anchors[a - 1] = b - 1;
                    a--;
                    b--;
                    break;
                case 1:
                    a--;
                    break;
                default:
                    b--;
                    break;
            }
        }

        return anchors;
    }

    /// <summary>1 minus the edit distance relative to the longer word.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a == b)
            return 1;
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>
    /// Gives a time to the words without an anchor, at singing pace: those that end the line of the anchor before them
    /// right after it, the others (the start of the next lines) just ahead of the anchor after them; squeezed between
    /// the two anchors when there is not room enough.
    /// </summary>
    private static void Place(List<(string Word, int Line)> words, int[] starts, bool[] known, int[] ends)
    {
        var i = 0;
        while (i < words.Count)
        {
            if (known[i])
            {
                i++;
                continue;
            }

            var runEnd = i;
            while (runEnd < words.Count && !known[runEnd])
                runEnd++;
            var lower = i > 0 ? Math.Max(ends[i - 1], starts[i - 1]) : 0;
            var upper = runEnd < words.Count ? starts[runEnd] : int.MaxValue;

            // The words still in the previous anchor's line follow it; the rest lead into the next anchor.
            var split = i;
            while (i > 0 && split < runEnd && words[split].Line == words[i - 1].Line)
                split++;
            var tail = PlaceForward(words, starts, i, split, lower, upper);
            PlaceBackward(words, starts, split, runEnd, Math.Max(lower, tail), upper);
            i = runEnd;
        }
    }

    /// <summary>Words from <paramref name="from"/> on, at singing pace; returns where they end.</summary>
    private static int PlaceForward(List<(string Word, int Line)> words, int[] starts, int first, int end, int from, int limit)
    {
        var time = from;
        for (var k = first; k < end; k++)
        {
            starts[k] = Math.Min(time, limit);
            time += (words[k].Word.Length + 1) * MsPerCharacter;
        }

        return Math.Min(time, limit);
    }

    /// <summary>Words ending at <paramref name="until"/> at singing pace, never before <paramref name="from"/>.</summary>
    private static void PlaceBackward(List<(string Word, int Line)> words, int[] starts, int first, int end, int from, int until)
    {
        if (first >= end)
            return;
        var needed = 0;
        for (var k = first; k < end; k++)
            needed += (words[k].Word.Length + 1) * MsPerCharacter;
        if (until == int.MaxValue)
        {
            // After the last anchor: on from it.
            PlaceForward(words, starts, first, end, from, int.MaxValue);
            return;
        }

        var begin = Math.Max(from, until - needed);
        var span = Math.Max(0, until - begin);
        var offset = 0;
        for (var k = first; k < end; k++)
        {
            starts[k] = begin + (int)((long)span * offset / Math.Max(1, needed));
            offset += (words[k].Word.Length + 1) * MsPerCharacter;
        }
    }

    [GeneratedRegex(@"^(\[[^\]]*\]|\([^)]*\)|\{[^}]*\})$")]
    private static partial Regex NoteLine();
}
