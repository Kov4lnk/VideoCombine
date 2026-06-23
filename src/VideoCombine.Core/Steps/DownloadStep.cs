namespace VideoCombine.Core.Steps;

using VideoCombine.Core.External;
using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;

/// <summary>
/// Крок завантаження (yt-dlp). Бере список URL із налаштувань, качає кожне
/// в робочу папку, додає SourceVideo у контекст.
///
/// Паралельність обмежена мережею (розділ 8.3): 2-4 одночасно.
/// Дрібний збій (одне биті/недоступне посилання) — пропускаємо й логуємо,
/// не валимо весь прогон (розділ 7.1).
/// </summary>
public sealed class DownloadStep : IStep
{
    private readonly ProcessRunner _runner;
    private readonly DownloadSettings _settings;

    public DownloadStep(ProcessRunner runner, DownloadSettings settings)
    {
        _runner = runner;
        _settings = settings;
    }

    public string TypeId => "download";
    public string DisplayName => "Завантаження";

    // Мережа: більше потоків не прискорить і дратує сервери (розділ 8.3).
    public int MaxConcurrency => 4;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();
        if (_settings.Urls.Count == 0)
            reqs.Add(new StepRequirement("Немає посилань для завантаження", IsBlocking: true));
        if (string.IsNullOrWhiteSpace(context.Settings.YtDlpPath))
            reqs.Add(new StepRequirement("Не вказано шлях до yt-dlp", IsBlocking: true));
        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        var limit = Math.Min(MaxConcurrency, context.Settings.MaxDownloadConcurrency);
        using var gate = new SemaphoreSlim(limit);

        int done = 0;
        var total = _settings.Urls.Count;
        var lockObj = new object();

        var tasks = _settings.Urls.Select(async url =>
        {
            await gate.WaitAsync(token);
            try
            {
                var source = await DownloadOneAsync(url, context, progress, token);
                if (source is not null)
                {
                    lock (lockObj) context.Sources.Add(source);
                    progress.Log(LogLevel.Success, $"Завантажено: {source.Title ?? source.Id}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Одне погане посилання не валить решту (розділ 7.1).
                progress.Log(LogLevel.Warning, $"{url}: пропущено ({ex.Message})");
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress.ReportStepProgress($"{n}/{total} посилань", (double)n / total);
            }
        });

        await Task.WhenAll(tasks);

        if (context.Sources.Count == 0)
            throw new InvalidOperationException("Жодне посилання не вдалося завантажити");
    }

    private async Task<SourceVideo?> DownloadOneAsync(
        string url, PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        // %(id)s — стабільний ідентифікатор від платформи; розширення обирає yt-dlp.
        var outputTemplate = Path.Combine(context.WorkingDirectory, "%(id)s.%(ext)s");

        // --print after_move:filepath друкує ТОЧНИЙ фінальний шлях після завантаження.
        // --no-simulate потрібен, щоб --print не вимкнув саме завантаження.
        var args = new List<string>
        {
            "--no-playlist",
            "-f", _settings.BuildFormatSelector(),
            "-o", outputTemplate,
            "--print", "after_move:filepath",
            "--no-simulate",
            "--no-warnings",
            url
        };

        string? finalPath = null;
        string? title = null;

        await _runner.RunAsync(context.Settings.YtDlpPath, args, line =>
        {
            // Перший рядок, що схожий на існуючий шлях, — це filepath від --print.
            var trimmed = line.Trim();
            if (finalPath is null && LooksLikePath(trimmed))
                finalPath = trimmed;
            else if (trimmed.StartsWith("[download] Destination:", StringComparison.OrdinalIgnoreCase))
                progress.ReportStepProgress(trimmed, 0); // живий рядок у деталь кроку
        }, token);

        if (finalPath is null || !File.Exists(finalPath))
            throw new InvalidOperationException("yt-dlp не повернув шлях до файлу");

        var id = Path.GetFileNameWithoutExtension(finalPath);
        title = id;

        return new SourceVideo
        {
            Id = id,
            FilePath = finalPath,
            SourceUrl = url,
            Title = title
        };
    }

    private static bool LooksLikePath(string s) =>
        s.Length > 3 && (s.Contains(Path.DirectorySeparatorChar) || s.Contains('/'))
        && !s.StartsWith('[');
}

/// <summary>Налаштування завантаження (серіалізуються в JSON-рецепт).</summary>
public sealed class DownloadSettings
{
    /// <summary>Посилання на відео для завантаження.</summary>
    public List<string> Urls { get; set; } = new();

    /// <summary>Бажана якість: "best" | "1080p" | "720p" | "480p".</summary>
    public string Quality { get; set; } = "best";

    /// <summary>
    /// Перетворює бажану якість у формат-селектор yt-dlp.
    /// "best" — найкраще відео+аудіо; "720p" — не вище 720 по висоті.
    /// </summary>
    public string BuildFormatSelector()
    {
        if (Quality.Equals("best", StringComparison.OrdinalIgnoreCase))
            return "bv*+ba/b";

        var digits = new string(Quality.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var height) && height > 0)
            return $"bv*[height<={height}]+ba/b[height<={height}]";

        return "bv*+ba/b";
    }
}
