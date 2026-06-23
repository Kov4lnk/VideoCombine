namespace VideoCombine.Core.Providers;

using VideoCombine.Core.Pipeline;

/// <summary>
/// ПАТЕРН ПРОВАЙДЕРА (розділ 3.5). Спільний інтерфейс "дай відео — поверну більше".
/// Реалізації: FFmpegScaleUpscaler, RealEsrganUpscaler (локальні, синхронні),
/// CloudApiUpscaler (Topaz/WaveSpeed/... — асинхронні, poll).
/// Той самий патерн застосовується до TTS, LLM, генерації медіа.
/// </summary>
public interface IUpscaleProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>true — рахує локально й синхронно; false — хмарний, опитує статус (розділ 3.5).</summary>
    bool IsCloud { get; }

    /// <summary>Апскейлити один файл. Хмарні реалізації всередині роблять poll job-id.</summary>
    Task<string> UpscaleAsync(
        string inputPath,
        UpscaleOptions options,
        IProgressReporter progress,
        CancellationToken token);
}

public sealed record UpscaleOptions(int Scale, string? TargetResolution);
