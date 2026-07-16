namespace VideoCombine.App.ViewModels;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoCombine.Core.External;
using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;
using VideoCombine.Core.Providers;
using VideoCombine.Core.Steps;

/// <summary>
/// Логіка таба «Виконання» (розділ 7). Реалізує обидва канали прогресу:
///   • IPipelineProgress — рівень пайплайну (загальний бар, перемикання кроків);
///   • IProgressReporter — рівень кроку (детальний бар, лог).
/// Показуємо один крок за раз, тож одному VM зручно бути обома.
///
/// ВАЖЛИВО (потоки): ланцюг гониться у фоні (Task.Run), щоб GUI не замерз.
/// Тому всі звіти приходять із фонового потоку — кожне оновлення UI
/// маршалимо на UI-потік через Dispatcher.
/// </summary>
public sealed partial class ExecutionViewModel : ObservableObject, IPipelineProgress, IProgressReporter
{
    private readonly SourcesViewModel _sources;
    private readonly BuilderViewModel _builder;
    private readonly SettingsViewModel _settings;

    public ExecutionViewModel(SourcesViewModel sources, BuilderViewModel builder, SettingsViewModel settings)
    {
        _sources = sources;
        _builder = builder;
        _settings = settings;
        // Старт залежить від наявності валідних джерел — оновлюємо, коли список змінюється.
        _sources.Sources.CollectionChanged += (_, _) =>
        {
            StartCommand.NotifyCanExecuteChanged();
            DownloadOnlyCommand.NotifyCanExecuteChanged();
        };
    }

    // --- Поля для прив'язки (генерують властивості + сповіщення) ---

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _currentStepName = "Очікування";
    [ObservableProperty] private string _currentDetail = "";
    [ObservableProperty] private double _stepProgress;     // 0..1 поточного кроку
    [ObservableProperty] private double _overallProgress;  // 0..1 усього пайплайну
    [ObservableProperty] private string _overallText = "";
    [ObservableProperty] private string? _finalOutputPath;

    /// <summary>Текст таймера: живий під час прогону, підсумковий по завершенні.</summary>
    [ObservableProperty] private string _elapsedText = "";

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    private CancellationTokenSource? _cts;

    // Вимір часу прогону + тікер, що оновлює ElapsedText щосекунди.
    private readonly Stopwatch _stopwatch = new();
    private DispatcherTimer? _ticker;

    // --- Команди ---

    /// <summary>Старт доступний лише коли не біжимо й є хоч одне джерело (URL або файл).</summary>
    private bool CanStart() => !IsRunning && _sources.HasUsableSources();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var urls = _sources.GetUsableUrls();
        var localFiles = _sources.GetLocalFiles();
        if (urls.Count == 0 && localFiles.Count == 0) return;

