namespace VideoCombine.Core.Models;

/// <summary>
/// Глобальні налаштування (таб "Налаштування" — розділ 4.3).
/// API-ключі тут НЕ зберігаються у відкритому вигляді у файлі рецепта —
/// тримаються окремо й захищено (розділ 9).
/// </summary>
public sealed class AppSettings
{
    // Шляхи до зовнішніх інструментів
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string? RealEsrganPath { get; set; }
    public string? WhisperPath { get; set; }

    // Ліміти одночасності (розділ 8.3) — розумні дефолти
    public int MaxFfmpegConcurrency { get; set; } = 8;
    public int MaxGpuConcurrency { get; set; } = 2;
    public int MaxDownloadConcurrency { get; set; } = 3;

    // Папки
    public string WorkingRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";

    /// <summary>Дефолтний LLM-бекенд (розділ 3.11).</summary>
    public string DefaultLlmProvider { get; set; } = "openai";
}
