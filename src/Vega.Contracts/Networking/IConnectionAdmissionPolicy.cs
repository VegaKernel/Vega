namespace Vega.Contracts.Networking;

/// <summary>Synchronous pre-world admission decision using the identity established by the runtime; never invoked on the ordinary gameplay packet path.</summary>
public interface IConnectionAdmissionPolicy
{
    PacketProcessingDecision Evaluate(ConnectionMetadataSnapshot connection);
}
