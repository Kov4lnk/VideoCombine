namespace VideoCombine.App.ViewModels;

using System.Windows.Media;
using VideoCombine.Core.Pipeline;

/// <summary>Один рядок логу на табі «Виконання» (розділ 7.1: три рівні з кольором).</summary>
public sealed class LogEntry
{
    public required string Mark { get; init; }
    public required string Message { get; init; }
    public required Brush Color { get; init; }

    public static LogEntry From(LogLevel level, string message) => new()
    {
        Message = message,
        Mark = level switch
        {
            LogLevel.Success => "✓",
            LogLevel.Warning => "⚠",
            LogLevel.Error => "✕",
            _ => "·"
        },
        Color = level switch
        {
            LogLevel.Success => Brushes.SeaGreen,
            LogLevel.Warning => Brushes.DarkGoldenrod,
            LogLevel.Error => Brushes.Firebrick,
            _ => Brushes.DimGray
        }
    };
}
