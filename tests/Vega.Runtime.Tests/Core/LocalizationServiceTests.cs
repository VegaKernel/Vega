using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Localization;
using Vega.Contracts.Messaging;
using Vega.Core.Commands.Players;
using Vega.Core.Localization;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class LocalizationServiceTests
{
    [Test]
    public void UsesRecipientCultureNamespaceAndDeterministicFallback()
    {
        var service = new LocalizationService("en-US", ["en-US", "ru-RU"]);
        using IDisposable catalog = service.RegisterCatalog(
            "module.example",
            "ru-RU",
            new Dictionary<string, string> { ["greeting"] = "Привет, {0}!" });
        var message = MessageDescriptor.Info("greeting", "Vega")
            .WithSource("module.example")
            .WithDefaultText("Hello, {0}!");

        Assert.Multiple(() =>
        {
            Assert.That(service.Format(message, new PlayerCulture("ru-RU")), Is.EqualTo("Привет, Vega!"));
            Assert.That(service.Format(message, new PlayerCulture("en-US")), Is.EqualTo("Hello, Vega!"));
        });
    }

    [Test]
    public async Task LanguageCommandChangesAndResetsOnlyTheCallingSession()
    {
        var service = new LocalizationService("en-US", ["en-US", "ru-RU"]);
        var command = new LanguageCommand(service);
        var principal = new CulturePrincipal("player:7");

        CommandExecutionResult changed = await command.ExecuteAsync(
            Invocation(principal, ["ru-RU"]),
            CancellationToken.None);
        PlayerCulture selected = service.Resolve("player:7");
        await command.ExecuteAsync(Invocation(principal, ["reset"]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(changed.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(selected.Name, Is.EqualTo("ru-RU"));
            Assert.That(service.Resolve("player:7"), Is.EqualTo(service.ServerCulture));
            Assert.That(service.Resolve("player:8"), Is.EqualTo(service.ServerCulture));
        });
    }

    [Test]
    public void NewerModuleCatalogRegistrationCannotBeRemovedByStaleUnload()
    {
        var service = new LocalizationService("en-US");
        IDisposable old = service.RegisterCatalog(
            "module.reload",
            "en-US",
            new Dictionary<string, string> { ["value"] = "old" });
        using IDisposable current = service.RegisterCatalog(
            "module.reload",
            "en-US",
            new Dictionary<string, string> { ["value"] = "new" });

        old.Dispose();

        Assert.That(
            service.Format(MessageDescriptor.Info("value").WithSource("module.reload"), service.ServerCulture),
            Is.EqualTo("new"));
    }

    private static CommandInvocation Invocation(
        ICommandPrincipal principal,
        IReadOnlyList<string> arguments) =>
        new("lang", arguments, principal, new NullOutput());

    private sealed class CulturePrincipal(string subject) : ICommandCulturePrincipal
    {
        public string Name => "Player";
        public bool IsServer => false;
        public string CultureSubjectId => subject;
        public bool HasPermission(string permission) => true;
    }

    private sealed class NullOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
