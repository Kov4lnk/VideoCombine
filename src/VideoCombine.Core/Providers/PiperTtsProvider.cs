namespace VideoCombine.Core.Providers;

using VideoCombine.Core.External;
using VideoCombine.Core.Pipeline;

/// <summary>
/// СКЕЛЕТ (розділ 3.6). Локальний безкоштовний TTS — один exe (piper) + файл голосу
/// (.onnx-модель), природа та сама, що ffmpeg/whisper.cpp: смикаємо через
/// ProcessRunner. Синхронний, без API-ключів.
///
/// Поки ЗАГЛУШКА: у режимі переробки нема тексту на вході (він з гілки "сценарій",
/// Фаза E). Форма класу й місце в проекті — фінальні; при переході на робочий TTS
/// (див. розмову A→Б) наповнюємо нутро реальним викликом piper.exe, решта не
/// міняється. Зберігаємо поряд із CloudTtsProvider як другий взаємозамінний бекенд
/// (хмара — фінальна якість, Piper — швидка безкоштовна чернетка).
/// </summary>
public sealed class PiperTtsProvider : ITtsProvider
{
    private readonly ProcessRunner _runner;
    private readonly string _piperPath;
    private readonly string? _modelPath;

    public PiperTtsProvider(ProcessRunner runner, string piperPath, string? modelPath = null)
    {
        _runner = runner;
        _piperPath = piperPath;
        _modelPath = modelPath;
    }

    public string Id => "piper";
    public string DisplayName => "Piper (локально, безкоштовно)";
    public bool IsCloud => false;

    public Task<string> SynthesizeAsync(
        string text, TtsOptions options, IProgressReporter progress, CancellationToken token)
    {
        // TODO (перехід A→Б): реальний виклик piper.exe:
        //   echo <text> | piper --model <_modelPath> --output_file <options.OutputPath>
        // + чек наявності exe/моделі в GetRequirements кроку. Аргументи винести
        // public static під тести (як BuildScaleArgs).
        throw new NotImplementedException(
            "Piper TTS ще не реалізовано — скелет під майбутню озвучку (Фаза E).");
    }
}
