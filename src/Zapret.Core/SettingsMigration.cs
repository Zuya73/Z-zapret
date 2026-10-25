namespace Zapret.Core;

public static class SettingsMigration
{
    public static AppSettings ForBundle(AppSettings saved, IReadOnlyList<Strategy> strategies, IReadOnlyList<string> fakes)
    {
        if (strategies.Count == 0 || fakes.Count == 0) throw new InvalidDataException("Комплект профилей или фейков пуст.");
        string fallback = fakes.FirstOrDefault(x => x.StartsWith("quic_", StringComparison.OrdinalIgnoreCase)) ?? fakes[0];
        return saved with
        {
            StrategyId = strategies.Any(x => x.Id == saved.StrategyId) ? saved.StrategyId :
                strategies.FirstOrDefault(x => x.Id == "general")?.Id ?? strategies[0].Id,
            DiscordFake = fakes.Contains(saved.DiscordFake) ? saved.DiscordFake : fallback,
            GameFake = fakes.Contains(saved.GameFake) ? saved.GameFake : fallback
        };
    }
}
