using System;
using System.IO;
using Multiplicity.Packets.Views;
using TerrariaPacketTypes = Terraria.ID.PacketTypes;

namespace TShockAPI.Net;

/// <summary>
/// Represents one inbound Terraria packet over the original TSAPI receive buffer.
/// </summary>
/// <remarks>
/// This type is stack-only so span-backed packet views cannot accidentally escape the synchronous receive path.
/// </remarks>
internal readonly ref struct IncomingPacket
{
	private readonly byte[] buffer;
	private readonly int offset;
	private readonly int length;

	public IncomingPacket(TerrariaPacketTypes type, TSPlayer player, byte[] buffer, int offset, int length)
	{
		ArgumentNullException.ThrowIfNull(player);
		ArgumentNullException.ThrowIfNull(buffer);
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfNegative(length);

		if (offset > buffer.Length - length)
			throw new ArgumentException("Packet payload exceeds the receive buffer.", nameof(length));

		Type = type;
		Player = player;
		this.buffer = buffer;
		this.offset = offset;
		this.length = length;
	}

	public TerrariaPacketTypes Type { get; }

	public TSPlayer Player { get; }

	public ReadOnlySpan<byte> Payload => new(buffer, offset, length);

	public PacketView View => PacketViewParser.ParsePayload((byte)Type, buffer, offset, length);

	public MemoryStream CreateLegacyStream()
	{
		return new MemoryStream(buffer, offset, length, writable: true, publiclyVisible: false);
	}
}
