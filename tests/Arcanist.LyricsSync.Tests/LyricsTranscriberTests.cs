namespace Arcanist.LyricsSync.Tests;

public class LyricsTranscriberTests
{
    /// <summary>Words of a line, 300 ms apart from <paramref name="startMs"/>, all with the same probability.</summary>
    private static IEnumerable<HeardWord> Sung(string line, int startMs, double probability = 0.9) =>
        line.Split(' ').Select((w, i) => new HeardWord(w, startMs + i * 300, startMs + i * 300 + 250, probability));

    [Fact]
    public void Lines_are_cut_at_pauses_and_sentence_ends_with_their_confidence()
    {
        var lines = LyricsTranscriber.Lines(new Transcript(
            [.. Sung("hello darkness my old friend.", 1_000), .. Sung("I've come to talk", 3_000, 0.5), .. Sung("with you again", 5_000, 0.7)],
            "en", null))!;

        Assert.Equal(["Hello darkness my old friend", "I've come to talk", "With you again"], lines.Select(l => l.Text));
        Assert.Equal([1_000, 3_000, 5_000], lines.Select(l => l.TimeMs));
        Assert.Equal([0.9, 0.5, 0.7], lines.Select(l => l.Confidence));
    }

    [Fact]
    public void A_long_instrumental_gets_a_break_and_what_whisper_makes_up_is_left_out()
    {
        var lines = LyricsTranscriber.Lines(new Transcript(
        [
            .. Sung("[Music]", 0), .. Sung("first we sing a little", 2_000), .. Sung("then the guitar comes", 4_000),
            .. Sung("thanks for watching", 20_000), .. Sung("and we sing again now", 30_000),
        ], "en", null))!;

        Assert.DoesNotContain(lines, l => l.Text.Contains("Music") || l.Text.Contains("watching"));
        var gap = lines.Single(l => l.Text.Length == 0);
        Assert.InRange(gap.TimeMs, 5_000, 7_000);
        Assert.Equal("And we sing again now", lines[^1].Text);
    }

    [Fact]
    public void Words_where_the_voice_is_silent_are_dropped_and_too_little_is_no_lyrics()
    {
        // The voice sings for the first 4 s; Whisper "hears" a phrase at 10 s where it is silent.
        var voice = Enumerable.Range(0, 120).Select(f => f < 40 ? 0.3f : 0.001f).ToList();
        var lines = LyricsTranscriber.Lines(new Transcript(
            [.. Sung("one two three four five six", 0), .. Sung("seven eight nine ten", 2_000), .. Sung("made up over the music", 10_000)],
            null, voice))!;
        Assert.DoesNotContain(lines, l => l.Text.Contains("made up", StringComparison.OrdinalIgnoreCase));

        Assert.Null(LyricsTranscriber.Lines(new Transcript([.. Sung("yeah", 1_000), .. Sung("oh oh", 5_000)], null, null)));
    }

    [Fact]
    public void A_line_looping_more_than_four_times_in_a_row_is_whisper_looping()
    {
        var words = Enumerable.Range(0, 10).SelectMany(i => Sung("na na na hey", i * 3_000)).ToList();
        var lines = LyricsTranscriber.Lines(new Transcript(words, null, null))!;
        Assert.Equal(LyricsTranscriber.MostRepeats, lines.Count(l => l.Text == "Na na na hey"));
    }
}
