using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Sandboxes;

// Simulate the typed one-shot native builder contract for supervisor tests. The
// native placement/save path itself is separately exercised in a server smoke.
if (args is ["--vega-build-schematic-template", var baseWorld, var schematic, var destination, _, _, _])
{
    Directory.CreateDirectory(destination);
    await File.WriteAllBytesAsync(Path.Combine(destination, "world.wld"),
        [.. await File.ReadAllBytesAsync(baseWorld), .. await File.ReadAllBytesAsync(schematic)]);
    if (Path.GetFileName(schematic) != "fail.trschem") File.WriteAllText(Path.Combine(destination, "complete"), "1");
    else Environment.ExitCode = 1;
    return;
}

SandboxBackendLaunchOptions launch = SandboxBackendLaunchOptions.Parse(args);
Console.InputEncoding = OperatingSystem.IsWindows() ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8;
File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "backend-working-directory.txt"), Environment.CurrentDirectory);
File.WriteAllLines(Path.Combine(Environment.CurrentDirectory, "backend-arguments.txt"), launch.ServerArguments);
using var reporter = new SandboxBackendReporter(
    launch,
    new ReadyLifecycle(),
    new ReadyServer(),
    new EmptyPlayers());
if (launch.WorldId.Value.StartsWith("crash", StringComparison.OrdinalIgnoreCase))
{
    await Task.Delay(750);
    return;
}

while (await Console.In.ReadLineAsync() is { } line)
{
    if (line.StartsWith("shutdown", StringComparison.OrdinalIgnoreCase))
    {
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "backend-graceful-stop.txt"), line);
        break;
    }
}

internal sealed class ReadyLifecycle : IRuntimeLifecycle
{
    public RuntimeLifecycleStage CurrentStage => RuntimeLifecycleStage.FullyWarmed;
    public TimeSpan Elapsed => TimeSpan.FromSeconds(1);
    public event Action<RuntimeLifecycleTransition>? Transitioned { add { } remove { } }
}

internal sealed class ReadyServer : IServerRuntimeStateReader
{
    public ServerRuntimeStateSnapshot Read() => new("test", 8, 7780, true, 60, DateTimeOffset.UtcNow);
}

internal sealed class EmptyPlayers : IPlayerDirectory
{
    public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Array.Empty<PlayerSnapshot>();
    public bool TryGet(PlayerId playerId, out PlayerSnapshot? player) { player = null; return false; }
    public bool TryFindExact(string name, out PlayerSnapshot? player) { player = null; return false; }
}
