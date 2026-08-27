using NUnit.Framework;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Vega.Admin.Client;
using Vega.Admin.Contracts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Runtime.Administration;
using Vega.TerminalUI;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class AdminClientBoundaryTests
{
    [Test]
    public async Task LocalClientPreservesStructuredCommandMessages()
    {
        var dispatcher = new RecordingDispatcher();
        var client = new LocalVegaAdminClient(
            new UnusedSnapshotProvider(),
            dispatcher,
            dispatcher);

        AdminCommandResult result = await client.ExecuteCommandAsync(
            new AdminCommandRequest("probe", ["alpha"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AdminCommandExecutionStatus.Success));
            Assert.That(dispatcher.LastInvocation?.Name, Is.EqualTo("probe"));
            Assert.That(dispatcher.LastInvocation?.Arguments, Is.EqualTo(new[] { "alpha" }));
            Assert.That(result.Messages, Has.Count.EqualTo(1));
            Assert.That(result.Messages[0].Key, Is.EqualTo("command.probe.ok"));
            Assert.That(result.Messages[0].FallbackText, Is.EqualTo("Probe completed for {0}."));
            Assert.That(result.Messages[0].Arguments, Is.EqualTo(new[]
            {
                new AdminMessageArgument(AdminScalarKind.String, "alpha")
            }));
            Assert.That(result.Messages[0].Severity, Is.EqualTo(AdminMessageSeverity.Success));
        });
    }

    [Test]
    public async Task EmbeddedCommandAdapterExecutesThroughTypedAdminClient()
    {
        var admin = new RecordingAdminClient
        {
            CommandResult = new AdminCommandResult(
                AdminCommandExecutionStatus.Forbidden,
                "admin.permission_denied",
                [new AdminMessageDescriptor(
                    "admin.permission_denied",
                    Array.Empty<AdminMessageArgument>(),
                    AdminMessageSeverity.Error,
                    "Permission denied.")])
        };
        var adapter = new AdminOperationsCommandServiceAdapter(admin);

        OperationsActionResult result = await adapter.ExecuteAsync(".shutdown now");

        Assert.Multiple(() =>
        {
            Assert.That(admin.LastRequest?.Name, Is.EqualTo("shutdown"));
            Assert.That(admin.LastRequest?.Arguments, Is.EqualTo(new[] { "now" }));
            Assert.That(admin.LastRequest?.Silent, Is.True);
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(result.ErrorCode, Is.EqualTo("admin.permission_denied"));
            Assert.That(result.Messages, Is.EqualTo(new[] { "Permission denied." }));
        });
    }

    [Test]
    public void EmbeddedCatalogAdapterUsesTransportNeutralDescriptors()
    {
        var admin = new RecordingAdminClient
        {
            Catalog =
            [
                new AdminCommandDescriptor(
                    "worldinfo",
                    ["world"],
                    "worldinfo",
                    ["server.view"],
                    AllowServer: true,
                    Owner: "world")
            ]
        };
        var adapter = new AdminCommandCatalogAdapter(admin);

        CommandDescriptor descriptor = adapter.SnapshotCommands().Single();

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Name, Is.EqualTo("worldinfo"));
            Assert.That(descriptor.Aliases, Is.EqualTo(new[] { "world" }));
            Assert.That(descriptor.Syntax, Is.EqualTo("worldinfo"));
            Assert.That(descriptor.Permissions, Is.EqualTo(new[] { "server.view" }));
        });
    }

    [Test]
    public async Task InitialProtocolAdvertisesOnlyImplementedVerticalSlice()
    {
        var admin = new RecordingAdminClient();

        AdminProtocolInfo protocol = await admin.GetProtocolInfoAsync();

        Assert.That(
            protocol.Capabilities.Select(static capability => capability.Name),
            Is.EqualTo(new[] { "server.status", "commands.catalog", "commands.execute" }));
    }

    [Test]
    public void TabDigitChordSelectsTerminalGuiV2WorkspaceWithoutConsumingTab()
    {
        var admin = new RecordingAdminClient();
        var workspaces = new AdminWorkspaceTabs(new View(), admin);
        var tab = new Key(Key.Tab);
        var digit = new Key('3');

        bool tabHandled = workspaces.HandleWorkspaceChord(tab);
        bool digitHandled = workspaces.HandleWorkspaceChord(digit);

        Assert.Multiple(() =>
        {
            Assert.That(tabHandled, Is.False, "Tab must continue normal Terminal.Gui focus traversal.");
            Assert.That(tab.Handled, Is.False);
            Assert.That(digitHandled, Is.True);
            Assert.That(digit.Handled, Is.True);
            Assert.That(workspaces.SelectedWorkspaceIndex, Is.EqualTo(2));
            Assert.That(workspaces.SelectedWorkspaceTitle, Does.Contain("Commands"));
        });
    }

    private sealed class RecordingDispatcher : ICommandDispatcher, ICommandCatalog
    {
        public CommandInvocation? LastInvocation { get; private set; }

        public bool CanDispatch(string name) => true;

        public IReadOnlyList<CommandDescriptor> SnapshotCommands() =>
            [new CommandDescriptor("probe", [], ["server.command"], Syntax: "probe <value>")];

        public ValueTask<CommandExecutionResult> DispatchAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            LastInvocation = invocation;
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.probe.ok", invocation.Arguments.Single())
                    .WithDefaultText("Probe completed for {0}.")));
        }
    }

    private sealed class RecordingAdminClient : IVegaAdminClient
    {
        public AdminCommandRequest? LastRequest { get; private set; }

        public AdminCommandResult CommandResult { get; init; } = new(
            AdminCommandExecutionStatus.Success,
            null,
            Array.Empty<AdminMessageDescriptor>());

        public IReadOnlyList<AdminCommandDescriptor> Catalog { get; init; } =
            Array.Empty<AdminCommandDescriptor>();

        public ValueTask<AdminProtocolInfo> GetProtocolInfoAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AdminProtocolInfo.Phase27Initial);

        public ValueTask<AdminServerStatusSnapshot> GetServerStatusAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AdminCommandDescriptor>> GetCommandCatalogAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Catalog);

        public ValueTask<AdminCommandResult> ExecuteCommandAsync(
            AdminCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(CommandResult);
        }
    }

    private sealed class UnusedSnapshotProvider : IOperationsSnapshotProvider
    {
        public OperationsSnapshot Capture(
            OperationsLogFilter? logFilter = null,
            OperationsSnapshotExposure exposure = OperationsSnapshotExposure.Redacted) =>
            throw new NotSupportedException();
    }
}
