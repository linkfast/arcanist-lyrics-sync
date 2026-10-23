using System.Text.Json;
using Arcanist.Core.Application.Catalog;
using Arcanist.Core.Media;
using Arcanist.Extensibility;

namespace Arcanist.LyricsSync;

/// <summary>
/// The extension's page in Arcanist's admin panel (Extensions › Lyrics Sync): what it needs, how far it got, and how
/// it works.
/// </summary>
public sealed class LyricsSyncAdminPage(
    Components components,
    SettingsStore settings,
    AttemptStore attempts,
    AlignmentWorker worker,
    LyricsTiming timing,
    FfmpegLocator ffmpeg) : IExtensionAdminPage
{
    public async Task<AdminPage> GetAsync(AdminPageContext context, CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        var current = settings.Current;
        var missing = components.Missing(current);
        var hasFfmpeg = (await ffmpeg.GetAsync(cancellationToken))?.Path is not null;

        var status = new List<AdminBlock>();
        if (Components.Platform is null)
            status.Add(AdminBlock.Notice(t.Unsupported, AdminTone.Bad));
        if (components.Progress is { } progress)
            status.Add(AdminBlock.Progress(t.Downloading(progress.Name), progress.Total > 0 ? 100.0 * progress.Done / progress.Total : 0,
                $"{progress.Done / 1048576} / {progress.Total / 1048576} MB"));
        else if (missing.Count > 0)
            status.Add(AdminBlock.Status(t.Components, t.ToDownload(missing.Sum(c => c.Size)), AdminTone.Warning));
        else
            status.Add(AdminBlock.Status(t.Components, t.Ready, AdminTone.Good));
        if (components.LastError is { } downloadError)
            status.Add(AdminBlock.Notice(t.DownloadFailed(downloadError), AdminTone.Bad));
        status.Add(AdminBlock.Status("ffmpeg", hasFfmpeg ? t.FfmpegFromArcanist : t.NoFfmpeg, hasFfmpeg ? AdminTone.Good : AdminTone.Bad));
        status.Add(AdminBlock.Status(t.Worker, t.State(worker.State, worker.Current), worker.State switch
        {
            WorkerState.Working or WorkerState.Idle => AdminTone.Good,
            WorkerState.NotReady or WorkerState.NoFfmpeg => AdminTone.Warning,
            _ => AdminTone.Neutral,
        }));

        var counts = await timing.CountAsync(cancellationToken);
        var tried = attempts.Counts();
        status.Add(AdminBlock.Status(t.TimedLabel, t.Timed(counts.Timed, counts.WithLyrics, counts.Aligned)));
        status.Add(AdminBlock.Status(t.LeftLabel, t.Left(counts.Untimed, counts.Waiting), help: t.LeftHelp));
        if (tried.Rejected + tried.Failed > 0)
            status.Add(AdminBlock.Status(t.NotTimedLabel, t.NotTimed(tried.Rejected, tried.Failed), AdminTone.Neutral, t.NotTimedHelp));
        if (worker.LastError is { } error)
            status.Add(AdminBlock.Notice(error, AdminTone.Warning));

        var models = Enum.GetValues<SpeechModel>().Select(m => new AdminOption(m.ToString(), t.Model(m))).ToList();
        var actions = new List<AdminAction>();
        if (missing.Count > 0 && !components.Installing && Components.Platform is not null)
            actions.Add(new AdminAction("install", t.Install, AdminActionStyle.Primary));
        actions.Add(new AdminAction("retry", t.Retry));
        actions.Add(new AdminAction("clear", t.Clear, AdminActionStyle.Destructive, t.ClearConfirm));

        return new AdminPage(
        [
            new AdminSection("status", t.StatusTitle, t.StatusHelp, status, actions),
            new AdminSection("settings", t.SettingsTitle, null,
            [
                AdminBlock.Field("background", t.Background, AdminFieldType.Switch, current.Background, t.BackgroundHelp),
                AdminBlock.Field("separateVocals", t.Separate, AdminFieldType.Switch, current.SeparateVocals, t.SeparateHelp),
                AdminBlock.Field("model", t.ModelLabel, AdminFieldType.Select, current.Model.ToString(), t.ModelHelp, options: models),
                AdminBlock.Field("threads", t.Threads, AdminFieldType.Number, current.Threads, t.ThreadsHelp, min: 1, max: LyricsSyncSettings.MaxThreads),
            ]),
            new AdminSection("about", t.AboutTitle, null, [AdminBlock.Notice(t.About)]),
        ], RefreshSeconds: components.Installing || worker.State == WorkerState.Working ? 3 : null)
        {
            // Offered by the panel right after installing the extension, with the administrator's consent.
            Setup = missing.Count == 0 || components.Installing || Components.Platform is null
                ? null
                : new AdminSetup(t.SetupTitle, t.SetupDescription(missing.Sum(c => c.Size)), missing.Select(c => t.Item(c)).ToList(), "install", t.InstallNow),
        };
    }

    public Task<AdminPageResult> SaveAsync(AdminPageContext context, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        var current = settings.Current;
        bool? Switch(string key) => values.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        var model = current.Model;
        if (values.TryGetValue("model", out var chosen) && chosen.ValueKind == JsonValueKind.String && !Enum.TryParse(chosen.GetString(), out model))
            return Task.FromResult(AdminPageResult.Failed(t.Invalid, new Dictionary<string, string> { ["model"] = t.Invalid }));
        var saved = new LyricsSyncSettings(
            Switch("background") ?? current.Background,
            values.TryGetValue("threads", out var threads) && threads.ValueKind == JsonValueKind.Number ? threads.GetInt32() : current.Threads,
            Switch("separateVocals") ?? current.SeparateVocals,
            model).Normalized();
        settings.Save(saved);
        components.RemoveUnused(saved);
        // What the new settings need comes down now (the administrator already agreed to the extension's downloads).
        if (components.Missing(saved).Count > 0 && Components.Platform is not null)
            components.StartInstall(saved, worker.Restart);
        worker.Restart();
        return Task.FromResult(AdminPageResult.Done(t.Saved));
    }

    public async Task<AdminPageResult> RunAsync(AdminPageContext context, string action, IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        var t = Texts.For(context.Language);
        switch (action)
        {
            case "install":
                if (Components.Platform is null)
                    return AdminPageResult.Failed(t.Unsupported);
                components.StartInstall(settings.Current, worker.Restart);
                return AdminPageResult.Done(t.InstallStarted);
            case "retry":
                var forgotten = attempts.ForgetUnaligned();
                worker.Restart();
                return AdminPageResult.Done(t.Retrying(forgotten));
            case "clear":
                var cleared = await timing.ClearAsync(null, cancellationToken);
                attempts.ForgetAll();
                worker.Restart();
                return AdminPageResult.Done(t.Cleared(cleared));
            default:
                return AdminPageResult.Failed(t.Invalid);
        }
    }

    /// <summary>The page's words, in Spanish or English.</summary>
    private sealed record Texts(bool Es)
    {
        public static Texts For(string language) => new(language == "es");

        private string S(string es, string en) => Es ? es : en;

        private static string Megabytes(long bytes) => $"{Math.Round(bytes / 1048576.0)} MB";

        public string StatusTitle => S("Estado", "Status");
        public string StatusHelp => S(
            "Calcula aquí los tiempos de las letras que no los tienen, a partir de la propia canción: primero las que alguien está mirando y después, en segundo plano, todas las demás.",
            "Works out here the timing of lyrics that have none, from the song itself: first those someone is looking at, then, in the background, all the others.");
        public string Unsupported => S("Este servidor no puede ejecutarla: hace falta Windows, Linux (glibc 2.34 o más reciente) o macOS, de 64 bits.",
            "This server cannot run it: it needs 64-bit Windows, Linux (glibc 2.34 or newer) or macOS.");
        public string Components => S("Modelos y librerías", "Models and libraries");
        public string Ready => S("Descargados", "Downloaded");
        public string ToDownload(long bytes) => S($"Falta descargar {Megabytes(bytes)}", $"{Megabytes(bytes)} still to download");
        public string Downloading(string name) => S($"Descargando {name}", $"Downloading {name}");
        public string DownloadFailed(string error) => S($"No se pudo descargar: {error}", $"Could not download: {error}");
        public string FfmpegFromArcanist => S("El de Arcanist", "Arcanist's");
        public string NoFfmpeg => S("No disponible: hace falta para leer las canciones (Transcodificación)", "Not available: it is needed to read the songs (Transcoding)");
        public string Worker => S("Trabajo", "Work");
        public string State(WorkerState state, string? current) => state switch
        {
            WorkerState.Working => S($"Sincronizando {current}", $"Timing {current}"),
            WorkerState.Paused => S("En pausa mientras Arcanist escanea o alguien escucha", "Paused while Arcanist scans or someone listens"),
            WorkerState.NotReady => S("Esperando las descargas", "Waiting for the downloads"),
            WorkerState.NoFfmpeg => S("Esperando a ffmpeg", "Waiting for ffmpeg"),
            WorkerState.Idle => S("Al día", "Up to date"),
            _ => S("Apagado", "Off"),
        };
        public string TimedLabel => S("Con tiempos", "Timed");
        public string Timed(int timed, int all, int aligned) =>
            S($"{timed} de {all} letras ({aligned} calculadas aquí)", $"{timed} of {all} lyrics ({aligned} worked out here)");
        public string LeftLabel => S("Por sincronizar", "Left to time");
        public string Left(int untimed, int waiting) => waiting > 0
            ? S($"{untimed}, y {waiting} esperando a LRCLib o sin copia en este servidor", $"{untimed}, and {waiting} waiting for LRCLib or with no copy on this server")
            : untimed.ToString();
        public string LeftHelp => S("Con LRCLib activado, sus tiempos (hechos por personas) van primero: aquí solo se calculan los que LRCLib no tiene.",
            "With LRCLib on, its timing (made by people) comes first: only what LRCLib does not have is worked out here.");
        public string NotTimedLabel => S("Sin sincronizar", "Not timed");
        public string NotTimed(int rejected, int failed) =>
            S($"{rejected} no coinciden con lo que se canta, {failed} con errores", $"{rejected} do not match the singing, {failed} with errors");
        public string NotTimedHelp => S("Letras de otra versión o idioma, música instrumental o en vivo. No se vuelven a intentar salvo que cambie la letra o pulses «Reintentar».",
            "Lyrics of another version or language, instrumental or live music. Not tried again unless the lyrics change or you press “Try again”.");
        public string Install => S("Descargar", "Download");
        public string InstallStarted => S("Descargando en segundo plano", "Downloading in the background");
        public string Retry => S("Reintentar las no sincronizadas", "Try the untimed ones again");
        public string Retrying(int count) => S($"{count} canciones se volverán a intentar", $"{count} songs will be tried again");
        public string Clear => S("Borrar los tiempos calculados", "Delete the timing worked out");
        public string ClearConfirm => S("Se borran los tiempos calculados aquí (no los de los archivos ni los de LRCLib) y se vuelven a calcular.",
            "The timing worked out here is deleted (not the files' or LRCLib's) and worked out again.");
        public string Cleared(int count) => S($"{count} letras sin tiempos otra vez", $"{count} lyrics without timing again");

        public string SettingsTitle => S("Ajustes", "Settings");
        public string Background => S("Sincronizar en segundo plano", "Time in the background");
        public string BackgroundHelp => S("Apagado, solo se sincronizan las letras que alguien abre en un reproductor.",
            "When off, only the lyrics someone opens in a player are timed.");
        public string Separate => S("Separar la voz de la música", "Separate the voice from the music");
        public string SeparateHelp => S("Mucho más preciso, sobre todo con música fuerte; tarda unos segundos más por canción y descarga 60 MB.",
            "Much more accurate, above all with loud music; takes a few more seconds per song and downloads 60 MB.");
        public string ModelLabel => S("Modelo de reconocimiento", "Recognition model");
        public string ModelHelp => S("Whisper small, multilingüe. La versión ligera va mejor en máquinas lentas (Raspberry Pi).",
            "Whisper small, multilingual. The light one suits slow machines (Raspberry Pi).");
        public string Model(SpeechModel model) => model switch
        {
            SpeechModel.SmallLight => S("Small ligero (190 MB)", "Small light (190 MB)"),
            _ => S("Small (264 MB, recomendado)", "Small (264 MB, recommended)"),
        };
        public string Threads => S("Hilos", "Threads");
        public string ThreadsHelp => S("Núcleos que usa para cada canción, siempre con prioridad baja.", "Cores used for each song, always at low priority.");
        public string Saved => S("Guardado", "Saved");
        public string Invalid => S("Valor no válido", "Invalid value");

        public string AboutTitle => S("Cómo funciona", "How it works");
        public string About => S(
            "Todo ocurre en este servidor: la canción no sale de él. ffmpeg la decodifica, Spleeter separa la voz, Whisper reconoce las palabras con el momento en que se cantan y se emparejan con la letra; cada línea toma el momento de su primera palabra. Si no coincide al menos la mitad de la letra, no se guarda nada. Los modelos (Whisper de OpenAI y Spleeter de Deezer, ambos MIT) se descargan de Hugging Face y las librerías de NuGet, comprobados con su SHA-256.",
            "Everything happens on this server: the song never leaves it. ffmpeg decodes it, Spleeter separates the voice, Whisper recognizes the words with the moment they are sung and they are matched to the lyrics; each line takes the moment of its first word. If less than half the lyrics match, nothing is saved. The models (OpenAI's Whisper and Deezer's Spleeter, both MIT) come from Hugging Face and the libraries from NuGet, checked against their SHA-256.");

        public string SetupTitle => S("Descargar lo que necesita Lyrics Sync", "Download what Lyrics Sync needs");
        public string SetupDescription(long bytes) => S(
            $"Para sincronizar las letras en este servidor hacen falta un modelo de reconocimiento de voz y sus librerías ({Megabytes(bytes)}).",
            $"Timing lyrics on this server needs a speech recognition model and its libraries ({Megabytes(bytes)}).");
        public string Item(Component component) => $"{component.Name} ({Megabytes(component.Size)})";
        public string InstallNow => S("Descargar ahora", "Download now");
    }
}
