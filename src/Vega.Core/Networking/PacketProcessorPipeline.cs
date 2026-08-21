using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

public sealed class PacketProcessorPipeline<TPacket> : IPacketProcessor<TPacket>
	where TPacket : allows ref struct
{
	private readonly IPacketProcessor<TPacket>[] processors;

	public PacketProcessorPipeline(IEnumerable<IPacketProcessor<TPacket>> processors)
	{
		ArgumentNullException.ThrowIfNull(processors);
		this.processors = processors.ToArray();
	}

	public PacketProcessingDecision Process(scoped in PacketProcessingContext<TPacket> context)
	{
		foreach (IPacketProcessor<TPacket> processor in processors)
		{
			PacketProcessingDecision decision = processor.Process(in context);
			if (decision.Kind != PacketDecisionKind.Allow)
			{
				return decision;
			}
		}

		return PacketProcessingDecision.Allow;
	}
}
