using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

/// <summary>Game-thread actor-local dialogue and quest state, one exact session per native player slot.</summary>
internal sealed class NpcActorInteractionState(NpcDialogueDefinition? dialogue, NpcQuestCatalog? quests)
{
    private readonly Dictionary<PlayerId, Session> sessions = [];
    private long nextViewRevision;
    public NpcDialogueDefinition? Dialogue { get; private set; } = dialogue;
    public NpcQuestCatalog? Quests { get; private set; } = quests;
    public long QuestRevision { get; private set; } = 1;

    private Session For(PlayerSnapshot player)
    {
        if (player.SessionId is not { } id) throw new InvalidOperationException("An exact player session is required.");
        if (!sessions.TryGetValue(player.Id, out var current) || current.Id != id)
            sessions[player.Id] = current = new Session(id);
        return current;
    }

    public void OnConversation(PlayerSnapshot player) => For(player).View = null;
    public void InvalidateDialogues() { foreach (var session in sessions.Values) session.View = null; }
    public void SetDialogue(NpcDialogueDefinition? value) { Dialogue = value; InvalidateDialogues(); }
    public void SetQuests(NpcQuestCatalog? value)
    {
        Quests = value;
        QuestRevision++;
        foreach (var session in sessions.Values) session.QuestStates.Clear();
    }

    public NpcDialogueResult Begin(PlayerSnapshot player)
    {
        if (Dialogue is not { } graph) return new(NpcDialogueStatus.NoDialogue);
        return Present(For(player), graph.Nodes[graph.InitialNode]);
    }

    public NpcDialogueResult Choose(PlayerSnapshot player, long revision, string choiceId, out (string NodeId, NpcDialogueChoice Choice)? selected)
    {
        selected = null;
        if (Dialogue is not { } graph) return new(NpcDialogueStatus.NoDialogue);
        var session = For(player);
        if (session.View is not { } view || view.Revision != revision)
            return new(NpcDialogueStatus.StaleView);
        var choice = view.Choices.FirstOrDefault(choice => choice.Id == choiceId);
        if (choice is null) return new(NpcDialogueStatus.InvalidChoice, view);
        selected = (view.NodeId, choice);
        if (choice.NextNode is { } next) return Present(session, graph.Nodes[next]);
        session.View = null;
        return new(NpcDialogueStatus.Closed);
    }

    private NpcDialogueResult Present(Session session, NpcDialogueNode node)
    {
        session.View = new(++nextViewRevision, node.Id, node.Text, node.Choices);
        return new(NpcDialogueStatus.Presented, session.View);
    }

    public IReadOnlyList<NpcQuestView> GetQuests(PlayerSnapshot player) => Quests is { } catalog
        ? Array.AsReadOnly(catalog.Quests.Select(quest => new NpcQuestView(quest, State(player, quest.Id))).ToArray()) : [];
    public NpcQuestDefinition? FindQuest(string questId) => Quests?.Quests.FirstOrDefault(quest => quest.Id == questId);
    public NpcQuestState State(PlayerSnapshot player, string questId) => For(player).QuestStates.GetValueOrDefault(questId);
    public void SetState(PlayerSnapshot player, string questId, NpcQuestState state) => For(player).QuestStates[questId] = state;

    public NpcQuestResult Accept(PlayerSnapshot player, string questId, long revision)
    {
        if (revision != QuestRevision) return new(NpcQuestStatus.CatalogChanged, QuestRevision);
        if (FindQuest(questId) is not { } quest) return new(NpcQuestStatus.UnknownQuest, QuestRevision);
        var state = State(player, questId);
        NpcQuestStatus status = state switch
        {
            NpcQuestState.Accepted => NpcQuestStatus.AlreadyAccepted,
            NpcQuestState.Completed when !quest.Repeatable => NpcQuestStatus.AlreadyCompleted,
            _ => NpcQuestStatus.Applied
        };
        if (status == NpcQuestStatus.Applied) SetState(player, questId, state = NpcQuestState.Accepted);
        return new(status, QuestRevision, new(quest, state));
    }

    private sealed class Session(PlayerSessionId id)
    {
        public PlayerSessionId Id { get; } = id;
        public NpcDialogueView? View;
        public Dictionary<string, NpcQuestState> QuestStates { get; } = new(StringComparer.Ordinal);
    }
}
