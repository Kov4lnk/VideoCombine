namespace VideoCombine.Core.External;

using System.Diagnostics;

/// <summary>
/// Запускає зовнішній .exe (ffmpeg, yt-dlp, real-esrgan) і читає його вивід.
/// yt-dlp та ffmpeg — це окремі процеси, не бібліотеки (розділ 2 концепту).
/// </summary>
public sealed class ProcessRunner
{
    public async Task<int> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        Action<string>? onOutputLine,
        CancellationToken token)
    {
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

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(token);
        return process.ExitCode;
    }
}
