using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Arcanist.LyricsSync;

/// <summary>
/// Something the extension downloads when the administrator sets it up: a model, or the native libraries of this
/// machine from a NuGet package (only the folder <paramref name="Extract"/> of it, into <paramref name="Target"/>).
/// Everything is checked against its SHA-256 before it is used.
/// </summary>
/// <param name="Target">Under the components folder: the file of a model, the folder of a package's libraries.</param>
public sealed record Component(string Id, string Name, string Url, string Sha256, long Size, string Target, string? Extract = null);

/// <summary>What this machine is, as the native libraries name it.</summary>
public sealed record NativePlatform(string Rid, string WhisperFolder, bool NoAvx);

/// <summary>What the extension needs, and downloading it.</summary>
public sealed class Components(ExtensionPaths paths, IHttpClientFactory http, ILogger<Components> logger)
{
    public const string HttpClientName = "lyrics-sync";

    private const string WhisperModels = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    private const string SpleeterModels = "https://huggingface.co/csukuangfj/sherpa-onnx-spleeter-2stems-fp16/resolve/93ba771920ade509f8cbd6825b1a90856c797e08/";
    private const string NuGet = "https://api.nuget.org/v3-flatcontainer/";

    private static readonly Dictionary<string, (string Sha256, long Size)> SherpaRuntimes = new()
    {
        ["win-x64"] = ("2257c06893d1d90792ea3157606de33a51122d935b6913bed5442d2ffb9e9bd7", 8535869),
        ["win-arm64"] = ("592463d28f93f6e0946acedfb98b140730448c54016f54e2df3ed382637d9b33", 8142361),
        ["linux-x64"] = ("65cf697ca95abfbd01dde3b015ede032880ac5fccff931aa6737cfe542c04e76", 10794309),
        ["linux-arm64"] = ("c1fd7628eddb84837f459c95bd9c41a8c3dc22ef1001b18f04f6c573c1d1d054", 13638800),
        ["osx-arm64"] = ("ce4c79d88f1a0aa0fe877f6acbfa4204842eeefc79750707b2fdd8b1e6a90bf8", 9824946),
        ["osx-x64"] = ("8b482b0d58ef651f1b97cd573112f95a0374a9db9bd010453030c999f757e14e", 11011142),
    };

    private readonly Lock _gate = new();
    private Task? _installing;

    /// <summary>The platform, or null where there are no native libraries for it (32-bit, musl…).</summary>
    public static NativePlatform? Platform { get; } = DetectPlatform();

    /// <summary>The download in progress: what, bytes so far and in all; null when none.</summary>
    public (string Name, long Done, long Total)? Progress { get; private set; }

    public string? LastError { get; private set; }

    public bool Installing
    {
        get
        {
            lock (_gate)
                return _installing is { IsCompleted: false };
        }
    }

    public string WhisperRoot => Path.Combine(paths.Components, "whisper");

    public string SherpaRoot => Path.Combine(paths.Components, "sherpa");

    public string ModelPath(SpeechModel model) => Path.Combine(paths.Components, Model(model).Target);

    public string VocalsPath => Path.Combine(paths.Components, "spleeter", "vocals.fp16.onnx");

    public string AccompanimentPath => Path.Combine(paths.Components, "spleeter", "accompaniment.fp16.onnx");

    /// <summary>The model files of each choice.</summary>
    public static Component Model(SpeechModel model) => model switch
    {
        SpeechModel.SmallLight => new Component("model-small-q5_1", "Whisper small (5-bit)", WhisperModels + "ggml-small-q5_1.bin",
            "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", 190085487, "models/ggml-small-q5_1.bin"),
        _ => new Component("model-small-q8_0", "Whisper small (8-bit)", WhisperModels + "ggml-small-q8_0.bin",
            "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f", 264464607, "models/ggml-small-q8_0.bin"),
    };

    /// <summary>Everything this machine needs with these settings (empty where it cannot run).</summary>
    public static IReadOnlyList<Component> Needed(LyricsSyncSettings settings)
    {
        if (Platform is not { } platform)
            return [];
        var list = new List<Component>
        {
            platform.NoAvx
                ? new Component("whisper-noavx", "Whisper (CPU without AVX)", NuGet + "whisper.net.runtime.noavx/1.9.1/whisper.net.runtime.noavx.1.9.1.nupkg",
                    "42b68b3970522e00eb7e9f9a006ad0055a4bdf741eb318698587e18967c90582", 2531679,
                    $"whisper/runtimes/noavx/{platform.WhisperFolder}", $"build/{platform.WhisperFolder}/")
                : new Component("whisper", "Whisper", NuGet + "whisper.net.runtime/1.9.1/whisper.net.runtime.1.9.1.nupkg",
                    "b5224f0dad44d5eb8233e5d83f4333a8a3fccadc77095f50f361f06d65e0736b", 18482556,
                    $"whisper/runtimes/{platform.WhisperFolder}", $"build/{platform.WhisperFolder}/"),
            Model(settings.Model),
        };
        if (settings.SeparateVocals)
        {
            var (sha, size) = SherpaRuntimes[platform.Rid];
            list.Add(new Component("sherpa", "ONNX Runtime (sherpa-onnx)",
                NuGet + $"org.k2fsa.sherpa.onnx.runtime.{platform.Rid}/1.13.8/org.k2fsa.sherpa.onnx.runtime.{platform.Rid}.1.13.8.nupkg",
                sha, size, "sherpa", $"runtimes/{platform.Rid}/native/"));
            list.Add(new Component("spleeter-vocals", "Spleeter (voice)", SpleeterModels + "vocals.fp16.onnx",
                "24cef84aedcd1fe87c0b743ef3370ad34dc1fabf6c9014d6128a75a538c7b668", 19681017, "spleeter/vocals.fp16.onnx"));
            list.Add(new Component("spleeter-accompaniment", "Spleeter (music)", SpleeterModels + "accompaniment.fp16.onnx",
                "d14cea55793cc531a5875f5f4da08207d1c5ab9292e8e0099a104eecb014fcc0", 19681024, "spleeter/accompaniment.fp16.onnx"));
        }

        return list;
    }

