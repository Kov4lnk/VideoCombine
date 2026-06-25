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
    /// <summary>Опція для ComboBox: внутрішній ключ + людський підпис.</summary>
    public sealed record ComboOption(string Key, string Label);

    // Пресети кодування. Шкали CPU і GPU несумісні: "medium" нічого не значить для
    // h264_nvenc, а "p5" — для libx264. Тому набір перемикається за UseNvenc.
    private static readonly IReadOnlyList<string> X264Presets = new[]
    {
        "ultrafast", "superfast", "veryfast", "faster", "fast",
        "medium", "slow", "slower", "veryslow"
    };
    private static readonly IReadOnlyList<string> NvencPresets = new[]
    {
        "p1", "p2", "p3", "p4", "p5", "p6", "p7"
    };

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

    /// <summary>Контейнер вихідного файлу — ffmpeg визначає мукспер за розширенням.</summary>
    [ObservableProperty] private string _container = "mp4";

    public IReadOnlyList<string> ContainerOptions { get; } = new[] { "mp4", "mkv", "mov" };

    /// <summary>"quality" (CRF/CQ) | "bitrate" (цільовий розмір). Внутрішні ключі.</summary>
    [ObservableProperty] private string _rateMode = "quality";

    public IReadOnlyList<ComboOption> RateModeOptions { get; } = new[]
    {
        new ComboOption("quality", "За якістю (CRF/CQ)"),
        new ComboOption("bitrate", "За бітрейтом")
    };

    /// <summary>
    /// Значення якості. Мапиться на Crf (CPU) або NvencCq (GPU) при складанні.
    /// Менше = краще/більший файл. Число валідне в обох шкалах, тож при перемиканні
    /// NVENC не скидаємо (на відміну від пресета).
    /// </summary>
    [ObservableProperty] private int _quality = 20;

    /// <summary>Цільовий відеобітрейт у кбіт/с (режим "bitrate").</summary>
    [ObservableProperty] private int _videoBitrateK = 6000;

    /// <summary>
    /// Пресет кодування поточної шкали (CPU або GPU — залежно від UseNvenc).
    /// Одне UI-поле, при складанні лягає в EncodePreset або NvencPreset.
    /// </summary>
    [ObservableProperty] private string _encodePreset = "medium";

    /// <summary>Набір пресетів для активної шкали — перемикається разом з UseNvenc.</summary>
    public IReadOnlyList<string> PresetOptions => UseNvenc ? NvencPresets : X264Presets;

    public bool IsQualityMode => RateMode.Equals("quality", StringComparison.OrdinalIgnoreCase);
    public bool IsBitrateMode => RateMode.Equals("bitrate", StringComparison.OrdinalIgnoreCase);

    // Перемикання GPU/CPU міняє шкалу пресетів: оновлюємо список і скидаємо пресет
    // у дефолт нової шкали (старе значення в новій шкалі недійсне).
    partial void OnUseNvencChanged(bool value)
    {
        EncodePreset = value ? "p5" : "medium";
        OnPropertyChanged(nameof(PresetOptions));
    }

    partial void OnRateModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsQualityMode));
        OnPropertyChanged(nameof(IsBitrateMode));
    }

    // --- Складання Settings для пайплайну ---

    /// <summary>Налаштування нарізки за поточним станом панелі.</summary>
    public CutSettings BuildCutSettings() => new()
    {
        Mode = CutMode,
        MinSec = CutMinSec,
        MaxSec = CutMaxSec
    };

    /// <summary>Налаштування склейки за поточним станом панелі.</summary>
    public AssembleSettings BuildAssembleSettings()
    {
        var nvenc = UseNvenc;
        return new AssembleSettings
        {
            Mode = "encode",
            Order = AssembleOrder,
            VideoCodec = nvenc ? "nvenc" : "libx264",
            RateMode = RateMode,
            // Якість і пресет лягають у шкалу активного кодека; неактивна лишається дефолтною.
            Crf = nvenc ? new AssembleSettings().Crf : Quality,
            NvencCq = nvenc ? Quality : new AssembleSettings().NvencCq,
            EncodePreset = nvenc ? new AssembleSettings().EncodePreset : EncodePreset,
            NvencPreset = nvenc ? EncodePreset : new AssembleSettings().NvencPreset,
            VideoBitrateK = VideoBitrateK,
            OutputFileName = $"output.{Container}"
        };
    }

    /// <summary>
    /// Заливає налаштування в поля панелі (завантаження рецепта). Зворотне до
    /// BuildCutSettings/BuildAssembleSettings. Прив'язки оновлять UI самі.
    /// </summary>
    public void Apply(CutSettings cut, AssembleSettings assemble)
    {
        CutMode = cut.Mode;
        CutMinSec = cut.MinSec;
        CutMaxSec = cut.MaxSec;

        AssembleOrder = assemble.Order;
        // UseNvenc ПЕРШИМ: його OnChanged скидає EncodePreset у дефолт шкали — далі
        // перекриваємо значенням із рецепта (для активної шкали).
        UseNvenc = assemble.VideoCodec.Equals("nvenc", StringComparison.OrdinalIgnoreCase);
        RateMode = assemble.RateMode;
        Quality = UseNvenc ? assemble.NvencCq : assemble.Crf;
        EncodePreset = UseNvenc ? assemble.NvencPreset : assemble.EncodePreset;
        VideoBitrateK = assemble.VideoBitrateK;
        Container = ContainerFromFileName(assemble.OutputFileName);
    }

    private static string ContainerFromFileName(string outputFileName)
    {
        var ext = System.IO.Path.GetExtension(outputFileName);
        return string.IsNullOrEmpty(ext) ? "mp4" : ext.TrimStart('.').ToLowerInvariant();
    }
}
