using System.Diagnostics;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

public sealed class PacketProcessorPipeline<TPacket> : IPacketProcessor<TPacket>
	where TPacket : allows ref struct
{
	private static readonly DiagnosticListener Diagnostics = new("Vega.PacketPipeline");
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
			bool diagnose = Diagnostics.IsEnabled("ProcessorCompleted");
			long startedAt = diagnose ? Stopwatch.GetTimestamp() : 0;
			PacketProcessingDecision decision = processor.Process(in context);
			if (diagnose)
			{
				Diagnostics.Write("ProcessorCompleted", new
				{
					Processor = processor.GetType().FullName ?? processor.GetType().Name,
					PacketType = typeof(TPacket).FullName ?? typeof(TPacket).Name,
					Direction = context.Direction.ToString(),
					Connection = context.ConnectionId.ToString(),
					Player = context.PlayerId?.ToString(),
					ElapsedMicroseconds = Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds,
					Decision = decision.Kind.ToString(),
					decision.Reason
				});
			}

			if (decision.Kind != PacketDecisionKind.Allow)
			{
				return decision;
			}
		}

		return PacketProcessingDecision.Allow;
	}
}
