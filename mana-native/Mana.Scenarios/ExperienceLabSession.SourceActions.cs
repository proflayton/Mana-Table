using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

public sealed partial class ExperienceLabSession
{
    private GameSnapshot? sourceActionParent;
    private string ballistaMode = ExperienceScenarios.BallistaDamageAction;
    private StackTarget? ballistaTarget;

    private void ResetSourceActions()
    {
        sourceActionParent = null;
        ballistaMode = ExperienceScenarios.BallistaDamageAction;
        ballistaTarget = null;
        preparedRecipient = 0;
        preparedAgain = false;
        counterPaymentDonor = CounterPaymentScenario.MareId;
        counterPaymentType = 12;
    }

    public SourceActionSet SourceActions(string gameId, string decisionId, string sourceCardKey)
    {
        lock (gate) {
            if (gameId != State.Id || decisionId != State.Decision?.Id || string.IsNullOrWhiteSpace(sourceCardKey)) throw Unsupported();
            var source = State.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
                .SingleOrDefault(card => card.Key == sourceCardKey);
            if (source == null) throw Unsupported();
            var viewer = State.Viewer ?? throw Unsupported();
            if (Scenario.Id == PreparedSpellScenario.Id) return PreparedSpellActions(source);
            if (Scenario.Id == PlayAccessScenario.Id) return PlayAccessActions(source);
            if (Scenario.Id == CounterPaymentScenario.Id) return CounterPaymentActions(source);
            SourceAction Action(string id, string text) => new(decisionId + ":" + sourceCardKey + ":" + id, "activate", text);
            SourceAction[] actions = [];
            if (Scenario.Id == DecisionFlowScenario.Id && StageIndex == 0
                && State.Decision is { Payload: PriorityPayload }
                && viewer.Zone("Hand").Cards.Contains(source) && source.Selectable && source.VisualId == DecisionFlowScenario.LandId)
                actions = [new(decisionId + ":" + sourceCardKey + ":play-forest", "play", "Play land")];
            else if (State.Decision is { Payload: PriorityPayload or PaymentPayload }
                && viewer.Zone("Battlefield").Cards.Contains(source) && source.Selectable && !source.FaceDown && !source.PhasedOut) {
                if (source.VisualId is ClutteredChangesScenario.ActionCardId or "reader-elf" or "mana-elf" && !source.Tapped)
                    actions = [Action("green-mana", "{T}: Add {G}.")];
                else if (Scenario.Id == "ability-targeting.v1" && StageIndex == 0 && source.VisualId == ExperienceScenarios.BallistaId) {
                    var available = new List<SourceAction>();
                    if (viewer.Mana.GetValueOrDefault("C") >= 4)
                        available.Add(Action(ExperienceScenarios.BallistaCounterAction, ExperienceScenarios.BallistaCounterText));
                    if (source.Counters.GetValueOrDefault("+1/+1") > 0)
                        available.Add(Action(ExperienceScenarios.BallistaDamageAction, ExperienceScenarios.BallistaDamageText));
                    actions = available.ToArray();
                }
            }
            return new(State.Id, decisionId, source.Key, source.VisualId, actions.ToImmutableArray());
        }
    }

    private GameSnapshot UseSourceAction(UseOfferedAction reply)
    {
        var offered = SourceActions(reply.GameId, reply.DecisionId, reply.Card!);
        var selected = offered.Actions.SingleOrDefault(action => action.Id == reply.ActionId) ?? throw Unsupported();
        if (Scenario.Id == PlayAccessScenario.Id) return UsePlayAccessAction(offered, selected);
        if (Scenario.Id == PreparedSpellScenario.Id) return AdvancePreparedSpell(1);
        if (Scenario.Id == DecisionFlowScenario.Id && StageIndex == 0 && offered.SourceCardId == DecisionFlowScenario.LandId)
            return AdvanceTo(1);
        if (offered.SourceCardId == ExperienceScenarios.BallistaId) {
            ballistaMode = selected.Id.EndsWith(":" + ExperienceScenarios.BallistaCounterAction, StringComparison.Ordinal)
                ? ExperienceScenarios.BallistaCounterAction : ExperienceScenarios.BallistaDamageAction;
            ballistaTarget = null;
            if (ballistaMode == ExperienceScenarios.BallistaCounterAction) return AdvanceBallista(1);
            sourceActionParent = State;
            var source = State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == offered.SourceCardId);
            return State = Stamp(State with {
                PriorityPlayerId = null, DecisionOwnerId = State.ViewerId, ActionClass = "selection",
                Players = State.Players.Select(player => player with {
                    Priority = false, Zones = player.Zones.Select(zone => zone with {
                        Cards = zone.Cards.Select(card => card with {
                            Selectable = zone.Name == "Battlefield" && card.VisualId is ExperienceScenarios.BallistaId or ExperienceScenarios.BallistaTargetId
                        }).ToImmutableArray()
                    }).ToImmutableArray()
                }).ToImmutableArray(),
                Decision = new("",
                    new(Message: "Choose any target for Walking Ballista's 1 damage. Removing a +1/+1 counter is the cost.",
                        Title: "Choose a target", Source: new(source.VisualId, source, State.ViewerId, "Battlefield")),
                    new TargetSelectionPayload(new("Confirm", false), new("Cancel", true), new(1, 1),
                        State.Players.Select(player => player.Id).ToImmutableArray()))
            });
        }
        State = State with { Players = State.Players.Select(player => player.Id != State.ViewerId ? player : player with {
            Mana = (new Dictionary<string, int>(player.Mana) { ["G"] = player.Mana.GetValueOrDefault("G") + 1 }).ToImmutableDictionary(),
            Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card.VisualId == offered.SourceCardId ? card with { Tapped = true, Selectable = false } : card).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() };
        abilityParent = null;
        if (Scenario.Id == "ability-choice.v1") StageIndex = 1;
        return State = Stamp(State with { PriorityPlayerId = State.ViewerId, DecisionOwnerId = State.ViewerId,
            ActionClass = "priority", Decision = PriorityDecision("One green mana is in your pool.") });
    }

    private GameSnapshot ReplyBallistaTarget(DecisionCommand reply)
    {
        if (sourceActionParent == null) throw Unsupported();
        var targeting = State.Decision?.Payload as TargetSelectionPayload ?? throw Unsupported();
        if (reply is CancelSelection && targeting.Cancel.Enabled) {
            var parent = sourceActionParent;
            sourceActionParent = null; ballistaTarget = null;
            return State = Stamp(parent);
        }
        if (reply is SelectPlayer { Player: var playerId } && targeting.Players.Contains(playerId)) {
            var player = State.Players.Single(player => player.Id == playerId);
            ballistaTarget = new("player", playerId.ToString(System.Globalization.CultureInfo.InvariantCulture), player.Name, playerId);
        } else if (reply is SelectCard selected) {
            var target = State.Players.SelectMany(player => player.Zone("Battlefield").Cards.Select(card => new { Player = player, Card = card }))
                .SingleOrDefault(item => item.Card.Key == selected.Card && item.Card.Selectable) ?? throw Unsupported();
            ballistaTarget = new("card", target.Card.VisualId, target.Card.Name, target.Player.Id);
        } else throw Unsupported();
        sourceActionParent = null;
        return AdvanceBallista(1);
    }

    private GameSnapshot AdvanceBallista(int stage)
    {
        sourceActionParent = null;
        StageIndex = stage;
        return State = Stamp(ExperienceScenarios.BallistaSnapshot(stage, ballistaMode, ballistaTarget));
    }
}
