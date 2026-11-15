namespace VideoCombine.Tests;

using VideoCombine.Core.Models;
using VideoCombine.Core.Steps;
using Xunit;

/// <summary>
/// Джерела БЕЗ звуку (стоки, AI-кліпи, запис екрана) не мають валити склейку
/// (docs/CHANGELOG.md, запис 002). Ланцюжок ознаки «є звук»:
/// ffprobe → SourceVideo.HasAudio → CutJob/Segment.HasAudio → фільтр склейки.
/// Тести чисті — без ffmpeg, як і решта StepLogicTests.
/// </summary>
public sealed class SilentAudioTests
{
    // --- AnalyzeStep.ParseProbe: чи є аудіо-потік ---

    [Fact]
    public void ParseProbe_бачить_аудіо_після_відео()
    {
        const string json = """
        { "format": { "duration": "10.0" },
          "streams": [
            { "codec_type": "video", "width": 1920, "height": 1080, "r_frame_rate": "30/1" },
            { "codec_type": "audio" } ] }
        """;

        var r = AnalyzeStep.ParseProbe(json);

        Assert.True(r.HasAudio);
        Assert.Equal(1920, r.Width);
    }

    [Fact]
    public void ParseProbe_бачить_аудіо_перед_відео()
    {
        const string json = """
        { "format": { "duration": "10.0" },
          "streams": [
            { "codec_type": "audio" },
            { "codec_type": "video", "width": 1280, "height": 720, "r_frame_rate": "25/1" } ] }
        """;

        var r = AnalyzeStep.ParseProbe(json);

        Assert.True(r.HasAudio);
        Assert.Equal(1280, r.Width);
    }

    [Fact]
    public void ParseProbe_без_аудіо_потоку_дає_HasAudio_false()
    {
        const string json = """
        { "format": { "duration": "4.0" },
          "streams": [
            { "codec_type": "video", "width": 640, "height": 360, "r_frame_rate": "25/1" } ] }
        """;

        var r = AnalyzeStep.ParseProbe(json);

        Assert.False(r.HasAudio);
        Assert.Equal(TimeSpan.FromSeconds(4), r.Duration);
    }

    [Fact]
    public void ParseProbe_розмір_береться_з_першого_відео_потоку()
    {
        // Другим відео-потоком буває вбудована обкладинка — вона не має перебити розмір.
        const string json = """
        { "format": { "duration": "4.0" },
          "streams": [
            { "codec_type": "video", "width": 1920, "height": 1080, "r_frame_rate": "30/1" },
            { "codec_type": "audio" },
            { "codec_type": "video", "width": 320, "height": 320, "r_frame_rate": "90000/1" } ] }
        """;

        var r = AnalyzeStep.ParseProbe(json);

        Assert.Equal(1920, r.Width);
        Assert.Equal(1080, r.Height);
        Assert.Equal(30.0, r.Fps);
        Assert.True(r.HasAudio);
    }

    // --- CutStep.PlanCuts: ознака звуку переходить від джерела до відрізка ---

    [Fact]
    public void PlanCuts_переносить_відсутність_звуку_на_відрізки()
    {
        var sources = new[]
        {
            Source("silent", 6, hasAudio: false),
            Source("loud", 6, hasAudio: true)
        };
        var settings = new CutSettings { Mode = "fixed", MinSec = 3, MaxSec = 3 };

        var jobs = CutStep.PlanCuts(sources, settings, "work", new Random(1));

        Assert.Equal(4, jobs.Count);
        Assert.All(jobs.Where(j => j.SourceId == "silent"), j => Assert.False(j.HasAudio));
        Assert.All(jobs.Where(j => j.SourceId == "loud"), j => Assert.True(j.HasAudio));
    }

