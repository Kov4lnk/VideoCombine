namespace VideoCombine.Tests;

using VideoCombine.Core.External;
using VideoCombine.Core.Models;
using VideoCombine.Core.Pipeline;
using VideoCombine.Core.Providers;
using VideoCombine.Core.Recipes;
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

    // --- AssembleStep.BuildConcatFilter: нормалізація + concat ---

    [Fact]
    public void BuildConcatFilter_fit_паддить_кожен_вхід_і_зшиває()
    {
        var f = AssembleStep.BuildConcatFilter(n: 2, width: 1920, height: 1080, fps: 30, fitMode: "fit");

        // fit: вписати (decrease) + pad чорними полями до 1920x1080, setsar=1, fps=30.
        Assert.Contains("[0:v:0]scale=1920:1080:force_original_aspect_ratio=decrease", f);
        Assert.Contains("pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30[v0]", f);
        Assert.Contains("[1:v:0]scale=1920:1080", f);
        // Аудіо ресемплиться, потоки зшиваються concat'ом.
        Assert.Contains("[0:a:0]aresample=async=1[a0]", f);
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1[outv][outa]", f);
    }

    [Fact]
    public void BuildConcatFilter_один_сегмент()
    {
        var f = AssembleStep.BuildConcatFilter(n: 1, width: 1280, height: 720, fps: 25, fitMode: "fit");

        Assert.Contains("[0:v:0]scale=1280:720", f);
        Assert.Contains("[v0][a0]concat=n=1:v=1:a=1[outv][outa]", f);
    }

    [Fact]
    public void BuildConcatFilter_crop_заповнює_і_ріже_без_pad()
    {
        // crop під вертикаль 9:16 з горизонтального джерела.
        var f = AssembleStep.BuildConcatFilter(n: 2, width: 1080, height: 1920, fps: 30, fitMode: "crop");

        // crop: заповнити (increase) + crop до WxH центром, setsar=1, fps.
        Assert.Contains("[0:v:0]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30[v0]", f);
        // Без чорних полів — pad бути не повинно.
        Assert.DoesNotContain("pad=", f);
        Assert.DoesNotContain("force_original_aspect_ratio=decrease", f);
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1[outv][outa]", f);
    }

    // --- PlatformProfile: вбудовані пресети ---

    [Fact]
    public void PlatformProfile_вертикальні_crop_горизонтальні_fit()
    {
        var all = PlatformProfile.All;

        Assert.Equal("Універсальний 16:9 1080p", PlatformProfile.Default.Name);
        Assert.Equal((1920, 1080, "fit"), Pick("Універсальний 16:9 1080p"));
        Assert.Equal((1920, 1080, "fit"), Pick("YouTube 16:9 1080p"));
        Assert.Equal((1080, 1920, "crop"), Pick("Вертикальне 9:16 (Shorts/TikTok/Reels)"));

        (int, int, string) Pick(string name)
        {
            var p = all.First(x => x.Name == name);
            return (p.Width, p.Height, p.FitMode);
        }
    }

    // --- AssembleStep.BuildVideoCodecArgs: CPU/GPU × quality/bitrate ---

    [Fact]
    public void BuildVideoCodecArgs_libx264_quality_дає_crf_і_пресет()
    {
        var a = AssembleStep.BuildVideoCodecArgs(
            codec: "libx264", x264Preset: "medium", crf: 20, nvencPreset: "p5", nvencCq: 23,
            rateMode: "quality", bitrateK: 6000);

        Assert.Equal(
            new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20" }, a);
    }

    [Fact]
    public void BuildVideoCodecArgs_nvenc_quality_дає_cq_без_crf()
    {
        var a = AssembleStep.BuildVideoCodecArgs(
            codec: "nvenc", x264Preset: "medium", crf: 20, nvencPreset: "p5", nvencCq: 23,
            rateMode: "quality", bitrateK: 6000);

        Assert.Equal(
            new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "23", "-b:v", "0" }, a);
        // NVENC не розуміє -crf — його тут бути не повинно.
        Assert.DoesNotContain("-crf", a);
        // І не libx264.
        Assert.DoesNotContain("libx264", a);
    }

    [Fact]
    public void BuildVideoCodecArgs_libx264_bitrate_дає_bv_без_crf()
    {
        var a = AssembleStep.BuildVideoCodecArgs(
            codec: "libx264", x264Preset: "fast", crf: 20, nvencPreset: "p5", nvencCq: 23,
            rateMode: "bitrate", bitrateK: 8000);

        Assert.Equal(
            new[] { "-c:v", "libx264", "-preset", "fast", "-b:v", "8000k" }, a);
        // У режимі бітрейту якість через -crf не задаємо.
        Assert.DoesNotContain("-crf", a);
    }

    [Fact]
    public void BuildVideoCodecArgs_nvenc_bitrate_дає_bv_без_cq()
    {
        var a = AssembleStep.BuildVideoCodecArgs(
            codec: "nvenc", x264Preset: "medium", crf: 20, nvencPreset: "p4", nvencCq: 23,
            rateMode: "bitrate", bitrateK: 12000);

        Assert.Equal(
            new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-b:v", "12000k" }, a);
        // -cq у режимі бітрейту відсутній (інакше vbr тяжіє до якості, не розміру).
        Assert.DoesNotContain("-cq", a);
    }

    [Fact]
    public void BuildVideoCodecArgs_невідомий_кодек_відкочується_на_libx264()
    {
        // Будь-що, крім "nvenc", трактуємо як CPU-шлях (безпечний дефолт).
        var a = AssembleStep.BuildVideoCodecArgs(
            codec: "щось_дивне", x264Preset: "slow", crf: 18, nvencPreset: "p7", nvencCq: 19,
            rateMode: "quality", bitrateK: 6000);

        Assert.Equal(
            new[] { "-c:v", "libx264", "-preset", "slow", "-crf", "18" }, a);
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

    // --- DownloadStep.GetRequirements: локальні файли vs URL ---

    [Fact]
    public void Requirements_лише_локальні_файли_не_вимагають_ytdlp()
    {
        var step = new DownloadStep(new ProcessRunner(),
            new DownloadSettings { LocalFiles = { @"C:\video.mp4" } });
        // yt-dlp НЕ вказано — але для самих локальних це не має блокувати.
        var ctx = MakeContext(ytDlpPath: "");

        var blocking = step.GetRequirements(ctx).Where(r => r.IsBlocking).ToList();

        Assert.Empty(blocking);
    }

    [Fact]
    public void Requirements_url_без_ytdlp_блокує()
    {
        var step = new DownloadStep(new ProcessRunner(),
            new DownloadSettings { Urls = { "https://example.com/v" } });
        var ctx = MakeContext(ytDlpPath: "");

        var blocking = step.GetRequirements(ctx).Where(r => r.IsBlocking).ToList();

        Assert.Contains(blocking, r => r.Message.Contains("yt-dlp"));
    }

    [Fact]
    public void Requirements_порожні_джерела_блокують()
    {
        var step = new DownloadStep(new ProcessRunner(), new DownloadSettings());
        var ctx = MakeContext(ytDlpPath: @"C:\yt-dlp.exe");

        var blocking = step.GetRequirements(ctx).Where(r => r.IsBlocking).ToList();

        Assert.Single(blocking);
    }

    // --- RecipeMapper: round-trip опцій експорту через JSON (JsonElement-пастка) ---

    [Fact]
    public void RecipeMapper_round_trip_зберігає_опції_експорту()
    {
        // Нетипові значення: бітрейт + контейнер mkv + GPU-кодек + вертикальний профіль crop.
        var assemble = new AssembleSettings
        {
            Order = "random",
            VideoCodec = "nvenc",
            RateMode = "bitrate",
            Crf = 17,
            NvencCq = 21,
            EncodePreset = "slow",
            NvencPreset = "p6",
            VideoBitrateK = 9000,
            TargetWidth = 1080,
            TargetHeight = 1920,
            TargetFps = 30,
            FitMode = "crop",
            OutputFileName = "output.mkv"
        };
        var cut = new CutSettings { Mode = "fixed", MinSec = 2, MaxSec = 5 };

        var recipe = RecipeMapper.ToRecipe(
            "test", "720p", cut, new UpscaleSettings(), assemble,
            new SubtitleSettings(), new TtsSettings());

        // Кругообіг через JSON тими самими опціями, що й RecipeStore — після нього
        // значення в Settings вертаються як JsonElement, а не raw int/string.
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var json = System.Text.Json.JsonSerializer.Serialize(recipe, opts);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<Recipe>(json, opts)!;

        var r = RecipeMapper.ReadAssembleSettings(loaded);

        Assert.Equal("random", r.Order);
        Assert.Equal("nvenc", r.VideoCodec);
        Assert.Equal("bitrate", r.RateMode);
        Assert.Equal(17, r.Crf);
        Assert.Equal(21, r.NvencCq);
        Assert.Equal("slow", r.EncodePreset);
        Assert.Equal("p6", r.NvencPreset);
        Assert.Equal(9000, r.VideoBitrateK);
        Assert.Equal(1080, r.TargetWidth);
        Assert.Equal(1920, r.TargetHeight);
        Assert.Equal(30, r.TargetFps);
        Assert.Equal("crop", r.FitMode);
        Assert.Equal("output.mkv", r.OutputFileName);
        Assert.Equal("720p", RecipeMapper.ReadQuality(loaded));
    }

    [Fact]
    public void RecipeMapper_відсутні_ключі_дають_дефолти()
    {
        // Старий рецепт без нових ключів (лише order/videoCodec) → дефолти AssembleSettings.
        var recipe = new Recipe
        {
            Name = "legacy",
            Steps = { new RecipeStep { TypeId = "assemble", Settings = { ["order"] = "sequential" } } }
        };
        var def = new AssembleSettings();

        var r = RecipeMapper.ReadAssembleSettings(recipe);

        Assert.Equal(def.RateMode, r.RateMode);
        Assert.Equal(def.Crf, r.Crf);
        Assert.Equal(def.VideoBitrateK, r.VideoBitrateK);
        Assert.Equal("output.mp4", r.OutputFileName);
    }

    // --- FFmpegScaleUpscaler.BuildScaleArgs: аргументи масштабування ---

    [Fact]
    public void BuildScaleArgs_множить_розмір_парно_lanczos()
    {
        var a = FFmpegScaleUpscaler.BuildScaleArgs(@"C:\work\seg.mp4", @"C:\work\seg_up2.mp4", scale: 2);

        // Множник у формулі, привід до парного (trunc/2*2), lanczos-фільтр.
        Assert.Contains("scale=trunc(iw*2/2)*2:trunc(ih*2/2)*2:flags=lanczos", a);
        // Відео перекодовуємо, аудіо копіюємо без змін.
        Assert.Contains("libx264", a);
        Assert.Equal("copy", a[a.ToList().IndexOf("-c:a") + 1]);
        // Вхід і вихід на місці.
        Assert.Equal(@"C:\work\seg.mp4", a[a.ToList().IndexOf("-i") + 1]);
        Assert.Equal(@"C:\work\seg_up2.mp4", a[^1]);
    }

    [Fact]
    public void BuildScaleArgs_множник_4_у_формулі()
    {
        var a = FFmpegScaleUpscaler.BuildScaleArgs("in.mp4", "out.mp4", scale: 4);

        Assert.Contains("scale=trunc(iw*4/2)*2:trunc(ih*4/2)*2:flags=lanczos", a);
    }

    // --- RecipeMapper: round-trip апскейлу через JSON ---

    [Fact]
    public void RecipeMapper_round_trip_зберігає_апскейл()
    {
        var upscale = new UpscaleSettings { Enabled = true, Provider = "ffmpeg-scale", Scale = 4 };

        var recipe = RecipeMapper.ToRecipe(
            "test", "720p", new CutSettings(), upscale, new AssembleSettings(),
            new SubtitleSettings(), new TtsSettings());

        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var json = System.Text.Json.JsonSerializer.Serialize(recipe, opts);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<Recipe>(json, opts)!;

        var r = RecipeMapper.ReadUpscaleSettings(loaded);

        Assert.True(r.Enabled);
        Assert.Equal("ffmpeg-scale", r.Provider);
        Assert.Equal(4, r.Scale);
    }

    [Fact]
    public void RecipeMapper_апскейл_відсутній_крок_дає_вимкнено()
    {
        // Старий рецепт без кроку upscale → дефолти UpscaleSettings (Enabled=false).
        var recipe = new Recipe
        {
            Name = "legacy",
            Steps = { new RecipeStep { TypeId = "cut", Settings = { ["mode"] = "random" } } }
        };

        var r = RecipeMapper.ReadUpscaleSettings(recipe);

        Assert.False(r.Enabled);
        Assert.Equal(new UpscaleSettings().Scale, r.Scale);
    }

    // --- SubtitleStep: аргументи whisper.cpp / витягування wav / burn-in ---

    [Fact]
    public void BuildExtractWavArgs_16k_mono_pcm_без_відео()
    {
        var a = SubtitleStep.BuildExtractWavArgs(@"C:\out\output.mp4", @"C:\work\output_whisper.wav");

        // whisper.cpp очікує саме 16 kHz mono PCM; відео відкидаємо (-vn).
        Assert.Contains("-vn", a);
        Assert.Equal("16000", a[a.ToList().IndexOf("-ar") + 1]);
        Assert.Equal("1", a[a.ToList().IndexOf("-ac") + 1]);
        Assert.Equal("pcm_s16le", a[a.ToList().IndexOf("-c:a") + 1]);
        Assert.Equal(@"C:\work\output_whisper.wav", a[^1]);
    }

    [Fact]
    public void BuildWhisperArgs_модель_вхід_srt_автомова()
    {
        var a = SubtitleStep.BuildWhisperArgs(@"C:\work\a.wav", @"C:\out\output", @"C:\models\ggml-base.bin");

        Assert.Equal(@"C:\models\ggml-base.bin", a[a.ToList().IndexOf("-m") + 1]);
        Assert.Equal(@"C:\work\a.wav", a[a.ToList().IndexOf("-f") + 1]);
        // Автовизначення мови й вивід SRT.
        Assert.Equal("auto", a[a.ToList().IndexOf("-l") + 1]);
        Assert.Contains("-osrt", a);
        // -of БЕЗ розширення — whisper.cpp сам допише .srt.
        Assert.Equal(@"C:\out\output", a[a.ToList().IndexOf("-of") + 1]);
        Assert.DoesNotContain(".srt", a[a.ToList().IndexOf("-of") + 1]);
    }

    [Theory]
    // Windows: \ → /, двокрапка диска екранується для filter-синтаксису ffmpeg.
    [InlineData(@"C:\out\output.srt", "C\\:/out/output.srt")]
    [InlineData("/tmp/a.srt", "/tmp/a.srt")]
    public void EscapeSubtitlesPath_екранує_для_фільтра(string input, string expected)
    {
        Assert.Equal(expected, SubtitleStep.EscapeSubtitlesPath(input));
    }

    [Fact]
    public void BuildBurnInArgs_вписує_субтитри_екранованим_шляхом()
    {
        var a = SubtitleStep.BuildBurnInArgs(@"C:\out\output.mp4", @"C:\out\output.srt", @"C:\out\output_sub.mp4");

        // Фільтр subtitles= з екранованим шляхом; відео перекодовуємо, аудіо копіюємо.
        var vf = a[a.ToList().IndexOf("-vf") + 1];
        Assert.Contains("subtitles='C\\:/out/output.srt'", vf);
        Assert.Contains("libx264", a);
        Assert.Equal("copy", a[a.ToList().IndexOf("-c:a") + 1]);
        Assert.Equal(@"C:\out\output_sub.mp4", a[^1]);
    }

    // --- SubtitleStep/TtsStep.GetRequirements: блокуючі передумови ---

    [Fact]
    public void Subtitle_Requirements_без_фіналу_whisper_моделі_блокує()
    {
        var step = new SubtitleStep(new ProcessRunner(), new SubtitleSettings { Enabled = true });
        // Порожній контекст: нема FinalOutputPath, нема whisper/моделі.
        var ctx = MakeContext(ytDlpPath: "");

        var blocking = step.GetRequirements(ctx).Where(r => r.IsBlocking).ToList();

        // Усі три причини мають підсвітитись (фінал, whisper, модель).
        Assert.Equal(3, blocking.Count);
    }

    [Fact]
    public void Tts_Requirements_без_тексту_блокує()
    {
        var step = new TtsStep(new PiperTtsProvider(new ProcessRunner(), "piper"), new TtsSettings { Enabled = true });
        var ctx = MakeContext(ytDlpPath: ""); // VoiceoverText порожній

        var blocking = step.GetRequirements(ctx).Where(r => r.IsBlocking).ToList();

        // Локальний Piper: єдина причина — нема тексту (ключ не потрібен).
        Assert.Single(blocking);
        Assert.Contains(blocking, r => r.Message.Contains("текст", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tts_Requirements_хмара_додатково_вимагає_ключа()
    {
        var step = new TtsStep(new CloudTtsProvider("elevenlabs"), new TtsSettings { Enabled = true });
        var ctx = MakeContext(ytDlpPath: "");

        var blocking = step.GetRequirements(ctx).Where(r => r.IsBlocking).ToList();

        // Хмара: нема тексту + нема ключа = дві причини.
        Assert.Equal(2, blocking.Count);
        Assert.Contains(blocking, r => r.Message.Contains("ключ", StringComparison.OrdinalIgnoreCase));
    }

    // --- RecipeMapper: round-trip субтитрів і TTS через JSON ---

    [Fact]
    public void RecipeMapper_round_trip_зберігає_субтитри_і_tts()
    {
        var subtitle = new SubtitleSettings { Enabled = true, BurnIn = true };
        var tts = new TtsSettings { Enabled = true, Provider = "cloud", VoiceId = "voice-42", Speed = 1.25 };

        var recipe = RecipeMapper.ToRecipe(
            "test", "720p", new CutSettings(), new UpscaleSettings(), new AssembleSettings(),
            subtitle, tts);

        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var json = System.Text.Json.JsonSerializer.Serialize(recipe, opts);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<Recipe>(json, opts)!;

        var s = RecipeMapper.ReadSubtitleSettings(loaded);
        Assert.True(s.Enabled);
        Assert.True(s.BurnIn);

        var t = RecipeMapper.ReadTtsSettings(loaded);
        Assert.True(t.Enabled);
        Assert.Equal("cloud", t.Provider);
        Assert.Equal("voice-42", t.VoiceId);
        Assert.Equal(1.25, t.Speed, precision: 3);
    }

    [Fact]
    public void RecipeMapper_субтитри_tts_відсутні_кроки_дають_дефолти()
    {
        // Старий рецепт без кроків subtitle/tts → дефолти (вимкнено).
        var recipe = new Recipe
        {
            Name = "legacy",
            Steps = { new RecipeStep { TypeId = "cut", Settings = { ["mode"] = "random" } } }
        };

        var s = RecipeMapper.ReadSubtitleSettings(recipe);
        var t = RecipeMapper.ReadTtsSettings(recipe);

        Assert.False(s.Enabled);
        Assert.False(s.BurnIn);
        Assert.False(t.Enabled);
        Assert.Equal(new TtsSettings().Provider, t.Provider);
    }

    private static PipelineContext MakeContext(string ytDlpPath) => new()
    {
        WorkingDirectory = @"C:\work",
        OutputDirectory = @"C:\out",
        Settings = new AppSettings { YtDlpPath = ytDlpPath }
    };

    private static Segment Seg(string id, string path) =>
        new() { Id = id, FilePath = path, SourceId = "src", Start = TimeSpan.Zero, Duration = TimeSpan.FromSeconds(1) };
}
