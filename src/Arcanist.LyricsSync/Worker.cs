using System.Text.Json;
using Arcanist.Core.Application.Catalog;
using Arcanist.Core.Application.Scanning;
using Arcanist.Core.Application.Videos;
using Arcanist.Core.Extensions;
using Arcanist.Core.Media;
using Arcanist.Core.Scanning;
using Arcanist.Core.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arcanist.LyricsSync;

public enum AttemptOutcome
{
    /// <summary>Timed and saved.</summary>
    Aligned,
    /// <summary>Too little of the lyrics was heard: not tried again until the lyrics or the method change.</summary>
    Rejected,
    /// <summary>Something failed (the file, ffmpeg): tried again a day later.</summary>
    Failed,
}

/// <param name="PlainHash">The lyrics it was for: other words are a new song to time.</param>
public sealed record Attempt(string PlainHash, AttemptOutcome Outcome, DateTime At, int Method, double? Matched = null, string? Error = null);

/// <summary>What was tried for each song, kept in the extension's folder.</summary>
public sealed class AttemptStore(ExtensionPaths paths)
{
    private readonly Lock _gate = new();
    private Dictionary<Guid, Attempt>? _attempts;

    public static readonly TimeSpan RetryFailed = TimeSpan.FromDays(1);

    /// <summary>Whether this song's lyrics still need trying.</summary>
    public bool ShouldTry(UntimedLyrics song, DateTime now)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(song.TrackId, out var attempt) || attempt.PlainHash != song.PlainHash || attempt.Method != LyricsAligner.Version)
                return true;
            return attempt.Outcome == AttemptOutcome.Failed && attempt.At + RetryFailed <= now;
        }
    }

    public void Record(Guid trackId, Attempt attempt)
    {
        lock (_gate)
        {
            Load()[trackId] = attempt;
            Files.WriteAllTextAtomically(paths.AttemptsFile, JsonSerializer.Serialize(_attempts, Json.Options));
        }
    }

    /// <summary>Forgets the songs given up on (and the failed ones), so they are tried again.</summary>
    public int ForgetUnaligned()
    {
        lock (_gate)
        {
            var removed = Load().Where(a => a.Value.Outcome != AttemptOutcome.Aligned).Select(a => a.Key).ToList();
            foreach (var id in removed)
                _attempts!.Remove(id);
            Files.WriteAllTextAtomically(paths.AttemptsFile, JsonSerializer.Serialize(_attempts, Json.Options));
            return removed.Count;
        }
    }

    public void ForgetAll()
    {
        lock (_gate)
        {
            _attempts = [];
            Files.WriteAllTextAtomically(paths.AttemptsFile, "{}");
        }
    }

    public (int Aligned, int Rejected, int Failed) Counts()
    {
        lock (_gate)
        {
            var all = Load().Values;
            return (all.Count(a => a.Outcome == AttemptOutcome.Aligned), all.Count(a => a.Outcome == AttemptOutcome.Rejected),
                all.Count(a => a.Outcome == AttemptOutcome.Failed));
        }
    }

    private Dictionary<Guid, Attempt> Load()
    {
        if (_attempts is not null)
            return _attempts;
        try
        {
            _attempts = File.Exists(paths.AttemptsFile)
                ? JsonSerializer.Deserialize<Dictionary<Guid, Attempt>>(File.ReadAllText(paths.AttemptsFile), Json.Options) ?? []
                : [];
        }
        catch (JsonException)
        {
            _attempts = [];
        }

        return _attempts;
    }
}

/// <summary>What the worker is doing, for the admin page.</summary>
public enum WorkerState
{
    Off,
    /// <summary>Waiting for the setup's downloads.</summary>
    NotReady,
    /// <summary>Waiting for ffmpeg, which decodes the songs.</summary>
    NoFfmpeg,
    /// <summary>Scans or listeners first.</summary>
    Paused,
    Working,
    Idle,
}

/// <summary>
/// A song to time, a music video timed on its own audio (<paramref name="Video"/>), or a song without lyrics whose
/// words are recognized (<paramref name="Transcribe"/>).
/// </summary>
/// <param name="Lyrics">What the recognizer hears: the song's file, or the video's (keyed by the video's id).</param>
internal sealed record TimingWork(UntimedLyrics Lyrics, UntimedVideoLyrics? Video = null, bool Transcribe = false)
{
    /// <summary>What a recognition is tried for in <see cref="AttemptStore"/> (a song has no words to hash yet).</summary>
    public const string TranscriptionKey = "transcription";