        // Повний ланцюг відеогілки. Потрібні всі інструменти (ffmpeg для нарізки/склейки).
        await RunPipelineAsync(
            buildSteps: (runner, settings) => BuildSteps(runner, settings, urls, localFiles),
            missingTools: ToolChecker.MissingTools);
    }

    /// <summary>«Тільки завантажити» доступне, коли не біжимо й є хоч один валідний URL.
    /// Локальні файли тут ні до чого — вони вже на диску, качати нема чого.</summary>
    private bool CanDownloadOnly() => !IsRunning && _sources.GetUsableUrls().Count > 0;

    /// <summary>
    /// Скачати відео з URL і зупинитись — без нарізки/склейки. Ланцюг із одного
    /// DownloadStep, файли лягають в Output (не в тимчасову робочу папку). Зручний
    /// шорткат для частого сценарію «просто завантаж». Загальний механізм вмикання/
    /// вимикання кроків з'явиться окремим слайсом, коли опційних кроків стане більше.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadOnly))]
    private async Task DownloadOnlyAsync()
    {
        var urls = _sources.GetUsableUrls();
        if (urls.Count == 0) return;

        await RunPipelineAsync(
            buildSteps: (runner, settings) => new List<IStep>
            {
                new DownloadStep(runner, new DownloadSettings
                {
                    Urls = urls.ToList(),
                    Quality = _sources.GetQualityValue(),
                    // Кладемо готові файли в Output, щоб лишились під рукою.
                    DestinationDirectory = settings.OutputRoot
                })
            },
            // Для завантаження ffmpeg/ffprobe не потрібні — перевіряємо лише yt-dlp.
            missingTools: s => ToolChecker.ToolPresent(s.YtDlpPath)
                ? Array.Empty<string>()
                : new[] { "Не вказано або не знайдено yt-dlp (таб «Налаштування»)" });
    }

    /// <summary>
    /// Спільний каркас прогону для всіх режимів (повний ланцюг, тільки завантаження
    /// тощо): прапор IsRunning, таймер, передпольотна перевірка інструментів, фон,
    /// обробка скасування/помилок. Відрізняються лише набір кроків (buildSteps) і
    /// які інструменти обов'язкові (missingTools).
    /// </summary>
    private async Task RunPipelineAsync(
        Func<ProcessRunner, AppSettings, List<IStep>> buildSteps,
        Func<AppSettings, IReadOnlyList<string>> missingTools)
    {
        IsRunning = true;
        LogEntries.Clear();
        FinalOutputPath = null;
        OverallProgress = 0;
        StepProgress = 0;
        _cts = new CancellationTokenSource();
        StartTimer();

        var settings = _settings.ToAppSettings();

        // Передпольотна перевірка інструментів (розділ 9): краще чіткий лог тут,
        // ніж падіння посеред пайплайну. Шляхи редагуються в табі «Налаштування».
        var missing = missingTools(settings);
        if (missing.Count > 0)
        {
            foreach (var m in missing) AppendLog(LogLevel.Error, m);
            AppendLog(LogLevel.Error, "Виправте шляхи на табі «Налаштування» і повторіть.");
            StopTimer();
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            return;
        }

        var runner = new ProcessRunner();
        var steps = buildSteps(runner, settings);

        var context = new PipelineContext
        {
            WorkingDirectory = settings.WorkingRoot,
            OutputDirectory = settings.OutputRoot,
            Settings = settings
        };

        Directory.CreateDirectory(settings.WorkingRoot);
        Directory.CreateDirectory(settings.OutputRoot);

        var executor = new PipelineExecutor();
        try
        {
            // У фон — щоб UI-потік лишався вільним.
            await Task.Run(() => executor.RunAsync(steps, context, this, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogLevel.Warning, "Прогон скасовано.");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, ex.Message);
        }
        finally
        {
            StopTimer();
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Збирає ланцюг кроків відеогілки. Завантаження→Аналіз→Нарізка→[Апскейл]→Склейка.
    /// Апскейл — ПЕРШИЙ опційний крок: вставляється між Нарізкою і Склейкою лише
    /// коли увімкнено в Конструкторі. Уся логіка порядку/опційності тут, в одному
    /// місці — коли з'явиться UI перетягування/вимикання кроків, переписуємо цей
    /// метод, а не полюємо на спецкейси по в'юмоделях.
    /// </summary>
    private List<IStep> BuildSteps(
        ProcessRunner runner, AppSettings settings,
        IReadOnlyList<string> urls, IReadOnlyList<string> localFiles)
    {
        var steps = new List<IStep>
        {
            new DownloadStep(runner, new DownloadSettings
            {
                Urls = urls.ToList(),
                LocalFiles = localFiles.ToList(),
                Quality = _sources.GetQualityValue()
            }),
            new AnalyzeStep(runner),
            new CutStep(runner, _builder.BuildCutSettings())
        };

        var upscale = _builder.BuildUpscaleSettings();
        if (upscale.Enabled)
        {
            // Поки єдиний бекенд — FFmpeg-scale. Інші (Real-ESRGAN, хмара) стануть
            // тут вибором за upscale.Provider, реалізуючи той самий IUpscaleProvider.
            var provider = new FFmpegScaleUpscaler(runner, settings.FfmpegPath);
            steps.Add(new UpscaleStep(provider, upscale));
        }

        steps.Add(new AssembleStep(runner, _builder.BuildAssembleSettings()));

        // Субтитри — опційний крок ХВОСТА, після Склейки: whisper.cpp транскрибує
        // готове фінальне відео (не оригінали — порядок сегментів інший) і кладе .srt
        // поряд; за burn-in переставляє FinalOutputPath на відео з субтитрами.
        var subtitle = _builder.BuildSubtitleSettings();
        if (subtitle.Enabled)
            steps.Add(new SubtitleStep(runner, subtitle));

        // TTS (Озвучка) свідомо НЕ вставляємо: крок-заглушка без джерела тексту
        // (гілка "сценарій" — Фаза E) і без сховища ключів. UI видимий, але
        // заблокований; вставка з'явиться при переході A→Б разом із джерелом тексту.

        // Зациклення — опційний крок ХВОСТА, після Субтитрів. Бере FinalOutputPath
        // і перетворює відео на безшовний луп (crossfade / reverse / smart-trim).
        var loopify = _builder.BuildLoopifySettings();
        if (loopify.Enabled)
            steps.Add(new LoopifyStep(runner, loopify));

        return steps;
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanOpenResult() => !string.IsNullOrEmpty(FinalOutputPath) && File.Exists(FinalOutputPath);

    [RelayCommand(CanExecute = nameof(CanOpenResult))]
    private void OpenResult()
    {
        if (FinalOutputPath is null) return;
        // Відкрити готовий файл у системному плеєрі (найпростіший preview, розділ 7.5).
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = FinalOutputPath,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Відкрити папку Output у провіднику — куди лягають завантажені файли й готові
    /// відео. Зручно після «Тільки завантажити». Папку створюємо, якщо ще нема,
    /// щоб провідник не лаявся на неіснуючий шлях.
    /// </summary>
    [RelayCommand]
    private void OpenDownloadFolder()
    {
        var dir = _settings.ToAppSettings().OutputRoot;
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    // --- IPipelineProgress (рівень пайплайну) ---

    public void BeginStep(int index, int total, string stepName) => OnUi(() =>
    {
        CurrentStepName = stepName;
        CurrentDetail = "";
        StepProgress = 0;
        OverallProgress = total == 0 ? 0 : (double)index / total;
        OverallText = $"Крок {index + 1} з {total}";
    });

    public IProgressReporter CreateReporterForCurrentStep() => this;

    public void CompleteStep(string stepName) => OnUi(() =>
    {
        StepProgress = 1;
        AppendLogCore(LogEntry.From(LogLevel.Success, $"{stepName} — завершено"));
    });

    public void Cancelled(string stepName) => OnUi(() =>
        AppendLogCore(LogEntry.From(LogLevel.Warning, $"{stepName} — скасовано")));

    public void Finished(string? finalOutputPath) => OnUi(() =>
    {
        OverallProgress = 1;
        OverallText = "Готово";
        CurrentStepName = "Готово";
        FinalOutputPath = finalOutputPath;
    });

    // --- IProgressReporter (рівень кроку) ---

    public void ReportStepProgress(string detail, double fraction) => OnUi(() =>
    {
        CurrentDetail = detail;
        if (fraction > 0) StepProgress = Math.Clamp(fraction, 0, 1);
    });

    public void Log(LogLevel level, string message) => AppendLog(level, message);

    // --- Допоміжне ---

    private void AppendLog(LogLevel level, string message) =>
        OnUi(() => AppendLogCore(LogEntry.From(level, message)));

    private void AppendLogCore(LogEntry entry) => LogEntries.Add(entry);

    /// <summary>Виконати дію на UI-потоці (звіти приходять із фонового).</summary>
    private static void OnUi(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(action);
        else
            action();
    }

    // --- Таймер прогону ---

    private void StartTimer()
    {
        _stopwatch.Restart();
        ElapsedText = "0:00";
        // Тікер на UI-потоці — оновлює текст щосекунди, поки йде прогон.
        _ticker = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _ticker.Tick += (_, _) => ElapsedText = Format(_stopwatch.Elapsed);
        _ticker.Start();
    }

    private void StopTimer()
    {
        _stopwatch.Stop();
        _ticker?.Stop();
        _ticker = null;
        // Підсумковий час по завершенні.
        ElapsedText = $"{Format(_stopwatch.Elapsed)} (завершено)";
    }

    /// <summary>Час як m:ss або h:mm:ss.</summary>
    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    // Команди-залежності: коли змінюється IsRunning / URL / шлях — оновити доступність кнопок.
    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        DownloadOnlyCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OpenResultCommand.NotifyCanExecuteChanged();
    }

    partial void OnFinalOutputPathChanged(string? value) => OpenResultCommand.NotifyCanExecuteChanged();
}
