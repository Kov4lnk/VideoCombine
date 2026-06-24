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

    public ExecutionViewModel(SourcesViewModel sources)
    {
        _sources = sources;
        // Старт залежить від наявності валідних джерел — оновлюємо, коли список змінюється.
        _sources.Sources.CollectionChanged += (_, _) => StartCommand.NotifyCanExecuteChanged();
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

    /// <summary>Старт доступний лише коли не біжимо й є хоч одне валідне джерело.</summary>
    private bool CanStart() => !IsRunning && _sources.GetUsableUrls().Count > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var urls = _sources.GetUsableUrls();
        if (urls.Count == 0) return;

        IsRunning = true;
        LogEntries.Clear();
        FinalOutputPath = null;
        OverallProgress = 0;
        StepProgress = 0;
        _cts = new CancellationTokenSource();
        StartTimer();

        var settings = BuildSettings();
        var runner = new ProcessRunner();

        var steps = new List<IStep>
        {
            new DownloadStep(runner, new DownloadSettings { Urls = urls.ToList(), Quality = _sources.GetQualityValue() }),
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
        CancelCommand.NotifyCanExecuteChanged();
        OpenResultCommand.NotifyCanExecuteChanged();
    }

    partial void OnFinalOutputPathChanged(string? value) => OpenResultCommand.NotifyCanExecuteChanged();

    /// <summary>Тимчасові налаштування (Фаза C винесе їх у таб «Налаштування»).</summary>
    private static AppSettings BuildSettings()
    {
        string Env(string key, string fallback) =>
            Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

        var repoRoot = FindRepoRoot();
        return new AppSettings
        {
            FfmpegPath = Env("VC_FFMPEG", @"C:\ffmpeg\bin\ffmpeg.exe"),
            FfprobePath = Env("VC_FFPROBE", @"C:\ffmpeg\bin\ffprobe.exe"),
            YtDlpPath = Env("VC_YTDLP", Path.Combine(repoRoot, "tools", "yt-dlp.exe")),
            WorkingRoot = Env("VC_WORK", Path.Combine(repoRoot, "work")),
            OutputRoot = Env("VC_OUTPUT", Path.Combine(repoRoot, "output")),
        };
    }

    private static string FindRepoRoot()
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
}
