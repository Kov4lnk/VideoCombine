namespace VideoCombine.Core.External;

using System.Globalization;

/// <summary>
/// Форматування чисел для аргументів ffmpeg.
///
/// ЧОМУ ОКРЕМИЙ КЛАС. ffmpeg розуміє дробові числа лише з КРАПКОЮ ("2.5").
/// А .NET за замовчуванням форматує числа за регіональними налаштуваннями Windows:
/// на українській, німецькій, польській тощо локалі 2.5.ToString() дає "2,5".
/// ffmpeg на таке відповідає "Invalid duration for option ss: 2,5" і не стартує.
/// Тобто той самий код працює на машині з англійським форматом і ламається
/// на машині з українським (docs/CHANGELOG.md, запис 003).
///
/// ПРАВИЛО: будь-яке число, що йде в аргументи ffmpeg/ffprobe/yt-dlp, форматуємо
/// через цей клас, а не через ToString() чи інтерполяцію $"{x:0.###}".
/// (Числа в повідомленнях ДЛЯ ЛЮДИНИ — лог, прогрес — цього не стосується.)
/// </summary>
public static class FfmpegFormat
{
    /// <summary>Секунди з точністю до мілісекунди, завжди з крапкою: 2.5 → "2.5", 3 → "3".</summary>
    public static string Seconds(double seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Те саме для TimeSpan.</summary>
    public static string Seconds(TimeSpan time) => Seconds(time.TotalSeconds);
}
