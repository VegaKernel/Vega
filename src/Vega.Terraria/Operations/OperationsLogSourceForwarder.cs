using Vega.Contracts.Operations;

namespace Vega.Terraria.Operations;

/// <summary>
/// Bridges preformatted subsystem log messages into the host logger. Sources own semantics and text;
/// the logger owns buffering and delivery to sinks.
/// </summary>
internal sealed class OperationsLogSourceForwarder : IDisposable
{
    private readonly IOperationsLogger logger;
    private readonly IOperationsLogSource[] sources;
    private int disposed;

    public OperationsLogSourceForwarder(
        IOperationsLogger logger,
        params IOperationsLogSource[] sources)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(sources);
        this.sources = sources
            .Where(static source => source is not null)
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToArray();

        foreach (IOperationsLogSource source in this.sources)
        {
            source.LogProduced += OnLogProduced;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (IOperationsLogSource source in sources)
        {
            source.LogProduced -= OnLogProduced;
        }
    }

    private void OnLogProduced(OperationsLogMessage message)
    {
        try
        {
            logger.Log(
                message.Level,
                message.Source,
                message.Text,
                message.Exception,
                message.CorrelationId);
        }
        catch
        {
            // A logging transport failure must not affect the subsystem that produced the message.
        }
    }
}
