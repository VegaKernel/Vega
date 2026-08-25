using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Sandbox discovery and lifecycle operations exposed to ordinary Vega plugins.</summary>
public interface IPluginSandboxes
{
    bool CanControl { get; }

    IReadOnlyList<SandboxStatus> Snapshot();

    ValueTask<SandboxOperationResult> CreateAsync(
        SandboxCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> StartAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> StopAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> RestartAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> ResetAsync(
        WorldId worldId,
        string? replacementSource = null,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> DestroyAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);
}

public static class VegaPluginSandboxes
{
    public static IPluginSandboxes Unavailable { get; } = new UnavailablePluginSandboxes();

    private sealed class UnavailablePluginSandboxes : IPluginSandboxes
    {
        public bool CanControl => false;

        public IReadOnlyList<SandboxStatus> Snapshot() => [];

        public ValueTask<SandboxOperationResult> CreateAsync(
            SandboxCreateRequest request,
            CancellationToken cancellationToken = default) => Rejected();

        public ValueTask<SandboxOperationResult> StartAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) => Rejected();

        public ValueTask<SandboxOperationResult> StopAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) => Rejected();

        public ValueTask<SandboxOperationResult> RestartAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) => Rejected();

        public ValueTask<SandboxOperationResult> ResetAsync(
            WorldId worldId,
            string? replacementSource = null,
            CancellationToken cancellationToken = default) => Rejected();

        public ValueTask<SandboxOperationResult> DestroyAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) => Rejected();

        private static ValueTask<SandboxOperationResult> Rejected() => ValueTask.FromResult(
            new SandboxOperationResult(
                SandboxOperationStatus.Rejected,
                ErrorCode: "sandbox.control_unavailable",
                Message: "Sandbox control is unavailable in this Vega process."));
    }
}
