using Mana.Contracts;

namespace Mana.Scenarios;

public sealed partial class ExperienceLabSession
{
    private const string BallistaTargetContext = "lab-ballista-target";
    private GameSnapshot? sourceActionParent;
    private string ballistaMode = ExperienceScenarios.BallistaDamageAction;
    private StackTarget? ballistaTarget;

    private void ResetSourceActions()
    {
        sourceActionParent = null;
        ballistaMode = ExperienceScenarios.BallistaDamageAction;
        ballistaTarget = null;
    }

    public SourceActionSet SourceActions(string gameId, string decisionId, string sourceCardKey)
    {
        lock (gate) {
            if (gameId != State.Id || decisionId != State.Decision?.Id || string.IsNullOrWhiteSpace(sourceCardKey)) throw Unsupported();
            var source = State.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
                .SingleOrDefault(card => card.Key == sourceCardKey);
            if (source == null) throw Unsupported();
            var viewer = State.Viewer ?? throw Unsupported();
            SourceAction Action(string id, string text) => new(decisionId + ":" + sourceCardKey + ":" + id, "activate", text);
            SourceAction[] actions = [];
            if (Scenario.Id == DecisionFlowScenario.Id && StageIndex == 0
                && State.Decision is { Kind: "input", Intent: DecisionIntent.Priority }
                && viewer.Zone("Hand").Cards.Contains(source) && source.Selectable && source.VisualId == DecisionFlowScenario.LandId)
                actions = [new(decisionId + ":" + sourceCardKey + ":play-forest", "play", "Play land")];
            else if (State.Decision is { Kind: "input", Intent: DecisionIntent.Priority or DecisionIntent.Mana }
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
            return new(State.Id, decisionId, source.Key, source.VisualId, actions);
        }
    }

    private GameSnapshot UseSourceAction(DecisionReply reply)
    {
        var offered = SourceActions(reply.GameId, reply.DecisionId, reply.Card!);
        var selected = offered.Actions.SingleOrDefault(action => action.Id == reply.AbilityId) ?? throw Unsupported();
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
                        }).ToArray()
                    }).ToArray()
                }).ToArray(),
                Decision = new() {
                    Kind = "input", Intent = DecisionIntent.Selection, Purpose = DecisionPurpose.TargetSelection, Context = BallistaTargetContext,
                    Title = "Choose a target", Message = "Choose any target for Walking Ballista's 1 damage. Removing a +1/+1 counter is the cost.",
                    Min = 1, Max = 1, CancelEnabled = true, PlayerChoices = State.Players.Select(player => player.Id).ToArray(),
                    SourceCard = source, SourceCardId = source.VisualId, SourceZone = "Battlefield", SourceControllerId = State.ViewerId
                }
            });
        }
        State = State with { Players = State.Players.Select(player => player.Id != State.ViewerId ? player : player with {
            Mana = new(player.Mana) { ["G"] = player.Mana.GetValueOrDefault("G") + 1 },
            Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card.VisualId == offered.SourceCardId ? card with { Tapped = true, Selectable = false } : card).ToArray()
            }).ToArray()
        }).ToArray() };
        abilityParent = null;
        if (Scenario.Id == "ability-choice.v1") StageIndex = 1;
        return State = Stamp(State with { PriorityPlayerId = State.ViewerId, DecisionOwnerId = State.ViewerId,
            ActionClass = "priority", Decision = PriorityDecision("One green mana is in your pool.") });
    }

    private GameSnapshot ReplyBallistaTarget(DecisionReply reply)
    {
        if (sourceActionParent == null) throw Unsupported();
        if (reply.Action == ReplyAction.Cancel && State.Decision!.CancelEnabled) {
            var parent = sourceActionParent;
            sourceActionParent = null; ballistaTarget = null;
            return State = Stamp(parent);
        }
        if (reply.Action == ReplyAction.SelectPlayer && reply.Player is { } playerId && State.Decision!.PlayerChoices.Contains(playerId)) {
            var player = State.Players.Single(player => player.Id == playerId);
            ballistaTarget = new("player", playerId.ToString(System.Globalization.CultureInfo.InvariantCulture), player.Name, playerId);
        } else if (reply.Action == ReplyAction.SelectCard) {
            var target = State.Players.SelectMany(player => player.Zone("Battlefield").Cards.Select(card => new { Player = player, Card = card }))
                .SingleOrDefault(item => item.Card.Key == reply.Card && item.Card.Selectable) ?? throw Unsupported();
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
