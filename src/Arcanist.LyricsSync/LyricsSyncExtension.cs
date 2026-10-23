using Arcanist.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcanist.LyricsSync;

/// <summary>
/// Lyrics Sync for Arcanist: works out on the server the timing of plain lyrics that have none (and that LRCLib does not
/// have), from the song itself. Players get it like any other timing, through Arcanist's lyrics API.
/// </summary>
public sealed class LyricsSyncExtension : IArcanistExtension
{
    public const string Id = "lyrics-sync";

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton(new ExtensionPaths(context.DataDirectory));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<AttemptStore>();
        services.AddSingleton<Components>();
        // Transcripts read from files instead of Whisper, for tests and demonstrations.
        if (context.Configuration["Transcripts"] is { Length: > 0 } transcripts)
            services.AddSingleton<ISpeechRecognizer>(new ReplayRecognizer(transcripts));
        else
            services.AddSingleton<ISpeechRecognizer, WhisperRecognizer>();
        services.AddSingleton<AlignmentWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<AlignmentWorker>());
        services.AddKeyedScoped<IExtensionAdminPage, LyricsSyncAdminPage>(context.Id);
        services.AddHttpClient(Components.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Arcanist-LyricsSync/" + Versions.Of(typeof(LyricsSyncExtension).Assembly));
            client.Timeout = TimeSpan.FromMinutes(30);
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ExtensionContext context)
    {
        endpoints.MapGet(context.BasePath + "/icon.svg", () =>
        {
            var stream = typeof(LyricsSyncExtension).Assembly.GetManifestResourceStream("icon.svg")!;
            return Results.Stream(stream, "image/svg+xml");
        });
        endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<LyricsSyncExtension>()
            .LogDebug("Lyrics Sync ready on this {Platform}", Components.Platform?.Rid ?? "unsupported platform");
    }
}
