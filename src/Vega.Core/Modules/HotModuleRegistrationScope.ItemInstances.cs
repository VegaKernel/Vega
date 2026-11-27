using System.Text.Json;
using System.Text.Json.Serialization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Items;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    // This collection is reserved in the same owner namespace as plugin Data. Typed mutations
    // retain revoked records forever, so a public document deletion cannot resurrect an instance.
    private const string ItemInstanceCollection = "vega.custom-item-instances";
    private IPluginCustomItemInstances itemInstances = null!;

    private void InitializeItemInstances(string pluginName, IPluginDocumentStore? store,
        IPlayerAccountSessionService? authentication, IPlayerDirectory? directory, WorldId? worldId) =>
        itemInstances = new PluginItemInstances(this, pluginName, store, authentication, directory, worldId);

    private static bool ValidItemRecipe(CustomItemRecipe recipe) =>
        !string.IsNullOrWhiteSpace(recipe.Name) && recipe.Name.Length <= 128 && !recipe.Name.Any(char.IsControl) &&
        recipe.ItemType is > 0 and <= short.MaxValue && recipe.Stack is > 0 and <= short.MaxValue &&
        recipe.Prefix is >= 0 and <= byte.MaxValue && recipe.WorldStats is not { IsValid: false };

    private sealed class PluginItemInstances(HotModuleRegistrationScope owner, string pluginName, IPluginDocumentStore? store,
        IPlayerAccountSessionService? authentication, IPlayerDirectory? directory, WorldId? worldId) : IPluginCustomItemInstances
    {
        private readonly record struct OwnerCapture(PlayerSnapshot Player, PlayerAccountBinding Binding, CustomItemOwner Identity);

        public bool IsAvailable => store is not null && authentication is not null && directory is not null && worldId.HasValue &&
            owner.executionGuard.AcceptsCallbacks && !owner.lifetime.IsCancellationRequested;

        public ValueTask<CustomItemInstanceResult> IssueAsync(PlayerSnapshot player, Guid id, string recipeName,
            int definitionVersion, CancellationToken cancellationToken = default)
        {
            string key = Key(id);
            if (definitionVersion < 1) throw new ArgumentOutOfRangeException(nameof(definitionVersion));
            ArgumentException.ThrowIfNullOrWhiteSpace(recipeName);
            if (!IsAvailable) return Result(CustomItemInstanceStatus.Unavailable);
            if (!TryCapture(player, out var captured)) return Result(CustomItemInstanceStatus.NotAuthenticated);
            // A durable instance is a single virtual item; never infer quantity from a native stack.
            var definition = owner.Players.Actions.CustomItems.Snapshot()
                .FirstOrDefault(recipe => recipe.Stack == 1 && recipe.Name.Equals(recipeName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (definition is null) return Result(CustomItemInstanceStatus.DefinitionNotFound);
            return owner.RunScopedAsync<CustomItemInstanceResult>(async token =>
            {
                var existing = await Read(key, token).ConfigureAwait(false);
                if (!Current(captured)) return new(CustomItemInstanceStatus.NotAuthenticated);
                if (existing is not null) return ExistingIssue(existing, captured.Identity, definition, definitionVersion);
                var now = DateTimeOffset.UtcNow;
                var value = new CustomItemInstance(id, captured.Identity, definitionVersion, definition, now, now);
                if (await Commit(key, 0, value, token).ConfigureAwait(false))
                    return Applied(value, 1);
                // A concurrent issuer won; a retry cannot create a second instance or overwrite its owner.
                existing = await Read(key, token).ConfigureAwait(false);
                if (!Current(captured)) return new(CustomItemInstanceStatus.NotAuthenticated);
                return existing is null ? new(CustomItemInstanceStatus.Conflict) : ExistingIssue(existing, captured.Identity, definition, definitionVersion);
            }, cancellationToken);
        }

        public ValueTask<CustomItemInstanceSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        {
            string key = Key(id);
            EnsureAvailable();
            return owner.RunScopedAsync(token => Read(key, token), cancellationToken);
        }

        public ValueTask<CustomItemInstanceSnapshot?> FindOwnedAsync(PlayerSnapshot player, Guid id, CancellationToken cancellationToken = default)
        {
            string key = Key(id);
            EnsureAvailable();
            if (!TryCapture(player, out var expected)) return ValueTask.FromResult<CustomItemInstanceSnapshot?>(null);
            return owner.RunScopedAsync<CustomItemInstanceSnapshot?>(async token =>
            {
                var result = await Read(key, token).ConfigureAwait(false);
                return result is { Instance.Revoked: false } && result.Instance.Owner == expected.Identity && Current(expected) ? result : null;
            }, cancellationToken);
        }

        public ValueTask<IReadOnlyList<CustomItemInstanceSnapshot>> ListAsync(Guid? afterId = null, int limit = 100,
            CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            string? after = afterId is { } cursor ? Key(cursor) : null;
            return owner.RunScopedAsync<IReadOnlyList<CustomItemInstanceSnapshot>>(async token =>
                Array.AsReadOnly((await store!.ListAsync(pluginName, ItemInstanceCollection, after, limit, token).ConfigureAwait(false))
                    .Select(Decode).ToArray()), cancellationToken);
        }

        public ValueTask<CustomItemInstanceResult> TransferAsync(Guid id, long expectedRevision, PlayerSnapshot from, PlayerSnapshot to,
            CancellationToken cancellationToken = default)
        {
            string key = Key(id);
            ValidateRevision(expectedRevision);
            if (!IsAvailable) return Result(CustomItemInstanceStatus.Unavailable);
            if (!TryCapture(from, out var source) || !TryCapture(to, out var destination)) return Result(CustomItemInstanceStatus.NotAuthenticated);
            if (source.Identity == destination.Identity) return Result(CustomItemInstanceStatus.Conflict);
            return owner.RunScopedAsync<CustomItemInstanceResult>(async token =>
            {
                var current = await Read(key, token).ConfigureAwait(false);
                var status = CheckMutation(current, expectedRevision);
                if (status != CustomItemInstanceStatus.Applied) return new(status);
                if (!Current(source) || !Current(destination)) return new(CustomItemInstanceStatus.NotAuthenticated);
                if (current!.Instance.Owner != source.Identity) return new(CustomItemInstanceStatus.NotOwner);
                var value = current.Instance with { Owner = destination.Identity, UpdatedUtc = NextTimestamp(current.Instance) };
                return await Commit(key, expectedRevision, value, token).ConfigureAwait(false)
                    ? Applied(value, expectedRevision + 1) : new(CustomItemInstanceStatus.Conflict);
            }, cancellationToken);
        }

        public ValueTask<CustomItemInstanceResult> RevokeAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default)
        {
            string key = Key(id);
            ValidateRevision(expectedRevision);
            if (!IsAvailable) return Result(CustomItemInstanceStatus.Unavailable);
            return owner.RunScopedAsync<CustomItemInstanceResult>(async token =>
            {
                var current = await Read(key, token).ConfigureAwait(false);
                var status = CheckMutation(current, expectedRevision);
                if (status != CustomItemInstanceStatus.Applied) return new(status);
                // This is a plugin-owned administrative operation; the owner need not be online.
                var value = current!.Instance with { Revoked = true, UpdatedUtc = NextTimestamp(current.Instance) };
                return await Commit(key, expectedRevision, value, token).ConfigureAwait(false)
                    ? Applied(value, expectedRevision + 1) : new(CustomItemInstanceStatus.Conflict);
            }, cancellationToken);
        }

        private bool TryCapture(PlayerSnapshot player, out OwnerCapture captured)
        {
            ArgumentNullException.ThrowIfNull(player);
            captured = default;
            if (player.WorldId != worldId || player.SessionId is not { } session ||
                directory?.TryGet(player.Id, out var current) != true || current is null || current.WorldId != player.WorldId || current.SessionId != session ||
                authentication?.TryGetBinding(current, out var binding) != true || binding.PlayerId != player.Id || binding.SessionId != session || binding.AuthenticationId == Guid.Empty ||
                !ReadOwner(binding, out var identity)) return false;
            captured = new(current, binding, identity);
            return true;
        }

        private bool Current(OwnerCapture expected) => IsAvailable &&
            directory!.TryGet(expected.Player.Id, out var current) && current is not null && current.WorldId == expected.Player.WorldId &&
            current.SessionId == expected.Binding.SessionId && ReadOwner(expected.Binding, out var identity) && identity == expected.Identity;

        private bool ReadOwner(PlayerAccountBinding binding, out CustomItemOwner identity)
        {
            identity = default;
            if (!authentication!.TryRunWithBinding(binding, () =>
                authentication.TryGetAccount(binding.PlayerId, out var account) && account is { RegisteredUtc: { } registered } && account.Id == binding.AccountId
                    ? new CustomItemOwner?(new(account.Id, registered.ToUniversalTime())) : null, out var observed) || observed is not { } value ||
                value.AccountId.Value <= 0 || value.RegisteredUtc == default) return false;
            identity = value;
            return true;
        }

        private async ValueTask<CustomItemInstanceSnapshot?> Read(string key, CancellationToken token)
        {
            var document = await store!.ReadAsync(pluginName, ItemInstanceCollection, key, token).ConfigureAwait(false);
            return document is null ? null : Decode(document);
        }

        private static CustomItemInstanceSnapshot Decode(PluginDocument document)
        {
            var instance = JsonSerializer.Deserialize(document.Json, CustomItemInstanceJsonContext.Default.CustomItemInstance);
            if (instance is null || instance.FormatVersion != 1 || instance.Id == Guid.Empty || Key(instance.Id) != document.Key ||
                instance.Owner.AccountId.Value <= 0 || instance.Owner.RegisteredUtc == default || instance.DefinitionVersion < 1 ||
                instance.Definition is null || instance.Definition.Stack != 1 || !ValidItemRecipe(instance.Definition) ||
                instance.IssuedUtc == default || instance.UpdatedUtc < instance.IssuedUtc || document.Revision <= 0)
                throw new InvalidDataException("Invalid persistent custom item instance; ownership was not changed.");
            return new(instance, document.Revision);
        }

        private ValueTask<bool> Commit(string key, long revision, CustomItemInstance value, CancellationToken token) =>
            store!.CommitAsync(pluginName, [new(ItemInstanceCollection, key, revision,
                JsonSerializer.Serialize(value, CustomItemInstanceJsonContext.Default.CustomItemInstance))], token);

        private static CustomItemInstanceResult ExistingIssue(CustomItemInstanceSnapshot existing, CustomItemOwner owner,
            CustomItemRecipe definition, int version)
        {
            if (existing.Instance.Revoked) return new(CustomItemInstanceStatus.Revoked);
            return existing.Instance.Owner == owner && existing.Instance.Definition == definition && existing.Instance.DefinitionVersion == version
                ? new(CustomItemInstanceStatus.AlreadyIssued, existing) : new(CustomItemInstanceStatus.Conflict);
        }

        private static CustomItemInstanceStatus CheckMutation(CustomItemInstanceSnapshot? current, long revision) =>
            current is null ? CustomItemInstanceStatus.NotFound : current.Revision != revision ? CustomItemInstanceStatus.Conflict :
                current.Instance.Revoked ? CustomItemInstanceStatus.Revoked : CustomItemInstanceStatus.Applied;

        private void EnsureAvailable()
        {
            if (!IsAvailable) throw new NotSupportedException("Persistent custom item ownership is unavailable in this activation.");
        }

        private static string Key(Guid id) => id != Guid.Empty ? id.ToString("N") : throw new ArgumentException("Instance ID cannot be empty.", nameof(id));
        private static void ValidateRevision(long revision)
        {
            if (revision is <= 0 or long.MaxValue) throw new ArgumentOutOfRangeException(nameof(revision));
        }
        private static DateTimeOffset NextTimestamp(CustomItemInstance value)
        {
            var now = DateTimeOffset.UtcNow;
            return now < value.UpdatedUtc ? value.UpdatedUtc : now;
        }
        private static ValueTask<CustomItemInstanceResult> Result(CustomItemInstanceStatus status) => ValueTask.FromResult(new CustomItemInstanceResult(status));
        private static CustomItemInstanceResult Applied(CustomItemInstance instance, long revision) => new(CustomItemInstanceStatus.Applied, new(instance, revision));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(CustomItemInstance))]
internal sealed partial class CustomItemInstanceJsonContext : JsonSerializerContext;
