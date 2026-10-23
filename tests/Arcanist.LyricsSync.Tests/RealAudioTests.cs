using System.Diagnostics;
using System.Text.Json;
using Arcanist.Core.Application.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcanist.LyricsSync.Tests;

/// <summary>
/// The whole pipeline with the real models (Spleeter and Whisper), against a song whose line times are known. Runs only
/// with <c>LYRICS_SYNC_REAL</c> set to a folder holding <c>song.*</c>, <c>lyrics.txt</c> and <c>expected.json</c>
/// (each line's start in ms); the components (about 340 MB) are downloaded once into its <c>components</c> folder.
/// ffmpeg must be in the PATH.
/// </summary>
public sealed class RealAudioTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_real_song_is_timed_within_half_a_second_a_line()
    {
        var folder = Environment.GetEnvironmentVariable("LYRICS_SYNC_REAL");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "LYRICS_SYNC_REAL is not set");
        var cancel = TestContext.Current.CancellationToken;

        var song = Directory.GetFiles(folder!, "song.*").Single();
        var lyrics = await File.ReadAllTextAsync(Path.Combine(folder!, "lyrics.txt"), cancel);
        var expected = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(Path.Combine(folder!, "expected.json"), cancel))!;

        var http = new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var components = new Components(new ExtensionPaths(folder!), http, NullLogger<Components>.Instance);
        foreach (var settings in new[] { LyricsSyncSettings.Default with { Threads = 4 }, LyricsSyncSettings.Default with { Threads = 4, SeparateVocals = false } })
        {
            foreach (var component in components.Missing(settings))
                await components.InstallAsync(component, cancel);

            var watch = Stopwatch.StartNew();
            var heard = await new WhisperRecognizer(components, NullLogger<WhisperRecognizer>.Instance)
                .RecognizeAsync(new UntimedLyrics(Guid.NewGuid(), lyrics, "", "Song", null, 0, song, null, null), "ffmpeg", settings, cancel);
            var aligned = LyricsAligner.Align(lyrics, heard);
            output.WriteLine($"Separating: {settings.SeparateVocals}, {watch.Elapsed.TotalSeconds:0.0} s, heard: {string.Join(' ', heard.Select(w => $"{w.Text}@{w.StartMs}"))}");
            Assert.NotNull(aligned);
            var errors = aligned.Lines.Select((l, i) => l.TimeMs - expected[i]).ToList();
            output.WriteLine($"Matched {aligned.Matched:P0}; errors (ms): {string.Join(", ", errors)}");
            File.AppendAllText(Path.Combine(folder!, "results.txt"), $"separating={settings.SeparateVocals} seconds={watch.Elapsed.TotalSeconds:0.0} matched={aligned.Matched:P0} errors={string.Join(",", errors)}{Environment.NewLine}");
            Assert.Equal(expected.Length, aligned.Lines.Count);
            Assert.All(errors, e => Assert.InRange(e, -500, 500));
        }
    }
}
