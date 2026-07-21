namespace AIUsage.Core.Support;

/// <summary>
/// Burn-rate pacing for a bounded metric. Direct port of the Swift Pace enum.
/// </summary>
public static class Pace
{
    public enum Status
    {
        Ahead,
        OnTrack,
        Behind
    }

    public sealed record Result(Status Status, double ProjectedUsage);

    public static double MinimumElapsed(double periodDurationSeconds) => Math.Max(60, periodDurationSeconds * 0.01);

    public static Result? Evaluate(double used, double limit, DateTimeOffset resetsAt, double periodDurationSeconds, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        if (limit <= 0 || periodDurationSeconds <= 0) return null;
        var windowStart = resetsAt.AddSeconds(-periodDurationSeconds);
        var elapsed = (now - windowStart).TotalSeconds;
        if (elapsed < MinimumElapsed(periodDurationSeconds) || now >= resetsAt) return null;

        if (used <= 0) return new Result(Status.Ahead, 0);
        var projected = used / elapsed * periodDurationSeconds;
        if (used >= limit) return new Result(Status.Behind, projected);

        Status status;
        if (projected <= limit * 0.9) status = Status.Ahead;
        else if (projected <= limit) status = Status.OnTrack;
        else status = Status.Behind;
        return new Result(status, projected);
    }

    public static double? SecondsToRunOut(double used, double limit, DateTimeOffset resetsAt, double periodDurationSeconds, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var result = Evaluate(used, limit, resetsAt, periodDurationSeconds, now);
        if (result is null || result.Status != Status.Behind) return null;
        var rate = result.ProjectedUsage / periodDurationSeconds;
        if (rate <= 0) return null;
        var eta = (limit - used) / rate;
        var remaining = (resetsAt - now).TotalSeconds;
        if (eta <= 0 || eta >= remaining) return null;
        return eta;
    }
}
