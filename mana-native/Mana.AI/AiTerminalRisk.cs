using Mana.Contracts;

namespace Mana.AI;

// A veto before voluntary initiation, not a rules prediction or a winning-line
// solver. Incomplete ordinary spells remain explorable, but a known loss recipe
// cannot disappear behind unknown timing, amounts, siblings or payment choices.
internal static class AiTerminalRisk
{
    internal static bool CanInitiate(GameSnapshot state, ActionOperationGraph graph, AiWorkBudget work)
    {
        if (graph.Knowledge == SemanticKnowledge.Unsupported || graph.Operations.IsDefault
            || graph.Operations.Length > 128) return false;
        if (graph.Operations.IsEmpty) return true;
        var nodes = new Dictionary<int, ActionOperation>();
        foreach (var node in graph.Operations) {
            if (!work.TrySpend() || !nodes.TryAdd(node.Id, node)) return false;
        }
        int edges = 0;
        var results = new Dictionary<(int Id, bool CurrentChoice), bool>();
        var path = new HashSet<int>();
        return Safe(graph.RootId, true);

        bool Safe(int id, bool currentChoice)
        {
            if (!work.TrySpend() || !nodes.TryGetValue(id, out var node) || path.Contains(id)) return false;
            if (results.TryGetValue((id, currentChoice), out bool safe)) return safe;
            if (node.Effect == ActionEffectKind.LoseGame && !ExcludesActor(node)) return false;
            path.Add(id);
            int safeChoices = 0, choiceCount = 0;
            foreach (var link in node.Links) {
                if (!work.TrySpend() || ++edges > 1024) { path.Remove(id); return false; }
                bool childSafe = Safe(link.OperationId, currentChoice && link.Kind != OperationLinkKind.Scheduled);
                if (link.Kind == OperationLinkKind.Choice) {
                    choiceCount++;
                    if (childSafe) safeChoices++;
                } else if (!childSafe) { path.Remove(id); return false; }
            }
            // Only an actual actor-owned current mode domain supplies a choice
            // witness. Future/other-player choices are not a current safe plan.
            safe = choiceCount == safeChoices || currentChoice && node.ActorPlayerId == state.ViewerId
                && node.Knowledge == SemanticKnowledge.Known && node.Effect == ActionEffectKind.Choose
                && node.MinChoices is { } min && node.MaxChoices is { } max
                && min >= 0 && max >= min && (min == 0 || safeChoices > 0 && (node.MayRepeatChoices || safeChoices >= min));
            path.Remove(id);
            results[(id, currentChoice)] = safe;
            return safe;
        }

        bool ExcludesActor(ActionOperation node)
        {
            var recipients = node.Recipients;
            // Target candidates do not prove which player will be chosen. The
            // policy has no terminal-effect target planner, so keep that unknown.
            return recipients.Knowledge == SemanticKnowledge.Known
                && (recipients.Binding is EffectRecipientBinding.Actor or EffectRecipientBinding.Explicit)
                && recipients.Entities.All(entity => entity.Kind == SemanticEntityKind.Player
                    && entity.PlayerId is { } player && player != state.ViewerId);
        }
    }
}
