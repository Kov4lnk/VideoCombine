namespace VideoCombine.Tests;

using VideoCombine.Core.Models;
using Xunit;

/// <summary>
/// Тести SettingsStore — нетривіальна логіка завантаження: round-trip,
/// стійкість до битого файлу, добивання порожніх полів дефолтами.
/// Кожен тест працює у власній тимчасовій теці (ізоляція).
/// </summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void Save_і_Load_зберігають_значення()
    {
        var path = TempPath();
        var store = new SettingsStore(path);

        store.Save(new AppSettings
        {
            FfmpegPath = @"D:\custom\ffmpeg.exe",
            WorkingRoot = @"D:\work",
            OutputRoot = @"D:\out"
        });

        var loaded = new SettingsStore(path).LoadOrDefault();

        Assert.Equal(@"D:\custom\ffmpeg.exe", loaded.FfmpegPath);
        Assert.Equal(@"D:\work", loaded.WorkingRoot);
        Assert.Equal(@"D:\out", loaded.OutputRoot);

        Cleanup(path);
    }

    [Fact]
    public void LoadOrDefault_без_файлу_дає_непорожні_шляхи()
    {
        var path = TempPath();
        var s = new SettingsStore(path).LoadOrDefault();

        // Файлу немає → дефолти з env/захардкоджені, не порожні й не "ffmpeg".
        Assert.False(string.IsNullOrWhiteSpace(s.FfmpegPath));
        Assert.NotEqual("ffmpeg", s.FfmpegPath);
        Assert.False(string.IsNullOrWhiteSpace(s.WorkingRoot));
        Assert.False(string.IsNullOrWhiteSpace(s.OutputRoot));

        Cleanup(path);
    }

    [Fact]
    public void LoadOrDefault_битий_файл_не_падає()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ це не валідний json ");

        var s = new SettingsStore(path).LoadOrDefault();

        // Замість винятку — дефолти.
        Assert.False(string.IsNullOrWhiteSpace(s.FfmpegPath));

        Cleanup(path);
    }

    [Fact]
    public void LoadOrDefault_порожні_поля_добиваються_дефолтами()
    {
        var path = TempPath();
        // Файл із порожніми шляхами (напр. збережений із пустими полями).
        File.WriteAllText(path, """{ "ffmpegPath": "", "workingRoot": "" }""");

        var s = new SettingsStore(path).LoadOrDefault();

        Assert.False(string.IsNullOrWhiteSpace(s.FfmpegPath));
        Assert.False(string.IsNullOrWhiteSpace(s.WorkingRoot));

        Cleanup(path);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "vc_settings_" + Guid.NewGuid() + ".json");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
