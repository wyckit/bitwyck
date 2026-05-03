namespace Bitwyck.Core.Interfaces;

public interface IChronoJob
{
    string JobId { get; }

    /// <summary>Cron expression: "m h d M w" — minute, hour, day-of-month, month, day-of-week.</summary>
    string CronExpression { get; }

    Task ExecuteAsync(CancellationToken ct);
}
