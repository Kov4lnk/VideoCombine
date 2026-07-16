namespace VideoCombine.Core.Recipes;

using System.Text.Json;
using VideoCombine.Core.Providers;
using VideoCombine.Core.Steps;

/// <summary>
/// Перетворює налаштування кроків ↔ Recipe (розділ 5). Окремо від App, бо
/// оперує лише Core-типами (CutSettings/AssembleSettings) — тож тестується юнітами.
///
/// ПІДВОДНИЙ КАМІНЬ, заради якого це винесено й покрито тестом: після кругообігу
/// через JSON значення в RecipeStep.Settings вертаються як JsonElement, а не як
/// int/string (бо словник — object?). Тому читання значень мусить розуміти ОБИДВА
/// випадки: щойно зібраний у пам'яті рецепт (raw int/string) і завантажений із
/// файлу (JsonElement). Звідси хелпери ReadString/ReadDouble.
/// </summary>
public static class RecipeMapper
{
    // TypeId кроків — мають збігатися з IStep.TypeId відповідних кроків.
    private const string Download = "download";
    private const string Cut = "cut";
    private const string Upscale = "upscale";
    private const string Subtitle = "subtitle";
    private const string Tts = "tts";
    private const string Assemble = "assemble";
    private const string Loopify = "loopify";

    /// <summary>
    /// Збирає рецепт із поточних налаштувань. Кроки Завантаження/Аналіз/Нарізка/
    /// Апскейл/Склейка. Апскейл ОПЦІЙНИЙ (upscale.Enabled) — у рецепт пишемо завжди,
    /// з прапорцем enabled, щоб вибір зберігався; чи вставляти крок у ланцюг,
    /// вирішує виконавець. Аналіз без налаштувань, тож порожній.
    /// </summary>
    public static Recipe ToRecipe(
        string name, string quality, CutSettings cut, UpscaleSettings upscale,
        AssembleSettings assemble, SubtitleSettings subtitle, TtsSettings tts,
        LoopifySettings loopify) => new()
    {
        Name = name,
        StartBranch = "video",
        Steps =
        {
            new RecipeStep { TypeId = Download, Settings = { ["quality"] = quality } },
            new RecipeStep { TypeId = "analyze" },
            new RecipeStep
            {
                TypeId = Cut,
                Settings =
                {
                    ["mode"] = cut.Mode,
                    ["minSec"] = cut.MinSec,
                    ["maxSec"] = cut.MaxSec
                }
            },
            new RecipeStep
            {
                TypeId = Upscale,
                Settings =
                {
                    ["enabled"] = upscale.Enabled,
                    ["provider"] = upscale.Provider,
                    ["scale"] = upscale.Scale
                }
            },
            new RecipeStep
            {
                TypeId = Assemble,
                Settings =
                {
                    ["order"] = assemble.Order,
                    ["videoCodec"] = assemble.VideoCodec,
                    ["rateMode"] = assemble.RateMode,
                    ["crf"] = assemble.Crf,
                    ["nvencCq"] = assemble.NvencCq,
                    ["encodePreset"] = assemble.EncodePreset,
                    ["nvencPreset"] = assemble.NvencPreset,
                    ["bitrateK"] = assemble.VideoBitrateK,
                    // Контейнер persistимо як розширення; OutputFileName відновлюємо при читанні.
                    ["container"] = ContainerOf(assemble.OutputFileName),
                    // Профіль платформи зберігаємо як самодостатні розміри+формат (не назву).
                    ["targetWidth"] = assemble.TargetWidth,
                    ["targetHeight"] = assemble.TargetHeight,
                    ["targetFps"] = assemble.TargetFps,
                    ["fitMode"] = assemble.FitMode
                }
            },
            // Хвіст (розділ 3.8/3.6): субтитри й озвучка — опційні кроки після Склейки.
            // Пишемо завжди з прапорцем enabled, щоб вибір зберігався; вставляти чи ні
            // в ланцюг — вирішує виконавець (як апскейл).
            new RecipeStep
            {
                TypeId = Subtitle,
                Settings =
                {
                    ["enabled"] = subtitle.Enabled,
                    ["burnIn"] = subtitle.BurnIn
                }
            },
            new RecipeStep
            {
                TypeId = Tts,
                Settings =
                {
                    ["enabled"] = tts.Enabled,
                    ["provider"] = tts.Provider,
                    ["voiceId"] = tts.VoiceId,
                    ["speed"] = tts.Speed
                }
            },
            new RecipeStep
            {
                TypeId = Loopify,
                Settings =
                {
                    ["enabled"] = loopify.Enabled,
                    ["mode"] = loopify.Mode,
                    ["fadeDuration"] = loopify.FadeDuration
                }
            }
        }
    };

    /// <summary>Якість завантаження з рецепта (дефолт "720p", якщо кроку/ключа нема).</summary>
    public static string ReadQuality(Recipe recipe) =>
        ReadString(FindStep(recipe, Download)?.Settings, "quality", "720p");

    /// <summary>Налаштування нарізки з рецепта; відсутні ключі → дефолти CutSettings.</summary>
    public static CutSettings ReadCutSettings(Recipe recipe)
    {
        var s = FindStep(recipe, Cut)?.Settings;
        var def = new CutSettings();
        return new CutSettings
        {
            Mode = ReadString(s, "mode", def.Mode),
            MinSec = ReadDouble(s, "minSec", def.MinSec),
            MaxSec = ReadDouble(s, "maxSec", def.MaxSec)
        };
    }

