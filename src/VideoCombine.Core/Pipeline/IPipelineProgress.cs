namespace VideoCombine.Core.Pipeline;

/// <summary>
/// Прогрес рівня всього пайплайну (загальний лоад-бар + перемикання кроків).
/// Реалізується у WPF-шарі (таб "Виконання"). Core лише викликає.
/// </summary>
public interface IPipelineProgress
{
    void BeginStep(int index, int total, string stepName);
    IProgressReporter CreateReporterForCurrentStep();
    void CompleteStep(string stepName);
    void Cancelled(string stepName);
    void Finished(string? finalOutputPath);
}
