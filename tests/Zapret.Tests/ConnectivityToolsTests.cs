using Zapret.Core;

internal static class ConnectivityToolsTests
{
    internal static void Run(Action<string, Action> check)
    {
        var start = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        ConnectionHealth Health(int seconds, bool service = false, bool internet = true) => new(start.AddSeconds(seconds),
            [new("Discord", "Discord", service, 10, ""), new("YouTube", "YouTube", service, 12, ""), new("Control", "Internet", internet, 5, "")]);
        check("A running engine is not equivalent to available services", () =>
        {
            Assert(!Health(0).ServicesAvailable && Health(0).ConfirmedFailure);
            Assert(Health(0, true).ServicesAvailable && !Health(0, true).ConfirmedFailure);
            var partial = new ConnectionHealth(start, [new("Discord", "Discord", false, 0, ""), new("CDN", "Discord", true, 0, ""), new("YT", "YouTube", true, 0, "")]);
            Assert(!partial.ServicesAvailable);
            Assert(!new ConnectionHealth(start, []).ServicesAvailable);
        });
        check("Reconnection requires three independent checks over at least 75 seconds", () =>
        {
            var p = new ReconnectPolicy();
            Assert(!p.Observe(Health(0), true, true, true));
            Assert(!p.Observe(Health(0), true, true, true));
            Assert(!p.Observe(Health(1), true, true, true));
            Assert(!p.Observe(Health(2), true, true, true));
            Assert(p.Observe(Health(90), true, true, true));
        });
        check("Offline networks, manual disconnect and disabled recovery never reconnect", () =>
        {
            foreach (var flags in new[] { (false, true, true), (true, false, true), (true, true, false) })
            {
                var p = new ReconnectPolicy();
                foreach (int second in new[] { 0, 45, 90, 135 }) Assert(!p.Observe(Health(second, false, flags.Item1), false, flags.Item2, flags.Item3));
            }
        });
        check("A stopped wanted engine can recover even when services work without it", () =>
        {
            var p = new ReconnectPolicy();
            p.Observe(Health(0, true), false, true, true); p.Observe(Health(45, true), false, true, true);
            Assert(p.Observe(Health(90, true), false, true, true));
        });
        check("Successful checks and long observation gaps reset failure evidence", () =>
        {
            var p = new ReconnectPolicy();
            p.Observe(Health(0), true, true, true); p.Observe(Health(45), true, true, true);
            Assert(!p.Observe(Health(90, true), true, true, true));
            Assert(!p.Observe(Health(135), true, true, true));
            Assert(!p.Observe(Health(400), true, true, true));
        });
        check("Reconnection is limited to three attempts per fifteen minutes with cooldown", () =>
        {
            var p = new ReconnectPolicy();
            Assert(p.BeginAttempt(start)); Assert(!p.BeginAttempt(start.AddSeconds(119)));
            Assert(p.BeginAttempt(start.AddMinutes(2))); Assert(p.BeginAttempt(start.AddMinutes(4)));
            Assert(!p.BeginAttempt(start.AddMinutes(14))); Assert(p.BeginAttempt(start.AddMinutes(15)));
        });
        check("Recovery commits only a twice-confirmed replacement", () =>
        {
            int started = 0, probes = 0, restored = 0; string? committed = null;
            var candidates = new[] { new Strategy("old", "Old", "", "", []), new Strategy("new", "New", "", "", []) };
            bool ok = ConnectionRecovery.RunAsync(candidates, _ => Task.CompletedTask,
                (_, _) => { started++; return Task.CompletedTask; }, _ => { probes++; return Task.FromResult(Health(90, started == 2)); },
                _ => { restored++; return Task.CompletedTask; }, (s, _) => committed = s.Id, _ => { }, CancellationToken.None, TimeSpan.Zero).GetAwaiter().GetResult();
            Assert(ok && committed == "new" && probes == 3 && restored == 0);
        });
        check("An offline network stops strategy cycling and restores the original connection", () =>
        {
            int started = 0, restored = 0;
            bool ok = ConnectionRecovery.RunAsync(Enumerable.Range(0, 10).Select(i => new Strategy(i.ToString(), "", "", "", [])),
                _ => Task.CompletedTask, (_, _) => { started++; return Task.CompletedTask; }, _ => Task.FromResult(Health(0, false, false)),
                _ => { restored++; return Task.CompletedTask; }, (_, _) => throw new Exception("Must not commit"), _ => { }, CancellationToken.None, TimeSpan.Zero).GetAwaiter().GetResult();
            Assert(!ok && started == 1 && restored == 1);
        });
        check("Recovery cancellation restores with a fresh token", () =>
        {
            using var cancel = new CancellationTokenSource(); bool restored = false;
            try
            {
                ConnectionRecovery.RunAsync([new("old", "", "", "", [])], _ => Task.CompletedTask,
                    (_, _) => Task.CompletedTask, ct => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(Health(0)); },
                    ct => { restored = !ct.IsCancellationRequested; return Task.CompletedTask; }, (_, _) => { }, _ => { }, cancel.Token, TimeSpan.Zero).GetAwaiter().GetResult();
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { Assert(restored); }
        });
        check("Recovery rolls back when committing settings fails", () =>
        {
            bool restored = false;
            try
            {
                ConnectionRecovery.RunAsync([new("old", "", "", "", [])], _ => Task.CompletedTask, (_, _) => Task.CompletedTask,
                    _ => Task.FromResult(Health(0, true)), _ => { restored = true; return Task.CompletedTask; },
                    (_, _) => throw new IOException("Settings are locked"), _ => { }, CancellationToken.None, TimeSpan.Zero).GetAwaiter().GetResult();
                throw new Exception("Expected settings error");
            }
            catch (IOException) { Assert(restored); }
        });
        check("Failed recovery restoration is observable and never returns success", () =>
        {
            try
            {
                ConnectionRecovery.RunAsync([], _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult(Health(0)),
                    _ => throw new IOException("Restore failed"), (_, _) => { }, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
                throw new Exception("Expected restoration error");
            }
            catch (IOException error) { Assert(error.Message == "Restore failed"); }
        });
        check("Latency loss and jitter exclude missing replies without inventing zero latency", () =>
        {
            var s = NetworkMeasurements.Summarize([new(1, 10), new(2, 20), new(3, null), new(4, 100)]);
            Assert(s.Sent == 4 && s.Received == 3 && s.LossPercent == 25 && s.JitterMs == 10);
            Assert(NetworkMeasurements.Summarize([new(1, null)]).AverageMs == null);
            Assert(NetworkMeasurements.Summarize([new(1, double.NaN)]).Received == 0);
            Assert(NetworkMeasurements.MegabitsPerSecond(25_000_000, TimeSpan.FromSeconds(2)) == 100);
        });
        check("Sensor histories are bounded, preserve stable IDs and ignore missing values", () =>
        {
            var history = new SensorHistory();
            for (int i = 0; i < 2000; i++) history.Add(start.AddSeconds(i * 2), [new("a", "CPU", "T", "Temperature", i), new("b", "GPU", "T", "Temperature", 40), new("missing", "X", "X", "Load", double.NaN)]);
            Assert(history.Series.Count == 2 && history.Series.All(x => x.Points.Count == 1800));
            history.Add(start, [new("a", "CPU", "T", "Temperature", 1)]);
            Assert(history.Series.First(x => x.Id == "a").Points[^1].Value == 1999);
            history.Add(start.AddHours(3), [new("a", "CPU", "T", "Temperature", 2)]);
            Assert(history.Series.First(x => x.Id == "a").Points.Count == 1);
        });
        check("Sensor threshold alerts have hysteresis, cooldown and finite-value validation", () =>
        {
            var p = new SensorAlertPolicy();
            Assert(p.Observe("cpu", 81, 80, start)); Assert(!p.Observe("cpu", 82, 80, start.AddMinutes(1)));
            Assert(!p.Observe("cpu", 79, 80, start.AddMinutes(2))); // still latched
            Assert(!p.Observe("cpu", 70, 80, start.AddMinutes(3)));
            Assert(p.Observe("cpu", 85, 80, start.AddMinutes(6)));
            Assert(!p.Observe("gpu", double.NaN, 80, start));
        });
        check("Sensor exports use invariant values, UTC and quote device names", () =>
        {
            var h = new SensorHistory(); h.Add(start, [new("a", "=cmd", "A,\"B", "Temperature", 52.5)]);
            string csv = h.Csv(); Assert(csv.Contains("52.5") && csv.Contains("\"'=cmd\"") && csv.Contains("\"A,\"\"B\""));
        });
        check("Space scan sums nested folders, finds largest files and leaves data untouched", () => WithFiles(root =>
        {
            var r = SpaceAnalyzer.Scan(root, null, CancellationToken.None);
            Assert(r.Files == 3 && r.Bytes == 600 && !r.Partial);
            Assert(r.LargestFiles[0].Bytes == 300 && r.Folders.Single(x => x.Path.EndsWith("sub")).Bytes == 500);
            Assert(File.ReadAllBytes(Path.Combine(root, "a.bin")).Length == 100);
        }));
        check("Space scan cancellation and entry limits return explicitly partial results", () => WithFiles(root =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert(SpaceAnalyzer.Scan(root, null, cancel.Token).Partial);
            Assert(SpaceAnalyzer.Scan(root, null, CancellationToken.None, maximumEntries: 1).Partial);
        }));
        check("Space scan skips directory links without following them", () => WithFiles(root =>
        {
            try { Directory.CreateSymbolicLink(Path.Combine(root, "sub", "cycle"), root); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }
            var r = SpaceAnalyzer.Scan(root, null, CancellationToken.None);
            Assert(r.Bytes == 600 && r.Skipped >= 1);
        }));
    }
    private static void WithFiles(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "x2-space-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub", "nested"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(root, "sub", "b.bin"), new byte[200]);
            File.WriteAllBytes(Path.Combine(root, "sub", "nested", "c.bin"), new byte[300]); action(root);
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Connectivity/tools regression failed"); }
}
