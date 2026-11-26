using System.Text;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Players;

public sealed class PlayingCommand(IPlayerDirectory players) : ICommandHandler
{
    private const string SeePlayerIdsPermission = "vega.admin.seeplayerids";
    private const int MaxLineLength = 80;
    private const int LinesPerPage = 4;

    public CommandDescriptor Descriptor { get; } = new(
        "playing",
        ["online", "who"],
        Array.Empty<string>(),
        Syntax: "playing [-i] [page]");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!TryParseArguments(invocation.Arguments, out bool displayIds, out int pageNumber))
        {
            return ValueTask.FromResult(InvalidUsage());
        }

        if (displayIds && !invocation.Principal.HasPermission(SeePlayerIdsPermission))
        {
            return ValueTask.FromResult(new CommandExecutionResult(
                CommandExecutionStatus.Forbidden,
                "command.playing.ids-forbidden",
                [MessageDescriptor.Error("command.playing.ids-forbidden")
                    .WithDefaultText("You do not have permission to see player IDs.")]));
        }

        IReadOnlyList<PlayerSnapshot> onlinePlayers = players.SnapshotOnlinePlayers().Where(player => player.WorldId == invocation.WorldId).ToArray();
        if (onlinePlayers.Count == 0)
        {
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.playing.none")
                    .WithDefaultText("There are currently no players online.")));
        }

        IReadOnlyList<string> lines = BuildLines(onlinePlayers.Select(player =>
            displayIds ? $"{player.Name} (Index: {player.Id.Slot})" : player.Name));
        int pageCount = (lines.Count - 1) / LinesPerPage + 1;
        pageNumber = Math.Min(pageNumber, pageCount);

        var messages = new List<MessageDescriptor>
        {
            MessageDescriptor.Info("command.playing.header", onlinePlayers.Count)
                .WithDefaultText("Online players ({0}):")
        };

        messages.AddRange(lines
            .Skip((pageNumber - 1) * LinesPerPage)
            .Take(LinesPerPage)
            .Select(static line => MessageDescriptor.Info("command.playing.players", line)
                .WithDefaultText("{0}")));

        if (pageNumber < pageCount)
        {
            messages.Add(MessageDescriptor.Info(
                    "command.playing.more",
                    displayIds ? "-i " : string.Empty,
                    pageNumber + 1)
                .WithDefaultText("Type /who {0}{1} for more."));
        }

        return ValueTask.FromResult(CommandExecutionResult.WithMessages(messages.ToArray()));
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> arguments,
        out bool displayIds,
        out int pageNumber)
    {
        displayIds = false;
        pageNumber = 1;
        bool pageSpecified = false;

        if (arguments.Count > 2)
        {
            return false;
        }

        foreach (string argument in arguments)
        {
            if (argument.Equals("-i", StringComparison.OrdinalIgnoreCase))
            {
                if (displayIds)
                {
                    return false;
                }

                displayIds = true;
            }
            else if (pageSpecified || !int.TryParse(argument, out pageNumber) || pageNumber < 1)
            {
                return false;
            }
            else
            {
                pageSpecified = true;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> BuildLines(IEnumerable<string> terms)
    {
        var lines = new List<string>();
        var line = new StringBuilder();

        foreach (string term in terms)
        {
            if (line.Length == 0)
            {
                line.Append(term);
            }
            else if (line.Length + 2 + term.Length <= MaxLineLength)
            {
                line.Append(", ").Append(term);
            }
            else
            {
                lines.Add(line.ToString());
                line.Clear().Append(term);
            }
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }

    private static CommandExecutionResult InvalidUsage() =>
        new(
            CommandExecutionStatus.InvalidUsage,
            "command.playing.usage",
            [MessageDescriptor.Error("command.playing.usage")
                .WithDefaultText("Usage: playing [-i] [page]. Aliases: online, who.")]);
}
