using NUnit.Framework;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class RuntimeResourceScopeTests
{
    [Test]
    public void UnwindsInReverseOrderIncludingAfterDisposalFailure()
    {
        var calls = new List<int>();
        var scope = new RuntimeResourceScope();
        scope.Own(new Resource(() => calls.Add(1)));
        scope.Own<IDisposable?>(null);
        scope.Own(new Resource(() => { calls.Add(2); throw new InvalidOperationException("middle"); }));
        scope.Own(new Resource(() => calls.Add(3)));

        Assert.That(() => scope.Dispose(), Throws.InvalidOperationException.With.Message.EqualTo("middle"));
        scope.Dispose();
        Assert.That(calls, Is.EqualTo(new[] { 3, 2, 1 }));
    }

    private sealed class Resource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
