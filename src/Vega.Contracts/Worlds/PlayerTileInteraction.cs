using Vega.Contracts.Players;
using Vega.Contracts.Scenes;

namespace Vega.Contracts.Worlds;

/// <summary>Requested gameplay operation, not a packet identifier or a committed tile change.</summary>
public enum TileInteractionKind
{
    Tile, ObjectPlacement, Door, Paint, Sign, Container, Liquid, TileEntity, Wiring, Other
}

public enum TileInteractionAccess { Build, Use }
public enum TileEditOperation { BreakTile, PlaceTile, ReplaceTile, BreakWall, PlaceWall, ReplaceWall, AddWire, RemoveWire,
    AddActuator, RemoveActuator, ChangeShape, FrameTrack, Activate, LogicGate, Other }

/// <summary>One exact player session's validated interaction intent before native processing.
/// Start/End are requested anchor positions; an object or wiring operation can affect additional tiles.</summary>
public readonly record struct PlayerTileInteractionEvent(WorldId WorldId, PlayerId PlayerId, PlayerSessionId SessionId,
    TileInteractionKind Kind, TileInteractionAccess Access, WorldTilePosition Start, WorldTilePosition End,
    TileEditOperation? Operation = null, int? ContentType = null);

/// <summary>Host-internal marker emitted only after all typed interaction policies allow the request.</summary>
public readonly record struct PlayerTileInteractionAcceptedEvent(PlayerTileInteractionEvent Intent);

/// <summary>One observed changed cell, with detached native before/after values.</summary>
public readonly record struct PlayerTileCellChange(WorldTilePosition Position, SceneTile Before, SceneTile After);

/// <summary>Changes observed around the accepted native player interaction, within a bounded anchor area.
/// Only returned native processing is observed: object state and effects outside the observed bounds are not included.</summary>
public readonly record struct PlayerTileChangesCommittedEvent(PlayerTileInteractionEvent Intent, SceneBounds ObservedBounds,
    IReadOnlyList<PlayerTileCellChange> Changes);
