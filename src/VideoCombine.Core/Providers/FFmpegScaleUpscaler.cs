namespace VideoCombine.Core.Providers;

using VideoCombine.Core.External;
using VideoCombine.Core.Pipeline;

/// <summary>
/// Найпростіший апскейлер (розділ 3.5): звичайне масштабування FFmpeg із
/// lanczos-фільтром, БЕЗ AI. Завжди працює — жодних зовнішніх бінарників понад
/// ffmpeg, що вже потрібен усюди. Локальний і синхронний (IsCloud=false).
///
/// Свідомо чесно: lanczos лише збільшує роздільність, нових деталей не вигадує —
/// якість майже та сама, що дає масштабування у склейці. Цінність цього модуля —
/// прокласти патерн провайдера (розділ 3.5): наступний бекенд (Real-ESRGAN,
/// хмара) реалізує той самий IUpscaleProvider, і решта пайплайну не міняється.
/// </summary>
public sealed class FFmpegScaleUpscaler : IUpscaleProvider
{
    private readonly ProcessRunner _runner;
    private readonly string _ffmpegPath;

    public FFmpegScaleUpscaler(ProcessRunner runner, string ffmpegPath)
    {
        _runner = runner;
        _ffmpegPath = ffmpegPath;
    }

    public string Id => "ffmpeg-scale";
    public string DisplayName => "FFmpeg-scale (lanczos)";

    // Локальний CPU-фільтр: рахує тут і зараз, статус не опитуємо.
    public bool IsCloud => false;

    public async Task<string> UpscaleAsync(
        string inputPath,
        UpscaleOptions options,
        IProgressReporter progress,
        CancellationToken token)
    {
        // Вихід кладемо поряд із входом із суфіксом множника — щоб не затирати
        // оригінал (раптом крок впаде на півдорозі) і щоб шлях був прозорий у логах.
        var dir = Path.GetDirectoryName(inputPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var outputPath = Path.Combine(dir, $"{name}_up{options.Scale}{ext}");

        var args = BuildScaleArgs(inputPath, outputPath, options.Scale);

        var code = await _runner.RunAsync(_ffmpegPath, args, null, token);
        if (code != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException($"ffmpeg scale код {code} для {Path.GetFileName(inputPath)}");

        return outputPath;
    }

    /// <summary>
    /// Аргументи ffmpeg для масштабування у scale разів. Винесено окремо й публічно
    /// — для тесту (як BuildVideoCodecArgs у склейці).
    ///
    /// scale=trunc(iw*N/2)*2 : trunc(ih*N/2)*2 — множимо розмір на N і ПРИВОДИМО
    /// до парного: libx264 (yuv420p) вимагає парних ширини/висоти, інакше падає
    /// "height not divisible by 2". flags=lanczos — якісніший фільтр за дефолтний.
    /// Відео перекодовуємо (libx264, crf 18 — близько до візуально без втрат),
    /// аудіо копіюємо без змін (-c:a copy).
    /// </summary>
    public static IReadOnlyList<string> BuildScaleArgs(string inputPath, string outputPath, int scale)
    {
        return new[]
        {
            "-i", inputPath,
            "-vf", $"scale=trunc(iw*{scale}/2)*2:trunc(ih*{scale}/2)*2:flags=lanczos",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18",
            "-c:a", "copy",
            "-y", outputPath
        };
    }
}

/// <summary>Налаштування апскейлу (серіалізуються в JSON-рецепт).</summary>
public sealed class UpscaleSettings
{
    /// <summary>
    /// Чи виконувати крок узагалі. Апскейл — перший ОПЦІЙНИЙ крок (на відміну від
    /// Завантаження/Аналіз/Нарізка/Склейка): вимкнено — крок не вставляється в ланцюг.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Який бекенд апскейлу (розділ 3.5). Поки лише "ffmpeg-scale".</summary>
    public string Provider { get; set; } = "ffmpeg-scale";

    /// <summary>У скільки разів збільшити роздільність (2 = вдвічі по кожній осі).</summary>
    public int Scale { get; set; } = 2;
}
