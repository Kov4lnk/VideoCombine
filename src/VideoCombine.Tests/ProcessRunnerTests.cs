namespace VideoCombine.Tests;

using System.Diagnostics;
using VideoCombine.Core.External;
using Xunit;

/// <summary>
/// Тести життєвого циклу зовнішніх процесів (docs/CHANGELOG.md, запис 001).
/// На відміну від StepLogicTests, тут запускаються СПРАВЖНІ процеси — інакше
/// «сироту» не зловити. Замість ffmpeg беремо те, що є на будь-якій машині:
/// PowerShell на Windows, sh деінде. Процес сам друкує свій PID — так тест
/// дізнається, кого перевіряти після скасування, без зміни API ProcessRunner.
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Скасування_вбиває_запущений_процес()
    {
        // Процес друкує власний PID і «зависає» на хвилину.
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "$PID; Start-Sleep -Seconds 60" })
            : ("sh", new[] { "-c", "echo $$; exec sleep 60" });

        var pid = await RunCancelAndGetPidAsync(exe, args);

        Assert.False(await IsAliveAfterGraceAsync(pid), $"процес {pid} пережив скасування (сирота)");
    }

    [Fact]
    public async Task Скасування_вбиває_і_дочірні_процеси()
    {
        // Модель yt-dlp → ffmpeg: батько запускає дитину, друкує ЇЇ PID і чекає на неї.
        // Після скасування має зникнути саме дитина — вбити лише батька недостатньо.
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell", new[]
              {
                  "-NoProfile", "-Command",
                  "$c = Start-Process -FilePath ping -ArgumentList '-n','60','127.0.0.1' " +
                  "-PassThru -WindowStyle Hidden; $c.Id; Wait-Process -Id $c.Id"
              })
            : ("sh", new[] { "-c", "sleep 60 & echo $!; wait" });

        var childPid = await RunCancelAndGetPidAsync(exe, args);

        Assert.False(await IsAliveAfterGraceAsync(childPid),
            $"дочірній процес {childPid} пережив скасування (сирота)");
    }

    [Fact]
    public async Task Скасований_заздалегідь_токен_не_запускає_процес()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Неіснуючий exe: якби RunAsync спробував стартувати, впав би іншим винятком.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync("this-exe-does-not-exist", Array.Empty<string>(), null, cts.Token));
    }

    [Fact]
    public async Task Звичайний_прогін_повертає_код_і_вивід()
    {
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "echo hello& exit 3" })
            : ("sh", new[] { "-c", "echo hello; exit 3" });

        var lines = new List<string>();
        var code = await new ProcessRunner().RunAsync(exe, args, l => { lock (lines) lines.Add(l); }, CancellationToken.None);

        Assert.Equal(3, code);
        Assert.Contains(lines, l => l.Trim() == "hello");
    }

    /// <summary>
    /// Запускає процес, чекає перший рядок виводу (PID), скасовує й перевіряє,
    /// що RunAsync завершився саме скасуванням. Повертає надрукований PID.
    /// </summary>
    private static async Task<int> RunCancelAndGetPidAsync(string exe, string[] args)
    {
        using var cts = new CancellationTokenSource();
        var pidSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = new ProcessRunner().RunAsync(exe, args, line =>
        {
            if (int.TryParse(line.Trim(), out var parsed))
                pidSource.TrySetResult(parsed);
        }, cts.Token);

        // PID має з'явитися швидко; 20 с — запас на холодний старт PowerShell.
        var first = await Task.WhenAny(pidSource.Task, run, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(first == pidSource.Task, "процес не надрукував PID (не стартував?)");
        var pid = await pidSource.Task;

        Assert.True(IsAlive(pid), $"процес {pid} мав би ще працювати до скасування");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        return pid;
    }

    /// <summary>Дає процесу до 3 с зникнути (вбивство дерева не миттєве) і каже, чи він ще живий.</summary>
    private static async Task<bool> IsAliveAfterGraceAsync(int pid)
    {
        for (int i = 0; i < 30; i++)
        {
            if (!IsAlive(pid)) return false;
            await Task.Delay(100);
        }
        return IsAlive(pid);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
        }
        catch (ArgumentException) { return false; }          // такого PID уже нема
        catch (InvalidOperationException) { return false; }  // процес завершився

        // Linux: убитий процес, якого ще не «прибрав» батько, висить як зомбі
        // (стан Z у /proc/<pid>/stat). Він уже не працює — вважаємо мертвим.
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var close = stat.LastIndexOf(')');
                if (close > 0 && stat.Length > close + 2 && stat[close + 2] == 'Z') return false;
            }
            catch { return false; }
        }
        return true;
    }
}
