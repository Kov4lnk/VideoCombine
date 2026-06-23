namespace VideoCombine.Core.Pipeline;

/// <summary>
/// СЕРЦЕ АРХІТЕКТУРИ. Кожна дія в програмі — це крок із цим інтерфейсом.
/// Завантаження, нарізка, унікалізатор, апскейл, озвучка, склейка — усі реалізують IStep.
///
/// Додати нову функцію = додати новий клас, що реалізує IStep. Решта програми не змінюється.
/// </summary>
public interface IStep
{
    /// <summary>Унікальний тип кроку (для JSON-рецептів), напр. "download", "cut", "upscale".</summary>
    string TypeId { get; }

    /// <summary>Людська назва для GUI, напр. "Завантаження".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Скільки незалежних одиниць можна обробляти одночасно (розділ 8 — паралельність).
    /// FFmpeg/CPU: багато; AI-апскейл/GPU: 1-2; хмарні API: за лімітом провайдера.
    /// </summary>
    int MaxConcurrency { get; }

    /// <summary>
    /// Передумови кроку (розділ 4.2 — залежності).
    /// Напр. b-roll "за змістом" повертає тут потребу в транскрипції.
    /// Виконавець/GUI перевіряє це й попереджає, перш ніж запускати.
    /// </summary>
    IReadOnlyList<StepRequirement> GetRequirements(PipelineContext context);

    /// <summary>
    /// Виконати крок. Читає потрібне з context, робить роботу, дописує результат назад.
    /// progress — канал звітування (розділ 7): крок ЗОБОВ'ЯЗАНИЙ звітувати.
    /// token — для скасування довгих операцій (важливо для апскейлу).
    /// </summary>
    Task ExecuteAsync(PipelineContext context, IProgressReporter progress, CancellationToken token);
}

/// <summary>Невиконана передумова кроку — показується в UI як попередження.</summary>
public sealed record StepRequirement(string Message, bool IsBlocking);
