using System.Text.Json;
using Arcanist.Core.Application.Catalog;
using Arcanist.Core.Application.Scanning;
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
/// Times the plain lyrics Arcanist hands out (<see cref="LyricsTiming"/>), one song at a time on a low-priority
/// thread: first the songs a listener is looking at, then (with <see cref="LyricsSyncSettings.Background"/>) all the
/// others. It waits while Arcanist scans or someone is listening.
/// </summary>
public sealed class AlignmentWorker(
    LyricsTiming timing,
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
    private readonly Queue<UntimedLyrics> _requested = new();
    private Guid? _cursor;

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
        }
    }

    /// <summary>Does one song (or decides to wait); returns how long to wait before the next.</summary>
    internal async Task<TimeSpan> RunOnceAsync(ExtensionHost host, ScanEvents scans, StreamActivity streams, FfmpegLocator ffmpeg,
        CancellationToken cancellationToken)
    {
        var current = settings.Current;
        if (!host.IsEnabled(LyricsSyncExtension.Id))
            return Set(WorkerState.Off, Rest);
        if (recognizer.NeedsSetup && (Components.Platform is null || components.Missing(current).Count > 0))
            return Set(WorkerState.NotReady, Rest);
        var ffmpegPath = recognizer.NeedsSetup ? (await ffmpeg.GetAsync(cancellationToken))?.Path : "";
        if (ffmpegPath is null)
            return Set(WorkerState.NoFfmpeg, Rest);
        if (scans.HasActiveJobs || streams.Active > 0)
            return Set(WorkerState.Paused, Pause);

        if (await NextAsync(current.Background, cancellationToken) is not { } song)
            return Set(WorkerState.Idle, Rest);

        State = WorkerState.Working;
        Current = song.Artist is null ? song.Title : $"{song.Artist} · {song.Title}";
        try
        {
            var aligned = await RunLowAsync(() => recognizer.RecognizeAsync(song, ffmpegPath, current, cancellationToken), cancellationToken) is { } heard
                ? LyricsAligner.Align(song.Plain, heard)
                : null;
            var now = time.GetUtcNow().UtcDateTime;
            if (aligned is null)
            {
                attempts.Record(song.TrackId, new Attempt(song.PlainHash, AttemptOutcome.Rejected, now, LyricsAligner.Version));
                logger.LogInformation("Lyrics Sync could not match the lyrics of {Song} to its singing", Current);
            }
            else
            {
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

    /// <summary>The next song: one a listener asked for, or (in the background) the next one left.</summary>
    private async Task<UntimedLyrics?> NextAsync(bool background, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (timing.TakeRequested() is { Count: > 0 } asked)
        {
            foreach (var song in await timing.FindAsync(asked, cancellationToken))
                _requested.Enqueue(song);
        }

        while (_requested.TryDequeue(out var requested))
        {
            if (attempts.ShouldTry(requested, now))
                return requested;
        }

        if (!background)
            return null;
        while (true)
        {
            var page = await timing.PendingAsync(_cursor, PageSize, cancellationToken);
            if (page.Count == 0)
            {
                // A whole pass done: the next one starts from the beginning.
                _cursor = null;
                return null;
            }

            foreach (var song in page)
            {
                _cursor = song.TrackId;
                if (attempts.ShouldTry(song, now))
                    return song;
            }
        }
    }

    /// <summary>On a dedicated below-normal thread: timing lyrics must never slow down streaming or the API.</summary>
    private static Task<T> RunLowAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            return work().GetAwaiter().GetResult();
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
