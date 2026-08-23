using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;

namespace Vega.Regions;

public sealed class BindableWorldResetParticipant(string name) : IWorldResetParticipant
{
    private IWorldResetParticipant? inner;

    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Participant name is required.", nameof(name))
        : name.Trim();

    public void Bind(IWorldResetParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (Interlocked.CompareExchange(ref inner, participant, null) is not null)
        {
            throw new InvalidOperationException($"World reset participant '{Name}' is already bound.");
        }
    }

    public ValueTask ValidateAsync(WorldResetPlan plan, CancellationToken cancellationToken = default) =>
        Target().ValidateAsync(plan, cancellationToken);

    public ValueTask<IPreparedWorldReset> PrepareAsync(
        WorldResetPlan plan,
        CancellationToken cancellationToken = default) =>
        Target().PrepareAsync(plan, cancellationToken);

    private IWorldResetParticipant Target() => Volatile.Read(ref inner) ??
        throw new InvalidOperationException($"World reset participant '{Name}' is not bound.");
}

public sealed class RegionWorldResetParticipant(IRegionService regions) : IWorldResetParticipant
{
    public string Name => "regions";

    public ValueTask ValidateAsync(WorldResetPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = regions.SnapshotRegions(plan.WorldId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IPreparedWorldReset> PrepareAsync(
        WorldResetPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RegionSnapshot[] snapshot = regions.SnapshotRegions(plan.WorldId).ToArray();
        return ValueTask.FromResult<IPreparedWorldReset>(new Prepared(regions, plan, snapshot));
    }

    private sealed class Prepared(
        IRegionService regions,
        WorldResetPlan plan,
        IReadOnlyList<RegionSnapshot> snapshot) : IPreparedWorldReset
    {
        private readonly List<RegionSnapshot> removed = [];

        public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            foreach (RegionSnapshot expected in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RegionSnapshot? current = regions.SnapshotRegions(plan.WorldId)
                    .FirstOrDefault(region => string.Equals(region.Name, expected.Name, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                {
                    continue;
                }

                if (current.Revision != expected.Revision)
                {
                    throw new InvalidOperationException(
                        $"Region '{expected.Name}' changed while world reset {plan.OperationId} was being prepared.");
                }

                RegionMutationResult result = await regions.DeleteAsync(
                    plan.WorldId,
                    current.Name,
                    current.Revision,
                    plan.RequestedBy,
                    cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Region '{expected.Name}' could not be removed for world reset: {result.Code}.");
                }

                removed.Add(expected);
            }
        }

        public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            for (var index = removed.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RegionSnapshot saved = removed[index];
                if (regions.SnapshotRegions(plan.WorldId).Any(region =>
                        string.Equals(region.Name, saved.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Region '{saved.Name}' reappeared while world reset rollback was restoring persistence.");
                }

                RegionMutationResult mutation = await regions.CreateAsync(
                    saved.WorldId,
                    saved.Name,
                    saved.X,
                    saved.Y,
                    saved.Width,
                    saved.Height,
                    saved.Priority,
                    saved.Owner,
                    plan.RequestedBy,
                    cancellationToken).ConfigureAwait(false);
                RegionSnapshot current = mutation.Succeeded && mutation.After is not null
                    ? mutation.After
                    : throw new InvalidOperationException(
                        $"Region '{saved.Name}' could not be restored after world reset rollback: {mutation.Code}.");

                foreach ((string flag, bool value) in saved.Flags)
                {
                    mutation = await regions.UpdateAsync(
                        saved.WorldId,
                        saved.Name,
                        new RegionUpdate(Flag: flag, FlagValue: value),
                        current.Revision,
                        plan.RequestedBy,
                        cancellationToken).ConfigureAwait(false);
                    current = RequireApplied(saved.Name, mutation);
                }

                foreach (string member in saved.Members)
                {
                    mutation = await regions.SetAccessAsync(
                        saved.WorldId,
                        saved.Name,
                        RegionAccessKind.Member,
                        member,
                        true,
                        current.Revision,
                        plan.RequestedBy,
                        cancellationToken).ConfigureAwait(false);
                    current = RequireApplied(saved.Name, mutation);
                }

                foreach (string group in saved.Groups)
                {
                    mutation = await regions.SetAccessAsync(
                        saved.WorldId,
                        saved.Name,
                        RegionAccessKind.Group,
                        group,
                        true,
                        current.Revision,
                        plan.RequestedBy,
                        cancellationToken).ConfigureAwait(false);
                    current = RequireApplied(saved.Name, mutation);
                }
            }

            removed.Clear();
        }

        public ValueTask DisposeAsync()
        {
            removed.Clear();
            return ValueTask.CompletedTask;
        }

        private static RegionSnapshot RequireApplied(string name, RegionMutationResult result) =>
            result.Succeeded && result.After is not null
                ? result.After
                : throw new InvalidOperationException(
                    $"Region '{name}' could not be restored after world reset rollback: {result.Code}.");
    }
}
