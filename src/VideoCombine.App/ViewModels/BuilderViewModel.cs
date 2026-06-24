namespace VideoCombine.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using VideoCombine.Core.Steps;

/// <summary>
/// Логіка таба «Конструктор» (розділ 4) — джерело правди для налаштувань кроків.
/// Раніше «Виконання» будувало ланцюг із захардкоджених значень; тепер бере їх
/// звідси. Тримає налаштування Нарізки й Склейки, віддає готові *Settings.
///
/// Свідомо поки БЕЗ: галочок увімк/вимк кроків (усі 4 кроки відеогілки поки
/// обов'язкові — вимикати нічого) і перетягування порядку. З'являться, коли
/// будуть опційні кроки (апскейл, субтитри — пізніші фази). Якість завантаження
/// живе на табі «Джерела», тут не дублюється.
/// </summary>
public sealed partial class BuilderViewModel : ObservableObject
{
    // --- Нарізка (CutStep) ---

    /// <summary>"random" (випадкові відрізки в межах хв/макс) | "fixed".</summary>
    [ObservableProperty] private string _cutMode = "random";

    public IReadOnlyList<string> CutModeOptions { get; } = new[] { "random", "fixed" };

    [ObservableProperty] private double _cutMinSec = 3;
    [ObservableProperty] private double _cutMaxSec = 8;

    // --- Склейка (AssembleStep) ---

    /// <summary>"sequential" (як є) | "random" (перемішати з seed).</summary>
    [ObservableProperty] private string _assembleOrder = "sequential";

    public IReadOnlyList<string> AssembleOrderOptions { get; } = new[] { "sequential", "random" };

    /// <summary>
    /// Кодувати склейку на GPU (h264_nvenc) замість CPU (libx264). Якщо GPU
    /// недоступний, AssembleStep тихо відкотиться на libx264 (AllowGpuFallback).
    /// Дефолт false — макс. якість/детермінізм, GPU вмикаємо свідомо.
    /// </summary>
    [ObservableProperty] private bool _useNvenc;

    // --- Складання Settings для пайплайну ---

    /// <summary>Налаштування нарізки за поточним станом панелі.</summary>
    public CutSettings BuildCutSettings() => new()
    {
        Mode = CutMode,
        MinSec = CutMinSec,
        MaxSec = CutMaxSec
    };

    /// <summary>Налаштування склейки за поточним станом панелі.</summary>
    public AssembleSettings BuildAssembleSettings() => new()
    {
        Mode = "encode",
        Order = AssembleOrder,
        VideoCodec = UseNvenc ? "nvenc" : "libx264"
    };
}
