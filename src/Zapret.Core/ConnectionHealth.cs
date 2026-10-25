namespace Zapret.Core;

public sealed record ServiceCheck(string Name, string Group, bool Available, double Milliseconds, string Detail);
public sealed record ConnectionHealth(DateTimeOffset CapturedAt, IReadOnlyList<ServiceCheck> Checks)
{
    public bool InternetAvailable => Checks.Any(x => x.Group == "Internet" && x.Available);
    public bool GroupAvailable(string group) => Checks.Any(x => x.Group == group) && Checks.Where(x => x.Group == group).All(x => x.Available);
    public bool ServicesAvailable => GroupAvailable("Discord") && GroupAvailable("YouTube");
    public bool ConfirmedFailure => InternetAvailable && !ServicesAvailable;
    public string Summary => string.Join(" · ", new[] { "Discord", "YouTube" }.Select(g =>
        g + (GroupAvailable(g) ? ": доступен" : ": не подтверждён")));
}

// A disconnected network or one failed endpoint must never cause a strategy-switch loop.
public sealed class ReconnectPolicy
{
    private int failures;
    private DateTimeOffset? lastFailure, firstFailure, lastAttempt;
    private readonly Queue<DateTimeOffset> attempts = new();
    public void ResetEvidence() { failures = 0; lastFailure = firstFailure = null; }
    public bool Observe(ConnectionHealth health, bool engineRunning, bool wanted, bool enabled)
    {
        if (!wanted || !enabled || !health.InternetAvailable || engineRunning && health.ServicesAvailable)
        { ResetEvidence(); return false; }
        if (lastFailure.HasValue && health.CapturedAt <= lastFailure.Value) return false;
        if (lastFailure.HasValue && health.CapturedAt - lastFailure.Value > TimeSpan.FromMinutes(3)) ResetEvidence();
        firstFailure ??= health.CapturedAt;
        lastFailure = health.CapturedAt;
        failures++;
        return failures >= 3 && health.CapturedAt - firstFailure.Value >= TimeSpan.FromSeconds(75) && CanAttempt(health.CapturedAt);
    }
    public bool CanAttempt(DateTimeOffset now)
    {
        while (attempts.TryPeek(out var old) && now - old >= TimeSpan.FromMinutes(15)) attempts.Dequeue();
        return attempts.Count < 3 && (!lastAttempt.HasValue || now - lastAttempt.Value >= TimeSpan.FromMinutes(2));
    }
    public bool BeginAttempt(DateTimeOffset now)
    {
        if (!CanAttempt(now)) return false;
        attempts.Enqueue(now); lastAttempt = now; ResetEvidence(); return true;
    }
}
