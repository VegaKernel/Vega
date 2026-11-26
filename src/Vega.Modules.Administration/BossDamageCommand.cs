using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Administration;

/// <summary>Reports actual recent native boss/invasion encounter credit, including disconnected participants.</summary>
public sealed class BossDamageCommand(IBossDamageHistory history) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("bossdamage", [], [], Syntax: "bossdamage [page]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        int page = 1;
        if (invocation.Arguments.Count > 1 || invocation.Arguments.Count == 1 &&
            (!int.TryParse(invocation.Arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
            return new(CommandExecutionStatus.InvalidUsage, "bossdamage.usage");
        var snapshot = await history.RecentAsync(invocation.WorldId, token).ConfigureAwait(false);
        if (snapshot is null || snapshot.WorldId != invocation.WorldId) return CommandExecutionResult.Failed("bossdamage.unavailable");
        var rows = snapshot.Attempts.SelectMany((attempt, index) => attempt.Contributions.Select(entry => (Index: index, Attempt: attempt, Entry: entry))).ToArray();
        if (rows.Length == 0)
            return CommandExecutionResult.WithMessages(MessageDescriptor.Warning("bossdamage.empty").WithDefaultText("No recent boss damage data found."));

        const int pageSize = 8;
        int pages = (rows.Length + pageSize - 1) / pageSize;
        if (page > pages) return new(CommandExecutionStatus.InvalidUsage, "bossdamage.page_unavailable");
        var messages = new List<MessageDescriptor> { MessageDescriptor.Info("bossdamage.page", page, pages)
            .WithDefaultText("Recent encounter damage (historical character names), page {0}/{1}:") };
        int lastAttempt = -1;
        foreach (var row in rows.Skip((page - 1) * pageSize).Take(pageSize))
        {
            if (row.Index != lastAttempt)
            {
                lastAttempt = row.Index;
                messages.Add(MessageDescriptor.Info("bossdamage.encounter", row.Attempt.Encounter, row.Attempt.DurationTicks)
                    .WithDefaultText("{0} ({1} ticks):"));
            }
            messages.Add(MessageDescriptor.Info("bossdamage.credit", row.Entry.Label, row.Entry.Damage, row.Entry.HitCount, row.Entry.Percentage)
                .WithDefaultText("{0}: {1} damage, {2} hits, {3}%."));
        }
        return CommandExecutionResult.WithMessages([.. messages]);
    }
}