    public static TimingWork Of(UntimedVideoLyrics video) =>
        new(new UntimedLyrics(video.VideoId, video.Plain, video.PlainHash, video.Title, video.Artist, video.DurationMs, video.VideoPath, null, null), video);

    public static TimingWork Of(UntranscribedSong song) =>
        new(new UntimedLyrics(song.TrackId, "", TranscriptionKey, song.Title, song.Artist, song.DurationMs, song.AudioPath, song.StartMs, song.EndMs)
        {
            Forced = song.Forced,
        }, Transcribe: true);
}

/// <summary>
/// Times the plain lyrics Arcanist hands out (<see cref="LyricsTiming"/>), one song at a time on a low-priority
/// thread: first the songs a listener is looking at, then (with <see cref="LyricsSyncSettings.Background"/>) all the
/// others. It waits while Arcanist scans or someone is listening.
/// <para>
/// Music videos that play their song need nothing: Arcanist carries the song's timing over to where the video plays
/// it, so their scenes (an intro, a dialogue, an outro) get no lyrics. Videos with audio of their own (a live video,
/// another edit: <see cref="VideoLyricsTimings"/>) are timed on that audio, after the songs: the words heard in their
/// scenes are dialogue that does not match the lyrics, and the alignment leaves them out.
/// </para>
/// <para>
/// Songs without lyrics have their words recognized (<see cref="LyricsTranscription"/>) when someone asks for them
/// from a player, and in the background with <see cref="LyricsSyncSettings.Transcribe"/>, last of all. What the model
/// writes is a draft for a person to review (<see cref="LyricsTranscriber"/> cleans it up first).
/// </para>
/// </summary>
public sealed class AlignmentWorker(
    LyricsTiming timing,
    VideoLyricsTimings videoTimings,
    LyricsTranscription transcription,
    Components components,
    SettingsStore settings,
    AttemptStore attempts,
    ISpeechRecognizer recognizer,
    IServiceProvider services,
    TimeProvider time,
    ILogger<AlignmentWorker> logger) : BackgroundService
{
    public const int PageSize = 50;

    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Rest = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _wake = new(0);
    private readonly Queue<UntimedLyrics> _resyncs = new();
    private readonly Queue<UntimedLyrics> _requested = new();
    private readonly Queue<UntimedVideoLyrics> _requestedVideos = new();
    private readonly Queue<UntranscribedSong> _requestedTranscriptions = new();
    private Guid? _cursor;
    private Guid? _videoCursor;
    private Guid? _transcriptionCursor;
    /// <summary>Where the background pass is: songs to time, videos, songs to recognize.</summary>
    private int _phase;

    public WorkerState State { get; private set; } = WorkerState.Off;

    /// <summary>The song being timed.</summary>
    public string? Current { get; private set; }

    public string? LastError { get; private set; }

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    /// <summary>Starts again from the first song (after the settings or the attempts changed).</summary>
    public void Restart()
    {
        _cursor = null;
        _videoCursor = null;
        _transcriptionCursor = null;
        _phase = 0;
        Wake();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = services.GetRequiredService<ExtensionHost>();
        var scans = services.GetRequiredService<ScanEvents>();
        var streams = services.GetRequiredService<StreamActivity>();
        var ffmpeg = services.GetRequiredService<FfmpegLocator>();
        var setup = services.GetRequiredService<IOptionsMonitor<SetupOptions>>();
        timing.Requested += Wake;
        videoTimings.Requested += Wake;
        transcription.Requested += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    // Nothing to read before the setup wizard has created the database.
                    wait = setup.CurrentValue.Completed ? await RunOnceAsync(host, scans, streams, ffmpeg, stoppingToken) : Pause;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Lyrics Sync stopped for a while");
                    LastError = e.Message;
                    wait = Rest;
                }

                if (wait > TimeSpan.Zero)
                    await _wake.WaitAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            timing.Requested -= Wake;
            videoTimings.Requested -= Wake;
            transcription.Requested -= Wake;
        }
    }

    /// <summary>Does one song (or decides to wait); returns how long to wait before the next.</summary>
    internal async Task<TimeSpan> RunOnceAsync(ExtensionHost host, ScanEvents scans, StreamActivity streams, FfmpegLocator ffmpeg,
        CancellationToken cancellationToken)
    {
        var current = settings.Current;
        if (!host.IsEnabled(LyricsSyncExtension.Id))
            return Set(WorkerState.Off, Rest);
        // Nothing can be timed until the setup is done: a re-sync a user asked for does not stay pending meanwhile.
        if (recognizer.NeedsSetup && (Components.Platform is null || components.Missing(current).Count > 0))
        {
            timing.EndAllResyncs();
            transcription.EndAll();
            return Set(WorkerState.NotReady, Rest);
        }

        var ffmpegPath = recognizer.NeedsSetup ? (await ffmpeg.GetAsync(cancellationToken))?.Path : "";
        if (ffmpegPath is null)
        {
            timing.EndAllResyncs();
            transcription.EndAll();
            return Set(WorkerState.NoFfmpeg, Rest);
        }
        if (scans.HasActiveJobs || streams.Active > 0)
            return Set(WorkerState.Paused, Pause);

        if (await NextAsync(current, cancellationToken) is not { } work)
            return Set(WorkerState.Idle, Rest);

        var song = work.Lyrics;
        State = WorkerState.Working;
        Current = song.Artist is null ? song.Title : $"{song.Artist} · {song.Title}";
        if (work.Transcribe)
            return await TranscribeAsync(song, ffmpegPath, current, cancellationToken);
        try
        {
            var aligned = await RunLowAsync(() => recognizer.RecognizeAsync(song, ffmpegPath, current, cancellationToken), cancellationToken) is { } heard
                ? LyricsAligner.Align(song.Plain, heard)
                : null;
            var now = time.GetUtcNow().UtcDateTime;
            if (aligned is null)
            {
                // A re-sync that cannot be matched leaves the timing the song had.
                if (song.Forced)
                    timing.EndResync(song.TrackId);
                attempts.Record(song.TrackId, new Attempt(song.PlainHash, AttemptOutcome.Rejected, now, LyricsAligner.Version));
                logger.LogInformation("Lyrics Sync could not match the lyrics of {Song} to its singing", Current);
            }
            else
            {
                if (work.Video is { } video)
                    await videoTimings.SaveAsync(video.VideoHash, song.PlainHash, Lrc.Format(aligned.Lines), cancellationToken);
                else
                    await timing.SaveAsync(song.TrackId, song.PlainHash, Lrc.Format(aligned.Lines), cancellationToken);
                attempts.Record(song.TrackId, new Attempt(song.PlainHash, AttemptOutcome.Aligned, now, LyricsAligner.Version, aligned.Matched));
                logger.LogInformation("Lyrics Sync timed {Song} ({Matched:P0} of its words heard)", Current, aligned.Matched);
            }

            LastError = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A song that cannot be read or decoded, or native libraries that do not load here: noted, never fatal.
            logger.LogWarning(e, "Lyrics Sync could not time {Song}", Current);
            LastError = $"{Current}: {e.Message}";
            if (song.Forced)
                timing.EndResync(song.TrackId);
            attempts.Record(song.TrackId, new Attempt(song.PlainHash, AttemptOutcome.Failed, time.GetUtcNow().UtcDateTime, LyricsAligner.Version,
                Error: e.Message));
        }
        finally
        {
            Current = null;
        }

        return TimeSpan.Zero;
    }

    /// <summary>Recognizes the words of a song without lyrics and saves them as a draft for review.</summary>
    private async Task<TimeSpan> TranscribeAsync(UntimedLyrics song, string ffmpegPath, LyricsSyncSettings current, CancellationToken cancellationToken)
    {
        transcription.Start(song.TrackId);
        try
        {
            var transcript = await RunLowAsync(() => recognizer.TranscribeAsync(song, ffmpegPath, current, cancellationToken), cancellationToken);
            var lines = LyricsTranscriber.Lines(transcript);
            var now = time.GetUtcNow().UtcDateTime;
            if (lines is null)
            {
                transcription.End(song.TrackId, TranscriptionOutcome.NoVoice);
                attempts.Record(song.TrackId, new Attempt(song.PlainHash, AttemptOutcome.Rejected, now, LyricsAligner.Version));
                logger.LogInformation("Lyrics Sync heard no singing in {Song}", Current);
            }
            else
            {
                await transcription.SaveDraftAsync(song.TrackId, lines, transcript.Language, $"Lyrics Sync · Whisper {current.Model}", cancellationToken);
                attempts.Record(song.TrackId, new Attempt(song.PlainHash, AttemptOutcome.Aligned, now, LyricsAligner.Version));
                logger.LogInformation("Lyrics Sync wrote the words of {Song} ({Lines} lines) for review", Current, lines.Count);
            }

            LastError = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Lyrics Sync could not recognize {Song}", Current);
            LastError = $"{Current}: {e.Message}";
            transcription.End(song.TrackId, TranscriptionOutcome.Failed);
            attempts.Record(song.TrackId, new Attempt(song.PlainHash, AttemptOutcome.Failed, time.GetUtcNow().UtcDateTime, LyricsAligner.Version,
                Error: e.Message));
        }
        finally
        {
            Current = null;
        }

        return TimeSpan.Zero;
    }

    private TimeSpan Set(WorkerState state, TimeSpan wait)
    {
        State = state;
        return wait;
    }

    /// <summary>
    /// The next song: one a user asked to re-sync (always tried, whatever was tried before), one a listener asked for,
    /// a video someone is watching, a song someone asked to have its words recognized (always tried), or in the
    /// background the next song left to time (<see cref="LyricsSyncSettings.Background"/>), then the next video, then
    /// the next song without lyrics (<see cref="LyricsSyncSettings.Transcribe"/>). Videos and recognitions are tried
    /// like songs (<see cref="AttemptStore"/>, by the video's or the song's id).
    /// </summary>
    private async Task<TimingWork?> NextAsync(LyricsSyncSettings current, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (timing.TakeRequested() is { Count: > 0 } asked)
        {
            foreach (var song in await timing.FindAsync(asked, cancellationToken))
                (song.Forced ? _resyncs : _requested).Enqueue(song);
        }
        if (videoTimings.TakeRequested() is { Count: > 0 } watched)
        {
            foreach (var video in await videoTimings.FindAsync(watched, cancellationToken))
                _requestedVideos.Enqueue(video);
        }
        if (transcription.TakeRequested() is { Count: > 0 } toRecognize)
        {
            foreach (var song in await transcription.FindAsync(toRecognize, cancellationToken))
                _requestedTranscriptions.Enqueue(song);
        }

        if (_resyncs.TryDequeue(out var resync))
            return new TimingWork(resync);

        while (_requested.TryDequeue(out var requested))
        {
            if (attempts.ShouldTry(requested, now))
                return new TimingWork(requested);
        }

        while (_requestedVideos.TryDequeue(out var requestedVideo))
        {
            var work = TimingWork.Of(requestedVideo);
            if (attempts.ShouldTry(work.Lyrics, now))
                return work;
        }

        // Asked for by a person: whatever was tried before.
        if (_requestedTranscriptions.TryDequeue(out var recognize))
            return TimingWork.Of(recognize);

        if (!current.Background && !current.Transcribe)
            return null;
        if (_phase == 0)
        {
            while (current.Background)
            {
                var page = await timing.PendingAsync(_cursor, PageSize, cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (var song in page)
                {
                    _cursor = song.TrackId;
                    if (attempts.ShouldTry(song, now))
                        return new TimingWork(song);
                }
            }

            _phase = 1;
        }

        if (_phase == 1)
        {
            while (current.Background)
            {
                var (videos, last) = await videoTimings.PendingAsync(_videoCursor, PageSize, cancellationToken);
                if (last is null)
                    break;
                foreach (var video in videos)
                {
                    _videoCursor = video.VideoId;
                    var work = TimingWork.Of(video);
                    if (attempts.ShouldTry(work.Lyrics, now))
                        return work;
                }

                _videoCursor = last;
            }

            _phase = 2;
        }

        while (current.Transcribe)
        {
            var page = await transcription.PendingAsync(_transcriptionCursor, PageSize, cancellationToken);
            if (page.Count == 0)
                break;
            foreach (var song in page)
            {
                _transcriptionCursor = song.TrackId;
                var work = TimingWork.Of(song);
                if (attempts.ShouldTry(work.Lyrics, now))
                    return work;
            }
        }

        // A whole pass done: the next one starts from the beginning.
        _cursor = null;
        _videoCursor = null;
        _transcriptionCursor = null;
        _phase = 0;
        return null;
    }
    /// <summary>On a dedicated below-normal thread: timing lyrics must never slow down streaming or the API.</summary>
    private static Task<T> RunLowAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            return work().GetAwaiter().GetResult();
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
