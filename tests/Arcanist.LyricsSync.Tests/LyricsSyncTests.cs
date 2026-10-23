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