    /// <summary>Налаштування апскейлу з рецепта; відсутні ключі → дефолти UpscaleSettings.</summary>
    public static UpscaleSettings ReadUpscaleSettings(Recipe recipe)
    {
        var s = FindStep(recipe, Upscale)?.Settings;
        var def = new UpscaleSettings();
        return new UpscaleSettings
        {
            Enabled = ReadBool(s, "enabled", def.Enabled),
            Provider = ReadString(s, "provider", def.Provider),
            Scale = ReadInt(s, "scale", def.Scale)
        };
    }

    /// <summary>Налаштування субтитрів з рецепта; відсутні ключі → дефолти SubtitleSettings.</summary>
    public static SubtitleSettings ReadSubtitleSettings(Recipe recipe)
    {
        var s = FindStep(recipe, Subtitle)?.Settings;
        var def = new SubtitleSettings();
        return new SubtitleSettings
        {
            Enabled = ReadBool(s, "enabled", def.Enabled),
            BurnIn = ReadBool(s, "burnIn", def.BurnIn)
        };
    }

    /// <summary>Налаштування озвучки з рецепта; відсутні ключі → дефолти TtsSettings.</summary>
    public static TtsSettings ReadTtsSettings(Recipe recipe)
    {
        var s = FindStep(recipe, Tts)?.Settings;
        var def = new TtsSettings();
        return new TtsSettings
        {
            Enabled = ReadBool(s, "enabled", def.Enabled),
            Provider = ReadString(s, "provider", def.Provider),
            VoiceId = ReadString(s, "voiceId", def.VoiceId),
            Speed = ReadDouble(s, "speed", def.Speed)
        };
    }

    /// <summary>Налаштування зациклення з рецепта; відсутні ключі → дефолти LoopifySettings.</summary>
    public static LoopifySettings ReadLoopifySettings(Recipe recipe)
    {
        var s = FindStep(recipe, Loopify)?.Settings;
        var def = new LoopifySettings();
        return new LoopifySettings
        {
            Enabled = ReadBool(s, "enabled", def.Enabled),
            Mode = ReadString(s, "mode", def.Mode),
            FadeDuration = ReadDouble(s, "fadeDuration", def.FadeDuration)
        };
    }

    /// <summary>Налаштування склейки з рецепта; відсутні ключі → дефолти AssembleSettings.</summary>
    public static AssembleSettings ReadAssembleSettings(Recipe recipe)
    {
        var s = FindStep(recipe, Assemble)?.Settings;
        var def = new AssembleSettings();
        var container = ReadString(s, "container", ContainerOf(def.OutputFileName));
        return new AssembleSettings
        {
            Mode = "encode",
            Order = ReadString(s, "order", def.Order),
            VideoCodec = ReadString(s, "videoCodec", def.VideoCodec),
            RateMode = ReadString(s, "rateMode", def.RateMode),
            Crf = ReadInt(s, "crf", def.Crf),
            NvencCq = ReadInt(s, "nvencCq", def.NvencCq),
            EncodePreset = ReadString(s, "encodePreset", def.EncodePreset),
            NvencPreset = ReadString(s, "nvencPreset", def.NvencPreset),
            VideoBitrateK = ReadInt(s, "bitrateK", def.VideoBitrateK),
            TargetWidth = ReadInt(s, "targetWidth", def.TargetWidth),
            TargetHeight = ReadInt(s, "targetHeight", def.TargetHeight),
            TargetFps = ReadInt(s, "targetFps", def.TargetFps),
            FitMode = ReadString(s, "fitMode", def.FitMode),
            OutputFileName = $"output.{container}"
        };
    }

    // --- Стійке читання значень (raw або JsonElement) ---

    private static RecipeStep? FindStep(Recipe recipe, string typeId) =>
        recipe.Steps.FirstOrDefault(st => st.TypeId == typeId);

    private static string ReadString(IReadOnlyDictionary<string, object?>? settings, string key, string fallback)
    {
        if (settings is null || !settings.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() ?? fallback : fallback;
        return v.ToString() ?? fallback;
    }

    private static double ReadDouble(IReadOnlyDictionary<string, object?>? settings, string key, double fallback)
    {
        if (settings is null || !settings.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is JsonElement je)
            return je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d) ? d : fallback;
        return v switch
        {
            double dd => dd,
            int ii => ii,
            long ll => ll,
            _ => double.TryParse(v.ToString(), out var p) ? p : fallback
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?>? settings, string key, int fallback)
    {
        if (settings is null || !settings.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is JsonElement je)
            return je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var i) ? i : fallback;
        return v switch
        {
            int ii => ii,
            long ll => (int)ll,
            double dd => (int)dd,
            _ => int.TryParse(v.ToString(), out var p) ? p : fallback
        };
    }

    private static bool ReadBool(IReadOnlyDictionary<string, object?>? settings, string key, bool fallback)
    {
        if (settings is null || !settings.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is JsonElement je)
            return je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            };
        return v switch
        {
            bool b => b,
            _ => bool.TryParse(v.ToString(), out var p) ? p : fallback
        };
    }

    /// <summary>Розширення (без крапки) з імені вихідного файлу — це й є контейнер. Дефолт "mp4".</summary>
    private static string ContainerOf(string outputFileName)
    {
        var ext = System.IO.Path.GetExtension(outputFileName);
        return string.IsNullOrEmpty(ext) ? "mp4" : ext.TrimStart('.').ToLowerInvariant();
    }
}
