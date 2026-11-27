using TerrariaRuntime;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Hooking;

/// <summary>Conservative native PvE admission. No damage estimation from client-reported items or source.</summary>
internal sealed class NativeNpcCombatAuthorityHooks(INetworkMetricsRecorder metrics) : IDisposable
{
    private static readonly PacketProcessingDecision Unavailable = PacketProcessingDecision.Reject("combat.npc_participant_unavailable");
    private Hooks.MessageBuffer.ServerNpcHitResolver? previous;
    private Hooks.MessageBuffer.ServerNpcHitResolver? installed;

    public void Attach()
    {
        if (installed is not null) return;
        previous = Hooks.MessageBuffer.ServerNpcHit;
        installed = AllowHit;
        Hooks.MessageBuffer.ServerNpcHit = installed;
    }

    private bool AllowHit(int slot, global::Terraria.NPC target, ref int damage, ref float knockback, ref int direction, ref byte critical)
    {
        // A fully connected, active sender is required. Do not reject delayed legitimate
        // projectile hits after player death just by testing the player's alive flag.
        var players = global::Terraria.Main.player;
        if (!NativeGameplayReadinessHooks.IsFullyConnected(slot) || (uint)slot >= (uint)players.Length ||
            players[slot] is not { active: true } || !IsCurrentTarget(target, global::Terraria.Main.npc))
        {
            metrics.RecordSecurityDecision(Unavailable);
            return false;
        }

        // The runtime's installed resolver boundary validates its final scalar values.
        // Retain an existing native resolver, if present, rather than replacing its decision.
        return previous?.Invoke(slot, target, ref damage, ref knockback, ref direction, ref critical) ?? true;
    }

    internal static bool IsCurrentTarget(global::Terraria.NPC target, global::Terraria.NPC[] npcs) =>
        target.active && (uint)target.whoAmI < (uint)npcs.Length && ReferenceEquals(npcs[target.whoAmI], target);

    public void Dispose()
    {
        if (installed is not null && ReferenceEquals(Hooks.MessageBuffer.ServerNpcHit, installed))
            Hooks.MessageBuffer.ServerNpcHit = previous;
        installed = null;
        previous = null;
    }
}
