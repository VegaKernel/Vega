using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class ServerTextFileCommandTests
{
    [Test]
    public async Task ReadsConfiguredFileAsCommandMessages()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vega-command-text-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(path, ["first line", "second line"]);
            var command = new ServerTextFileCommand("motd", path, "command.motd");

            CommandExecutionResult result = await command.ExecuteAsync(
                new CommandInvocation("motd", [], new ServerPrincipal(), new NullOutput()),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
                Assert.That(result.Messages, Has.Count.EqualTo(2));
                Assert.That(command.Descriptor.Permissions, Is.Empty);
                Assert.That(command.Descriptor.AllowServer, Is.True);
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MissingFileIsAStableInformationalResult()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vega-command-text-missing-{Guid.NewGuid():N}.txt");
        var command = new ServerTextFileCommand("rules", path, "command.rules");

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation("rules", [], new ServerPrincipal(), new NullOutput()),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages, Has.Count.EqualTo(1));
        });
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public string Name => "server";
        public bool IsServer => true;
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
