using System.Net;
using System.Text;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Updates;
using Vega.Contracts.Worlds;
using Vega.Core.Commands.System;
using Vega.Core.Updates;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class CheckUpdatesCommandTests
{
    [Test]
    public async Task CommandReportsPublishedReleaseStatesAndRejectsArguments()
    {
        var checker = new RecordingUpdateChecker(new UpdateCheckResult("1.0.0", null, false));
        var dispatcher = new Vega.Core.Commands.CommandDispatcher([new CheckUpdatesCommand(checker)]);
        var principal = new Principal("operator", ["vega.cfg.maintenance"]);

        CommandExecutionResult invalid = await dispatcher.DispatchAsync(
            Invocation("checkupdates", ["unexpected"], principal));
        CommandExecutionResult noRelease = await dispatcher.DispatchAsync(
            Invocation("checkupdates", [], principal));

        checker.Result = new UpdateCheckResult("1.0.0", "v1.1.0", true);
        CommandExecutionResult updateAvailable = await dispatcher.DispatchAsync(
            Invocation("checkupdates", [], principal));

        checker.Result = new UpdateCheckResult("1.1.0", "v1.1.0", false);
        CommandExecutionResult current = await dispatcher.DispatchAsync(
            Invocation("checkupdates", [], principal));

        Assert.Multiple(() =>
        {
            Assert.That(invalid.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(checker.CheckCount, Is.EqualTo(3));
            Assert.That(noRelease.Messages.Single().Key, Is.EqualTo("command.checkupdates.no-published-release"));
            Assert.That(updateAvailable.Messages.Single().Key, Is.EqualTo("command.checkupdates.update-available"));
            Assert.That(updateAvailable.Messages.Single().Severity, Is.EqualTo(MessageSeverity.Warning));
            Assert.That(current.Messages.Single().Key, Is.EqualTo("command.checkupdates.current"));
            Assert.That(current.Messages.Single().Severity, Is.EqualTo(MessageSeverity.Success));
        });
    }

    [Test]
    public async Task CommandMapsCheckerFailureWithoutLeakingExceptionDetails()
    {
        var dispatcher = new Vega.Core.Commands.CommandDispatcher([
            new CheckUpdatesCommand(new ThrowingUpdateChecker())
        ]);
        var principal = new Principal("operator", ["vega.cfg.maintenance"]);

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation("checkupdates", [], principal));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("update.check_failed"));
            Assert.That(result.Messages.Single().Key, Is.EqualTo("command.checkupdates.failed"));
        });
    }

    [Test]
    public async Task GitHubCheckerTreatsMissingReleaseAsNormalState()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var checker = new GitHubReleaseUpdateChecker(client, "1.2.3");

        UpdateCheckResult result = await checker.CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.CurrentVersion, Is.EqualTo("1.2.3"));
            Assert.That(result.LatestVersion, Is.Null);
            Assert.That(result.IsUpdateAvailable, Is.False);
        });
    }

    [TestCase("v1.2.4", true)]
    [TestCase("1.2.3", false)]
    [TestCase("v1.2.3.0", false)]
    [TestCase("v1.2.2", false)]
    public async Task GitHubCheckerComparesReleaseTagToRunningVersion(
        string latestTag,
        bool expectedUpdate)
    {
        using var client = new HttpClient(new StubHttpMessageHandler(
            _ => JsonResponse($"{{\"tag_name\":\"{latestTag}\"}}")));
        using var checker = new GitHubReleaseUpdateChecker(client, "1.2.3.0");

        UpdateCheckResult result = await checker.CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.LatestVersion, Is.EqualTo(latestTag));
            Assert.That(result.IsUpdateAvailable, Is.EqualTo(expectedUpdate));
        });
    }

    [Test]
    public void GitHubCheckerRejectsNonVersionReleaseTags()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(
            _ => JsonResponse("{\"tag_name\":\"nightly\"}")));
        using var checker = new GitHubReleaseUpdateChecker(client, "1.2.3");

        Assert.ThrowsAsync<InvalidDataException>(async () => await checker.CheckAsync());
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        ICommandPrincipal principal) =>
        new(name, arguments, principal, NullOutput.Instance) { WorldId = WorldId.Primary };

    private sealed class RecordingUpdateChecker(UpdateCheckResult result) : IUpdateChecker
    {
        public UpdateCheckResult Result { get; set; } = result;
        public int CheckCount { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class ThrowingUpdateChecker : IUpdateChecker
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<UpdateCheckResult>(new HttpRequestException("secret upstream detail"));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class Principal(string name, IReadOnlyCollection<string> permissions) : ICommandPrincipal
    {
        public string Name { get; } = name;
        public bool IsServer => false;
        public bool HasPermission(string permission) => permissions.Contains(permission);
    }

    private sealed class NullOutput : ICommandOutput
    {
        public static NullOutput Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
