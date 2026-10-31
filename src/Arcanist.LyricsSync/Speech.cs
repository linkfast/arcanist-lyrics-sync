using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Arcanist.Core.Application.Catalog;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Arcanist.LyricsSync;

/// <summary>A word heard in the song, and when; <paramref name="Probability"/> is how sure the model is of it (0–1).</summary>
public sealed record HeardWord(string Text, int StartMs, int EndMs, double? Probability = null);

/// <summary>Hears the words sung in a song.</summary>
public interface ISpeechRecognizer
{
    /// <summary>Whether it decodes the songs with ffmpeg (and downloads its models): Whisper does.</summary>
    bool NeedsSetup => true;

    /// <summary>The words, in order; empty when nothing was sung.</summary>
    Task<IReadOnlyList<HeardWord>> RecognizeAsync(UntimedLyrics song, string ffmpeg, LyricsSyncSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Everything sung in a song with no lyrics (<see cref="UntimedLyrics.Plain"/> is empty), with each word's
    /// probability, the language and how loud the voice is along the song.
    /// </summary>
    Task<Transcript> TranscribeAsync(UntimedLyrics song, string ffmpeg, LyricsSyncSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// The song decoded by Arcanist's ffmpeg, the voice taken out of the music by Spleeter (sherpa-onnx) and recognized by
/// Whisper (whisper.cpp), with the time of each word. Runs on the caller's thread, which should be a low-priority one.
/// </summary>
public sealed class WhisperRecognizer(Components components, ILogger<WhisperRecognizer> logger) : ISpeechRecognizer
{
    private const int WhisperRate = 16000;
    private const int SeparationRate = 44100;

    private static readonly Lock NativeGate = new();
    private static bool _nativeReady;

    public async Task<IReadOnlyList<HeardWord>> RecognizeAsync(UntimedLyrics song, string ffmpeg, LyricsSyncSettings settings, CancellationToken cancellationToken)
    {
        var voice = await VoiceAsync(song, ffmpeg, settings, cancellationToken);
        return await TranscribeAsync(voice, song.Plain, settings, cancellationToken);
    }

    public async Task<Transcript> TranscribeAsync(UntimedLyrics song, string ffmpeg, LyricsSyncSettings settings, CancellationToken cancellationToken)
    {
        var voice = await VoiceAsync(song, ffmpeg, settings, cancellationToken);
        using var factory = Factory(settings);
        await using var processor = factory.CreateBuilder()
            .WithLanguage("auto")
            .WithThreads(settings.Threads)
            .WithTokenTimestamps()
            .WithProbabilities()
            .WithNoContext()
            .Build();
        var language = new string?[1];
        var words = await HearAsync(processor, voice, 0, cancellationToken, language);
        logger.LogDebug("Heard {Count} words ({Language})", words.Count, language[0]);
        return new Transcript(words, language[0], Levels(voice));
    }

    /// <summary>The song's voice at Whisper's rate: separated from the music when the settings say so, or the whole song.</summary>
    private async Task<float[]> VoiceAsync(UntimedLyrics song, string ffmpeg, LyricsSyncSettings settings, CancellationToken cancellationToken)
    {
        PrepareNatives();
        float[] voice;
        if (settings.SeparateVocals)
        {
            var stereo = await Ffmpeg.DecodeAsync(ffmpeg, song, channels: 2, SeparationRate, cancellationToken);
            var vocals = Separate(stereo, settings.Threads);
            voice = await Ffmpeg.ResampleAsync(ffmpeg, vocals, SeparationRate, WhisperRate, cancellationToken);
        }
        else
        {
            voice = (await Ffmpeg.DecodeAsync(ffmpeg, song, channels: 1, WhisperRate, cancellationToken))[0];
        }

        cancellationToken.ThrowIfCancellationRequested();
        return voice;
    }

    /// <summary>How loud the voice is (RMS) every <see cref="Transcript.VoiceFrameMs"/>.</summary>
    private static float[] Levels(float[] voice)
    {
        var frame = WhisperRate * Transcript.VoiceFrameMs / 1000;
        var levels = new float[(voice.Length + frame - 1) / frame];
        for (var f = 0; f < levels.Length; f++)
        {
            double sum = 0;
            var end = Math.Min(voice.Length, (f + 1) * frame);
            for (var i = f * frame; i < end; i++)
                sum += voice[i] * voice[i];
            levels[f] = (float)Math.Sqrt(sum / Math.Max(1, end - f * frame));
        }

        return levels;
    }

    private WhisperFactory Factory(LyricsSyncSettings settings) => WhisperFactory.FromPath(components.ModelPath(settings.Model), new WhisperFactoryOptions
    {
        UseGpu = false,
        // Cross-attention timing per token: much closer to when each word is sung than the decoder's own.
        UseDtwTimeStamps = true,
        HeadsPreset = WhisperAlignmentHeadsPreset.Small,
    });

    /// <summary>The voice of the song, in mono.</summary>
    private float[] Separate(float[][] stereo, int threads)
    {
        var config = new OfflineSourceSeparationConfig();
        config.Model.Spleeter.Vocals = components.VocalsPath;
        config.Model.Spleeter.Accompaniment = components.AccompanimentPath;
        config.Model.NumThreads = threads;
        using var separation = new OfflineSourceSeparation(config);
        using var output = separation.Process(stereo, SeparationRate);
        // Spleeter's stems are the voice first, then the music.
        var vocals = output.GetStemSamples(0);
        var mono = new float[vocals[0].Length];
        for (var c = 0; c < vocals.Length; c++)
        {
            var channel = vocals[c];
            for (var i = 0; i < mono.Length && i < channel.Length; i++)
                mono[i] += channel[i] / vocals.Length;
        }

        return mono;
    }

    /// <summary>Most passes over parts of the song where whole lines were not heard.</summary>
    private const int MaxSecondPasses = 4;

    private async Task<IReadOnlyList<HeardWord>> TranscribeAsync(float[] samples, string lyrics, LyricsSyncSettings settings,
        CancellationToken cancellationToken)
    {
        using var factory = Factory(settings);
        await using var processor = factory.CreateBuilder()
            .WithLanguage("auto")
            .WithThreads(settings.Threads)
            .WithTokenTimestamps()
            // No prompt with the lyrics: Whisper copies a prompt where nothing is sung.
            .WithNoContext()
            .Build();

        var words = await HearAsync(processor, samples, 0, cancellationToken);

        // Whisper skips lines now and then (often across the end of its 30-second windows): the parts of the song
        // where whole lines went unheard are listened to again on their own.
        var durationMs = (int)(samples.LongLength * 1000 / WhisperRate);
        foreach (var (fromMs, toMs) in LyricsAligner.Unheard(lyrics, words, durationMs).Take(MaxSecondPasses))
        {
            var first = (int)Math.Min(samples.LongLength, (long)fromMs * WhisperRate / 1000);
            var last = (int)Math.Min(samples.LongLength, (long)toMs * WhisperRate / 1000);
            var again = await HearAsync(processor, samples[first..last], fromMs, cancellationToken);
            words = words.Where(w => w.StartMs < fromMs || w.StartMs >= toMs).Concat(again).OrderBy(w => w.StartMs).ToList();
        }

        logger.LogDebug("Heard {Count} words", words.Count);
        return words;
    }

    /// <summary>
    /// The words Whisper hears in these samples, at <paramref name="offsetMs"/> in the song, each with the mean
    /// probability of its tokens; the language it heard goes in <paramref name="language"/>, when given.
    /// </summary>
    private static async Task<List<HeardWord>> HearAsync(WhisperProcessor processor, float[] samples, int offsetMs, CancellationToken cancellationToken,
        string?[]? language = null)
    {
        var words = new List<HeardWord>();
        (string Text, long Start, long End, double Probability, int Tokens)? word = null;
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
        {
            if (language is not null && language[0] is null && !string.IsNullOrEmpty(segment.Language) && segment.Language != "auto")
                language[0] = segment.Language;
            foreach (var token in segment.Tokens)
            {
                var text = token.Text;
                // Special tokens ([_BEG_], <|en|>…) and timestamps.
                if (string.IsNullOrEmpty(text) || text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal))
                    continue;
                var start = token.DtwTimestamp > 0 ? token.DtwTimestamp : token.Start;
                if (word is null || char.IsWhiteSpace(text[0]))
                {
                    Flush();
                    word = (text.Trim(), start, token.End, token.Probability, 1);
                }
                else
                {
                    word = (word.Value.Text + text, word.Value.Start, token.End, word.Value.Probability + token.Probability, word.Value.Tokens + 1);
                }
            }

            Flush();
        }

        Flush();
        return words;

        void Flush()
        {
            if (word is { } w && w.Text.Length > 0)
                // whisper.cpp counts time in hundredths of a second.
                words.Add(new HeardWord(w.Text, offsetMs + (int)(w.Start * 10), offsetMs + (int)(Math.Max(w.Start, w.End) * 10),
                    Math.Round(Math.Clamp(w.Probability / w.Tokens, 0, 1), 3)));
            word = null;
        }
    }

    /// <summary>Points Whisper and sherpa-onnx to the libraries downloaded for this machine (once per process).</summary>
    private void PrepareNatives()
    {
        lock (NativeGate)
        {
            if (_nativeReady)
                return;

            // Whisper.net looks for runtimes/<platform>-<arch>/ beside the file named here.
            RuntimeOptions.LibraryPath = Path.Combine(components.WhisperRoot, "whisper.net");
            RuntimeOptions.RuntimeLibraryOrder = Components.Platform is { NoAvx: true } ? [RuntimeLibrary.CpuNoAvx] : [RuntimeLibrary.Cpu];

            var sherpa = components.SherpaRoot;
            NativeLibrary.SetDllImportResolver(typeof(OfflineSourceSeparation).Assembly, (name, _, _) =>
            {
                if (!name.Contains("sherpa-onnx", StringComparison.Ordinal))
                    return IntPtr.Zero;
                // ONNX Runtime first, from the same folder: the loader would not look for it there.
                foreach (var runtime in new[] { "onnxruntime.dll", "libonnxruntime.so", "libonnxruntime.dylib" })
                {
                    var path = Path.Combine(sherpa, runtime);
                    if (File.Exists(path))
                        NativeLibrary.Load(path);
                }

                foreach (var file in new[] { "sherpa-onnx-c-api.dll", "libsherpa-onnx-c-api.so", "libsherpa-onnx-c-api.dylib" })
                {
                    var path = Path.Combine(sherpa, file);
                    if (File.Exists(path))
                        return NativeLibrary.Load(path);
                }

                return IntPtr.Zero;
            });
            _nativeReady = true;
        }
    }
}

/// <summary>Decoding with Arcanist's ffmpeg, to raw 32-bit float samples.</summary>
internal static class Ffmpeg
{
    /// <summary>The song (only its part, for a track of a file with a CUE sheet), one array per channel.</summary>
    public static async Task<float[][]> DecodeAsync(string ffmpeg, UntimedLyrics song, int channels, int rate, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-nostdin", "-v", "error" };
        if (song.StartMs is { } start)
            arguments.AddRange(["-ss", Seconds(start)]);
        if (song.EndMs is { } end)
            arguments.AddRange(["-to", Seconds(end)]);
        arguments.AddRange(["-i", song.AudioPath, "-vn", "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "-ar", rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-"]);
        var interleaved = await RunAsync(ffmpeg, arguments, null, cancellationToken);
        var frames = interleaved.Length / channels;
        var result = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            var channel = result[c] = new float[frames];
            for (var i = 0; i < frames; i++)
                channel[i] = interleaved[i * channels + c];
        }

        return result;
    }

    /// <summary>Mono samples at another rate.</summary>
    public static Task<float[]> ResampleAsync(string ffmpeg, float[] samples, int from, int to, CancellationToken cancellationToken) =>
        RunAsync(ffmpeg,
            ["-nostdin", "-v", "error", "-f", "f32le", "-ar", from.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-i", "-",
                "-ar", to.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-"],
            samples, cancellationToken);

    private static string Seconds(int ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    private static async Task<float[]> RunAsync(string ffmpeg, IEnumerable<string> arguments, float[]? input, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("ffmpeg did not start.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Already gone, or not allowed here.
        }

        using var cancel = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var writing = input is null ? Task.CompletedTask : Task.Run(async () =>
        {
            await using var stdin = process.StandardInput.BaseStream;
            await stdin.WriteAsync(MemoryMarshal.AsBytes(input.AsSpan()).ToArray(), cancellationToken);
        }, cancellationToken);

        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        await writing;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new IOException($"ffmpeg failed: {(await errors).Trim()}");

        var bytes = output.GetBuffer().AsSpan(0, (int)output.Length);
        return MemoryMarshal.Cast<byte, float>(bytes[..(bytes.Length / 4 * 4)]).ToArray();
    }
}

/// <summary>
/// For tests and demonstrations: the words of each song read from <c>&lt;folder&gt;/&lt;title&gt;.json</c> (a list of
/// <see cref="HeardWord"/>), set with <c>Arcanist:Extensions:lyrics-sync:Transcripts</c>. Nothing is downloaded.
/// </summary>
public sealed class ReplayRecognizer(string folder) : ISpeechRecognizer
{
    public bool NeedsSetup => false;

    public async Task<IReadOnlyList<HeardWord>> RecognizeAsync(UntimedLyrics song, string ffmpeg, LyricsSyncSettings settings, CancellationToken cancellationToken)
    {
        var file = Path.Combine(folder, song.Title + ".json");
        if (!File.Exists(file))
            throw new IOException($"No transcript for {song.Title}.");
        await using var stream = File.OpenRead(file);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<List<HeardWord>>(stream, Json.Options, cancellationToken) ?? [];
    }

    /// <summary>The same words, as everything sung in a song without lyrics (no voice levels nor language).</summary>
    public async Task<Transcript> TranscribeAsync(UntimedLyrics song, string ffmpeg, LyricsSyncSettings settings, CancellationToken cancellationToken) =>
        new(await RecognizeAsync(song, ffmpeg, settings, cancellationToken), null, null);
}

internal static class Versions
{
    public static string Of(Assembly assembly) => assembly.GetName().Version?.ToString(3) ?? "0";
}
