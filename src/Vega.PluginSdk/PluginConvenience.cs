using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>Small adapters over the existing guarded SDK. No second command dispatcher or lifecycle.</summary>
public static class PluginCommandExtensions
{
    public static void Register(this IPluginCommands commands, CommandDescriptor descriptor,
        Func<CommandInvocation, CancellationToken, ValueTask<CommandExecutionResult>> handler) =>
        commands.Register(new DelegateCommand(descriptor, handler));

    public static void Register(this IPluginCommands commands, CommandDescriptor descriptor,
        Func<CommandInvocation, CommandExecutionResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        commands.Register(descriptor, (invocation, _) => ValueTask.FromResult(handler(invocation)));
    }

    public static void Register(this IPluginCommands commands, string name, string permission,
        Func<CommandInvocation, CommandExecutionResult> handler, string[]? aliases = null, bool allowServer = true, string? syntax = null) =>
        commands.Register(Describe(name, permission, aliases, allowServer, syntax), handler);

    public static void Register(this IPluginCommands commands, string name, string permission,
        Func<CommandInvocation, CancellationToken, ValueTask<CommandExecutionResult>> handler,
        string[]? aliases = null, bool allowServer = true, string? syntax = null) =>
        commands.Register(Describe(name, permission, aliases, allowServer, syntax), handler);

    private static CommandDescriptor Describe(string name, string permission, string[]? aliases, bool allowServer, string? syntax) =>
        new(name, Array.AsReadOnly(aliases?.ToArray() ?? []), string.IsNullOrEmpty(permission) ? [] : [permission], allowServer, syntax ?? name);

    private sealed class DelegateCommand(CommandDescriptor descriptor,
        Func<CommandInvocation, CancellationToken, ValueTask<CommandExecutionResult>> handler) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        private readonly Func<CommandInvocation, CancellationToken, ValueTask<CommandExecutionResult>> execute = handler ?? throw new ArgumentNullException(nameof(handler));
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken) => execute(invocation, cancellationToken);
    }
}

public static class PluginReply
{
    public static CommandExecutionResult Info(string text) => CommandExecutionResult.WithMessages(MessageDescriptor.Info("plugin.reply.info", text).WithDefaultText("{0}"));
    public static CommandExecutionResult Success(string text) => CommandExecutionResult.WithMessages(MessageDescriptor.Success("plugin.reply.success", text).WithDefaultText("{0}"));
    public static CommandExecutionResult Error(string code, string text) => CommandExecutionResult.Failed(code, MessageDescriptor.Error(code, text).WithDefaultText("{0}"));
    public static CommandExecutionResult Usage(string syntax) => new(CommandExecutionStatus.InvalidUsage, "plugin.reply.usage",
        [MessageDescriptor.Error("plugin.reply.usage", syntax).WithDefaultText("Usage: {0}")]);
}

public static class PluginPlayerExtensions
{
    /// <summary>Resolve the original command's player, never a different connection reusing its slot.</summary>
    public static bool TryGetCommandPlayer(this IPluginPlayers players, CommandInvocation invocation, [NotNullWhen(true)] out PlayerSnapshot? player)
    {
        player = null;
        if (invocation.Principal is not IPlayerCommandPrincipal { SessionId: { } session } principal ||
            !players.TryGet(principal.PlayerId, out var current) || current is null || current.SessionId != session || current.WorldId != invocation.WorldId)
            return false;
        player = current;
        return true;
    }
}

public static class PluginLogExtensions
{
    public static void Info(this IPluginLog log, string text) => log.Write(OperationsLogLevel.Information, text);
    public static void Warn(this IPluginLog log, string text) => log.Write(OperationsLogLevel.Warning, text);
    public static void Error(this IPluginLog log, string text, Exception? exception = null) => log.Write(OperationsLogLevel.Error, text, exception);
}

public static class PluginDocumentConvenience
{
    public static ValueTask<bool> TrySetAsync<T>(this IPluginData data, string collection, string key, long expectedRevision,
        T value, JsonTypeInfo<T> type, CancellationToken token = default) =>
        data.CommitAsync([PluginDataExtensions.Set(collection, key, expectedRevision, value, type)], token);

    public static ValueTask<bool> TryDeleteAsync(this IPluginData data, string collection, string key, long expectedRevision,
        CancellationToken token = default) => data.CommitAsync([PluginDataExtensions.Delete(collection, key, expectedRevision)], token);
}