    public bool IsInstalled(Component component) =>
        File.Exists(Marker(component)) && File.ReadAllText(Marker(component)).Trim() == component.Sha256;

    /// <summary>What is still to download for these settings.</summary>
    public IReadOnlyList<Component> Missing(LyricsSyncSettings settings) => Needed(settings).Where(c => !IsInstalled(c)).ToList();

    /// <summary>Starts downloading what is missing, in the background; false when a download is already running.</summary>
    public bool StartInstall(LyricsSyncSettings settings, Action? done = null)
    {
        lock (_gate)
        {
            if (_installing is { IsCompleted: false })
                return false;
            LastError = null;
            _installing = Task.Run(async () =>
            {
                try
                {
                    foreach (var component in Missing(settings))
                        await InstallAsync(component, CancellationToken.None);
                }
                catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or TaskCanceledException
                                              or UnauthorizedAccessException)
                {
                    logger.LogWarning(e, "Could not download the components of Lyrics Sync");
                    LastError = e.Message;
                }
                finally
                {
                    Progress = null;
                    done?.Invoke();
                }
            });
            return true;
        }
    }

    /// <summary>Downloads one component, checks it and puts it in place.</summary>
    public async Task InstallAsync(Component component, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Components);
        var download = Path.Combine(paths.Components, $".download-{component.Id}");
        try
        {
            using (var client = http.CreateClient(HttpClientName))
            using (var response = await client.GetAsync(component.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? component.Size;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(download);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                Progress = (component.Name, 0, total);
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    Progress = (component.Name, done, total);
                }

                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != component.Sha256)
                    throw new InvalidDataException($"{component.Name}: the download does not match its SHA-256.");
            }

            var target = Path.Combine(paths.Components, component.Target);
            if (component.Extract is { } prefix)
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(target);
                using (var zip = ZipFile.OpenRead(download))
                {
                    foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
                    {
                        // Only the files right in that folder: nothing can land outside the target.
                        if (entry.FullName[prefix.Length..].Contains('/'))
                            continue;
                        entry.ExtractToFile(Path.Combine(target, entry.Name), overwrite: true);
                    }
                }

                File.Delete(download);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(download, target, overwrite: true);
            }

            File.WriteAllText(Marker(component), component.Sha256);
            logger.LogInformation("Lyrics Sync downloaded {Component}", component.Name);
        }
        finally
        {
            if (File.Exists(download))
                File.Delete(download);
        }
    }

    /// <summary>Removes what the current settings no longer use (the other model, the separation when it is off).</summary>
    public void RemoveUnused(LyricsSyncSettings settings)
    {
        if (Installing)
            return;
        var needed = Needed(settings).Select(c => c.Id).ToHashSet();
        foreach (var model in Enum.GetValues<SpeechModel>().Select(Model).Where(c => !needed.Contains(c.Id)))
            Delete(model);
        if (!settings.SeparateVocals)
        {
            foreach (var id in new[] { "spleeter-vocals", "spleeter-accompaniment" })
            {
                var marker = Path.Combine(paths.Components, $".{id}.sha256");
                if (File.Exists(marker))
                    File.Delete(marker);
            }

            TryDelete(Path.Combine(paths.Components, "spleeter"));
        }
    }

    private void Delete(Component component)
    {
        if (File.Exists(Marker(component)))
            File.Delete(Marker(component));
        var target = Path.Combine(paths.Components, component.Target);
        if (File.Exists(target))
            File.Delete(target);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // In use until the next start.
        }
    }

    private string Marker(Component component) => Path.Combine(paths.Components, $".{component.Id}.sha256");

    private static NativePlatform? DetectPlatform()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch is null)
            return null;
        var (os, whisperOs) = OperatingSystem.IsWindows() ? ("win", "win")
            : OperatingSystem.IsMacOS() ? ("osx", "macos")
            : OperatingSystem.IsLinux() && !IsMusl() ? ("linux", "linux")
            : (null, null);
        if (os is null)
            return null;
        // Whisper's usual build needs AVX2 (and FMA, F16C) on x64; older CPUs (some NAS) get the build without.
        var noAvx = arch == "x64" && !(Avx2.IsSupported && Fma.IsSupported);
        return new NativePlatform($"{os}-{arch}", $"{whisperOs}-{arch}", noAvx);
    }

    private static bool IsMusl() => RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal);
}
