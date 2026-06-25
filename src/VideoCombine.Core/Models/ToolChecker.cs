namespace VideoCombine.Core.Models;

/// <summary>
/// Передпольотна перевірка зовнішніх інструментів (розділ 9): чи реально існують
/// exe за вказаними шляхами. Окремо й у Core — щоб і WPF, і ConsoleRunner мали
/// ОДНУ логіку (раніше File.Exists-перевірка жила лише в ConsoleRunner, тож GUI
/// падав уже посеред пайплайну з незрозумілою помилкою).
///
/// Перевіряємо лише обов'язкові для відеогілки інструменти: ffmpeg, ffprobe,
/// yt-dlp. real-esrgan/whisper — опційні, з'являться з відповідними кроками.
/// </summary>
public static class ToolChecker
{
    /// <summary>
    /// Повертає людиночитані повідомлення про відсутні інструменти (порожній
    /// список = усе на місці). Шлях без роздільника (напр. "ffmpeg") вважаємо
    /// «шукати в PATH» — існування файлу не перевіряємо, бо ОС знайде сама.
    /// </summary>
    public static IReadOnlyList<string> MissingTools(AppSettings s)
    {
        var missing = new List<string>();
        Check("ffmpeg", s.FfmpegPath);
        Check("ffprobe", s.FfprobePath);
        Check("yt-dlp", s.YtDlpPath);
        return missing;

        void Check(string name, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                missing.Add($"Не вказано шлях до {name}");
                return;
            }
            if (IsBareName(path)) return; // шукається в PATH — не перевіряємо файл
            if (!File.Exists(path))
                missing.Add($"Не знайдено {name}: {path}");
        }
    }

    /// <summary>Імʼя без роздільника шляху — трактуємо як пошук у PATH.</summary>
    private static bool IsBareName(string path) =>
        !path.Contains(Path.DirectorySeparatorChar) && !path.Contains('/');

    /// <summary>
    /// Чи доступний інструмент за цим шляхом: голе ім'я (PATH) вважаємо ОК,
    /// інакше файл має існувати. Для живої валідації полів у табі «Налаштування».
    /// </summary>
    public static bool ToolPresent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return IsBareName(path) || File.Exists(path);
    }
}
