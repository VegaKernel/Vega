using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Terraria.Networking;

/// <summary>
/// Enforces the protocol trust boundary before Terraria mutates connection or world state.
/// Gameplay-specific authorization remains in the Core packet policy pipeline.
/// </summary>
internal static class TerrariaInboundPacketGuard
{
    private const short MinimumItemNetId = -48;
    private const short MaximumItemStack = 9_999;
    private const byte MaximumVanillaTeam = 5;

    internal static PacketProcessingDecision Validate(
        byte packetId,
        TerrariaConnectionState connectionState,
        int playerSlot,
        ReadOnlySpan<byte> payload)
    {
        PacketPipelineProfile profile = PacketPipelineClassification.Get(packetId);
        PacketTypes packetType = (PacketTypes)packetId;

        switch (profile.Ingress)
        {
            case PacketIngressDisposition.Unknown:
                return PacketProcessingDecision.Violation("packet.ingress.unclassified");

            case PacketIngressDisposition.ServerOnly:
                return PacketProcessingDecision.Violation("packet.ingress.server_only");

            case PacketIngressDisposition.Metadata:
                // TerrariaConnectionMetadataRegistry owns these packets and executes before this guard.
                return PacketProcessingDecision.Allow;

            case PacketIngressDisposition.AllowedByDesign:
                return PacketProcessingDecision.Allow;

            case PacketIngressDisposition.Handshake:
                if (connectionState >= TerrariaConnectionState.FullyConnected)
                {
                    return PacketProcessingDecision.Violation("packet.ingress.handshake_after_login");
                }

                return ValidateExactStructure(packetType, payload);

            case PacketIngressDisposition.EarlyValidated:
                return ValidateEarly(packetType, playerSlot, payload);

            case PacketIngressDisposition.GameplayValidated:
                return connectionState < TerrariaConnectionState.FullyConnected
                    ? PacketProcessingDecision.Violation("packet.ingress.gameplay_before_login")
                    : PacketProcessingDecision.Allow;

            default:
                return PacketProcessingDecision.Violation("packet.ingress.invalid_disposition");
        }
    }

