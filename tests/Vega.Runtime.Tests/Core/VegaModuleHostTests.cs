using NUnit.Framework;
using Vega.Contracts.Modules;
using Vega.Core.Modules;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class VegaModuleHostTests
{
    [Test]
    public void StartsInDependencyOrderAndStopsInReverseOrder()
    {
        var calls = new List<string>();
        var dependent = new RecordingModule("dependent", ["dependency"], calls);
        var dependency = new RecordingModule("dependency", [], calls);
        using (var host = new VegaModuleHost([dependent, dependency]))
        {
            host.Start();
        }

        Assert.That(calls, Is.EqualTo(new[]
        {
            "start:dependency", "start:dependent", "stop:dependent", "stop:dependency"
        }));
    }

    [Test]
    public void RejectsDuplicateNamesAndDependencyCycles()
    {
        var calls = new List<string>();
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => new VegaModuleHost([
                new RecordingModule("same", [], calls), new RecordingModule("same", [], calls)]));
            Assert.Throws<InvalidOperationException>(() => new VegaModuleHost([
                new RecordingModule("a", ["b"], calls), new RecordingModule("b", ["a"], calls)]));
        });
    }

    [Test]
    public void TransfersCompatibleRestartStateBeforeTheReplacementStarts()
    {
        var source = new StatefulModule(42);
        IReadOnlyDictionary<string, ModuleRestartState> state;
        using (var sourceHost = new VegaModuleHost([source]))
        {
            sourceHost.Start();
            state = sourceHost.CaptureRestartState();
        }

        var replacement = new StatefulModule(0);
        using (var replacementHost = new VegaModuleHost([replacement]))
        {
            replacementHost.RestoreRestartState(state);
            replacementHost.Start();
        }

        Assert.That(replacement.ValueAtStart, Is.EqualTo(42));
    }

    [Test]
    public void DisposesModulesThatWereConstructedButNeverStarted()
    {
        var calls = new List<string>();
        var module = new RecordingModule("unstarted", [], calls);

        new VegaModuleHost([module]).Dispose();

        Assert.That(calls, Is.EqualTo(new[] { "stop:unstarted" }));
    }

    private sealed class RecordingModule(
        string name,
        IReadOnlyList<string> dependencies,
        List<string> calls) : IVegaModule
    {
        public VegaModuleDescriptor Descriptor { get; } = new(name, "test", dependencies);
        public void Start() => calls.Add($"start:{name}");
        public void Dispose() => calls.Add($"stop:{name}");
    }

    private sealed class StatefulModule(int value) : IVegaModule, IModuleRestartStateParticipant
    {
        public VegaModuleDescriptor Descriptor { get; } = new("stateful", "test", []);
        public string RestartStateKey => "stateful";
        public int RestartStateVersion => 1;
        public int ValueAtStart { get; private set; }

        public void Start() => ValueAtStart = value;
        public ModuleRestartState CaptureRestartState() =>
            new(RestartStateKey, RestartStateVersion, BitConverter.GetBytes(value));
        public void RestoreRestartState(ModuleRestartState state) =>
            value = BitConverter.ToInt32(state.Payload.Span);
        public void Dispose() { }
    }
}
