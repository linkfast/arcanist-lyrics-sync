using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcanist.LyricsSync;

/// <summary>The Whisper models the administrator can choose: smaller ones are faster and a little less accurate.</summary>
public enum SpeechModel
{
    /// <summary>Whisper small, 8-bit (264 MB): nearly as accurate as the full model, at half its size.</summary>
    Small,
    /// <summary>Whisper small, 5-bit (190 MB): for slow machines such as a Raspberry Pi.</summary>
    SmallLight,
}

/// <param name="Background">Times every song that needs it, in the background; when off, only the songs listeners look at.</param>
/// <param name="Threads">CPU threads for the work of one song (one song at a time).</param>
/// <param name="SeparateVocals">Takes the voice out of the music before recognizing it: slower, much more accurate.</param>
public sealed record LyricsSyncSettings(bool Background, int Threads, bool SeparateVocals, SpeechModel Model)
{
    public const int MaxThreads = 16;

    public static LyricsSyncSettings Default => new(true, Math.Clamp(Environment.ProcessorCount / 2, 1, 4), true, SpeechModel.Small);

    public LyricsSyncSettings Normalized() => this with { Threads = Math.Clamp(Threads, 1, MaxThreads) };
}

/// <summary>The extension's settings, kept in its data folder.</summary>
public sealed class SettingsStore(ExtensionPaths paths)
{
    private readonly Lock _gate = new();
    private LyricsSyncSettings? _current;

    public LyricsSyncSettings Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is not null)
                    return _current;
                try
                {
                    _current = File.Exists(paths.SettingsFile)
                        ? (JsonSerializer.Deserialize<LyricsSyncSettings>(File.ReadAllText(paths.SettingsFile), Json.Options) ?? LyricsSyncSettings.Default).Normalized()
                        : LyricsSyncSettings.Default;
                }
                catch (JsonException)
                {
                    _current = LyricsSyncSettings.Default;
                }

                return _current;
            }
        }
    }

    public void Save(LyricsSyncSettings settings)
    {
        lock (_gate)
        {
            Files.WriteAllTextAtomically(paths.SettingsFile, JsonSerializer.Serialize(settings, Json.Options));
            _current = settings;
        }
    }
}

/// <summary>Where the extension keeps its things, inside its data folder.</summary>
public sealed class ExtensionPaths(string root)
{
    public string Root { get; } = root;

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string AttemptsFile => Path.Combine(Root, "attempts.json");

    /// <summary>The downloaded models and native libraries.</summary>
    public string Components => Path.Combine(Root, "components");

    public string Work => Path.Combine(Root, "work");
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
}

internal static class Files
{
    /// <summary>Writes beside the file and then replaces it, so a crash never leaves half a file.</summary>
    public static void WriteAllTextAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
