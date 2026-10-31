using System.Net.Http.Json;
using System.Text.Json;
using Arcanist.Core.Setup;
using Arcanist.Core.Tests;
using Arcanist.Core.Tests.Scanning;

namespace Arcanist.LyricsSync.Tests;

/// <summary>
/// The extension installed in a server, with the words "heard" in each song read from files instead of Whisper: plain
/// lyrics get their timing, lyrics that do not match are left alone, and the admin page tells what happened.
/// </summary>
public sealed class LyricsSyncTests : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly string Extension = Path.Combine(AppContext.BaseDirectory, "extension");

    private readonly string _music = Directory.CreateTempSubdirectory("arcanist-lyrics-sync-music-").FullName;
    private readonly string _transcripts = Directory.CreateTempSubdirectory("arcanist-lyrics-sync-heard-").FullName;
    private ArcanistFactory _factory = null!;
    private HttpClient _admin = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private void Heard(string title, params (string Words, int StartMs)[] lines)
    {
        var words = lines.SelectMany(l => l.Words.Split(' ').Select((w, i) => new HeardWord(w, l.StartMs + i * 300, l.StartMs + i * 300 + 250)));
        File.WriteAllText(Path.Combine(_transcripts, title + ".json"), JsonSerializer.Serialize(words, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private async Task<string> TrackIdAsync(string title)
    {
        var page = await _admin.GetFromJsonAsync<JsonElement>($"/rest/v1/tracks?search={Uri.EscapeDataString(title)}", ArcanistFactory.Json, Cancel);
        return page.GetProperty("items")[0].GetProperty("id").GetString()!;
    }

    private Task<JsonElement> LyricsAsync(string id) =>
        _admin.GetFromJsonAsync<JsonElement>($"/rest/v1/tracks/{id}/lyrics", ArcanistFactory.Json, Cancel);

    [Fact]
    public async Task Plain_lyrics_are_timed_from_what_is_sung_and_others_are_left_alone()
    {
        AudioFixtures.WriteWav(Path.Combine(_music, "sung.wav"), 1,
            new FixtureTags("Sung", "Band", Lyrics: "[Intro]\nFirst line of words\nSecond line follows\n\nThird and last"));
        AudioFixtures.WriteWav(Path.Combine(_music, "other.wav"), 2, new FixtureTags("Other", "Band", Lyrics: "Words that nobody sings here"));
        Heard("Sung", ("First line of words", 4_000), ("Second line follows", 9_500), ("Third and last", 15_000));
        Heard("Other", ("Something else entirely is heard", 2_000));

        _factory = new ArcanistFactory(new Dictionary<string, string?>
        {
            ["Arcanist:Extensions:lyrics-sync:Enabled"] = "true",
            ["Arcanist:Extensions:lyrics-sync:Transcripts"] = _transcripts,
        });
        Directory.CreateDirectory(_factory.DataPath);
        CopyFolder(Extension, Path.Combine(_factory.DataPath, "extensions", "lyrics-sync"));
        await _factory.CompleteSetupAsync(new SetupLibrary("Music", [_music]));
        _admin = await _factory.CreateAuthenticatedClientAsync();
        await _factory.WaitForScansAsync(_admin);

        var sung = await TrackIdAsync("Sung");
        JsonElement lyrics = default;
        for (var i = 0; i < 300; i++)
        {
            lyrics = await LyricsAsync(sung);
            if (lyrics.GetProperty("synced").ValueKind == JsonValueKind.Array)
                break;
            await Task.Delay(100, Cancel);
        }

        Assert.Equal("ALIGNED", lyrics.GetProperty("syncedSource").GetString());
        Assert.Equal([(4_000, "First line of words"), (9_500, "Second line follows"), (15_000, "Third and last")],
            lyrics.GetProperty("synced").EnumerateArray().Select(l => (l.GetProperty("timeMs").GetInt32(), l.GetProperty("text").GetString())));
        // The file's words stay as they are.
        Assert.StartsWith("[Intro]", lyrics.GetProperty("plain").GetString());

        // The other song's lyrics are not what is sung: no timing, and the page says so.
        JsonElement page = default;
        for (var i = 0; i < 300; i++)
        {
            page = await _admin.GetFromJsonAsync<JsonElement>("/rest/v1/extensions/lyrics-sync/admin?language=en", ArcanistFactory.Json, Cancel);
            if (page.ToString().Contains("1 do not match the singing"))
                break;
            await Task.Delay(100, Cancel);
        }

        Assert.Contains("1 do not match the singing", page.ToString());
        Assert.Contains("1 of 2 lyrics (1 worked out here)", page.ToString());
        Assert.Equal(JsonValueKind.Null, (await LyricsAsync(await TrackIdAsync("Other"))).GetProperty("synced").ValueKind);

        // Deleting the timing worked out here brings it back (worked out again).
        (await _admin.PostAsJsonAsync("/rest/v1/extensions/lyrics-sync/admin/actions/clear?language=en", new { values = new { } }, Cancel))
            .EnsureSuccessStatusCode();
        for (var i = 0; i < 300 && (await LyricsAsync(sung)).GetProperty("synced").ValueKind != JsonValueKind.Array; i++)
            await Task.Delay(100, Cancel);
        Assert.Equal("ALIGNED", (await LyricsAsync(sung)).GetProperty("syncedSource").GetString());
    }

    [Fact]
    public async Task A_resync_times_again_what_was_tried_before_and_lyrics_timed_by_their_file()
    {
        AudioFixtures.WriteWav(Path.Combine(_music, "sung.wav"), 1, new FixtureTags("Sung", "Band", Lyrics: "First line of words\nSecond line follows"));
        AudioFixtures.WriteWav(Path.Combine(_music, "timed.wav"), 2,
            new FixtureTags("Timed", "Band", Lyrics: "[00:01.00]Words in the file\n[00:02.00]Timed by the file"));
        AudioFixtures.WriteWav(Path.Combine(_music, "kept.wav"), 3,
            new FixtureTags("Kept", "Band", Lyrics: "[00:01.00]Nobody sings these\n[00:02.00]Words at all"));
        Heard("Sung", ("First line of words", 4_000), ("Second line follows", 9_500));
        Heard("Timed", ("Words in the file", 3_000), ("Timed by the file", 7_000));
        Heard("Kept", ("Something else entirely is heard", 2_000));

        _factory = new ArcanistFactory(new Dictionary<string, string?>
        {
            ["Arcanist:Extensions:lyrics-sync:Enabled"] = "true",
            ["Arcanist:Extensions:lyrics-sync:Transcripts"] = _transcripts,
        });
        Directory.CreateDirectory(_factory.DataPath);
        CopyFolder(Extension, Path.Combine(_factory.DataPath, "extensions", "lyrics-sync"));
        await _factory.CompleteSetupAsync(new SetupLibrary("Music", [_music]));
        _admin = await _factory.CreateAuthenticatedClientAsync();
        await _factory.WaitForScansAsync(_admin);

        var sung = await TrackIdAsync("Sung");
        Assert.Equal(new[] { 4_000, 9_500 }, await TimesWhenAsync(sung, l => l.GetProperty("synced").ValueKind == JsonValueKind.Array));

        // Sung differently now: tried before with these words, it is only timed again because a user asks.
        Heard("Sung", ("First line of words", 5_000), ("Second line follows", 12_000));
        var asked = await ResyncAsync(sung);
        Assert.True(asked.GetProperty("canResync").GetBoolean());
        Assert.Equal(new[] { 5_000, 12_000 }, await TimesWhenAsync(sung, l => !l.GetProperty("resyncPending").GetBoolean()));

        // Lyrics timed by their file are timed from the singing in their place.
        var timed = await TrackIdAsync("Timed");
        Assert.Equal("EMBEDDED", (await LyricsAsync(timed)).GetProperty("syncedSource").GetString());
        Assert.True((await ResyncAsync(timed)).GetProperty("resyncPending").GetBoolean());
        Assert.Equal(new[] { 3_000, 7_000 }, await TimesWhenAsync(timed, l => !l.GetProperty("resyncPending").GetBoolean()));
        Assert.Equal("ALIGNED", (await LyricsAsync(timed)).GetProperty("syncedSource").GetString());

        // What does not match the singing keeps the timing it had.
        var kept = await TrackIdAsync("Kept");
        await ResyncAsync(kept);
        Assert.Equal(new[] { 1_000, 2_000 }, await TimesWhenAsync(kept, l => !l.GetProperty("resyncPending").GetBoolean()));
        Assert.Equal("EMBEDDED", (await LyricsAsync(kept)).GetProperty("syncedSource").GetString());
    }

    /// <summary>
    /// A song with no lyrics anywhere: a player asks for its words, the model writes them as a draft (cleaned up) that
    /// waits for review, and the admin page links to the editor.
    /// </summary>
    [Fact]
    public async Task A_song_without_lyrics_gets_a_recognized_draft_when_a_player_asks()
    {
        AudioFixtures.WriteWav(Path.Combine(_music, "unsung.wav"), 5, new FixtureTags("Unsung", "Band"));
        Heard("Unsung", ("[Music]", 0), ("these are words nobody wrote down", 2_000), ("and the model heard them all", 6_000));

        _factory = new ArcanistFactory(new Dictionary<string, string?>
        {
            ["Arcanist:Extensions:lyrics-sync:Enabled"] = "true",
            ["Arcanist:Extensions:lyrics-sync:Transcripts"] = _transcripts,
        });
        Directory.CreateDirectory(_factory.DataPath);
        CopyFolder(Extension, Path.Combine(_factory.DataPath, "extensions", "lyrics-sync"));
        await _factory.CompleteSetupAsync(new SetupLibrary("Music", [_music]));
        _admin = await _factory.CreateAuthenticatedClientAsync();
        await _factory.WaitForScansAsync(_admin);

        var id = await TrackIdAsync("Unsung");
        (await _admin.PostAsync($"/rest/v1/tracks/{id}/lyrics/transcription", null, Cancel)).EnsureSuccessStatusCode();
        JsonElement status = default;
        for (var i = 0; i < 300; i++)
        {
            status = await _admin.GetFromJsonAsync<JsonElement>($"/rest/v1/tracks/{id}/lyrics/transcription", ArcanistFactory.Json, Cancel);
            if (status.GetProperty("state").GetString() == "NEEDS_REVIEW")
                break;
            await Task.Delay(100, Cancel);
        }

        Assert.Equal("NEEDS_REVIEW", status.GetProperty("state").GetString());
        Assert.Equal(["These are words nobody wrote down", "And the model heard them all"],
            status.GetProperty("draft").GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()));
        // Not the song's lyrics until someone accepts them.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _admin.GetAsync($"/rest/v1/tracks/{id}/lyrics", Cancel)).StatusCode);
        var page = await _admin.GetStringAsync("/rest/v1/extensions/lyrics-sync/admin?language=en", Cancel);
        Assert.Contains("/admin/lyrics/review", page);
        Assert.Contains("Review 1", page);
    }

    /// <summary>
    /// A live video of a song (its own performance, so the song's timing does not fit it) is timed on its own audio:
    /// what is said in its opening scene is not in the lyrics and is left out. A video that plays its song needs
    /// nothing: the song's timing follows it (Arcanist's own tests cover that).
    /// </summary>
    [Fact]
    public async Task A_live_video_is_timed_on_its_own_audio_without_its_dialogue()
    {
        var ffmpeg = FfmpegOnPath();
        Assert.SkipWhen(ffmpeg is null, "ffmpeg is not installed");
        Assert.SkipUnless(Arcanist.Core.Analysis.AudioAligner.IsAvailable, "the native library has no alignment");
        AudioFixtures.WriteWav(Path.Combine(_music, "studio.wav"), 1,
            new FixtureTags("Studio", "Band", Lyrics: "First line of words\nSecond line follows\nThird and last"));
        Heard("Studio", ("First line of words", 4_000), ("Second line follows", 9_500), ("Third and last", 15_000));
        // The concert: someone talks first, then the song, sung later and slower than in the studio.
        Heard("Concert (Live)", ("Good evening everybody thanks for coming tonight", 1_000), ("First line of words", 8_000),
            ("Second line follows", 15_000), ("Third and last", 22_000));
        var wav = Path.Combine(_transcripts, "concert.wav");
        await File.WriteAllBytesAsync(wav, AudioFixtures.Wav(AudioFixtures.Noise(7, 30)), Cancel);
        var video = Path.Combine(_music, "Band - Concert (Live).mp4");
        using (var process = System.Diagnostics.Process.Start(ffmpeg,
                   ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=30", "-i", wav,
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video]))
            await process.WaitForExitAsync(Cancel);

        _factory = new ArcanistFactory(new Dictionary<string, string?>
        {
            ["Arcanist:Analysis:Enabled"] = "true",
            ["Arcanist:Ffmpeg:Path"] = ffmpeg,
            ["Arcanist:Ffmpeg:SearchSystem"] = "false",
            ["Arcanist:Extensions:lyrics-sync:Enabled"] = "true",
            ["Arcanist:Extensions:lyrics-sync:Transcripts"] = _transcripts,
        });
        Directory.CreateDirectory(_factory.DataPath);
        CopyFolder(Extension, Path.Combine(_factory.DataPath, "extensions", "lyrics-sync"));
        await _factory.CompleteSetupAsync(new SetupLibrary("Music", [_music]));
        _admin = await _factory.CreateAuthenticatedClientAsync();
        await _factory.WaitForScansAsync(_admin);

        var videos = await _admin.GetFromJsonAsync<JsonElement>("/rest/v1/videos", ArcanistFactory.Json, Cancel);
        var id = videos.GetProperty("items")[0].GetProperty("id").GetString()!;
        var trackId = await TrackIdAsync("Studio");
        (await _admin.PutAsJsonAsync($"/rest/v1/videos/{id}/link", new { trackId }, ArcanistFactory.Json, Cancel)).EnsureSuccessStatusCode();

        JsonElement lyrics = default;
        for (var i = 0; i < 600; i++)
        {
            lyrics = await _admin.GetFromJsonAsync<JsonElement>($"/rest/v1/videos/{id}/lyrics", ArcanistFactory.Json, Cancel);
            if (lyrics.GetProperty("sync").GetString() == "VIDEO")
                break;
            await Task.Delay(100, Cancel);
        }

        Assert.Equal("VIDEO", lyrics.GetProperty("sync").GetString());
        Assert.Equal([(8_000, "First line of words"), (15_000, "Second line follows"), (22_000, "Third and last")],
            lyrics.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("text").GetString()!.Length > 0)
                .Select(l => (l.GetProperty("startMs").GetInt32(), l.GetProperty("text").GetString())));
    }

    private async Task<JsonElement> ResyncAsync(string id)
    {
        var response = await _admin.PostAsync($"/rest/v1/tracks/{id}/lyrics/resync", null, Cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ArcanistFactory.Json, Cancel);
    }

    /// <summary>The times of the song's lines once <paramref name="done"/> says so (or after 30 s).</summary>
    private async Task<int[]> TimesWhenAsync(string id, Func<JsonElement, bool> done)
    {
        JsonElement lyrics = default;
        for (var i = 0; i < 300; i++)
        {
            lyrics = await LyricsAsync(id);
            if (done(lyrics))
                break;
            await Task.Delay(100, Cancel);
        }

        return lyrics.GetProperty("synced") is { ValueKind: JsonValueKind.Array } synced
            ? synced.EnumerateArray().Select(l => l.GetProperty("timeMs").GetInt32()).ToArray()
            : [];
    }

    /// <summary>
    /// The installed extension with the real models, loaded in its own context as on a server: runs only with
    /// <c>LYRICS_SYNC_REAL</c> (see <see cref="RealAudioTests"/>, whose downloaded components it reuses).
    /// </summary>
    [Fact]
    public async Task The_installed_extension_times_a_real_song()
    {
        var real = Environment.GetEnvironmentVariable("LYRICS_SYNC_REAL");
        Assert.SkipWhen(string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "components")), "LYRICS_SYNC_REAL is not set up");
        var song = Path.Combine(_music, "real" + Path.GetExtension(Directory.GetFiles(real!, "song.*").Single()));
        File.Copy(Directory.GetFiles(real!, "song.*").Single(), song);
        var tags = new ATL.Track(song)
        {
            Title = "Real",
            Artist = "Voice",
            Lyrics = [new ATL.LyricsInfo { UnsynchronizedLyrics = await File.ReadAllTextAsync(Path.Combine(real!, "lyrics.txt"), Cancel) }],
        };
        Assert.True(tags.Save());
        var expected = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(Path.Combine(real!, "expected.json"), Cancel))!;

        _factory = new ArcanistFactory(new Dictionary<string, string?>
        {
            ["Arcanist:Extensions:lyrics-sync:Enabled"] = "true",
            ["Arcanist:Ffmpeg:Path"] = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).First(File.Exists),
        });
        Directory.CreateDirectory(_factory.DataPath);
        CopyFolder(Extension, Path.Combine(_factory.DataPath, "extensions", "lyrics-sync"));
        CopyFolder(Path.Combine(real!, "components"), Path.Combine(_factory.DataPath, "extension-data", "lyrics-sync", "components"));
        await _factory.CompleteSetupAsync(new SetupLibrary("Music", [_music]));
        _admin = await _factory.CreateAuthenticatedClientAsync();
        await _factory.WaitForScansAsync(_admin);

        var id = await TrackIdAsync("Real");
        JsonElement lyrics = default;
        for (var i = 0; i < 1200; i++)
        {
            lyrics = await LyricsAsync(id);
            if (lyrics.GetProperty("synced").ValueKind == JsonValueKind.Array)
                break;
            await Task.Delay(250, Cancel);
        }

        var times = lyrics.GetProperty("synced").EnumerateArray().Select(l => l.GetProperty("timeMs").GetInt32()).ToList();
        Assert.Equal(expected.Length, times.Count);
        Assert.All(times.Zip(expected), t => Assert.InRange(t.First - t.Second, -500, 500));
    }

    private static string? FfmpegOnPath() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        Directory.Delete(_music, recursive: true);
        Directory.Delete(_transcripts, recursive: true);
    }
}
