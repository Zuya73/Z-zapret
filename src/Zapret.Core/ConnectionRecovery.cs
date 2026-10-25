namespace Zapret.Core;

public static class ConnectionRecovery
{
    public static async Task<bool> RunAsync(IEnumerable<Strategy> candidates,
        Func<CancellationToken, Task> stop, Func<Strategy, CancellationToken, Task> start,
        Func<CancellationToken, Task<ConnectionHealth>> probe, Func<CancellationToken, Task> restore,
        Action<Strategy, ConnectionHealth> commit, Action<string> log, CancellationToken token,
        TimeSpan? confirmationDelay = null)
    {
        bool committed = false;
        try
        {
            foreach (var candidate in candidates.Take(4))
            {
                token.ThrowIfCancellationRequested(); await stop(token);
                try { await start(candidate, token); }
                catch (InvalidOperationException error) { log(error.Message); continue; }
                var health = await probe(token);
                if (!health.InternetAvailable) break;
                if (!health.ServicesAvailable) continue;
                await Task.Delay(confirmationDelay ?? TimeSpan.FromSeconds(1), token);
                health = await probe(token);
                if (!health.InternetAvailable) break;
                if (!health.ServicesAvailable) continue;
                token.ThrowIfCancellationRequested(); commit(candidate, health); committed = true; return true;
            }
            return false;
        }
        finally
        {
            if (!committed)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await stop(recovery.Token); await restore(recovery.Token);
            }
        }
    }
}
