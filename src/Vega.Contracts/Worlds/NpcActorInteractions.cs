using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

/// <summary>Explicit server-side item currency or quest item quantity; never a vanilla coin price.</summary>
public readonly record struct NpcItemAmount(short ItemType, int Amount, byte Prefix = 0)
{
    public bool IsValid => ItemType > 0 && Amount is > 0 and <= 1_000_000;
}

public sealed record NpcDialogueChoice(string Id, string Text, string? NextNode = null);
public sealed record NpcDialogueNode(string Id, string Text, IReadOnlyList<NpcDialogueChoice> Choices);

/// <summary>Detached bounded dialogue graph, presented through the plugin's stock-client chat/command UI.</summary>
public sealed class NpcDialogueDefinition
{
    public NpcDialogueDefinition(string initialNode, IEnumerable<NpcDialogueNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        NpcDialogueNode[] source = nodes.Take(65).ToArray();
        if (source.Length is < 1 or > 64 || !ValidId(initialNode)) throw new ArgumentException("Invalid dialogue graph.", nameof(nodes));
        var copy = new Dictionary<string, NpcDialogueNode>(StringComparer.Ordinal);
        foreach (var node in source)
        {
            if (node is null || !ValidId(node.Id) || !ValidText(node.Text, 512) || node.Choices is null)
                throw new ArgumentException("Invalid dialogue node.", nameof(nodes));
            NpcDialogueChoice[] choices = node.Choices.Take(11).ToArray();
            if (choices.Length > 10 || choices.Any(choice => choice is null || !ValidId(choice.Id) || !ValidText(choice.Text, 160) ||
                    choice.NextNode is not null && !ValidId(choice.NextNode)) ||
                choices.Select(choice => choice.Id).Distinct(StringComparer.Ordinal).Count() != choices.Length ||
                !copy.TryAdd(node.Id, node with { Choices = Array.AsReadOnly(choices) }))
                throw new ArgumentException("Invalid or duplicate dialogue choice/node.", nameof(nodes));
        }
        if (!copy.ContainsKey(initialNode) || copy.Values.SelectMany(node => node.Choices).Any(choice =>
                choice.NextNode is { } next && !copy.ContainsKey(next)))
            throw new ArgumentException("Dialogue references a missing node.", nameof(nodes));
        InitialNode = initialNode;
        Nodes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, NpcDialogueNode>(copy);
    }
    public string InitialNode { get; }
    public IReadOnlyDictionary<string, NpcDialogueNode> Nodes { get; }
    internal static bool ValidId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    internal static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}

public enum NpcDialogueStatus { Presented, Closed, Unavailable, ActorGone, NotInteracting, NoDialogue, StaleView, InvalidChoice }
public sealed record NpcDialogueView(long Revision, string NodeId, string Text, IReadOnlyList<NpcDialogueChoice> Choices);
public sealed record NpcDialogueResult(NpcDialogueStatus Status, NpcDialogueView? View = null);
public readonly record struct NpcActorDialogueChoiceEvent(NpcActorId Actor, PlayerSnapshot Player, string NodeId, string ChoiceId, string? NextNode);

/// <summary>One server-authoritative item hand-in quest for a session. Plugins may persist completion through events.</summary>
public sealed record NpcQuestDefinition(string Id, string Title, string Description, NpcItemAmount Required, NpcItemAmount Reward, bool Repeatable = false);

public sealed class NpcQuestCatalog
{
    public NpcQuestCatalog(IEnumerable<NpcQuestDefinition> quests)
    {
        ArgumentNullException.ThrowIfNull(quests);
        NpcQuestDefinition[] copy = quests.Take(65).ToArray();
        if (copy.Length > 64 || copy.Any(quest => quest is null || !NpcDialogueDefinition.ValidId(quest.Id) ||
                !NpcDialogueDefinition.ValidText(quest.Title, 160) || !NpcDialogueDefinition.ValidText(quest.Description, 512) ||
                !quest.Required.IsValid || !quest.Reward.IsValid || quest.Reward.Amount > short.MaxValue) ||
            copy.Select(quest => quest.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Invalid or duplicate quest definition.", nameof(quests));
        Quests = Array.AsReadOnly(copy);
    }
    public IReadOnlyList<NpcQuestDefinition> Quests { get; }
}

public enum NpcQuestState { Available, Accepted, Completed }
public enum NpcQuestStatus { Applied, Unavailable, ActorGone, NotInteracting, CatalogChanged, UnknownQuest, AlreadyAccepted,
    AlreadyCompleted, NotAccepted, InventoryRejected }
public sealed record NpcQuestView(NpcQuestDefinition Definition, NpcQuestState State);
public sealed record NpcQuestResult(NpcQuestStatus Status, long CatalogRevision = 0, NpcQuestView? Quest = null,
    NpcShopPurchaseStatus? InventoryFailure = null);
public readonly record struct NpcActorQuestEvent(NpcActorId Actor, PlayerSnapshot Player, string QuestId, NpcQuestState State);
/// <summary>Optional bounded actor behavior notification, emitted once per 60 native AI visits.</summary>
public readonly record struct NpcActorThinkEvent(NpcActorId Actor, NpcEventSnapshot Npc, NpcActorIntentKind Intent);
