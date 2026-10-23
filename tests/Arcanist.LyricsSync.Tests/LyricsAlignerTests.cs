namespace Arcanist.LyricsSync.Tests;

public sealed class LyricsAlignerTests
{
    private const string Lyrics = """
        [Verse 1]
        Hello darkness, my old friend
        I've come to talk with you again

        (x2)
        Because a vision softly creeping
        """;

    /// <summary>Words heard one after another, from <paramref name="startMs"/>, 400 ms each.</summary>
    private static List<HeardWord> Heard(string text, int startMs) =>
        text.Split(' ').Select((w, i) => new HeardWord(w, startMs + i * 400, startMs + i * 400 + 350)).ToList();

    [Fact]
    public void Each_line_takes_the_time_of_its_first_word_and_notes_are_left_out()
    {
        var heard = Heard("Hello darkness my old friend", 12_000)
            .Concat(Heard("I've come to talk with you again", 15_000))
            .Concat(Heard("Because a vision softly creeping", 20_000)).ToList();

        var aligned = LyricsAligner.Align(Lyrics, heard)!;

        Assert.Equal(
            [(12_000, "Hello darkness, my old friend"), (15_000, "I've come to talk with you again"), (20_000, "Because a vision softly creeping")],
            aligned.Lines);
        Assert.Equal(1, aligned.Matched);
    }

    [Fact]
    public void Misheard_missed_and_extra_words_do_not_shift_the_rest()
    {
        // An intro heard as words, "darkness" misheard, a line's first word missed, and a cough.
        var heard = new List<HeardWord> { new("Thank", 1_000, 1_200), new("you.", 1_300, 1_500) };
        heard.AddRange(Heard("Hello darknes my old friend", 12_000));
        heard.AddRange(Heard("come to talk with you again", 15_400));
        heard.Add(new HeardWord("uh", 18_000, 18_100));
        heard.AddRange(Heard("Because a vision softly creeping", 20_000));

        var aligned = LyricsAligner.Align(Lyrics, heard)!;

        Assert.Equal([12_000, 20_000], new[] { aligned.Lines[0].TimeMs, aligned.Lines[2].TimeMs });
        // "I've" was not heard: placed just before "come", at singing pace.
        Assert.InRange(aligned.Lines[1].TimeMs, 14_700, 15_400);
        Assert.True(aligned.Matched is > 0.9 and < 1);
    }

    [Fact]
    public void Lyrics_of_another_song_are_not_timed()
    {
        Assert.Null(LyricsAligner.Align(Lyrics, Heard("Is this the real life is this just fantasy caught in a landslide", 5_000)));
        Assert.Null(LyricsAligner.Align(Lyrics, []));
        Assert.Null(LyricsAligner.Align("[Instrumental]", Heard("la la la", 0)));
    }

    [Fact]
    public void Repeated_choruses_each_get_their_own_time()
    {
        const string song = "La la la\nOh oh oh\nLa la la";
        var heard = Heard("la la la", 1_000).Concat(Heard("oh oh oh", 5_000)).Concat(Heard("la la la", 9_000)).ToList();

        Assert.Equal([1_000, 5_000, 9_000], LyricsAligner.Align(song, heard)!.Lines.Select(l => l.TimeMs));
    }

    [Theory]
    [InlineData("Ágætis byrjun", "agaetis byrjun")]
    [InlineData("I've  come, to TALK!", "ive come to talk")]
    [InlineData("Don’t stop", "dont stop")]
    public void Words_are_compared_without_case_accents_or_punctuation(string text, string normalized) =>
        Assert.Equal(normalized, LyricsAligner.Normalize(text));
}
