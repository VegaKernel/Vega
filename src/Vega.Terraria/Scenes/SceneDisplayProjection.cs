using Multiplicity.Packets;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using Terraria.Net.Sockets;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;

namespace Vega.Terraria.Scenes;

/// <summary>Observer-local native display metadata; never registers an entity in the server world.</summary>
internal sealed class SceneDisplayProjection
{
    internal const int MaximumFramesPerViewer = 256;
    private readonly Viewer?[] viewers = new Viewer?[256];

    private sealed class Viewer(PlayerSessionId session, ISocket socket)
    {
        public PlayerSessionId Session { get; } = session;
        public ISocket Socket { get; } = socket;
        public Dictionary<Point16, PresentedFrame> Frames { get; } = [];
    }
    private readonly record struct PresentedFrame(int NativeId, SceneId Scene, OverlayId Overlay, Guid Entity, int Width, int Height);
    private readonly record struct Candidate(SceneId Scene, OverlayId Overlay, Guid Entity, int Width, int Height, TileEntity Native);

    public bool HasPresentations(PlayerId player) => (uint)player.Slot < viewers.Length && Volatile.Read(ref viewers[player.Slot]) is not null;

    public void Forget(PlayerId player, PlayerSessionId session)
    {
        if ((uint)player.Slot >= viewers.Length) return;
        var current = Volatile.Read(ref viewers[player.Slot]);
        if (current?.Session == session) Interlocked.CompareExchange(ref viewers[player.Slot], null, current);
    }

    public void Clear() => Array.Clear(viewers);

    /// <summary>Runs after the matching virtual tile update on the native game thread.</summary>
    public void Reconcile(SceneSessionObserver observer, ISocket socket, SceneBounds region,
        IReadOnlyList<SceneVirtualItemFrameSnapshot> desired, Action<byte[]> send) => Reconcile(observer, socket, region, desired, [], send);

    public void Reconcile(SceneSessionObserver observer, ISocket socket, SceneBounds region,
        IReadOnlyList<SceneVirtualItemFrameSnapshot> frames, IReadOnlyList<SceneVirtualDisplaySnapshot> displays, Action<byte[]> send)
    {
        int slot = observer.Player.Slot;
        if ((uint)slot >= viewers.Length) return;
        Viewer? viewer = Volatile.Read(ref viewers[slot]);
        if (viewer is not null && (viewer.Session != observer.Session || !ReferenceEquals(viewer.Socket, socket)))
        {
            Volatile.Write(ref viewers[slot], null);
            viewer = null;
        }
        if (viewer is null && frames.Count == 0 && displays.Count == 0) return;

        // Only project over positions without a canonical tile entity. If one subsequently
        // appears, the next redraw removes our client-only ID and restores the real record.
        Dictionary<Point16, Candidate>? candidates = null;
        foreach (var snapshot in frames)
        {
            if (!TryCreateNative(snapshot, out var native) || TileEntity.ByPosition.ContainsKey(native.Position)) continue;
            (candidates ??= []).TryAdd(native.Position, new(snapshot.SceneId, snapshot.OverlayId, snapshot.Definition.Id.Value, 2, 2, native));
        }
        foreach (var snapshot in displays)
        {
            if (!TryCreateNative(snapshot, out var native) || TileEntity.ByPosition.ContainsKey(native.Position)) continue;
            var definition = snapshot.Definition;
            (candidates ??= []).TryAdd(native.Position, new(snapshot.SceneId, snapshot.OverlayId, definition.Id.Value, definition.Width, definition.Height, native));
        }
        if (viewer is null && candidates is null) return;
        if (viewer is null) Volatile.Write(ref viewers[slot], viewer = new Viewer(observer.Session, socket));
        List<Point16>? removals = null;
        foreach (var pair in viewer.Frames)
        {
            Point16 position = pair.Key;
            PresentedFrame present = pair.Value;
            if (!new SceneBounds(position.X, position.Y, present.Width, present.Height).Intersects(region)) continue;
            if (candidates is not null && candidates.TryGetValue(position, out var candidate) && Matches(present, candidate)) continue;
            (removals ??= []).Add(position);
        }
        if (removals is not null)
        foreach (Point16 position in removals)
        {
            send(Encode(viewer.Frames[position].NativeId, null));
            viewer.Frames.Remove(position);
            if (TileEntity.ByPosition.TryGetValue(position, out var canonical)) send(Encode(canonical.ID, canonical));
        }
        if (candidates is not null)
        foreach (var pair in candidates)
        {
            var candidate = pair.Value;
            var native = candidate.Native;
            if (!viewer.Frames.TryGetValue(pair.Key, out var present))
            {
                if (viewer.Frames.Count >= MaximumFramesPerViewer) continue;
                // Reserve through the vanilla monotonic allocator, but do not insert into
                // ByID/ByPosition. Native IDs and virtual IDs cannot collide in this world lifetime.
                int id = TileEntity.AssignNewID();
                if (id < 0 || TileEntity.ByID.ContainsKey(id)) continue;
                present = new(id, candidate.Scene, candidate.Overlay, candidate.Entity, candidate.Width, candidate.Height);
            }
            native.ID = present.NativeId;
            // Repeat metadata after a redraw: a preceding native section refresh could have
            // discarded client-side entities even when the logical item revision is unchanged.
            send(Encode(present.NativeId, native));
            viewer.Frames[pair.Key] = present;
        }
        if (viewer.Frames.Count == 0) Interlocked.CompareExchange(ref viewers[slot], null, viewer);
    }

