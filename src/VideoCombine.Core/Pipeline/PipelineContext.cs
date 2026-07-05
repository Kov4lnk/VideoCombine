namespace VideoCombine.Core.Pipeline;

using VideoCombine.Core.Models;

/// <summary>
/// Спільна "коробка", яку кроки передають один одному по ланцюгу.
/// Кожен крок читає, що йому треба, і додає свій результат.
/// Кроки НЕ знають один про одного — спілкуються лише через цей контекст.
/// </summary>
public sealed class PipelineContext
{
    /// <summary>Вихідні відео-джерела (завантажені або локальні файли).</summary>
    public List<SourceVideo> Sources { get; } = new();

    /// <summary>Нарізані сегменти (після кроку нарізки).</summary>
    public List<Segment> Segments { get; } = new();

    /// <summary>Згенеровані/обрані аудіо-доріжки (озвучка, музика).</summary>
    public List<AudioTrack> AudioTracks { get; } = new();

    /// <summary>Сцени генеративної гілки (storyboard), якщо використовується.</summary>
    public List<Scene> Scenes { get; } = new();

    /// <summary>
    /// Текст для озвучки (вхід TtsStep). "Труба" під перехід A→Б: зараз порожня —
    /// у режимі переробки тексту нема; при робочому TTS її наповнить гілка сценарію
    /// (Фаза E) або ручне поле "встав текст". TtsStep читає звідси й світить
    /// "нема тексту", поки null.
    /// </summary>
    public string? VoiceoverText { get; set; }

    /// <summary>Робоча (тимчасова) папка цього прогону.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Куди покласти фінальний файл.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Шлях до підсумкового файлу — заповнює крок експорту наприкінці.</summary>
    public string? FinalOutputPath { get; set; }

    /// <summary>Спільні налаштування (ліміти, шляхи до exe тощо).</summary>
    public required AppSettings Settings { get; init; }
}
