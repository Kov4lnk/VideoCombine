namespace VideoCombine.Core.Pipeline;

/// <summary>
/// Проганяє кроки ПО ЧЕРЗІ (конвеєр — розділ 8.1: кроки залежать один від одного).
/// Паралельність живе ВСЕРЕДИНІ кроків (розділ 8.2), не тут.
/// </summary>
public sealed class PipelineExecutor
{
    /// <summary>
    /// Виконати список увімкнених кроків у заданому (фіксованому) порядку.
    /// Перед кожним перевіряє передумови; на критичній помилці — зупиняє.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<IStep> enabledSteps,
        PipelineContext context,
        IPipelineProgress progress,
        CancellationToken token)
    {
        for (int i = 0; i < enabledSteps.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var step = enabledSteps[i];

            // Перевірка передумов (розділ 4.2) — блокуючі зупиняють до старту.
            var blocking = step.GetRequirements(context).Where(r => r.IsBlocking).ToList();
            if (blocking.Count > 0)
            {
                var reasons = string.Join("; ", blocking.Select(b => b.Message));
                throw new PipelineException($"Крок '{step.DisplayName}' не може стартувати: {reasons}");
            }

            progress.BeginStep(i, enabledSteps.Count, step.DisplayName);
            var reporter = progress.CreateReporterForCurrentStep();

            try
            {
                await step.ExecuteAsync(context, reporter, token);
                progress.CompleteStep(step.DisplayName);
            }
            catch (OperationCanceledException)
            {
                progress.Cancelled(step.DisplayName);
                throw;
            }
            catch (Exception ex)
            {
                // Критична помилка кроку зупиняє прогон (розділ 7.1).
                reporter.Log(LogLevel.Error, $"{step.DisplayName}: {ex.Message}");
                throw new PipelineException($"Крок '{step.DisplayName}' впав: {ex.Message}", ex);
            }
        }

        progress.Finished(context.FinalOutputPath);
    }
}

public sealed class PipelineException : Exception
{
    public PipelineException(string message, Exception? inner = null) : base(message, inner) { }
}
