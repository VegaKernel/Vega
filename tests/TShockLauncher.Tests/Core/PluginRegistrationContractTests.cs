using NUnit.Framework;
using TShockAPI;

namespace TShockLauncher.Tests.Core;

[TestFixture]
public sealed class PluginRegistrationContractTests
{
    [Test]
    public void LegacyCommandRegistryRegistersFindsAndUnregistersCommands()
    {
        string alias = $"contract-{Guid.NewGuid():N}";
        var command = new Command(_ => { }, alias);
        ILegacyCommandRegistry registry = Commands.Registry;

        registry.Register(command);
        try
        {
            Assert.That(registry.FindFirst(alias), Is.SameAs(command));
            Assert.That(registry.FindAll(alias), Is.EquivalentTo(new[] { command }));
        }
        finally
        {
            registry.Unregister(command);
        }

        Assert.That(registry.FindFirst(alias), Is.Null);
    }

    [Test]
    public void HookRegistryRegistersInvokesAndUnregistersHandlers()
    {
        var handlers = new HandlerList<EventArgs>();
        IHookRegistry<EventArgs> registry = handlers;
        int invocationCount = 0;
        EventHandler<EventArgs> handler = (_, _) => invocationCount++;

        registry.Register(handler);
        handlers.Invoke(this, EventArgs.Empty);
        registry.Unregister(handler);
        handlers.Invoke(this, EventArgs.Empty);

        Assert.That(invocationCount, Is.EqualTo(1));
    }
}
