namespace VideoCombine.Tests;

using VideoCombine.Core.Models;
using VideoCombine.Core.Steps;
using Xunit;

/// <summary>
/// Тести чистих методів кроків Фази B — без реального відео й без exe.
/// Перевіряють розбір/складання, а не виклики ffmpeg.
/// </summary>
public sealed class StepLogicTests
{
    // --- AnalyzeStep.ParseProbe: розбір JSON-виводу ffprobe ---

    [Fact]
    public void ParseProbe_бере_тривалість_розмір_і_fps()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "width": 1920, "height": 1080, "r_frame_rate": "30000/1001", "duration": "12.345" },
            { "codec_type": "audio", "r_frame_rate": "0/0" }
          ],
          "format": { "duration": "12.345678" }
        }
        """;

        var r = AnalyzeStep.ParseProbe(json);

        Assert.NotNull(r.Duration);
        Assert.Equal(12.345678, r.Duration!.Value.TotalSeconds, precision: 3);
        Assert.Equal(1920, r.Width);
        Assert.Equal(1080, r.Height);
        Assert.Equal(29.97, r.Fps!.Value, precision: 2);
    }

    [Fact]
    public void ParseProbe_тривалість_із_потоку_коли_нема_у_format()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "width": 640, "height": 480, "r_frame_rate": "25/1", "duration": "8.0" }
          ],
          "format": {}
        }
        """;

        var r = AnalyzeStep.ParseProbe(json);

        Assert.Equal(8.0, r.Duration!.Value.TotalSeconds, precision: 3);
        Assert.Equal(25.0, r.Fps!.Value, precision: 2);
    }

    [Fact]
    public void ParseProbe_без_відеопотоку_дає_null_розмір()
    {
        const string json = """
        { "streams": [ { "codec_type": "audio" } ], "format": { "duration": "3.5" } }
        """;

        var r = AnalyzeStep.ParseProbe(json);

        Assert.Equal(3.5, r.Duration!.Value.TotalSeconds, precision: 3);
        Assert.Null(r.Width);
        Assert.Null(r.Height);
    }

    // --- AssembleStep.BuildConcatList: список для concat demuxer ---

    [Fact]
    public void BuildConcatList_формує_рядки_file()
    {
        var segs = new[]
        {
            Seg("a", @"C:\work\a.mp4"),
            Seg("b", @"C:\work\b.mp4")
        };

        var list = AssembleStep.BuildConcatList(segs);

        Assert.Equal("file 'C:\\work\\a.mp4'\nfile 'C:\\work\\b.mp4'\n", list);
    }

    [Fact]
    public void BuildConcatList_екранує_одинарні_лапки()
    {
        var segs = new[] { Seg("x", "/tmp/it's a clip.mp4") };

        var list = AssembleStep.BuildConcatList(segs);

        // Одинарна лапка в concat-синтаксисі екранується як '\''
        Assert.Equal("file '/tmp/it'\\''s a clip.mp4'\n", list);
    }

    // --- AssembleStep.OrderSegments: порядок сегментів ---

    [Fact]
    public void OrderSegments_sequential_не_міняє_порядок()
    {
        var segs = new[] { Seg("1", "1"), Seg("2", "2"), Seg("3", "3") };

        var r = AssembleStep.OrderSegments(segs, "sequential", seed: 0);

        Assert.Equal(new[] { "1", "2", "3" }, r.Select(s => s.Id));
    }

    [Fact]
    public void OrderSegments_random_детермінований_за_seed()
    {
        var segs = Enumerable.Range(0, 10).Select(i => Seg(i.ToString(), i.ToString())).ToArray();

        var a = AssembleStep.OrderSegments(segs, "random", seed: 42).Select(s => s.Id).ToArray();
        var b = AssembleStep.OrderSegments(segs, "random", seed: 42).Select(s => s.Id).ToArray();

        Assert.Equal(a, b); // той самий seed → той самий порядок
        Assert.Equal(10, a.Length);
        Assert.Equal(segs.Select(s => s.Id).OrderBy(x => x), a.OrderBy(x => x)); // нічого не загублено
    }

    // --- DownloadSettings.BuildFormatSelector: селектор якості yt-dlp ---

    [Theory]
    [InlineData("best", "bv*+ba/b")]
    [InlineData("720p", "bv*[height<=720]+ba/b[height<=720]")]
    [InlineData("1080p", "bv*[height<=1080]+ba/b[height<=1080]")]
    [InlineData("480", "bv*[height<=480]+ba/b[height<=480]")]
    public void BuildFormatSelector_перетворює_якість(string quality, string expected)
    {
        var s = new DownloadSettings { Quality = quality };
        Assert.Equal(expected, s.BuildFormatSelector());
    }

    private static Segment Seg(string id, string path) =>
        new() { Id = id, FilePath = path, SourceId = "src", Start = TimeSpan.Zero, Duration = TimeSpan.FromSeconds(1) };
}
