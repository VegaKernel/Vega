using System.Reflection;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Wipe;

public sealed class WipeModule(
    IWorldResetOrchestrator resets,
    IServerControl server,
    string? currentWorldPath,
    TimeProvider? timeProvider = null) : IVegaModule
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private PendingReset? pending;

    public VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Modules.Wipe",
        typeof(WipeModule).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        ["Vega.Contracts"]);

    public void Start()
    {
    }

    public WorldResetStatus Status => resets.Read();

    public string? Prepare(WorldId worldId, string sourcePath, string actor)
    {
        if (string.IsNullOrWhiteSpace(currentWorldPath))
        {
            return null;
        }

        string token = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant();
        var plan = new WorldResetPlan(
            Guid.NewGuid(),
            worldId,
            Path.GetFullPath(currentWorldPath),
            Path.GetFullPath(sourcePath),
            actor,
            time.GetUtcNow());
        lock (sync)
        {
            pending = new PendingReset(token, plan, time.GetUtcNow().AddMinutes(2));
        }

        return token;
    }

    public async ValueTask<WorldResetResult?> ConfirmAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        PendingReset? selected;
        lock (sync)
        {
            selected = pending;
            if (selected is null || selected.ExpiresAt <= time.GetUtcNow() ||
                !string.Equals(selected.Token, token, StringComparison.Ordinal))
            {
                pending = null;
                return null;
            }

            pending = null;
        }

        WorldResetResult result = await resets.ExecuteAsync(selected.Plan, cancellationToken)
            .ConfigureAwait(false);
        if (result.Succeeded)
        {
            await server.ShutdownAsync(
                $"World reset {selected.Plan.OperationId} prepared; restart applies replacement.",
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public bool Cancel()
    {
        lock (sync)
        {
            if (pending is null)
            {
                WorldResetStatus active = resets.Read();
                return active.OperationId is Guid operationId && resets.Cancel(operationId);
            }

            pending = null;
            return true;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            pending = null;
        }
    }

    private sealed record PendingReset(string Token, WorldResetPlan Plan, DateTimeOffset ExpiresAt);
}

public sealed class WipeCommand(WipeModule module) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "wipe",
        ["worldreset"],
        ["vega.world.reset"],
        Syntax: "wipe <prepare <replacement.wld>|confirm <token>|cancel|status>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 2 &&
            string.Equals(invocation.Arguments[0], "prepare", StringComparison.OrdinalIgnoreCase))
        {
            string? token = module.Prepare(invocation.WorldId, invocation.Arguments[1], invocation.Principal.Name);
            return token is null
                ? Failure("world-reset.no-active-path", "World reset requires an explicit -world/config worldPath.")
                : CommandExecutionResult.WithMessages(
                    MessageDescriptor.Warning("command.wipe.prepared").WithDefaultText(
                        $"Destructive reset prepared for two minutes. Run 'wipe confirm {token}'. The server will save and shut down; replacement applies atomically on restart."));
        }

        if (invocation.Arguments.Count == 2 &&
            string.Equals(invocation.Arguments[0], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            WorldResetResult? result = await module
                .ConfirmAsync(invocation.Arguments[1], cancellationToken)
                .ConfigureAwait(false);
            return result is null
                ? Failure("world-reset.confirmation-invalid", "Reset confirmation is invalid or expired.")
                : result.Succeeded
                    ? CommandExecutionResult.WithMessages(
                        MessageDescriptor.Warning("command.wipe.committed").WithDefaultText(
                            "Replacement staged. Server shutdown requested; the next start applies it atomically."))
                    : Failure(result.ErrorCode ?? "world-reset.failed", result.Status.Detail ?? "World reset failed.");
        }

        if (invocation.Arguments.Count == 1 &&
            string.Equals(invocation.Arguments[0], "cancel", StringComparison.OrdinalIgnoreCase))
        {
            return module.Cancel()
                ? Info("World reset cancelled.")
                : Info("No world reset is pending.");
        }

        if (invocation.Arguments.Count == 1 &&
            string.Equals(invocation.Arguments[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            WorldResetStatus status = module.Status;
            return Info($"World reset: {status.State}; operation={status.OperationId}; {status.Detail}");
        }

        return Failure(
            "command.wipe.usage",
            "Usage: wipe prepare <replacement.wld> | wipe confirm <token> | wipe cancel | wipe status");

    }

    private static CommandExecutionResult Info(string text) => CommandExecutionResult.WithMessages(
        MessageDescriptor.Info("command.wipe.info").WithDefaultText(text));

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}
