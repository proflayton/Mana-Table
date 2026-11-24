using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

internal static partial class AiReleaseGoldens
{
    // Read-only historical evidence, never a command or a current card handle.
    private sealed record CooperativeStackWitness(string GameId, string StackId, int ActorPlayerId,
        string ExecutionId, string SourceCardId, string TargetCardId, int TargetControllerId,
        long ActivationActivityId, long BoardRevision, int ObservedViewerId);

    private static CooperativeStackWitness? CaptureCooperativeStackWitness(GameSnapshot state, string gameId,
        int actorId, string sourceId, string targetId, int targetControllerId, IEnumerable<AcceptedCommand> receipts)
    {
        if (state.Id != gameId || string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(sourceId)
            || string.IsNullOrWhiteSpace(targetId) || state.Stack.IsDefault || state.Activity.IsDefault) return null;
        var executions = receipts.Where(item => item.Seat == actorId && item.Kind == nameof(SelectCard)
                && !item.SelectionIsCost && item.CostOwner == sourceId && item.SelectedCardId == targetId
                && item.SelectedControllerId == targetControllerId && !string.IsNullOrWhiteSpace(item.ExecutionId))
            .Select(item => item.ExecutionId).ToHashSet(StringComparer.Ordinal);
        if (executions.Count == 0) return null;
        foreach (var row in state.Stack) {
            if (row.Kind != StackActionKind.ActivatedAbility || row.ControllerId != actorId
                || string.IsNullOrWhiteSpace(row.Id) || !executions.Contains(row.ExecutionId)) continue;
            var activation = state.Activity.FirstOrDefault(item => item.Kind == "ability" && item.PlayerId == actorId
                && item.CardId == sourceId && item.StackId == row.Id && item.ExecutionId == row.ExecutionId);
            if (activation != null) return new(gameId, row.Id, actorId, row.ExecutionId, sourceId, targetId,
                targetControllerId, activation.Id, state.BoardRevision, state.ViewerId);
        }
        return null;
    }

    internal static void CheckCooperativeStackWitness(Action<bool, string> check)
    {
        const string game = "cooperative-stack-binding", source = "original-public-source", target = "chosen-target",
            execution = "accepted-source-target-execution", stackId = "157";
        const int actor = 0, recipient = 2;
        var card = new Card { VisualId = source, Name = "Untrusted portrait name", Type = "Artifact" };
        var row = new StackItem("Untrusted stack name", "Untrusted text", StackActionKind.ActivatedAbility, card) {
            Id = stackId, ControllerId = actor, ExecutionId = execution
        };
        var activation = new ActivityEntry(17, 1, "UPKEEP", "ability", actor, "Untrusted prose", source, "Another name") {
            StackId = stackId, ExecutionId = execution
        };
        var state = new GameSnapshot(game, "playing", null, null, actor, 1, "Upkeep", "UPKEEP", actor,
            [new(actor, "Actor", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
             new(recipient, "Recipient", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [row], null, null) {
            Revision = 185, BoardRevision = 43, Activity = [activation]
        };
        var receipt = new AcceptedCommand(actor, nameof(SelectCard), 1, "UPKEEP", "", source, null, null, null,
            null, 7, false, false, false, false, null, false) {
            ExecutionId = execution, SelectedCardId = target, SelectedControllerId = recipient
        };
        CooperativeStackWitness? Capture(GameSnapshot observed, params AcceptedCommand[] accepted) =>
            CaptureCooperativeStackWitness(observed, game, actor, source, target, recipient, accepted);
        foreach (var portrait in new Card?[] { card, card with { VisualId = "", Name = "Sacrificed source portrait" }, null }) {
            var observed = state with { Stack = [row with { Card = portrait }] };
            check(Capture(observed, receipt) is { StackId: stackId, ActorPlayerId: actor, ExecutionId: execution,
                    SourceCardId: source, TargetCardId: target, TargetControllerId: recipient,
                    ActivationActivityId: 17, BoardRevision: 43, ObservedViewerId: actor },
                "An actual activated stack row and source-bound activation event prove its accepted target execution regardless of current portrait availability");
        }
        foreach (var changed in new[] {
            state with { Id = "another-game" }, state with { Stack = [] }, state with { Activity = [] },
            state with { Stack = [row with { Id = "" }] }, state with { Stack = [row with { Id = "another-stack" }] },
            state with { Stack = [row with { ExecutionId = "" }] }, state with { Stack = [row with { ExecutionId = "another-execution" }] },
            state with { Stack = [row with { Kind = StackActionKind.Spell }] },
            state with { Stack = [row with { Kind = StackActionKind.TriggeredAbility }] },
            state with { Stack = [row with { Kind = StackActionKind.Ability }] },
            state with { Stack = [row with { ControllerId = null }] }, state with { Stack = [row with { ControllerId = recipient }] },
            state with { Activity = [activation with { Kind = "resolved", Outcome = "resolved" }] },
            state with { Activity = [activation with { PlayerId = recipient }] },
            state with { Activity = [activation with { CardId = "another-source" }] },
            state with { Activity = [activation with { CardId = null }] },
            state with { Activity = [activation with { StackId = null }] },
            state with { Activity = [activation with { StackId = "another-stack" }] },
            state with { Activity = [activation with { ExecutionId = "another-execution" }] }
        }) check(Capture(changed, receipt) == null,
            "A missing or mismatched game, real stack row, kind, actor, source, stack ID or execution cannot supply the cooperative stack witness");
        check(Capture(state) == null, "A stack row and activation cannot replace the required accepted source/target receipt");
        foreach (var changed in new[] {
            receipt with { Seat = recipient }, receipt with { Kind = nameof(UseOfferedAction) },
            receipt with { SelectionIsCost = true }, receipt with { CostOwner = "another-source" },
            receipt with { SelectedCardId = "another-target" }, receipt with { SelectedControllerId = actor },
            receipt with { ExecutionId = "" }, receipt with { ExecutionId = "another-execution" }
        }) check(Capture(state, changed) == null,
            "Unaccepted or differently bound actor, selection purpose, source, target, controller and execution receipts cannot authorize a stack witness");
        var consequenceOnly = state with { Stack = [], Activity = [activation with { Kind = "resolved", Outcome = "resolved" },
            new(18, 1, "UPKEEP", "damage", recipient, "Damage", target, "Target") { ExecutionId = execution }] };
        check(Capture(consequenceOnly, receipt) == null,
            "Resolved and damage consequences alone never substitute for an actual observed stack occurrence");
    }
}
