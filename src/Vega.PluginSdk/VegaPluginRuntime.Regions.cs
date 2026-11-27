using System.Runtime.CompilerServices;
using System.Collections.Frozen;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

internal static partial class VegaPluginRuntime
{
    private sealed class PluginRegions(IVegaPluginContext context) : IPluginRegions
    {
        public bool IsAvailable => Services.Regions is not null;

        public IReadOnlyList<RegionSnapshot> Snapshot()
        {
            VegaPluginRuntimeServices current = Services;
            IRegionService regions = current.Regions ?? throw new NotSupportedException("Region snapshots are unavailable in this host.");
            return Array.AsReadOnly(regions.SnapshotRegions(current.WorldId)
                .Where(region => region.WorldId == current.WorldId).Select(Detach).ToArray());
        }

        public RegionSnapshot? FindTop(int tileX, int tileY)
        {
            VegaPluginRuntimeServices current = Services;
            IRegionService regions = current.Regions ?? throw new NotSupportedException("Region snapshots are unavailable in this host.");
            RegionSnapshot? region = regions.FindTopRegion(current.WorldId, tileX, tileY);
            return region is not null && region.WorldId == current.WorldId ? Detach(region) : null;
        }

        public async ValueTask<RegionMutationResult> CreateAsync(string name, int x, int y, int width, int height, int priority = 0,
            string? owner = null, CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            if (current.Regions is null) return Unavailable();
            if (!await ValidBoundsAsync(current, x, y, width, height, cancellationToken).ConfigureAwait(false))
                return new(false, "region.invalid-bounds");
            return DetachResult(current.WorldId, await current.Regions.CreateAsync(current.WorldId, name, x, y, width, height, priority,
                owner, Actor(context), cancellationToken).ConfigureAwait(false));
        }

        public async ValueTask<RegionMutationResult> UpdateAsync(RegionSnapshot expected, RegionUpdate update, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(update);
            VegaPluginRuntimeServices current = Services;
            if (current.Regions is null) return Unavailable();
            if (expected.WorldId != current.WorldId) return WrongWorld();
            if ((update.Flag is null) != (update.FlagValue is null) || (update.ClearOwner && update.Owner is not null))
                return new(false, "region.invalid-update");
            if (update.X is not null || update.Y is not null || update.Width is not null || update.Height is not null)
            {
                // Derive geometry from the trusted current snapshot, not a record fabricated by the plugin.
                RegionSnapshot? actual = current.Regions.SnapshotRegions(current.WorldId).FirstOrDefault(x =>
                    x.WorldId == current.WorldId && x.Id == expected.Id && x.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase));
                if (actual is null) return new(false, "region.identity-conflict");
                if (actual.Revision != expected.Revision) return new(false, "region.revision-conflict", Detach(actual));
                if (!await ValidBoundsAsync(current, update.X ?? actual.X, update.Y ?? actual.Y, update.Width ?? actual.Width,
                        update.Height ?? actual.Height, cancellationToken).ConfigureAwait(false)) return new(false, "region.invalid-bounds");
            }
            return DetachResult(current.WorldId, await current.Regions.UpdateAsync(expected, update, Actor(context), cancellationToken).ConfigureAwait(false));
        }

        public async ValueTask<RegionMutationResult> RenameAsync(RegionSnapshot expected, string newName, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            VegaPluginRuntimeServices current = Services;
            if (current.Regions is null) return Unavailable();
            if (expected.WorldId != current.WorldId) return WrongWorld();
            return DetachResult(current.WorldId, await current.Regions.RenameAsync(expected, newName, Actor(context), cancellationToken).ConfigureAwait(false));
        }

        public async ValueTask<RegionMutationResult> SetAccessAsync(RegionSnapshot expected, RegionAccessKind kind, string subject, bool enabled,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            VegaPluginRuntimeServices current = Services;
            if (current.Regions is null) return Unavailable();
            if (expected.WorldId != current.WorldId) return WrongWorld();
            return DetachResult(current.WorldId, await current.Regions.SetAccessAsync(expected, kind, subject, enabled, Actor(context), cancellationToken).ConfigureAwait(false));
        }

        public async ValueTask<RegionMutationResult> DeleteAsync(RegionSnapshot expected, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            VegaPluginRuntimeServices current = Services;
            if (current.Regions is null) return Unavailable();
            if (expected.WorldId != current.WorldId) return WrongWorld();
            return DetachResult(current.WorldId, await current.Regions.DeleteAsync(expected, Actor(context), cancellationToken).ConfigureAwait(false));
        }

        private static async ValueTask<bool> ValidBoundsAsync(VegaPluginRuntimeServices current, int x, int y, int width, int height, CancellationToken token)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0) return false;
            var world = current.Environment is null ? null : await current.Environment.CaptureAsync(current.WorldId, token).ConfigureAwait(false);
            return world is not null && world.WorldId == current.WorldId && (long)x + width <= world.WidthTiles && (long)y + height <= world.HeightTiles;
        }

        private static RegionMutationResult WrongWorld() => new(false, "region.wrong-world");
        private static RegionMutationResult Unavailable() => new(false, "region.mutation-unavailable");
        private static RegionMutationResult DetachResult(WorldId world, RegionMutationResult value) => value with
        {
            Before = value.Before is { } before && before.WorldId == world ? Detach(before) : null,
            After = value.After is { } after && after.WorldId == world ? Detach(after) : null
        };

        private static RegionSnapshot Detach(RegionSnapshot region) => region with
        {
            Members = region.Members.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            Groups = region.Groups.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            Flags = region.Flags.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
        };
    }
}
