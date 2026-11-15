namespace VideoCombine.Core.External;

using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// Запускає зовнішній .exe (ffmpeg, yt-dlp, real-esrgan) і читає його вивід.
/// yt-dlp та ffmpeg — це окремі процеси, не бібліотеки (розділ 2 концепту).
///
/// ЖИТТЄВИЙ ЦИКЛ ПРОЦЕСУ (розділ 9, «Скасування»). Запущений процес не має
/// пережити ні скасування, ні саму програму — інакше лишається «сирота»:
/// ffmpeg далі їсть CPU/GPU і тримає файли заблокованими. Тому:
///   • скасування (token) → вбиваємо процес РАЗОМ із його дочірніми
///     (yt-dlp сам запускає ffmpeg для зведення доріжок) і чекаємо, поки він
///     справді завершиться, і лише тоді прокидаємо OperationCanceledException;
///   • вихід із програми (закрили вікно посеред прогону) → усі ще живі
///     процеси, запущені через ProcessRunner, вбиваються в обробнику ProcessExit.
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>Скільки чекати завершення вбитого процесу, перш ніж здатися.</summary>
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);

    // Усі процеси, що зараз біжать (з будь-якого екземпляра ProcessRunner).
    // Потрібно для прибирання при виході з програми. Значення не використовується.
    private static readonly ConcurrentDictionary<Process, byte> Running = new();

    static ProcessRunner()
    {
        // Звичайне завершення програми (закриття вікна, return з Main, Environment.Exit).
        // Аварійне «зняти задачу» в диспетчері цей обробник НЕ викликає — це свідома
        // межа простого рішення (див. docs/CHANGELOG.md, запис 001).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAllRunning();
    }

    /// <summary>Кількість процесів, що зараз біжать (для діагностики й тестів).</summary>
    public static int RunningCount => Running.Count;

    public async Task<int> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        Action<string>? onOutputLine,
        CancellationToken token)
    {
        // Якщо скасували ще до старту — процес не запускаємо взагалі.
        token.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutputLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutputLine?.Invoke(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Не вдалося запустити: {exePath}");

        Running.TryAdd(process, 0);
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Скасування само процес НЕ зупиняє — WaitForExitAsync лише перестає
                // чекати. Без цього блоку ffmpeg/yt-dlp лишався б працювати «сиротою».
                TryKillTree(process);
                await WaitForExitQuietlyAsync(process);
                throw;
            }

            return process.ExitCode;
        }
        finally
        {
            Running.TryRemove(process, out _);
        }
    }

    /// <summary>
    /// Вбиває всі процеси, що ще біжать. Викликається автоматично при виході з
    /// програми; публічний, щоб оболонка (WPF) могла викликати його й сама.
    /// </summary>
    public static void KillAllRunning()
    {
        foreach (var process in Running.Keys)
            TryKillTree(process);
    }

    /// <summary>Вбити процес із дочірніми. Ніколи не кидає — це прибирання.</summary>
    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Процес міг завершитись сам між перевіркою і Kill, або бути вже
            // звільненим — у будь-якому разі вбивати більше нічого.
        }
    }

    /// <summary>
    /// Дочекатися фактичного завершення вбитого процесу (щоб Windows відпустила
    /// файли, які він тримав), але не довше за KillWaitTimeout. Ніколи не кидає.
    /// </summary>
    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(KillWaitTimeout);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            // Не дочекались за відведений час — не блокуємо скасування вічно.
        }
    }
}