    private static PacketProcessingDecision ValidateEarly(
        PacketTypes packetType,
        int playerSlot,
        ReadOnlySpan<byte> payload)
    {
        try
        {
            return packetType switch
            {
                PacketTypes.PlayerInfo => ValidatePlayerInfo(payload),
                PacketTypes.PlayerSlot => ValidatePlayerSlot(payload),
                PacketTypes.TileGetSection => ValidateTileGetSection(payload),
                PacketTypes.PlayerSpawn => ValidatePlayerSpawn(payload),
                PacketTypes.RequestSection => ValidateRequestSection(payload),
                _ => PacketProcessingDecision.Violation("packet.ingress.early_validator_missing")
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            return PacketProcessingDecision.Reject("packet.ingress.malformed");
        }
    }

    private static PacketProcessingDecision ValidatePlayerInfo(ReadOnlySpan<byte> payload)
    {
        if (!PacketViewParser.TryParsePayload(PacketTypes.PlayerInfo, payload, out PacketView packet))
        {
            return PacketProcessingDecision.Reject("packet.player_info.malformed");
        }

        var info = new PlayerInfoView(packet);
        string name = info.GetName().Trim();
        if (name.Length == 0)
        {
            return PacketProcessingDecision.Violation("packet.player_info.empty_name");
        }

        if (name.StartsWith("tsi:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("tsn:", StringComparison.OrdinalIgnoreCase))
        {
            return PacketProcessingDecision.Violation("packet.player_info.reserved_name_prefix");
        }

        if (!float.IsFinite(info.VoicePitchOffset))
        {
            return PacketProcessingDecision.Violation("packet.player_info.non_finite_voice_pitch");
        }

        return PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidatePlayerSlot(ReadOnlySpan<byte> payload)
    {
        if (!PacketViewParser.TryParsePayload(PacketTypes.PlayerSlot, payload, out PacketView packet))
        {
            return PacketProcessingDecision.Reject("packet.player_slot.malformed");
        }

        var slot = new PlayerSlotView(packet);
        if (slot.SlotId < 0 || slot.SlotId >= TerrariaPacketSecurityLimits.PlayerItemSlotCount)
        {
            return PacketProcessingDecision.Violation("packet.player_slot.slot_out_of_range");
        }

        if (slot.Stack is < 0 or > MaximumItemStack)
        {
            return PacketProcessingDecision.Violation("packet.player_slot.stack_out_of_range");
        }

        if (slot.ItemNetId < MinimumItemNetId || slot.ItemNetId >= TerrariaPacketSecurityLimits.ItemTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.player_slot.item_type_out_of_range");
        }

        if (slot.Prefix >= TerrariaPacketSecurityLimits.PrefixCount)
        {
            return PacketProcessingDecision.Violation("packet.player_slot.prefix_out_of_range");
        }

        return (slot.ItemFlags & ~0x03) != 0
            ? PacketProcessingDecision.Violation("packet.player_slot.flags_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidateTileGetSection(ReadOnlySpan<byte> payload)
    {
        // Multiplicity 2.6.2 still models the old eight-byte form. Protocol 325 appends team.
        // Keep this reader exact so Vega is safe while the protocol-325 package fix is staged.
        if (payload.Length != 9)
        {
            return PacketProcessingDecision.Reject("packet.tile_get_section.malformed");
        }

        int x = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int y = BinaryPrimitives.ReadInt32LittleEndian(payload[sizeof(int)..]);
        byte team = payload[sizeof(int) * 2];

        if (team > MaximumVanillaTeam)
        {
            return PacketProcessingDecision.Violation("packet.tile_get_section.team_out_of_range");
        }

        bool spawnRequest = x == -1 && y == -1;
        bool inWorld = x >= 0 && y >= 0 &&
            x < TerrariaPacketSecurityLimits.WorldWidth &&
            y < TerrariaPacketSecurityLimits.WorldHeight;
        return spawnRequest || inWorld
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Violation("packet.tile_get_section.position_out_of_range");
    }

    private static PacketProcessingDecision ValidatePlayerSpawn(ReadOnlySpan<byte> payload)
    {
        // Protocol 325 payload: player, spawnX/Y, respawn timer, PvE/PvP deaths, team, context.
        if (payload.Length != 15)
        {
            return PacketProcessingDecision.Reject("packet.player_spawn.malformed");
        }

        short spawnX = BinaryPrimitives.ReadInt16LittleEndian(payload[1..]);
        short spawnY = BinaryPrimitives.ReadInt16LittleEndian(payload[3..]);
        int respawnTimer = BinaryPrimitives.ReadInt32LittleEndian(payload[5..]);
        short deathsPve = BinaryPrimitives.ReadInt16LittleEndian(payload[9..]);
        short deathsPvp = BinaryPrimitives.ReadInt16LittleEndian(payload[11..]);
        byte team = payload[13];

        if (spawnX < -1 || spawnY < -1 ||
            spawnX >= TerrariaPacketSecurityLimits.WorldWidth ||
            spawnY >= TerrariaPacketSecurityLimits.WorldHeight)
        {
            return PacketProcessingDecision.Violation("packet.player_spawn.position_out_of_range");
        }

        if (respawnTimer < 0 || deathsPve < 0 || deathsPvp < 0)
        {
            return PacketProcessingDecision.Violation("packet.player_spawn.counter_out_of_range");
        }

        return team > MaximumVanillaTeam
            ? PacketProcessingDecision.Violation("packet.player_spawn.team_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidateRequestSection(ReadOnlySpan<byte> payload)
    {
        if (!PacketViewParser.TryParsePayload(PacketTypes.RequestSection, payload, out PacketView packet))
        {
            return PacketProcessingDecision.Reject("packet.request_section.malformed");
        }

        _ = new RequestSectionView(packet);
        return PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidateExactStructure(
        PacketTypes packetType,
        ReadOnlySpan<byte> payload) =>
        PacketViewParser.TryParsePayload(packetType, payload, out _)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject("packet.ingress.malformed");
}
