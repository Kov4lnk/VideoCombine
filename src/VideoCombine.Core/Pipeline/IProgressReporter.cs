namespace VideoCombine.Core.Pipeline;

/// <summary>
/// Канал, через який крок повідомляє "що я зараз роблю" (розділ 7 — live-прогрес).
/// Core не знає про GUI: він лише викликає ці методи, а WPF-шар їх показує
/// (лоад-бари, лог). Так само працює консоль чи тести.
/// </summary>
public interface IProgressReporter
{
    /// <summary>Оновити поточну дію + відсоток кроку (0.0–1.0). Напр. ("segment_087 → x2", 0.72).</summary>
    void ReportStepProgress(string detail, double fraction);

    /// <summary>Дописати рядок у лог. level: Info / Success / Warning / Error.</summary>
    void Log(LogLevel level, string message);
}

public enum LogLevel
{
    Info,
    Success,
    Warning,  // дрібний збій — пропущено, не зупиняємо (розділ 7.1)
    Error     // критичний — зупиняє прогон
}
