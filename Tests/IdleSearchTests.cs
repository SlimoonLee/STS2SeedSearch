using SeedSearch;

static class IdleSearchTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run()
    {
        double external = WindowsCpuLoadSource.ExternalFraction(1000, 100, 100);
        Assert(Math.Abs(external - 0.8) < 0.0001, "Other-process CPU fraction calculation is incorrect");

        var policy = new IdleCpuPolicy(8, 8);
        for (int i = 0; i < 20; i++) policy.Update(0);
        var saturated = policy.Update(0);
        Assert(saturated.Workers == 8 && saturated.Duty > 0.89, "Idle policy did not recover available CPU capacity");

        var pressured = policy.Update(0.8);
        Assert(pressured.Workers == 1 && pressured.Duty <= 1, "Idle policy did not quickly yield under external CPU load");
        policy.Update(0); policy.Update(0);
        Assert(policy.Update(0).Workers >= 2, "Idle policy did not recover gradually after load dropped");

        using var governor = new IdleSearchGovernor(4, new UnavailableCpuSource());
        Assert(!governor.Status.MonitoringAvailable && governor.Status.AllowedWorkers == 1
            && Math.Abs(governor.Status.DutyCycle - 0.2) < 0.0001,
            "Idle policy fallback was not conservative when monitoring is unavailable");

        string output = Path.Combine(Path.GetTempPath(), "seedsearch-idle-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var result = SearchEngine.Run(new SearchConfig
            {
                count = 64, threads = 2, minFightsChecked = 0, idleSearch = true, outFile = output
            });
            Assert(!result.Canceled && result.Progress.Evaluated == 64 && result.Progress.Idle != null,
                "Idle search did not complete through the shared search engine");
        }
        finally
        {
            foreach (string path in new[] { output, output + ".checkpoint.json", output + ".checkpoint.json.bak", output + ".errors.jsonl", output + ".lock" })
                if (File.Exists(path)) File.Delete(path);
        }
        Console.WriteLine("PASS: idle CPU fraction, fast load shedding, gradual recovery, and conservative fallback.");
    }

    sealed class UnavailableCpuSource : ICpuLoadSource
    {
        public bool TrySample(out double fraction, out string detail) { fraction = 0; detail = "test"; return false; }
        public void Dispose() { }
    }
}
