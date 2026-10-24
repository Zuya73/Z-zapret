namespace Zapret.Core;

public static class Ranking
{
    public static bool Eligible(StrategyReport report, IReadOnlyCollection<ProbeTarget> targets)
    {
        if (report.StartError != null || report.Passed == 0 || report.SuccessRate < .80) return false;
        // Missing results and unsupported protocol checks never count as successful coverage.
        foreach (var target in targets.Where(x => !x.PingOnly))
        {
            var checks = report.Results.Where(x => x.TargetId == target.Id && x.Protocol != "PING").ToArray();
            if (checks.Length == 0) return false;
            if (target.Required && (checks.All(x => x.Status != ProbeStatus.Passed) ||
                checks.Any(x => x.Status is ProbeStatus.Failed or ProbeStatus.Inconclusive))) return false;
        }
        return true;
    }

    public static IEnumerable<StrategyReport> Order(IEnumerable<StrategyReport> reports) => reports
        .OrderByDescending(x => x.SuccessRate).ThenByDescending(x => x.Passed).ThenBy(x => x.AverageMs)
        .ThenBy(x => x.StrategyId, StringComparer.Ordinal);

    public static string? Recommend(IEnumerable<StrategyReport> first, IEnumerable<StrategyReport> confirmation,
        IReadOnlyCollection<ProbeTarget> targets)
    {
        var verified = confirmation.Where(x => Eligible(x, targets)).ToDictionary(x => x.StrategyId);
        return Order(first.Where(x => Eligible(x, targets) && verified.ContainsKey(x.StrategyId)))
            .Select(x => x.StrategyId).FirstOrDefault();
    }
}
