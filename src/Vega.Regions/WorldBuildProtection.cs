using System.Globalization;
using System.Text.Json;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Regions;

/// <summary>World-local build policy layered ahead of ordinary region authorization.</summary>
public sealed class WorldBuildProtection
{
    public const string GlobalBypass = "vega.world.build.bypass";
    public const string SpawnBypass = "vega.world.spawn.bypass";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly WorldId worldId;
    private readonly IPlayerAuthorizationService authorization;
    private readonly Func<WorldTilePosition?> spawnPosition;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private BuildProtectionState state;

    public WorldBuildProtection(string path, WorldId worldId, IPlayerAuthorizationService authorization, Func<WorldTilePosition?> spawnPosition)
    {
        this.path = Path.GetFullPath(path);
        this.worldId = worldId;
        this.authorization = authorization;
        this.spawnPosition = spawnPosition;
        state = File.Exists(this.path) ? Read(this.path) : new(false, false, 20);
    }

    public BuildProtectionState Current => Volatile.Read(ref state);
    public WorldId WorldId => worldId;

    public async ValueTask<bool> ReloadAsync(CancellationToken token = default)
    {
        if (!await writeGate.WaitAsync(0, token).ConfigureAwait(false)) throw new IOException("Build protection settings are busy.");
        try
        {
            if (!File.Exists(path)) return false;
            var next = await Task.Run(() => Read(path), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref state, next);
            return true;
        }
        finally { writeGate.Release(); }
    }

    public PacketProcessingDecision Evaluate(WorldId world, PlayerId player, int startX, int startY, int endX, int endY)
    {
        if (world != worldId) return PacketProcessingDecision.Reject("packet.build.wrong_world");
        BuildProtectionState settings = Current;
        if (settings.GlobalDisabled && !authorization.HasPermission(player, GlobalBypass))
            return PacketProcessingDecision.Reject("packet.build.disabled");
        if (!settings.SpawnProtected || authorization.HasPermission(player, SpawnBypass)) return PacketProcessingDecision.Allow;
        if (spawnPosition() is not { } spawn) return PacketProcessingDecision.Reject("packet.build.spawn_unavailable");
        int radius = settings.SpawnRadius;
        bool intersects = Math.Min(startX, endX) <= (long)spawn.X + radius && Math.Max(startX, endX) >= (long)spawn.X - radius &&
                          Math.Min(startY, endY) <= (long)spawn.Y + radius && Math.Max(startY, endY) >= (long)spawn.Y - radius;
        return intersects ? PacketProcessingDecision.Reject("packet.build.spawn_protected") : PacketProcessingDecision.Allow;
    }

    public async ValueTask<BuildProtectionState> ChangeAsync(Func<BuildProtectionState, BuildProtectionState> change, CancellationToken token = default)
    {
        if (!await writeGate.WaitAsync(0, token).ConfigureAwait(false)) throw new IOException("Build protection settings are busy.");
        try
        {
            var next = change(Current);
            Validate(next);
            if (next == Current) return next;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, next, JsonOptions);
                        stream.Flush(flushToDisk: true);
                    }
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, path, true);
                    Volatile.Write(ref state, next);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }, token).ConfigureAwait(false);
            return next;
        }
        finally { writeGate.Release(); }
    }

    private static BuildProtectionState Read(string path)
    {
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Build protection file is too large.");
        var settings = JsonSerializer.Deserialize<BuildProtectionState>(File.ReadAllText(path), JsonOptions) ??
            throw new InvalidDataException("Build protection settings are empty.");
        Validate(settings);
        return settings;
    }

    private static void Validate(BuildProtectionState settings)
    {
        if (settings.SpawnRadius is < 1 or > 500) throw new InvalidDataException("Spawn protection radius must be 1–500 tiles.");
    }
}

public sealed record BuildProtectionState(bool GlobalDisabled, bool SpawnProtected, int SpawnRadius);

public sealed class WorldBuildProtectionCommand(WorldBuildProtection policy, bool spawn) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = spawn
        ? new("protectspawn", [], ["vega.world.protectspawn"], Syntax: "protectspawn [on|off|status|radius <1..500>]")
        : new("antibuild", [], ["vega.world.antibuild"], Syntax: "antibuild [on|off|status]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.WorldId != policy.WorldId) return CommandExecutionResult.Failed("build.wrong_world");
        var args = invocation.Arguments;
        string action = args.Count == 0 ? "toggle" : args[0].ToLowerInvariant();
        BuildProtectionState current = policy.Current;
        try
        {
            if (spawn && action == "radius" && args.Count == 2 &&
                int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int radius) && radius is >= 1 and <= 500)
                current = await policy.ChangeAsync(s => s with { SpawnRadius = radius }, token).ConfigureAwait(false);
            else if (args.Count <= 1 && action == "status") { }
            else if (args.Count <= 1 && action is "toggle" or "on" or "off")
                current = await policy.ChangeAsync(s => spawn
                    ? s with { SpawnProtected = action == "on" || action == "toggle" && !s.SpawnProtected }
                    : s with { GlobalDisabled = action == "on" || action == "toggle" && !s.GlobalDisabled }, token).ConfigureAwait(false);
            else return new(CommandExecutionStatus.InvalidUsage, "build.usage");
        }
        catch (IOException) { return CommandExecutionResult.Failed("build.save_failed"); }
        catch (UnauthorizedAccessException) { return CommandExecutionResult.Failed("build.save_failed"); }

        return CommandExecutionResult.WithMessages(spawn
            ? MessageDescriptor.Info("build.spawn_status", current.SpawnProtected, current.SpawnRadius).WithDefaultText("Spawn protection: {0}, radius {1} tiles.")
            : MessageDescriptor.Info("build.global_status", current.GlobalDisabled).WithDefaultText("Global build protection: {0}."));
    }
}
