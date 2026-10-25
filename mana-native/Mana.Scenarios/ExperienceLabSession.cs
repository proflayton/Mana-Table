using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>Replays authored snapshots and a few explicit UI acknowledgements, never Magic rules.</summary>
public sealed partial class ExperienceLabSession
{
    private readonly object gate = new();
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private int epoch;
    private long revision;
    private GameSnapshot? abilityParent;
    public ExperienceScenario Scenario { get; private set; }
    public IReadOnlyList<ExperienceStage> Stages { get; private set; }
    public int StageIndex { get; private set; }
    public GameSnapshot State { get; private set; } = null!;
    public ExperienceStage Stage => Stages[StageIndex];
    public ExperienceLabSession(string id = ExperienceScenarios.DefaultId)
    {
        Scenario = ExperienceScenarios.Find(id); Stages = Scenario.Create(); Replay(0);
    }
    public IReadOnlyList<GameSnapshot> Select(string id)
    {
        lock (gate) { Scenario = ExperienceScenarios.Find(id); Stages = Scenario.Create(); return Replay(0); }
    }
    public IReadOnlyList<GameSnapshot> Replay(int stage)
    {
        lock (gate) {
            abilityParent = null;
            ResetSourceActions();
            StageIndex = Math.Clamp(stage, 0, Stages.Count - 1); epoch++;
            var states = Enumerable.Range(0, StageIndex + 1).Select(index => Stamp(Stages[index].Snapshot)).ToArray();
            State = states[^1]; return Array.AsReadOnly(states);
        }
    }
    /// <summary>An explicit authored external transition, preserving the current game/response epoch.</summary>
    public GameSnapshot AdvanceTo(int stage)
    {
        lock (gate) {
            if (stage != StageIndex + 1 || stage >= Stages.Count) throw new ArgumentOutOfRangeException(nameof(stage), "Continue to the next authored stage; replay to move backward.");
            abilityParent = null;
            if (Scenario.Id == "ability-targeting.v1") return AdvanceBallista(stage);
            if (Scenario.Id == PreparedSpellScenario.Id) return AdvancePreparedSpell(stage);
            StageIndex = stage; return State = Stamp(Stages[stage].Snapshot);
        }
    }
    private GameSnapshot Stamp(GameSnapshot state)
    {
        string game = "experience-lab:" + sessionId + ":" + Scenario.Id + ":" + epoch;
        string prompt = game + ":decision:" + ++revision;
        int? priorityPlayer = state.PriorityPlayerId ?? (state.Decision?.Payload is PriorityPayload ? state.ViewerId : null);
        return state with { Id = game, Revision = revision, BoardRevision = revision,
            Decision = state.Decision == null ? null : state.Decision with { Id = prompt },
            DecisionOwnerId = state.DecisionOwnerId ?? (state.Decision == null ? null : state.ViewerId),
            PriorityPlayerId = priorityPlayer,
            ActionClass = state.ActionClass.Length > 0 ? state.ActionClass : state.Decision?.Payload switch {
                PriorityPayload => "priority", OpeningHandPayload => "mulligan", PaymentPayload => "mana",
                AttackPayload => "attack", BlockPayload => "block", TargetSelectionPayload or EntitySelectionPayload => "selection",
                ConfirmationPayload => "confirm", OptionsPayload or OrderingPayload or AbilityChoicePayload => "choice",
                RevealPayload => "reveal", NumberPayload => "number", TextPayload => "text", AllocationPayload => "allocate", _ => ""
            },
            Players = state.Players.Select(player => player with { Priority = priorityPlayer == player.Id, Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card with { Key = prompt + ":" + (card.VisualId.Length > 0 ? card.VisualId : card.CombatId) }).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray()
        };
    }
    public GameSnapshot Reply(GameCommand command)
    {
        lock (gate) {
            if (command is not DecisionCommand reply || reply.GameId != State.Id || reply.DecisionId != State.Decision?.Id)
                throw new EngineCommandRejectedException("This scripted snapshot changed. Use its current controls.");
            if (Scenario.Id == DecisionFlowScenario.Id) return ReplyDecisionFlow(reply);
            if (Scenario.Id == OptionalDecisionScenario.Id) return ReplyOptionalDecision(reply);
            var decision = State.Decision!;
            var options = decision.Payload switch {
                OptionsPayload value => value.Selection, OrderingPayload value => value.Selection,
                AbilityChoicePayload value => value.Selection, _ => null
            };
            var cards = State.Viewer!.Zones.SelectMany(zone => zone.Cards).ToArray();
            if (reply is UseOfferedAction) {
                reply.Validate();
                return UseSourceAction((UseOfferedAction)reply);
            } else if (Scenario.Id == PreparedSpellScenario.Id) {
                return ReplyPreparedSpell(reply);
            } else if (Scenario.Id == "ability-targeting.v1" && sourceActionParent != null
                && decision.Payload is TargetSelectionPayload) {
                return ReplyBallistaTarget(reply);
            } else if (Scenario.Id == CounterDamageScenario.Id && StageIndex % 3 == 0
                && decision.Payload is TargetSelectionPayload damageTargeting) {
                StackTarget target;
                if (reply is SelectPlayer { Player: var playerId } && damageTargeting.Players.Contains(playerId)) {
                    var player = State.Players.Single(player => player.Id == playerId);
                    target = new("player", playerId.ToString(System.Globalization.CultureInfo.InvariantCulture), player.Name, playerId);
                } else if (reply is SelectCard targetCard) {
                    var selected = State.Players.SelectMany(player => player.Zone("Battlefield").Cards.Select(card => (player, card)))
                        .SingleOrDefault(item => item.card.Key == targetCard.Card && item.card.Selectable);
                    if (selected.card == null) throw Unsupported();
                    target = new("card", selected.card.VisualId, selected.card.Name, selected.player.Id);
                } else throw Unsupported();
                StageIndex++;
                return State = Stamp(CounterDamageScenario.Snapshot(StageIndex, target));
            } else if (Scenario.Id == CounterDamageScenario.Id && StageIndex % 3 == 1
                && reply is PassPriority && decision.Payload is PriorityPayload) {
                var target = State.Stack.Single().Targets.Single();
                StageIndex++;
                return State = Stamp(CounterDamageScenario.Snapshot(StageIndex, target));
            } else if (Scenario.Id == "ability-targeting.v1" && StageIndex == 1
                && reply is PassPriority && decision.Payload is PriorityPayload) {
                return AdvanceBallista(2);
            } else if (reply is SelectCard selection && decision.Payload is TargetSelectionPayload or EntitySelectionPayload) {
                var chosen = cards.FirstOrDefault(card => card.Key == selection.Card && card.Selectable)
                    ?? throw Unsupported();
                var players = State.Players.Select(player => player.Id != State.ViewerId ? player : player with {
                    Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.Key != chosen.Key ? card : card with {
                        Highlighted = !card.Highlighted
                    }).ToImmutableArray() }).ToImmutableArray()
                }).ToImmutableArray();
                var next = State with { Players = players };
                if (decision.Payload is EntitySelectionPayload { Range: { } range } entities) {
                    int count = players.Single(player => player.Id == State.ViewerId).Zones.SelectMany(zone => zone.Cards).Count(card => card.Highlighted);
                    next = next with { Decision = decision with { Payload = entities with { Complete = entities.Complete with { Enabled = count >= range.Min && count <= range.Max } } } };
                }
                State = Stamp(next);
            } else if (reply is ActivateCard or UseManaSource && decision.Payload is PriorityPayload or PaymentPayload) {
                var sourceKey = reply switch { ActivateCard activation => activation.Card, UseManaSource mana => mana.Card, _ => "" };
                var source = cards.FirstOrDefault(card => card.Key == sourceKey && card.Selectable);
                if (source?.VisualId is not (ClutteredChangesScenario.ActionCardId or "reader-elf" or "mana-elf")) throw Unsupported();
                abilityParent = State;
                State = Stamp(State with {
                    ActionClass = "", PriorityPlayerId = null,
                    Decision = new("", new(Message: "Choose an ability. Costs and any targets come next.", Title: "Activate " + source.Name,
                            Source: new(source.VisualId, source, State.ViewerId, "Battlefield")),
                        new AbilityChoicePayload(new([new(0, "{T}: Add {G}.", null)], new(0, 1))))
                });
            } else if (reply is SelectPlayer { Player: var recipient } && decision.Payload is EntitySelectionPayload recipients
                && recipients.Players.Contains(recipient) && Scenario.Id == "trigger-search.v1") {
                if (recipient == State.ViewerId) { StageIndex = 3; State = Stamp(Stages[3].Snapshot); }
                else State = Stamp(State with { Decision = null, DecisionOwnerId = recipient, PriorityPlayerId = null, ActionClass = "choice",
                    Notices = ["Scripted recipient: Opponent " + recipient + ". Their private search choices are not supplied to this view."] });
            } else if (reply is ChooseOptions or OrderOptions && options != null && (decision.Payload is OrderingPayload) == (reply is OrderOptions)) {
                var selected = reply switch { ChooseOptions choose => choose.Choices, OrderOptions order => order.Choices, _ => [] };
                if (selected.Length < options.Range.Min || selected.Length > options.Range.Max || selected.Distinct().Count() != selected.Length
                    || selected.Any(index => !options.Choices.Any(choice => choice.Index == index))) throw Unsupported();
                if (Scenario.Id == "trigger-search.v1" && StageIndex == 3 && options.Disclosures.Length > 0 && selected.Length == 1) {
                    var selectedCard = options.Disclosures.Single(card => card.Index == selected[0]).Card;
                    StageIndex = 4;
                    State = Stamp(Stages[4].Snapshot with { Players = Stages[4].Snapshot.Players.Select(player => player.Id != State.ViewerId ? player : player with {
                        Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Cards = [selectedCard with {
                            VisualId = "searched-card", CombatId = "searched-card", Selectable = false
                        }] }).ToImmutableArray()
                    }).ToImmutableArray() });
                } else if (decision.Payload is AbilityChoicePayload && selected.Length == 0) {
                    var parent = abilityParent;
                    abilityParent = null;
                    State = Stamp(parent ?? State with { ActionClass = "", PriorityPlayerId = State.ViewerId,
                        DecisionOwnerId = State.ViewerId, Decision = PriorityDecision("") });
                } else if (decision.Payload is AbilityChoicePayload && selected.Length == 1 && decision.Context.Source?.Card is { } source
                    && source.VisualId is ClutteredChangesScenario.ActionCardId or "reader-elf" or "mana-elf") {
                    State = State with { Players = State.Players.Select(player => player.Id != State.ViewerId ? player : player with {
                        Mana = (new Dictionary<string, int>(player.Mana) { ["G"] = player.Mana.GetValueOrDefault("G") + 1 }).ToImmutableDictionary(),
                        Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.VisualId != source.VisualId ? card : card with {
                            Tapped = true, Selectable = false
                        }).ToImmutableArray() }).ToImmutableArray()
                    }).ToImmutableArray() };
                    abilityParent = null;
                    if (Scenario.Id == "ability-choice.v1") {
                        StageIndex = 1;
                        State = Stamp(State with { ActionClass = "", PriorityPlayerId = State.ViewerId, DecisionOwnerId = State.ViewerId,
                            Decision = PriorityDecision("One green mana is in your pool.") });
                    } else Complete("Added one green mana.");
                } else Complete("Scripted order recorded: " + string.Join(" → ", selected.Select(index => options.Choices.Single(choice => choice.Index == index).Label)) + ". Reset to repeat.");
            } else if (reply is AllocateAmounts allocation && decision.Payload is AllocationPayload) {
                var amounts = allocation.Amounts;
                if (!GameCommandPermissions.Allows(State, reply)) throw new EngineCommandRejectedException("The scripted allocation must satisfy the displayed total and recipient thresholds.");
                Complete("Scripted allocation recorded: " + string.Join(", ", amounts) + ". No combat rules were simulated. Reset to repeat.");
            } else if (reply is CompleteSelection && decision.Payload is EntitySelectionPayload entities) {
                int selected = cards.Count(card => card.Highlighted);
                if (!entities.Complete.Enabled || entities.Range is { } range && (selected < range.Min || selected > range.Max))
                    throw new EngineCommandRejectedException("Choose the required cards in this scripted selection.");
                Complete("Scripted selection recorded: " + selected + " cards. No cards were discarded. Reset to repeat.");
            } else if (reply is PassPriority && decision.Payload is PriorityPayload
                && (Scenario.Id is "trigger-search.v1" or "stack-priority.v1") && (StageIndex == 0 || Scenario.Id == "stack-priority.v1" && StageIndex == 2)) {
                StageIndex++; State = Stamp(Stages[StageIndex].Snapshot);
            } else if (reply is PayMana && decision.Payload is PaymentPayload && Scenario.Id == "mana-payment.v1" && StageIndex == 0) {
                StageIndex = 1; State = Stamp(Stages[1].Snapshot);
            } else if (reply is CancelSelection or CancelPayment or DeclineConfirmation or UndoLastAction or MulliganOpeningHand) { abilityParent = null; State = Stamp(Stage.Snapshot); }
            else throw Unsupported();
            return State;
        }
    }
    private static Decision PriorityDecision(string message) => new("", new(Message: message), new PriorityPayload(new("Confirm", false)));
    private void Complete(string message) => State = Stamp(State with {
        ActionClass = "", PriorityPlayerId = null,
        Decision = new("", new(Message: message), new ConfirmationPayload(new("Confirm", false)))
    });
    private static EngineCommandRejectedException Unsupported() => new("This is a scripted presentation snapshot. Inspect cards or use Previous, Next, Reset and Scenarios; start a real game for rules-driven play.");

    private GameSnapshot ReplyDecisionFlow(DecisionCommand reply)
    {
        reply.Validate();
        var decision = State.Decision ?? throw Unsupported();
        if (State.DecisionOwnerId != State.ViewerId) throw Unsupported();
        if (StageIndex == 0 && decision.Payload is PriorityPayload) {
            if (reply is UseOfferedAction) return UseSourceAction((UseOfferedAction)reply);
            if (reply is PlayCard play && State.Viewer!.Zone("Hand").Cards.Any(card =>
                card.VisualId == DecisionFlowScenario.LandId && card.Selectable && card.Key == play.Card))
                return AdvanceTo(1);
        }
        if (StageIndex == 4 && decision is { Payload: TargetSelectionPayload }
            && reply is SelectCard target && State.Players.Where(player => player.Id != State.ViewerId)
                .SelectMany(player => player.Zone("Battlefield").Cards).Any(card =>
                    card.VisualId == CounterDamageScenario.TargetId && card.Selectable && card.Key == target.Card))
            return AdvanceTo(5);
        if (StageIndex is 1 or 5 or 7 && reply is PassPriority
            && decision is { Payload: PriorityPayload { Pass.Enabled: true } })
            // Acknowledges only the next authored checkpoint. Other seats'
            // subsequent actions/choices still require explicit lab Next.
            return AdvanceTo(StageIndex + 1);
        throw Unsupported();
    }
}
