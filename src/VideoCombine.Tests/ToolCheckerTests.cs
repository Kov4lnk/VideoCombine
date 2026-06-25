namespace VideoCombine.Tests;

using VideoCombine.Core.Models;
using Xunit;

/// <summary>
/// Тести ToolChecker — передпольотна перевірка наявності exe (розділ 9).
/// Перевіряють саме нюанси логіки: відсутній файл, порожній шлях, голе ім'я
/// (PATH) не вважати помилкою. Реальні exe не потрібні — оперуємо шляхами.
/// </summary>
public sealed class ToolCheckerTests
{
    [Fact]
    public void Голі_імена_у_PATH_не_вважаються_відсутніми()
    {
        // Дефолтні "ffmpeg"/"ffprobe"/"yt-dlp" без шляху — шукаються в PATH.
        var s = new AppSettings
        {
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            YtDlpPath = "yt-dlp"
        };

        Assert.Empty(ToolChecker.MissingTools(s));
    }

    [Fact]
    public void Неіснуючий_файл_за_шляхом_повертає_помилку()
    {
        var s = new AppSettings
        {
            FfmpegPath = @"Z:\нема\ffmpeg.exe",
            FfprobePath = "ffprobe",
            YtDlpPath = "yt-dlp"
        };

        var missing = ToolChecker.MissingTools(s);

        Assert.Single(missing);
        Assert.Contains("ffmpeg", missing[0]);
    }

    [Fact]
    public void Порожній_шлях_повертає_помилку()
    {
        var s = new AppSettings
        {
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            YtDlpPath = "  "
        };

        var missing = ToolChecker.MissingTools(s);

        Assert.Single(missing);
        Assert.Contains("yt-dlp", missing[0]);
    }

    [Fact]
    public void Існуючий_файл_за_повним_шляхом_не_помилка()
    {
        // Беремо будь-який гарантовано наявний файл як «exe».
        var real = typeof(ToolCheckerTests).Assembly.Location;
        var s = new AppSettings
        {
            FfmpegPath = real,
            FfprobePath = "ffprobe",
            YtDlpPath = "yt-dlp"
        };

        Assert.Empty(ToolChecker.MissingTools(s));
    }
}
