namespace VideoCombine.Core.Models;

/// <summary>Вихідне відео-джерело: завантажене з URL або локальний файл.</summary>
public sealed class SourceVideo
{
    public required string Id { get; init; }
    public required string FilePath { get; set; }
    public string? SourceUrl { get; init; }
    public string? Title { get; set; }
    public TimeSpan? Duration { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? Fps { get; set; }
    /// <summary>
    /// Чи є у файлі аудіодоріжка. Заповнює AnalyzeStep (ffprobe). null — ще не
    /// аналізували; тоді вважаємо, що звук Є (так було до появи цього поля).
    /// Потрібно склейці: німі джерела (стоки, AI-кліпи, запис екрана) без цього
    /// валили весь прогон — див. docs/CHANGELOG.md, запис 002.
    /// </summary>
    public bool? HasAudio { get; set; }
    /// <summary>Транскрипція (Whisper) — заповнюється ліниво, коли потрібна.</summary>
    public string? Transcript { get; set; }
}

/// <summary>Нарізаний сегмент відео.</summary>
public sealed class Segment
{
    public required string Id { get; init; }
    public required string FilePath { get; set; }
    public required string SourceId { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>
    /// Чи є в сегменті звук (успадковується від джерела при нарізці). Дефолт true —
    /// поведінка як раніше. false → склейка підставляє тишу замість аудіодоріжки.
    /// Крок, що сам створює сегменти (напр. майбутня AI-генерація кліпів), має
    /// виставити це поле чесно.
    /// </summary>
    public bool HasAudio { get; init; } = true;
}

/// <summary>Аудіо-доріжка (озвучка чи музика).</summary>
public sealed class AudioTrack
{
    public required string FilePath { get; set; }
    public AudioKind Kind { get; init; }
    public double Volume { get; set; } = 1.0;
}

public enum AudioKind { Voiceover, Music }

/// <summary>Сцена генеративної гілки (storyboard) — розділ 3.9.1.</summary>
public sealed class Scene
{
    public required string Id { get; init; }
    public string VoiceoverText { get; set; } = "";
    public string ImagePrompt { get; set; } = "";   // що намалювати (NanoBanana)
    public string MotionPrompt { get; set; } = "";   // як оживити (Kling тощо)
    public string? GeneratedImagePath { get; set; }
    public string? GeneratedClipPath { get; set; }
}