    private static bool Matches(PresentedFrame present, Candidate candidate) =>
        present.Scene == candidate.Scene && present.Overlay == candidate.Overlay && present.Entity == candidate.Entity &&
        present.Width == candidate.Width && present.Height == candidate.Height;

    private static bool TryCreateNative(SceneVirtualItemFrameSnapshot snapshot, out TEItemFrame native)
    {
        native = null!;
        if (TileID.ItemFrame != SceneVirtualItemFrameDefinition.TileType || snapshot.Position.X is < 0 or > short.MaxValue ||
            snapshot.Position.Y is < 0 or > short.MaxValue) return false;
        var definition = snapshot.Definition;
        if (!TryCreateItem(new(definition.ItemType, definition.Stack, definition.Prefix), out var item)) return false;
        native = new TEItemFrame { Position = new Point16(snapshot.Position.X, snapshot.Position.Y), type = TEItemFrame.EntityTypeID, item = item };
        return true;
    }

    private static bool TryCreateNative(SceneVirtualDisplaySnapshot snapshot, out TileEntity native)
    {
        native = null!;
        if (snapshot.Position.X is < 0 or > short.MaxValue || snapshot.Position.Y is < 0 or > short.MaxValue) return false;
        var definition = snapshot.Definition;
        switch (definition.Kind)
        {
            case SceneVirtualDisplayKind.WeaponsRack when definition.TileType == TileID.WeaponsRack2:
                if (!TryCreateItem(definition.Items.FirstOrDefault(), out var rackItem)) return false;
                native = new TEWeaponsRack { type = TEWeaponsRack.EntityTypeID, item = rackItem };
                break;
            case SceneVirtualDisplayKind.DisplayDoll when definition.TileType == TileID.DisplayDoll:
                var doll = new TEDisplayDoll { type = TEDisplayDoll.EntityTypeID };
                if (!TryCopyItems(definition.Items, doll._equip) || !TryCopyItems(definition.Dyes, doll._dyes)) return false;
                native = doll;
                break;
            case SceneVirtualDisplayKind.HatRack when definition.TileType == TileID.HatRack:
                var hat = new TEHatRack { type = TEHatRack.EntityTypeID };
                if (!TryCopyItems(definition.Items, hat._items) || !TryCopyItems(definition.Dyes, hat._dyes)) return false;
                native = hat;
                break;
            case SceneVirtualDisplayKind.FoodPlatter when definition.TileType == TileID.FoodPlatter:
                if (!TryCreateItem(definition.Items.FirstOrDefault(), out var food)) return false;
                native = new TEFoodPlatter { type = TEFoodPlatter.EntityTypeID, item = food };
                break;
            default: return false;
        }
        native.Position = new(snapshot.Position.X, snapshot.Position.Y);
        return true;
    }

    private static bool TryCopyItems(IReadOnlyList<SceneDisplayItem> source, global::Terraria.Item[] destination)
    {
        if (source.Count > destination.Length) return false;
        for (int i = 0; i < source.Count; i++)
        {
            if (!TryCreateItem(source[i], out var item)) return false;
            destination[i] = item;
        }
        return true;
    }

    private static bool TryCreateItem(SceneDisplayItem value, out global::Terraria.Item item)
    {
        item = new global::Terraria.Item();
        if (value.ItemType == 0) return value.Stack == 0 && value.Prefix == 0;
        if (value.ItemType < 0 || value.ItemType >= ItemID.Count || value.Prefix >= PrefixID.Count) return false;
        item.SetDefaults(value.ItemType);
        if (value.Stack < 1 || value.Stack > item.maxStack || value.Prefix != 0 && !item.Prefix(value.Prefix)) return false;
        item.stack = value.Stack;
        return true;
    }

    internal static byte[] Encode(int id, TileEntity? entity)
    {
        using var stream = new MemoryStream();
        byte[] payload = [];
        if (entity is not null)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            TileEntity.Write(writer, entity, true);
            payload = stream.ToArray();
            stream.SetLength(0);
        }
        new UpdateTileEntity { Key = id, Exists = entity is not null, TileEntityPayload = payload }.ToStream(stream);
        return stream.ToArray();
    }
}
