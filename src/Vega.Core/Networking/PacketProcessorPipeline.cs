using System.Diagnostics;
using Vega.Contracts.Diagnostics;
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
				Diagnostics.Write("ProcessorCompleted", new PacketProcessorDiagnosticEvent(
					processor.GetType().FullName ?? processor.GetType().Name,
					typeof(TPacket).FullName ?? typeof(TPacket).Name,
					context.Direction.ToString(),
					context.ConnectionId.ToString(),
					context.PlayerId?.ToString(),
					Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds,
					decision.Kind.ToString(),
					decision.Reason));
			}

			if (decision.Kind != PacketDecisionKind.Allow)
			{
				return decision;
			}
		}

		return PacketProcessingDecision.Allow;
	}
}
