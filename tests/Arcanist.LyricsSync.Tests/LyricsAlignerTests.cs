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
    public void Japanese_lyrics_are_timed_character_by_character_and_kana_of_either_kind_match()
    {
        const string japanese = """
            君の名前を呼んだ
            夜空にひかる星
            Baby, I love you
            """;
        // Whisper gives Japanese in pieces of a few characters, without spaces; here one of them in katakana.
        var heard = new List<HeardWord>
        {
            new("君の", 10_000, 10_400), new("名前を", 10_400, 11_000), new("呼んだ", 11_000, 11_600),
            new("夜空に", 15_000, 15_600), new("ヒカル", 15_600, 16_200), new("星", 16_200, 16_500),
            new("Baby,", 20_000, 20_400), new("I", 20_400, 20_600), new("love", 20_600, 21_000), new("you", 21_000, 21_400),
        };

        var aligned = LyricsAligner.Align(japanese, heard)!;

        Assert.Equal([(10_000, "君の名前を呼んだ"), (15_000, "夜空にひかる星"), (20_000, "Baby, I love you")], aligned.Lines);
        Assert.Equal(1, aligned.Matched);
    }

    [Fact]
    public void A_line_of_chinese_heard_in_pieces_gets_its_time()
    {
        var aligned = LyricsAligner.Align("月亮代表我的心\n你问我爱你有多深", [
            new HeardWord("月亮", 3_000, 3_500), new HeardWord("代表我的心", 3_500, 5_000),
            new HeardWord("你问我", 8_000, 8_900), new HeardWord("爱你有多深", 8_900, 10_000),
        ])!;

        Assert.Equal([3_000, 8_000], aligned.Lines.Select(l => l.TimeMs));
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
