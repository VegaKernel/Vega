using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Sandboxes;

internal static class SscCheckpointBenchmark
{
    private const int Players = 255;
    private static readonly string Initial = new('x', 2048);
    private static readonly string Changed = new('y', 2048);

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length == 3)
        {
            await Worker(args[1], int.Parse(args[2]));
            return;
        }
        string root = Directory.CreateTempSubdirectory("vega-ssc-benchmark-").FullName;
        try
        {
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            store.ImportPrimary(Enumerable.Range(0, Players).ToDictionary(i => i.ToString(), _ => Initial), DateTimeOffset.UtcNow);
            for (int i = 1; i < Players; i += 2)
                if (!store.Save(i.ToString(), new WorldId("arena"), SandboxSscMode.Isolated, Initial, 0, DateTimeOffset.UtcNow).IsSuccess)
                    throw new InvalidDataException("Sandbox fixture setup failed.");
            using var primary = StartWorker(root, 0);
            using var arena = StartWorker(root, 1);
            await Task.WhenAll(primary.WaitForExitAsync(), arena.WaitForExitAsync()).WaitAsync(TimeSpan.FromMinutes(3));
            if (primary.ExitCode != 0 || arena.ExitCode != 0) throw new InvalidDataException("SSC worker failed.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Process StartWorker(string root, int index)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string arg in new[] { "--ssc-checkpoints", root, index.ToString() }) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private static async Task Worker(string root, int index)
    {
        var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
        var world = index == 0 ? WorldId.Primary : new WorldId("arena");
        var mode = index == 0 ? SandboxSscMode.Inherit : SandboxSscMode.Isolated;
        int[] accounts = Enumerable.Range(0, Players).Where(i => i % 2 == index).ToArray();
        var revisions = accounts.ToDictionary(i => i, i => store.Read(i.ToString(), world, mode)!.Revision);
        foreach (bool change in new[] { false, true })
        {
            string prefix = Path.Combine(root, $"ready-{change}-");
            File.WriteAllText(prefix + index, "");
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(prefix + (1 - index)) && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(5);
            if (!File.Exists(prefix + (1 - index))) throw new TimeoutException("SSC peer did not become ready.");
            var latency = new double[Players];
            var elapsed = Stopwatch.StartNew();
            Parallel.ForEach(accounts, new ParallelOptions { MaxDegreeOfParallelism = 8 }, id =>
            {
                long start = Stopwatch.GetTimestamp();
                var saved = store.SaveCheckpoint(id.ToString(), world, mode, change ? Changed : Initial, revisions[id], DateTimeOffset.UtcNow);
                if (!saved.IsSuccess || (saved.State!.Revision != revisions[id]) != change)
                    throw new InvalidDataException($"Invalid SSC checkpoint for {id}.");
                latency[id] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            });
            elapsed.Stop();
            double[] sorted = accounts.Select(i => latency[i]).Order().ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new { World = world.Value, Changed = change, Players = accounts.Length,
                ElapsedMs = elapsed.Elapsed.TotalMilliseconds, P95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1] }));
        }
        foreach (int id in accounts)
            if (store.Read(id.ToString(), world, mode)?.Payload != Changed) throw new InvalidDataException("SSC readback failed.");
    }
}
