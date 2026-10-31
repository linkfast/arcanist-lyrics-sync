using System.Globalization;
using System.Text;
using Arcanist.Core.Application.Catalog;

namespace Arcanist.LyricsSync;

/// <summary>What the model heard in a song with no lyrics.</summary>
/// <param name="Words">The words with their times and probabilities.</param>
/// <param name="Language">The language it heard (ISO 639-1), if it said.</param>
/// <param name="Voice">How loud the voice is, every <see cref="VoiceFrameMs"/> (the separated voice, or the song); null when unknown.</param>
public sealed record Transcript(IReadOnlyList<HeardWord> Words, string? Language, IReadOnlyList<float>? Voice)
{
    public const int VoiceFrameMs = 100;
}

/// <summary>
/// Turns what the model heard into lines of lyrics to review: without what it makes up where nobody sings (Whisper
/// writes credits, thanks and loops over instrumentals), in lines cut at pauses and sentence ends, with a break where
/// the singing stops for a while, and the model's confidence for each line so the reviewer looks at the doubtful
/// ones first. Null when too little was sung to be lyrics.
/// </summary>
public static class LyricsTranscriber
{
    /// <summary>Fewer words than this is not a song's lyrics (an instrumental with a shout, a sample).</summary>
    public const int FewestWords = 8;

    /// <summary>A pause this long starts a new line.</summary>
    public const int LinePauseMs = 800;

    /// <summary>Silence this long between lines gets a break (an empty line) after the last one.</summary>
    public const int BreakPauseMs = 6000;

    public const int MaxWordsPerLine = 12;

    /// <summary>The same line more times in a row than this is the model looping, not a chorus.</summary>
    public const int MostRepeats = 4;

    /// <summary>What Whisper writes where nothing is sung (from subtitles it learned from), normalized.</summary>
    private static readonly string[] MadeUp =
    [
        "thanks for watching", "thank you for watching", "thank you so much for watching", "please subscribe", "subscribe to my channel",
        "like and subscribe", "subtitles by", "subtitled by", "captions by", "transcribed by", "translated by", "amara org",
        "gracias por ver", "suscribete al canal", "subtitulos realizados por", "subtitulos por la comunidad de amara", "subtitulado por",
        "obrigado por assistir", "legendas pela comunidade", "untertitel im auftrag", "sous titres realises par",
    ];

    public static IReadOnlyList<TranscribedLine>? Lines(Transcript transcript)
    {
        var words = transcript.Words.Where(w => Clean(w.Text).Length > 0).OrderBy(w => w.StartMs).ToList();
        words = Voiced(words, transcript.Voice);
        words = WithoutMadeUp(words);
        if (words.Count < FewestWords)
            return null;

        var lines = new List<(List<HeardWord> Words, int Repeats)>();
        var current = new List<HeardWord>();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (current.Count > 0 && EndsLine(current, word))
            {
                lines.Add((current, 0));
                current = [];
            }

            current.Add(word);
        }

        if (current.Count > 0)
            lines.Add((current, 0));

        var result = new List<TranscribedLine>();
        string? previous = null;
        var repeats = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = Text(lines[i].Words);
            var key = Normalize(text);
            repeats = key == previous ? repeats + 1 : 1;
            previous = key;
            if (repeats > MostRepeats)
                continue;
            var probabilities = lines[i].Words.Where(w => w.Probability is not null).Select(w => w.Probability!.Value).ToList();
            result.Add(new TranscribedLine(lines[i].Words[0].StartMs, text, probabilities.Count > 0 ? Math.Round(probabilities.Average(), 3) : null));
            // A long stretch without singing after this line: an empty line clears it in players.
            var end = lines[i].Words[^1].EndMs;
            if (i + 1 < lines.Count && lines[i + 1].Words[0].StartMs - end >= BreakPauseMs)
                result.Add(new TranscribedLine(end + 1000, "", null));
        }

        return result.Count(l => l.Text.Length > 0) == 0 ? null : result;
    }

    /// <summary>Whether <paramref name="next"/> starts a new line after the words of the line so far.</summary>
    private static bool EndsLine(List<HeardWord> line, HeardWord next)
    {
        var last = line[^1];
        if (next.StartMs - last.EndMs >= LinePauseMs || line.Count >= MaxWordsPerLine)
            return true;
        var ending = last.Text.TrimEnd().LastOrDefault();
        if (ending is '.' or '?' or '!' && line.Count >= 3)
            return true;
        return ending is ',' or ';' && line.Count >= 7;
    }

    /// <summary>
    /// The words where someone sings: with the voice's loudness known, a word where it stays under a fifth of how
    /// loud the singing usually is (the 90th percentile) was made up over the music.
    /// </summary>
    private static List<HeardWord> Voiced(List<HeardWord> words, IReadOnlyList<float>? voice)
    {
        if (voice is not { Count: > 0 })
            return words;
        var sorted = voice.OrderBy(v => v).ToList();
        var loud = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.9))];
        if (loud <= 0)
            return [];
        var threshold = loud * 0.2f;
        return words.Where(w =>
        {
            var from = Math.Clamp(w.StartMs / Transcript.VoiceFrameMs, 0, voice.Count - 1);
            var to = Math.Clamp(Math.Max(w.EndMs, w.StartMs + 1) / Transcript.VoiceFrameMs, from, voice.Count - 1);
            for (var f = from; f <= to; f++)
                if (voice[f] >= threshold)
                    return true;
            return false;
        }).ToList();
    }

    /// <summary>Without the phrases Whisper makes up, and without annotations ([Music], (applause), ♪).</summary>
    private static List<HeardWord> WithoutMadeUp(List<HeardWord> words)
    {
        var kept = words.Where(w => !IsAnnotation(w.Text)).ToList();
        var normalized = kept.Select(w => Normalize(w.Text)).ToList();
        var drop = new bool[kept.Count];
        foreach (var phrase in MadeUp)
        {
            var parts = phrase.Split(' ');
            for (var i = 0; i + parts.Length <= kept.Count; i++)
            {
                if (Enumerable.Range(0, parts.Length).All(k => normalized[i + k] == parts[k]))
                    for (var k = 0; k < parts.Length; k++)
                        drop[i + k] = true;
            }
        }

        return kept.Where((_, i) => !drop[i]).ToList();
    }

    private static bool IsAnnotation(string text)
    {
        var t = text.Trim();
        return t.Length == 0 || t.All(c => c is '♪' or '♫' or '*' or '-' or '…') || (t[0] is '[' or '(' && t[^1] is ']' or ')');
    }

    /// <summary>A line's text: its words, without a final period or comma (lyrics are not prose), starting with a capital.</summary>
    private static string Text(List<HeardWord> words)
    {
        var text = string.Join(' ', words.Select(w => Clean(w.Text))).Trim().TrimEnd('.', ',', ';', ':');
        return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
    }

    private static string Clean(string word) => word.Replace('♪', ' ').Replace('♫', ' ').Trim();

    /// <summary>Lower case, no accents nor punctuation: to compare phrases.</summary>
    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
            else if (char.IsWhiteSpace(c) && builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }

        return builder.ToString().Trim();
    }
}
