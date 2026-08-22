using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Regions;

namespace Vega.Core.Commands.Administration;

public sealed class RegionCommand(IRegionService regions) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "region",
        ["regions"],
        ["vega.regions.manage"]);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            return Usage();
        }

        string action = invocation.Arguments[0].ToLowerInvariant();
        return action switch
        {
            "list" => List(invocation),
            "show" => Show(invocation),
            "create" => await CreateAsync(invocation, cancellationToken).ConfigureAwait(false),
            "delete" => await DeleteAsync(invocation, cancellationToken).ConfigureAwait(false),
            "resize" => await ResizeAsync(invocation, cancellationToken).ConfigureAwait(false),
            "priority" => await PriorityAsync(invocation, cancellationToken).ConfigureAwait(false),
            "owner" => await OwnerAsync(invocation, cancellationToken).ConfigureAwait(false),
            "member" => await AccessAsync(invocation, RegionAccessKind.Member, cancellationToken).ConfigureAwait(false),
            "group" => await AccessAsync(invocation, RegionAccessKind.Group, cancellationToken).ConfigureAwait(false),
            "flag" => await FlagAsync(invocation, cancellationToken).ConfigureAwait(false),
            _ => Usage()
        };
    }

    private CommandExecutionResult List(CommandInvocation invocation)
    {
        RegionSnapshot[] snapshot = regions.SnapshotRegions(invocation.WorldId).Take(200).ToArray();
        return snapshot.Length == 0
            ? Info("command.region.list-empty", "There are no regions in this world.")
            : CommandExecutionResult.WithMessages(snapshot.Select(region =>
                MessageDescriptor.Info(
                        "command.region.list-entry",
                        region.Id,
                        region.Name,
                        region.X,
                        region.Y,
                        region.Width,
                        region.Height,
                        region.Priority,
                        region.Revision)
                    .WithDefaultText("{0} | {1} | ({2},{3}) {4}x{5} | priority {6} | revision {7}"))
                .ToArray());
    }

    private CommandExecutionResult Show(CommandInvocation invocation)
    {
        RegionSnapshot? region = Find(invocation);
        return region is null
            ? NotFound()
            : CommandExecutionResult.WithMessages(
                MessageDescriptor.Info(
                        "command.region.details",
                        region.Name,
                        region.WorldId.Value,
                        region.Owner ?? "none",
                        region.Priority,
                        Join(region.Members),
                        Join(region.Groups),
                        region.Flags.Count == 0
                            ? "none"
                            : string.Join(", ", region.Flags.Select(static pair => $"{pair.Key}={pair.Value}")),
                        region.Revision)
                    .WithDefaultText("{0} | world {1} | owner {2} | priority {3} | members {4} | groups {5} | flags {6} | revision {7}"));
    }

    private async ValueTask<CommandExecutionResult> CreateAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 6 or > 8 ||
            !TryInt(invocation.Arguments[2], out int x) ||
            !TryInt(invocation.Arguments[3], out int y) ||
            !TryInt(invocation.Arguments[4], out int width) ||
            !TryInt(invocation.Arguments[5], out int height) ||
            (invocation.Arguments.Count >= 7 && !TryInt(invocation.Arguments[6], out _)))
        {
            return Usage();
        }

        int priority = invocation.Arguments.Count >= 7
            ? int.Parse(invocation.Arguments[6], CultureInfo.InvariantCulture)
            : 0;
        string? owner = invocation.Arguments.Count == 8 ? invocation.Arguments[7] : null;
        return Mutation(await regions.CreateAsync(
            invocation.WorldId,
            invocation.Arguments[1],
            x,
            y,
            width,
            height,
            priority,
            owner,
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> DeleteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        RegionSnapshot? before = Find(invocation);
        return before is null
            ? NotFound()
            : Mutation(await regions.DeleteAsync(
                invocation.WorldId,
                before.Name,
                before.Revision,
                invocation.Principal.Name,
                cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> ResizeAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 6 ||
            !TryInt(invocation.Arguments[2], out int x) ||
            !TryInt(invocation.Arguments[3], out int y) ||
            !TryInt(invocation.Arguments[4], out int width) ||
            !TryInt(invocation.Arguments[5], out int height))
        {
            return Usage();
        }

        return await UpdateAsync(
            invocation,
            new RegionUpdate(X: x, Y: y, Width: width, Height: height),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> PriorityAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken) =>
        invocation.Arguments.Count == 3 && TryInt(invocation.Arguments[2], out int priority)
            ? await UpdateAsync(invocation, new RegionUpdate(Priority: priority), cancellationToken).ConfigureAwait(false)
            : Usage();

    private async ValueTask<CommandExecutionResult> OwnerAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken) =>
        invocation.Arguments.Count == 3
            ? await UpdateAsync(
                invocation,
                invocation.Arguments[2].Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? new RegionUpdate(ClearOwner: true)
                    : new RegionUpdate(Owner: invocation.Arguments[2]),
                cancellationToken).ConfigureAwait(false)
            : Usage();

    private async ValueTask<CommandExecutionResult> FlagAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 4 ||
            !bool.TryParse(invocation.Arguments[3], out bool value))
        {
            return Usage();
        }

        return await UpdateAsync(
            invocation,
            new RegionUpdate(Flag: invocation.Arguments[2], FlagValue: value),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> AccessAsync(
        CommandInvocation invocation,
        RegionAccessKind kind,
        CancellationToken cancellationToken)
    {
        string operation = invocation.Arguments.Count > 2
            ? invocation.Arguments[2].ToLowerInvariant()
            : string.Empty;
        if (invocation.Arguments.Count != 4 || operation is not ("add" or "remove"))
        {
            return Usage();
        }

        RegionSnapshot? before = regions.SnapshotRegions(invocation.WorldId)
            .FirstOrDefault(region => region.Name.Equals(invocation.Arguments[1], StringComparison.OrdinalIgnoreCase));
        return before is null
            ? NotFound()
            : Mutation(await regions.SetAccessAsync(
                invocation.WorldId,
                before.Name,
                kind,
                invocation.Arguments[3],
                operation == "add",
                before.Revision,
                invocation.Principal.Name,
                cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> UpdateAsync(
        CommandInvocation invocation,
        RegionUpdate update,
        CancellationToken cancellationToken)
    {
        RegionSnapshot? before = Find(invocation);
        return before is null
            ? NotFound()
            : Mutation(await regions.UpdateAsync(
                invocation.WorldId,
                before.Name,
                update,
                before.Revision,
                invocation.Principal.Name,
                cancellationToken).ConfigureAwait(false));
    }

    private RegionSnapshot? Find(CommandInvocation invocation) =>
        invocation.Arguments.Count < 2
            ? null
            : regions.SnapshotRegions(invocation.WorldId).FirstOrDefault(region =>
                region.Name.Equals(invocation.Arguments[1], StringComparison.OrdinalIgnoreCase));

    private static CommandExecutionResult Mutation(RegionMutationResult result) =>
        result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.region.mutation-applied",
                        result.After?.Name ?? result.Before?.Name ?? "region",
                        result.Code)
                    .WithDefaultText("Region {0}: {1}."))
            : CommandExecutionResult.Failed(
                "command.region.mutation-failed",
                MessageDescriptor.Error("command.region.mutation-failed", result.Code)
                    .WithDefaultText("Region operation failed: {0}."));

    private static bool TryInt(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static string Join(IEnumerable<string> values)
    {
        string[] snapshot = values.ToArray();
        return snapshot.Length == 0 ? "none" : string.Join(", ", snapshot);
    }

    private static CommandExecutionResult Info(string key, string text) =>
        CommandExecutionResult.WithMessages(MessageDescriptor.Info(key).WithDefaultText(text));

    private static CommandExecutionResult NotFound() => CommandExecutionResult.Failed(
        "command.region.not-found",
        MessageDescriptor.Error("command.region.not-found").WithDefaultText("Region not found."));

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.region.usage",
        [MessageDescriptor.Error("command.region.usage").WithDefaultText(
            "Usage: region list | show <name> | create <name> <x> <y> <width> <height> [priority] [owner] | delete <name> | resize <name> <x> <y> <width> <height> | priority <name> <value> | owner <name> <owner|none> | member|group <name> add|remove <subject> | flag <name> <flag> <true|false>.")]);
}
