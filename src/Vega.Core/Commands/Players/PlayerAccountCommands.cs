using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Core.Authorization;

namespace Vega.Core.Commands.Players;

public sealed class RegisterAccountCommand(PlayerAccountSessionService sessions) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "register", [], [], AllowServer: false,
        Syntax: "register <password> | register <account> <password>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Principal is not IPlayerCommandPrincipal player ||
            invocation.Arguments.Count is < 1 or > 2)
        {
            return PlayerAccountCommandResult.Usage("register <password> | register <account> <password>");
        }

        string accountName = invocation.Arguments.Count == 2 ? invocation.Arguments[0] : player.PlayerName;
        string password = invocation.Arguments[^1];
        PlayerAccountSessionResult result = await sessions.RegisterAsync(
            player.PlayerId, accountName, password, cancellationToken, player.SessionId).ConfigureAwait(false);
        return PlayerAccountCommandResult.Authentication(result, "Account {0} registered and authenticated.");
    }
}

public sealed class LoginAccountCommand(PlayerAccountSessionService sessions) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "login", ["auth"], [], AllowServer: false,
        Syntax: "login <password> | login <account> <password>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Principal is not IPlayerCommandPrincipal player ||
            invocation.Arguments.Count is < 1 or > 2)
        {
            return PlayerAccountCommandResult.Usage("login <password> | login <account> <password>");
        }

        string accountName = invocation.Arguments.Count == 2 ? invocation.Arguments[0] : player.PlayerName;
        string password = invocation.Arguments[^1];
        PlayerAccountSessionResult result = await sessions.LoginAsync(
            player.PlayerId, accountName, password, cancellationToken, player.SessionId).ConfigureAwait(false);
        return PlayerAccountCommandResult.Authentication(result, "Authenticated as {0}.");
    }
}

public sealed class LogoutAccountCommand(PlayerAccountSessionService sessions) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "logout", [], [], AllowServer: false,
        Syntax: "logout");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Principal is not IPlayerCommandPrincipal player || invocation.Arguments.Count != 0)
        {
            return ValueTask.FromResult(PlayerAccountCommandResult.Usage("logout"));
        }

        PlayerAccountSessionResult result = sessions.Logout(player.PlayerId, player.SessionId);
        return ValueTask.FromResult(result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.logout.success")
                    .WithDefaultText("Logged out."))
            : PlayerAccountCommandResult.Failure(result.Code));
    }
}

public sealed class PasswordAccountCommand(PlayerAccountSessionService sessions) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "password", ["passwd"], [], AllowServer: false,
        Syntax: "password <current> <new>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Principal is not IPlayerCommandPrincipal player || invocation.Arguments.Count != 2)
        {
            return PlayerAccountCommandResult.Usage("password <current> <new>");
        }

        PlayerAccountSessionResult result = await sessions.ChangePasswordAsync(
            player.PlayerId,
            invocation.Arguments[0],
            invocation.Arguments[1],
            cancellationToken,
            player.SessionId).ConfigureAwait(false);
        return result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.password.success")
                    .WithDefaultText("Password changed."))
            : PlayerAccountCommandResult.Failure(result.Code);
    }
}

internal static class PlayerAccountCommandResult
{
    public static CommandExecutionResult Authentication(PlayerAccountSessionResult result, string text) =>
        result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.account.authenticated", result.Account!.Name)
                    .WithDefaultText(text))
            : Failure(result.Code);

    public static CommandExecutionResult Failure(string code) =>
        CommandExecutionResult.Failed(
            code,
            MessageDescriptor.Error(code, code).WithDefaultText(code switch
            {
                "account.already-authenticated" => "You are already authenticated. Use /logout first.",
                "account.not-authenticated" => "You are not authenticated.",
                "account.invalid-credentials" => "Invalid account name or password.",
                "account.exists" => "That account already exists.",
                "account.banned" => "This account is banned.",
                "account.in-use" => "This account is already in use on the server.",
                "account.invalid-password" => "Password length must be between 8 and 1024 characters.",
                "account.invalid-name" => "Account name is invalid.",
                _ => "Account operation failed: {0}."
            }));

    public static CommandExecutionResult Usage(string usage) => new(
        CommandExecutionStatus.InvalidUsage,
        "command.account-session.usage",
        [MessageDescriptor.Error("command.account-session.usage", usage)
            .WithDefaultText("Usage: /{0}.")]);
}