    [Fact]
    public void PlanCuts_неаналізоване_джерело_вважається_зі_звуком()
    {
        // HasAudio == null (ffprobe не заповнив) → поведінка як до змін: звук є.
        var sources = new[] { Source("unknown", 3, hasAudio: null) };
        var settings = new CutSettings { Mode = "fixed", MinSec = 3, MaxSec = 3 };

        var jobs = CutStep.PlanCuts(sources, settings, "work", new Random(1));

        Assert.True(Assert.Single(jobs).HasAudio);
    }

    // --- AssembleStep.BuildConcatFilter: тиша замість відсутньої доріжки ---

    [Fact]
    public void BuildConcatFilter_німому_сегменту_підставляє_тишу()
    {
        var segments = new[]
        {
            Seg("a", 3, hasAudio: true),
            Seg("b", 2.5, hasAudio: false),
            Seg("c", 2, hasAudio: true)
        };

        var f = AssembleStep.BuildConcatFilter(segments, width: 1920, height: 1080, fps: 30, fitMode: "fit");

        // Сегменти зі звуком — як і раніше, з власної доріжки.
        Assert.Contains("[0:a:0]aresample=async=1[a0]", f);
        Assert.Contains("[2:a:0]aresample=async=1[a2]", f);
        // Німий — тиша потрібної довжини, і ЖОДНОГО посилання на його аудіо
        // (саме воно валило ffmpeg: "Stream specifier ':a:0' matches no streams").
        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration=2.5[a1]", f);
        Assert.DoesNotContain("[1:a:0]", f);
        // Відео німого сегмента нормалізується як у всіх.
        Assert.Contains("[1:v:0]scale=1920:1080", f);
        // Concat отримує пару відео+аудіо від КОЖНОГО входу.
        Assert.Contains("[v0][a0][v1][a1][v2][a2]concat=n=3:v=1:a=1[outv][outa]", f);
    }

    [Fact]
    public void BuildConcatFilter_усі_зі_звуком_дає_той_самий_фільтр_що_й_раніше()
    {
        var segments = new[] { Seg("a", 3, hasAudio: true), Seg("b", 4, hasAudio: true) };

        var bySegments = AssembleStep.BuildConcatFilter(segments, 1920, 1080, 30, "fit");
        var byCount = AssembleStep.BuildConcatFilter(n: 2, width: 1920, height: 1080, fps: 30, fitMode: "fit");

        Assert.Equal(byCount, bySegments);
        Assert.DoesNotContain("anullsrc", bySegments);
    }

    [Fact]
    public void BuildConcatFilter_усі_німі_теж_дає_аудіодоріжку()
    {
        // Навіть якщо звуку нема ніде, у фінальному файлі має бути (тиха) аудіодоріжка:
        // на неї розраховують наступні кроки (зациклення, субтитри) і платформи.
        var segments = new[] { Seg("a", 3, hasAudio: false), Seg("b", 4, hasAudio: false) };

        var f = AssembleStep.BuildConcatFilter(segments, 1280, 720, 25, "crop");

        Assert.Contains("atrim=duration=3[a0]", f);
        Assert.Contains("atrim=duration=4[a1]", f);
        Assert.DoesNotContain(":a:0]", f);
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1[outv][outa]", f);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.0001)]
    public void BuildSilenceSource_нульова_тривалість_замінюється_на_безпечну(double seconds)
    {
        // atrim=duration=0 дав би порожню доріжку — підставляємо 0.1 с.
        var s = AssembleStep.BuildSilenceSource(TimeSpan.FromSeconds(seconds));

        Assert.Equal("anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration=0.1", s);
    }

    private static SourceVideo Source(string id, double durationSec, bool? hasAudio) => new()
    {
        Id = id,
        FilePath = id + ".mp4",
        Duration = TimeSpan.FromSeconds(durationSec),
        HasAudio = hasAudio
    };

    private static Segment Seg(string id, double durationSec, bool hasAudio) => new()
    {
        Id = id,
        FilePath = id + ".mp4",
        SourceId = "src",
        Start = TimeSpan.Zero,
        Duration = TimeSpan.FromSeconds(durationSec),
        HasAudio = hasAudio
    };
}
