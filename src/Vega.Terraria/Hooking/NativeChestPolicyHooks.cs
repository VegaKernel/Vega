using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using OTAPI;
using Vega.Contracts.Operations;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Hooking;

internal sealed class NativeChestPolicyHooks(
    IWorldInteractionState interactions,
    IChestInteractionPolicy policy,
    IGameThreadDispatcher dispatcher,
    IOperationsLogger logger) : IDisposable
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
            !PacketViewParser.TryParsePayloadBounded(
                args.PacketId,
                args.Instance.readBuffer,
                args.ReadOffset,
                args.Length,
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

        var identity = new ChestIdentity(WorldId.Primary, position.X, position.Y);
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
