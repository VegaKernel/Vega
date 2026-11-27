using Vega.Contracts.Worlds;

namespace Vega.Contracts.Scenes;

public readonly record struct VirtualSignId(Guid Value)
{
    public static VirtualSignId New() => new(Guid.NewGuid());
}

/// <summary>Immutable, provider-owned text attached to a logical 2x2 sign footprint.</summary>
public sealed record SceneVirtualSignDefinition
{
    public const int MaximumTextLength = 1024;

    public SceneVirtualSignDefinition(VirtualSignId id, int localX, int localY, string text, long revision)
        : this(id, localX, localY, text, revision, false) { }

    public SceneVirtualSignDefinition(VirtualSignId id, int localX, int localY, string text, long revision, bool allowEdits)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (id.Value == Guid.Empty || localX < 0 || localY < 0 || revision < 1 || !IsValidText(text))
            throw new ArgumentException("Virtual sign identity, position, revision or text is invalid.");
        Id = id;
        LocalX = localX;
        LocalY = localY;
        Text = text;
        Revision = revision;
        AllowEdits = allowEdits;
    }

    public VirtualSignId Id { get; }
    public int LocalX { get; }
    public int LocalY { get; }
    public string Text { get; }
    public long Revision { get; }
    public bool AllowEdits { get; }

    public static bool IsValidText(string? text) => text is not null && text.Length <= MaximumTextLength &&
        !text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
}

public readonly record struct SceneVirtualSignSnapshot(SceneId SceneId, OverlayId OverlayId, WorldId WorldId,
    WorldTilePosition Position, SceneVirtualSignDefinition Definition);

/// <summary>A read intent for a plugin-owned sign, not a native sign-index assignment.</summary>
public readonly record struct SceneVirtualSignReadRequest(SceneVirtualSignSnapshot Sign, SceneSessionObserver Observer,
    DateTimeOffset RequestedAt);

/// <summary>Observed edit of an opted-in logical sign after a read by the same session. Never forwarded to native sign storage.</summary>
public readonly record struct SceneVirtualSignEditRequest(SceneVirtualSignSnapshot Sign, SceneSessionObserver Observer,
    string Text, DateTimeOffset RequestedAt);
