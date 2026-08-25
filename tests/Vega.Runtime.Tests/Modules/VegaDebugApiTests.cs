using System.Text.Json;
using NUnit.Framework;
using Vega.Core.Modules;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
public sealed class VegaDebugApiTests
{
    [Test]
    public void Debug_context_is_only_granted_to_named_plugins()
    {
        var registry = new VegaDebugRegistry(Options(enabled: true));
        registry.Register("probe", new Probe());

        Assert.That(registry.CreateContext("VegaToolsMCP").Capabilities, Is.Not.EqualTo(VegaDebugCapabilities.None));
        Assert.That(registry.CreateContext("Other.Plugin").Capabilities, Is.EqualTo(VegaDebugCapabilities.None));
    }

    [Test]
    public void Safe_context_inspects_public_members_only()
    {
        var registry = new VegaDebugRegistry(Options(enabled: true));
        registry.Register("probe", new Probe());
        IVegaDebugContext debug = registry.CreateContext("VegaToolsMCP");

        VegaDebugObjectSnapshot snapshot = debug.Inspect("probe");

        Assert.That(snapshot.Members.Any(member => member.Name == nameof(Probe.PublicValue) && member.Value == "5"), Is.True);
        Assert.That(snapshot.Members.Any(member => member.Name == "secret"), Is.False);
        Assert.That(debug.SnapshotRoots().Any(root => root.Name == "probe"), Is.True);
    }

    [Test]
    public async Task Unsafe_context_can_invoke_and_mutate_non_public_state()
    {
        var registry = new VegaDebugRegistry(Options(
            enabled: true,
            allowNonPublicMembers: true,
            allowMethodInvoke: true,
            allowStateMutation: true));
        var probe = new Probe();
        registry.Register("probe", probe);
        IVegaDebugContext debug = registry.CreateContext("VegaToolsMCP");

        VegaDebugInvocationResult invocation = await debug.InvokeAsync(new VegaDebugInvocationRequest(
            "probe",
            string.Empty,
            "Increment",
            [JsonSerializer.SerializeToElement(41)]));
        VegaDebugMutationResult mutation = debug.SetValue(new VegaDebugMutationRequest(
            "probe",
            "secret",
            JsonSerializer.SerializeToElement(9)));

        Assert.That(invocation.Succeeded, Is.True, invocation.Error);
        Assert.That(invocation.Value, Is.EqualTo("42"));
        Assert.That(mutation.Succeeded, Is.True, mutation.Error);
        Assert.That(probe.ReadSecret(), Is.EqualTo(9));
    }

    [Test]
    public void Runtime_snapshot_reports_current_process()
    {
        var registry = new VegaDebugRegistry(Options(enabled: true));
        IVegaDebugContext debug = registry.CreateContext("VegaToolsMCP");

        VegaDebugRuntimeSnapshot snapshot = debug.CaptureRuntime();

        Assert.That(snapshot.ProcessId, Is.EqualTo(Environment.ProcessId));
        Assert.That(snapshot.ManagedHeapBytes, Is.GreaterThan(0));
        Assert.That(snapshot.CapturedAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
    }

    private static VegaDebugOptions Options(
        bool enabled,
        bool allowNonPublicMembers = false,
        bool allowMethodInvoke = false,
        bool allowStateMutation = false) => new(
        enabled,
        ["VegaToolsMCP"],
        allowNonPublicMembers,
        allowMethodInvoke,
        allowStateMutation,
        false,
        256,
        4096);

    private sealed class Probe
    {
        private int secret = 3;

        public int PublicValue = 5;

        private int Increment(int value) => value + 1;

        public int ReadSecret() => secret;
    }
}
