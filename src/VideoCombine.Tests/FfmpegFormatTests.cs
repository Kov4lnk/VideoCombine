namespace VideoCombine.Tests;

using System.Globalization;
using VideoCombine.Core.External;
using VideoCombine.Core.Steps;
using Xunit;

/// <summary>
/// Числа в аргументах ffmpeg мають бути з крапкою НЕЗАЛЕЖНО від регіональних
/// налаштувань Windows (docs/CHANGELOG.md, запис 003). Кожен тест вмикає
/// українську локаль (де десятковий роздільник — кома) і перевіряє, що коми
/// в аргументах нема. На англійській локалі ці тести проходили б і без виправлення —
/// саме тому помилку не було видно.
/// </summary>
public sealed class FfmpegFormatTests
{
    [Fact]
    public void Seconds_завжди_з_крапкою()
    {
        UnderUkrainianCulture(() =>
        {
            // Контроль: локаль справді «з комою» — інакше тест нічого не доводить.
            Assert.Equal("2,5", 2.5.ToString("0.###"));

            Assert.Equal("2.5", FfmpegFormat.Seconds(2.5));
            Assert.Equal("5.123", FfmpegFormat.Seconds(5.1234));
            Assert.Equal("3", FfmpegFormat.Seconds(3.0));
            Assert.Equal("1.5", FfmpegFormat.Seconds(TimeSpan.FromMilliseconds(1500)));
        });
    }

    [Fact]
    public void BuildCutArgs_час_із_крапкою_на_локалі_з_комою()
    {
        UnderUkrainianCulture(() =>
        {
            var job = new CutJob(
                SourceId: "vid", SegmentId: "vid_seg0001",
                InputPath: "in.mp4", OutputPath: "out.mp4",
                Start: TimeSpan.FromSeconds(5.123), Length: TimeSpan.FromSeconds(7.5));

            var a = CutStep.BuildCutArgs(job);

            Assert.Equal(new[] { "-ss", "5.123", "-i", "in.mp4", "-t", "7.5", "-c", "copy", "-y", "out.mp4" }, a);
        });
    }

    [Fact]
    public void Loopify_аргументи_без_коми_на_локалі_з_комою()
    {
        UnderUkrainianCulture(() =>
        {
            var crossfade = LoopifyStep.BuildCrossfadeArgs("in.mp4", "out.mp4", durationSec: 10.5, fadeSec: 1.5);
            var filter = crossfade[crossfade.ToList().IndexOf("-filter_complex") + 1];
            Assert.Contains("xfade=transition=fade:duration=1.5:offset=9[outv]", filter);
            Assert.Contains("acrossfade=d=1.5[outa]", filter);

            var trim = LoopifyStep.BuildTrimArgs("in.mp4", "out.mp4", trimToSec: 8.25);
            Assert.Equal("8.25", trim[trim.ToList().IndexOf("-t") + 1]);

            var ssim = LoopifyStep.BuildSsimArgs("in.mp4", "ref.png", "ssim.log", searchStart: 2.75);
            Assert.Equal("2.75", ssim[ssim.ToList().IndexOf("-ss") + 1]);
        });
    }

    [Fact]
    public void BuildSilenceSource_тривалість_із_крапкою_на_локалі_з_комою()
    {
        UnderUkrainianCulture(() =>
        {
            var s = AssembleStep.BuildSilenceSource(TimeSpan.FromSeconds(2.5));

            Assert.Equal("anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration=2.5", s);
        });
    }

    /// <summary>Виконує дію під українською локаллю й повертає попередню назад.</summary>
    private static void UnderUkrainianCulture(Action action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
        try { action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
