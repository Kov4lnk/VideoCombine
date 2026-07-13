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

    // Крок збирає джерела двох типів: завантажені з URL і локальні файли з диска.
    public string DisplayName => "Джерела";

    // Мережа: більше потоків не прискорить і дратує сервери (розділ 8.3).
    public int MaxConcurrency => 4;

    public IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context)
    {
        var reqs = new List<StepRequirement>();
        // Має бути хоч щось: або посилання, або локальні файли.
        if (_settings.Urls.Count == 0 && _settings.LocalFiles.Count == 0)
            reqs.Add(new StepRequirement("Немає джерел (посилань або локальних файлів)", IsBlocking: true));
        // yt-dlp потрібен ЛИШЕ для завантаження з URL; для самих локальних — ні.
        if (_settings.Urls.Count > 0 && string.IsNullOrWhiteSpace(context.Settings.YtDlpPath))
            reqs.Add(new StepRequirement("Не вказано шлях до yt-dlp", IsBlocking: true));
        return reqs;
    }

    public async Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        // 1) Локальні файли — у джерела напряму, без завантаження (вже на диску).
        foreach (var path in _settings.LocalFiles)
        {
            var source = MakeLocalSource(path);
            if (source is not null)
            {
                context.Sources.Add(source);
                progress.Log(LogLevel.Success, $"Локальний файл: {source.Title ?? source.Id}");
            }
            else
            {
                progress.Log(LogLevel.Warning, $"Локальний файл пропущено (не знайдено): {path}");
            }
        }

        // 2) Завантаження з URL (паралельно, з лімітом мережі).
        var limit = Math.Min(MaxConcurrency, context.Settings.MaxDownloadConcurrency);
        using var gate = new SemaphoreSlim(limit);

        int done = 0;
        var total = _settings.Urls.Count;

        // Паралельні задачі фінішують ВРОЗБІЙ — тож не додаємо в Sources у міру
        // завершення (це дало б випадковий порядок джерел щопрогону, а CutStep
        // сортує сегменти за індексом джерела → фінальне відео склеювалось би
        // непередбачувано, навіть у режимі "sequential"). Замість цього кожен
        // результат кладемо у слот за індексом його URL, а в Sources вливаємо
        // в порядку списку URL — так "sequential" знову відтворюваний.
        var slots = new SourceVideo?[total];

        var tasks = _settings.Urls.Select(async (url, i) =>
        {
            await gate.WaitAsync(token);
            try
            {
                var source = await DownloadOneAsync(url, context, progress, token);
                if (source is not null)
                {
                    slots[i] = source;
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
                if (total > 0)
                    progress.ReportStepProgress($"{n}/{total} посилань", (double)n / total);
            }
        });

        await Task.WhenAll(tasks);

        // Вливаємо в детермінованому порядку URL (пропущені лишаються null).
        foreach (var source in slots)
            if (source is not null)
                context.Sources.Add(source);

        if (context.Sources.Count == 0)
            throw new InvalidOperationException("Жодне джерело не вдалося додати");
    }

    /// <summary>
    /// Локальний файл як джерело: без завантаження, лише валідуємо існування.
    /// Id — імʼя файлу без розширення (для нарізки/іменування сегментів);
    /// SourceUrl лишаємо null — ознака, що джерело локальне, не з мережі.
    /// </summary>
    private static SourceVideo? MakeLocalSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var id = Path.GetFileNameWithoutExtension(path);
        return new SourceVideo
        {
            Id = id,
            FilePath = path,
            SourceUrl = null,
            Title = id
        };
    }

    private async Task<SourceVideo?> DownloadOneAsync(
        string url, PipelineContext context, IProgressReporter progress, CancellationToken token)
    {
        // Куди класти готовий файл: явне призначення (режим «тільки завантажити»
        // кладе в Output) або, як зазвичай, робоча папка прогону для подальшої обробки.
        var destDir = string.IsNullOrWhiteSpace(_settings.DestinationDirectory)
            ? context.WorkingDirectory
            : _settings.DestinationDirectory!;
        Directory.CreateDirectory(destDir);

        // %(id)s — стабільний ідентифікатор від платформи; розширення обирає yt-dlp.
        var outputTemplate = Path.Combine(destDir, "%(id)s.%(ext)s");

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

    /// <summary>
    /// Локальні відеофайли з диска — додаються в джерела БЕЗ завантаження
    /// (вже на диску). Шляхи не серіалізуємо в рецепт за замовчуванням сенсу:
    /// машинозалежні, як і налаштування. Поки лишаємо в DownloadSettings заради
    /// одного кроку-джерела (варіант A: «Завантаження» збирає і URL, і локальні).
    /// </summary>
    public List<string> LocalFiles { get; set; } = new();

    /// <summary>Бажана якість: "best" | "1080p" | "720p" | "480p".</summary>
    public string Quality { get; set; } = "best";

    /// <summary>
    /// Куди класти завантажені файли. null/порожнє — робоча папка прогону (дефолт,
    /// для подальшої обробки). Режим «тільки завантажити» задає сюди Output, щоб
    /// файли лишились під рукою, а не в тимчасовій папці.
    /// </summary>
    public string? DestinationDirectory { get; set; }

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
