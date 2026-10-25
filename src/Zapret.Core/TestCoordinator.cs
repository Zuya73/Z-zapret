namespace Zapret.Core;

public interface IEngineController
{
    bool TestProcessRunning { get; }
    Task<EngineSnapshot> CaptureAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task StartTestAsync(Strategy strategy, string listsDirectory, AppSettings settings, CancellationToken cancellationToken);
    Task RestoreAsync(EngineSnapshot snapshot, AppSettings settings, CancellationToken cancellationToken);
}

public interface IConnectivityProbe
{
    Task<IReadOnlyList<ProbeResult>> RunAsync(IReadOnlyList<ProbeTarget> targets, TestMode mode,
        Action<ProbeResult> onResult, CancellationToken cancellationToken);
}

public sealed class TestCoordinator(IEngineController engine, IConnectivityProbe probe)
{
    private int running;

    public async Task<TestSession> RunAsync(IReadOnlyList<Strategy> strategies, IReadOnlyList<ProbeTarget> targets,
        TestMode mode, string testListsDirectory, AppSettings settings, IProgress<TestProgress> progress,
        Action<EngineSnapshot> persistRecovery, Action clearRecovery, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Проверка уже выполняется.");
        try
        {
            if (strategies.Count == 0 || targets.Count == 0) throw new InvalidOperationException("Не найдены профили или серверы для проверки. Проверьте, что архив программы распакован целиком.");
            var started = DateTimeOffset.Now;
            var baseline = new StrategyReport("baseline", "Без обхода", []);
            var reports = new List<StrategyReport>();
            var confirmed = new List<StrategyReport>();
            bool cancelled = false;
            int completed = 0, total = strategies.Count + 1;
            var snapshot = await engine.CaptureAsync(cancellationToken);
            persistRecovery(snapshot); // Durable before stopping the previous connection.
            try
            {
                await engine.StopAsync(cancellationToken);
                progress.Report(new(completed, total, "Проверка соединения без обхода"));
                baseline = new("baseline", "Без обхода", await probe.RunAsync(targets, mode,
                    r => progress.Report(new(completed, total, "Без обхода", r)), cancellationToken));
                progress.Report(new(++completed, total, "Исходное соединение проверено"));

                foreach (var strategy in strategies)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reports.Add(await RunStrategy(strategy));
                    progress.Report(new(++completed, total, $"Профиль проверен: {strategy.Name}"));
                }

                var finalists = Ranking.Order(reports.Where(r => Ranking.Eligible(r, targets.ToArray()))).Take(3).ToArray();
                total += finalists.Length;
                foreach (var finalist in finalists)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var strategy = strategies.Single(x => x.Id == finalist.StrategyId);
                    progress.Report(new(completed, total, $"Повторная проверка: {strategy.Name}"));
                    confirmed.Add(await RunStrategy(strategy, true));
                    progress.Report(new(++completed, total, $"Повторная проверка завершена: {strategy.Name}"));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cancelled = true; }
            finally
            {
                progress.Report(new(completed, total, "Восстановление предыдущего подключения"));
                // Cancellation of the test must never cancel restoration.
                using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await engine.StopAsync(recoveryTimeout.Token);
                await engine.RestoreAsync(snapshot, settings, recoveryTimeout.Token);
                clearRecovery(); // A failed restoration leaves the journal for the next launch.
            }
            return new(started, mode, baseline, reports, confirmed,
                cancelled ? null : Ranking.Recommend(reports, confirmed, targets.ToArray()), cancelled);

            async Task<StrategyReport> RunStrategy(Strategy strategy, bool confirmation = false)
            {
                string label = confirmation ? "Повторно: " + strategy.Name : strategy.Name;
                await engine.StopAsync(cancellationToken);
                progress.Report(new(completed, total, $"Запуск: {strategy.Name}"));
                try { await engine.StartTestAsync(strategy, testListsDirectory, settings, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    progress.Report(new(completed, total, strategy.Name,
                        new ProbeResult("__engine", "Запуск обхода", "Движок", "ENGINE", ProbeStatus.Failed, 0, null, 0, ex.Message)));
                    return new(strategy.Id, strategy.Name, [], ex.Message);
                }
                var results = await probe.RunAsync(targets, mode,
                    r => progress.Report(new(completed, total, label, r)), cancellationToken);
                string? error = engine.TestProcessRunning ? null : "Модуль обхода завершился во время проверки. Результат этого профиля не используется.";
                if (error != null)
                    progress.Report(new(completed, total, label,
                        new ProbeResult("__engine", "Работа обхода", "Движок", "ENGINE", ProbeStatus.Failed, 0, null, 0, error)));
                return new(strategy.Id, strategy.Name, results, error);
            }
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
