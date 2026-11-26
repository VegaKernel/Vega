using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using TerrariaRuntime;
using Vega.Contracts.Operations;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Hooking;

internal sealed class NativeChestPolicyHooks(
    IWorldInteractionState interactions,
    IChestInteractionPolicy policy,
    IGameThreadDispatcher dispatcher,
    IOperationsLogger logger,
    WorldId worldId) : IDisposable
{
    private bool attached;

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        Hooks.MessageBuffer.GetData += OnReceiveData;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.MessageBuffer.GetData -= OnReceiveData;
        attached = false;
    }

    private void OnReceiveData(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        if (args.Result == HookResult.Cancel || args.PacketId != (byte)PacketTypes.ChestItem ||
            !MultiplicityPacketProcessing.TryParseInboundPayload(
                args.PacketId,
                args.Instance.readBuffer,
                args.ReadOffset,
                args.GetPayloadLength(),
                out PacketView packet))
        {
            return;
        }

        int chestId;
        try
        {
            chestId = packet.AsChestItemView().ChestId;
        }
        catch (Exception)
        {
            return;
        }

        if (!interactions.TryGetChestPosition(chestId, out WorldTilePosition position))
        {
            return;
        }

        var identity = new ChestIdentity(worldId, position.X, position.Y);
        _ = dispatcher.TryPost("chest-policy.observe", () => _ = ObserveSafeAsync(identity));
    }

    private async Task ObserveSafeAsync(ChestIdentity identity)
    {
        try
        {
            await policy.ObserveAsync(identity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "chests",
                $"Chest policy failed for {identity}.",
                ex);
        }
    }
}
