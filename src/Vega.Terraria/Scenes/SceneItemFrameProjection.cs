using Multiplicity.Packets;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using Terraria.Net.Sockets;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;

namespace Vega.Terraria.Scenes;

/// <summary>Observer-local native item-frame metadata; never registers an entity in the server world.</summary>
internal sealed class SceneItemFrameProjection
{
    internal const int MaximumFramesPerViewer = 256;
    private readonly Viewer?[] viewers = new Viewer?[256];

    private sealed class Viewer(PlayerSessionId session, ISocket socket)
    {
        public PlayerSessionId Session { get; } = session;
        public ISocket Socket { get; } = socket;
        public Dictionary<Point16, PresentedFrame> Frames { get; } = [];
    }
    private readonly record struct PresentedFrame(int NativeId, SceneId Scene, OverlayId Overlay, VirtualItemFrameId Frame);

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
        IReadOnlyList<SceneVirtualItemFrameSnapshot> desired, Action<byte[]> send)
    {
        int slot = observer.Player.Slot;
        if ((uint)slot >= viewers.Length) return;
        Viewer? viewer = Volatile.Read(ref viewers[slot]);
        if (viewer is not null && (viewer.Session != observer.Session || !ReferenceEquals(viewer.Socket, socket)))
        {
            Volatile.Write(ref viewers[slot], null);
            viewer = null;
        }
        if (viewer is null && desired.Count == 0) return;

        // Only project over positions without a canonical tile entity. If one subsequently
        // appears, the next redraw removes our client-only ID and restores the real record.
        Dictionary<Point16, (SceneVirtualItemFrameSnapshot Snapshot, TEItemFrame Native)>? candidates = null;
        foreach (var snapshot in desired)
        {
            if (!TryCreateNative(snapshot, out var native) || TileEntity.ByPosition.ContainsKey(native.Position)) continue;
            (candidates ??= []).TryAdd(native.Position, (snapshot, native));
        }
        if (viewer is null && candidates is null) return;
        if (viewer is null) Volatile.Write(ref viewers[slot], viewer = new Viewer(observer.Session, socket));
        List<Point16>? removals = null;
        foreach (var pair in viewer.Frames)
        {
            Point16 position = pair.Key;
            if (!new SceneBounds(position.X, position.Y, 2, 2).Intersects(region)) continue;
            PresentedFrame present = pair.Value;
            if (candidates is not null && candidates.TryGetValue(position, out var candidate) && Matches(present, candidate.Snapshot)) continue;
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
            var (snapshot, native) = pair.Value;
            if (!viewer.Frames.TryGetValue(pair.Key, out var present))
            {
                if (viewer.Frames.Count >= MaximumFramesPerViewer) continue;
                // Reserve through the vanilla monotonic allocator, but do not insert into
                // ByID/ByPosition. Native IDs and virtual IDs cannot collide in this world lifetime.
                int id = TileEntity.AssignNewID();
                if (id < 0 || TileEntity.ByID.ContainsKey(id)) continue;
                present = new(id, snapshot.SceneId, snapshot.OverlayId, snapshot.Definition.Id);
            }
            native.ID = present.NativeId;
            // Repeat metadata after a redraw: a preceding native section refresh could have
            // discarded client-side entities even when the logical item revision is unchanged.
            send(Encode(present.NativeId, native));
            viewer.Frames[pair.Key] = present;
        }
        if (viewer.Frames.Count == 0) Interlocked.CompareExchange(ref viewers[slot], null, viewer);
    }

    private static bool Matches(PresentedFrame present, SceneVirtualItemFrameSnapshot candidate) =>
        present.Scene == candidate.SceneId && present.Overlay == candidate.OverlayId && present.Frame == candidate.Definition.Id;

    private static bool TryCreateNative(SceneVirtualItemFrameSnapshot snapshot, out TEItemFrame native)
    {
        native = null!;
        if (TileID.ItemFrame != SceneVirtualItemFrameDefinition.TileType || snapshot.Position.X is < 0 or > short.MaxValue ||
            snapshot.Position.Y is < 0 or > short.MaxValue) return false;
        var definition = snapshot.Definition;
        if (definition.ItemType >= ItemID.Count || definition.Prefix >= PrefixID.Count) return false;
        var item = new global::Terraria.Item();
        if (definition.ItemType != 0)
        {
            item.SetDefaults(definition.ItemType);
            if (definition.Stack > item.maxStack || definition.Prefix != 0 && !item.Prefix(definition.Prefix)) return false;
            item.stack = definition.Stack;
        }
        native = new TEItemFrame { Position = new Point16(snapshot.Position.X, snapshot.Position.Y), type = TEItemFrame.EntityTypeID, item = item };
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
