using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

/// <summary>Stable plugin-owned actor identity, independent of the reusable vanilla NPC slot.</summary>
public readonly record struct NpcActorId(Guid Value);

/// <summary>One item in the vanilla presentation catalog. BaseValue is not an authoritative final purchase price.</summary>
public readonly record struct NpcShopOffer(string Id, short ItemType, short Stack, int BaseValue, byte Prefix = 0, bool BuyOnce = false,
    long? CoinPrice = null);

public sealed class NpcShopCatalog
{
    public const int MaximumOffers = 40;
    public NpcShopCatalog(IEnumerable<NpcShopOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        NpcShopOffer[] values = offers.Take(MaximumOffers + 1).ToArray();
        if (values.Length > MaximumOffers || values.Any(offer => string.IsNullOrWhiteSpace(offer.Id) || offer.Id.Length > 128 ||
                offer.Id.Any(char.IsControl) || offer.ItemType <= 0 || offer.Stack <= 0 || offer.BaseValue < 0 || offer.CoinPrice < 0) ||
            values.Select(static offer => offer.Id).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Shop requires unique, valid offers and at most forty slots.", nameof(offers));
        Offers = Array.AsReadOnly(values);
    }
    public IReadOnlyList<NpcShopOffer> Offers { get; }
}

public sealed record NpcActorDefinition(int BaseType, string Name, bool Friendly = true, bool Immortal = true,
    bool Stationary = true, NpcShopCatalog? Shop = null);

public enum NpcActorIntentKind { Vanilla, Stop, MoveTo, FollowPlayer }

/// <summary>Horizontal intent; Terraria retains movement integration, gravity and collision.</summary>
public sealed record NpcActorIntent(NpcActorIntentKind Kind, PlayerPosition Target = default, PlayerSnapshot? Follow = null,
    float Speed = 1.5f, float Acceleration = 0.08f, float StopDistance = 24f)
{
    public bool IsValid => Enum.IsDefined(Kind) && float.IsFinite(Speed) && Speed is > 0 and <= 16 &&
        float.IsFinite(Acceleration) && Acceleration is > 0 and <= 4 && float.IsFinite(StopDistance) && StopDistance is >= 0 and <= 1024 &&
        (Kind != NpcActorIntentKind.MoveTo || float.IsFinite(Target.X) && float.IsFinite(Target.Y)) &&
        (Kind != NpcActorIntentKind.FollowPlayer || Follow?.SessionId is not null);
}

public sealed record NpcActorSnapshot(NpcActorId Id, NpcEventSnapshot Npc, string Name, NpcActorIntentKind Intent,
    NpcShopCatalog? Shop, long ShopRevision)
{
    public IReadOnlyList<string> SoldOutOffers { get; init; } = [];
}

public sealed record NpcActorCreationResult(NpcOperationStatus Status, INpcActor? Actor = null, string? ErrorCode = null);

public enum NpcShopPresentationStatus { Sent, Unavailable, NotInteracting, NoCatalog, InvalidSession, ActorGone }
public enum NpcShopPurchaseStatus { Committed, Unavailable, ActorGone, NotInteracting, CatalogChanged, OfferUnavailable,
    InvalidInventory, InsufficientFunds, InventoryFull, InvalidOffer, InventoryChanged, InventoryAuthorityRequired }
public sealed record NpcShopPurchaseResult(NpcShopPurchaseStatus Status, long ShopRevision = 0, long CoinsPaid = 0,
    int ItemType = 0, int Stack = 0);
public readonly record struct NpcActorPurchasedEvent(NpcActorId Actor, PlayerSnapshot Buyer, string OfferId, NpcShopPurchaseResult Purchase);

/// <summary>Plugin-owned exact native actor lifetime. Dispose retires this actor, never a reused slot.</summary>
public interface INpcActor : IDisposable
{
    NpcActorId Id { get; }
    bool IsRetired { get; }
    ValueTask<NpcActorSnapshot?> CaptureAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> RenameAsync(string name, CancellationToken cancellationToken = default);
    ValueTask<bool> TeleportAsync(PlayerPosition position, CancellationToken cancellationToken = default);
    ValueTask<bool> SetIntentAsync(NpcActorIntent intent, CancellationToken cancellationToken = default);
    ValueTask<bool> SetShopAsync(NpcShopCatalog? shop, CancellationToken cancellationToken = default);
    /// <summary>Send catalog presentation to a player's already open vanilla shop. Does not open UI or commit a purchase.</summary>
    ValueTask<NpcShopPresentationStatus> PresentShopAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
    /// <summary>Explicit server inventory transaction requiring native authoritative SSC for multiplayer. Independent of vanilla client shop purchases.</summary>
    ValueTask<NpcShopPurchaseResult> PurchaseAsync(PlayerSnapshot player, string offerId, long expectedShopRevision,
        CancellationToken cancellationToken = default);
}

public interface IWorldNpcActorService
{
    ValueTask<NpcActorCreationResult> CreateAsync(WorldId worldId, NpcActorDefinition definition, PlayerPosition position,
        CancellationToken cancellationToken = default);
}

/// <summary>Confirmed conversation selection for an exact player session and custom actor.</summary>
public readonly record struct NpcActorInteractedEvent(NpcActorId Actor, NpcEventSnapshot Npc, PlayerSnapshot Player);
