using VideoCombine.Core.External;
using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;
using VideoCombine.Core.Steps;

// ─────────────────────────────────────────────────────────────────────────────
// VideoCombine — консольний наскрізний тест Фази B.
// Ланцюг: Завантаження (yt-dlp) → Аналіз (ffprobe) → Нарізка → Склейка.
// Мета: довести, що Core працює БЕЗ GUI (залізне правило, розділ 6).
//
// Запуск:
//   dotnet run --project src/VideoCombine.ConsoleRunner -- <URL> [<URL> ...]
// Перевизначення шляхів (за потреби):
//   VC_FFMPEG, VC_FFPROBE, VC_YTDLP, VC_WORK, VC_OUTPUT
// ─────────────────────────────────────────────────────────────────────────────

if (args.Length == 0)
{
    Console.WriteLine("Вкажи хоча б одне посилання:");
    Console.WriteLine("  dotnet run --project src/VideoCombine.ConsoleRunner -- <URL> [<URL> ...]");
    return 1;
}

var settings = BuildSettings();

Console.WriteLine("=== VideoCombine — наскрізний прогон ===");
Console.WriteLine($"ffmpeg : {settings.FfmpegPath}");
Console.WriteLine($"ffprobe: {settings.FfprobePath}");
Console.WriteLine($"yt-dlp : {settings.YtDlpPath}");
Console.WriteLine($"робоча : {settings.WorkingRoot}");
Console.WriteLine($"вивід  : {settings.OutputRoot}");
Console.WriteLine($"посилань: {args.Length}");
Console.WriteLine();

// Перевірка інструментів перед стартом (розділ 9) — щоб не падати посеред прогону.
if (!CheckTools(settings))
    return 2;

Directory.CreateDirectory(settings.WorkingRoot);
Directory.CreateDirectory(settings.OutputRoot);

var runner = new ProcessRunner();

// Складаємо ланцюг кроків (фіксований розумний порядок, розділ 4.1).
var steps = new List<IStep>
{
    new DownloadStep(runner, new DownloadSettings { Urls = args.ToList(), Quality = "720p" }),
    new AnalyzeStep(runner),
    new CutStep(runner, new CutSettings { Mode = "random", MinSec = 3, MaxSec = 8 }),
    new AssembleStep(runner, new AssembleSettings { Mode = "encode", Order = "sequential" })
};

var context = new PipelineContext
{
    WorkingDirectory = settings.WorkingRoot,
    OutputDirectory = settings.OutputRoot,
    Settings = settings
};

// Ctrl+C → скасування (розділ 9).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var progress = new ConsoleProgress();
var executor = new PipelineExecutor();

try
{
    await executor.RunAsync(steps, context, progress, cts.Token);
    Console.WriteLine();
    Console.WriteLine($"✓ ГОТОВО → {context.FinalOutputPath}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("✕ Скасовано користувачем.");
    return 130;
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"✕ ПОМИЛКА: {ex.Message}");
    return 3;
}

// ─────────────────────────────────────────────────────────────────────────────

static AppSettings BuildSettings()
{
    string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    var repoRoot = FindRepoRoot();

    return new AppSettings
    {
        FfmpegPath  = Env("VC_FFMPEG",  @"C:\ffmpeg\bin\ffmpeg.exe"),
        FfprobePath = Env("VC_FFPROBE", @"C:\ffmpeg\bin\ffprobe.exe"),
        YtDlpPath   = Env("VC_YTDLP",   Path.Combine(repoRoot, "tools", "yt-dlp.exe")),
        WorkingRoot = Env("VC_WORK",    Path.Combine(repoRoot, "work")),
        OutputRoot  = Env("VC_OUTPUT",  Path.Combine(repoRoot, "output")),
    };
}

// Шукає корінь репо вгору по дереву (де лежить .gitignore / .sln),
// щоб tools/work/output були поряд із проектом, а не в bin/.
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "VideoCombine.sln")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static bool CheckTools(AppSettings s)
{
    bool ok = true;
    void Need(string name, string path, bool onPathOk)
    {
        // Шлях без роздільника (напр. "ffmpeg") вважаємо «шукати в PATH» — пропускаємо перевірку файлу.
        var isBareName = !path.Contains(Path.DirectorySeparatorChar) && !path.Contains('/');
        if (isBareName && onPathOk) return;
        if (!File.Exists(path))
        {
            Console.WriteLine($"✕ Не знайдено {name}: {path}");
            ok = false;
        }
    }
    Need("ffmpeg",  s.FfmpegPath,  onPathOk: true);
    Need("ffprobe", s.FfprobePath, onPathOk: true);
    Need("yt-dlp",  s.YtDlpPath,   onPathOk: true);
    if (!ok)
        Console.WriteLine("Виправ шляхи (або змінні VC_FFMPEG / VC_FFPROBE / VC_YTDLP) і повтори.");
    return ok;
}

// ─────────────────────────────────────────────────────────────────────────────
// Прогрес у консоль — те, що WPF зробить лоад-барами (розділ 7).
// Core лише викликає ці методи; як саме показувати — справа оболонки.
// ─────────────────────────────────────────────────────────────────────────────
sealed class ConsoleProgress : IPipelineProgress
{
    private int _index, _total;
    private string _step = "";

    public void BeginStep(int index, int total, string stepName)
    {
        _index = index; _total = total; _step = stepName;
        Console.WriteLine();
        Console.WriteLine($"▶ Крок {index + 1}/{total}: {stepName}");
    }

    public IProgressReporter CreateReporterForCurrentStep() => new ConsoleReporter(_step);

    public void CompleteStep(string stepName) => Console.WriteLine($"  ✓ {stepName} — завершено");
    public void Cancelled(string stepName) => Console.WriteLine($"  ✕ {stepName} — скасовано");
    public void Finished(string? finalOutputPath) { }

    private sealed class ConsoleReporter : IProgressReporter
    {
        private readonly string _step;
        public ConsoleReporter(string step) => _step = step;

        public void ReportStepProgress(string detail, double fraction)
        {
            var pct = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
            // \r — оновлюємо той самий рядок (живий прогрес).
            Console.Write($"\r  {_step}: {detail} [{pct,3}%]            ");
            if (fraction >= 1.0) Console.WriteLine();
        }

        public void Log(LogLevel level, string message)
        {
            var mark = level switch
            {
                LogLevel.Success => "✓",
                LogLevel.Warning => "⚠",
                LogLevel.Error   => "✕",
                _ => "·"
            };
            Console.WriteLine($"\r  {mark} {message}");
        }
    }
}
