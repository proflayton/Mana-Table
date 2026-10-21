using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>Replays authored snapshots and a few explicit UI acknowledgements, never Magic rules.</summary>
public sealed class ExperienceLabSession
{
    private readonly object gate = new();
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private int epoch;
    private long revision;
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
            StageIndex = stage; return State = Stamp(Stages[stage].Snapshot);
        }
    }
    private GameSnapshot Stamp(GameSnapshot state)
    {
        string game = "experience-lab:" + sessionId + ":" + Scenario.Id + ":" + epoch;
        string prompt = game + ":decision:" + ++revision;
        int? priorityPlayer = state.PriorityPlayerId ?? (state.Decision?.Intent == DecisionIntent.Priority ? state.ViewerId : null);
        return state with { Id = game, Revision = revision, BoardRevision = revision,
            Decision = state.Decision == null ? null : state.Decision with { Id = prompt },
            DecisionOwnerId = state.DecisionOwnerId ?? (state.Decision == null ? null : state.ViewerId),
            PriorityPlayerId = priorityPlayer,
            ActionClass = state.ActionClass.Length > 0 ? state.ActionClass : state.Decision is { Kind: "input" } decision
                ? decision.Intent switch { DecisionIntent.Priority => "priority", DecisionIntent.Mana => "mana", DecisionIntent.Selection => "selection", _ => "input" }
                : state.Decision?.Kind ?? "",
            Players = state.Players.Select(player => player with { Priority = priorityPlayer == player.Id, Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card with { Key = prompt + ":" + (card.VisualId.Length > 0 ? card.VisualId : card.CombatId) }).ToArray()
            }).ToArray() }).ToArray()
        };
    }
    public GameSnapshot Reply(DecisionReply reply)
    {
        lock (gate) {
            if (reply.GameId != State.Id || reply.DecisionId != State.Decision?.Id)
                throw new EngineCommandRejectedException("This scripted snapshot changed. Use its current controls.");
            var decision = State.Decision!;
            var cards = State.Viewer!.Zones.SelectMany(zone => zone.Cards).ToArray();
            if (reply.Action == ReplyAction.SelectCard && decision.Intent == DecisionIntent.Selection) {
                var chosen = cards.FirstOrDefault(card => card.Key == reply.Card && card.Selectable)
                    ?? throw Unsupported();
                State = Stamp(State with { Players = State.Players.Select(player => player.Id != State.ViewerId ? player : player with {
                    Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.Key != chosen.Key ? card : card with {
                        Highlighted = !card.Highlighted
                    }).ToArray() }).ToArray()
                }).ToArray() });
            } else if (reply.Action == ReplyAction.SelectCard && decision.Intent is DecisionIntent.Priority or DecisionIntent.Mana) {
                var source = cards.FirstOrDefault(card => card.Key == reply.Card && card.Selectable);
                if (source?.VisualId is not (ClutteredChangesScenario.ActionCardId or "reader-elf" or "mana-elf")) throw Unsupported();
                State = Stamp(State with { ActionClass = "", PriorityPlayerId = null, Decision = new() { Kind = "choice", Context = "playAbility", SourceCard = source,
                    SourceZone = "Battlefield", Title = "Scripted activation preview", Message = "This authored interaction demonstrates the activation UI; Forge is not running.",
                    Min = 0, Max = 1, Choices = [new(0, "Preview tapping this Elf and adding one green mana", null)] } });
            } else if (reply.Action == ReplyAction.SelectPlayer && decision.Intent == DecisionIntent.Selection
                && reply.Player is { } recipient && decision.PlayerChoices.Contains(recipient) && Scenario.Id == "trigger-search.v1") {
                if (recipient == State.ViewerId) { StageIndex = 3; State = Stamp(Stages[3].Snapshot); }
                else State = Stamp(State with { Decision = null, DecisionOwnerId = recipient, PriorityPlayerId = null, ActionClass = "choice",
                    Notices = ["Scripted recipient: Opponent " + recipient + ". Their private search choices are not supplied to this view."] });
            } else if (reply.Action == ReplyAction.Choose && decision.Kind == "choice") {
                var selected = reply.Choices ?? [];
                if (selected.Length < decision.Min || selected.Length > decision.Max || selected.Distinct().Count() != selected.Length
                    || selected.Any(index => !decision.Choices.Any(choice => choice.Index == index)
                        && !decision.LibraryCards.Any(choice => choice.Index == index))) throw Unsupported();
                if (Scenario.Id == "trigger-search.v1" && decision.Context == "librarySearch" && selected.Length == 1) {
                    var selectedCard = decision.LibraryCards.Single(card => card.Index == selected[0]).Card;
                    StageIndex = 4;
                    State = Stamp(Stages[4].Snapshot with { Players = Stages[4].Snapshot.Players.Select(player => player.Id != State.ViewerId ? player : player with {
                        Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Cards = [selectedCard with {
                            VisualId = "searched-card", CombatId = "searched-card", Selectable = false
                        }] }).ToArray()
                    }).ToArray() });
                } else if (decision.Context == "playAbility" && selected.Length == 1 && decision.SourceCard is { } source) {
                    State = State with { Players = State.Players.Select(player => player.Id != State.ViewerId ? player : player with {
                        Mana = new(player.Mana) { ["G"] = player.Mana.GetValueOrDefault("G") + 1 },
                        Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.VisualId != source.VisualId ? card : card with { Tapped = true }).ToArray() }).ToArray()
                    }).ToArray() };
                    Complete("Scripted activation recorded: the Elf is tapped and one green mana is shown. Reset to repeat.");
                } else Complete("Scripted order recorded: " + string.Join(" → ", selected.Select(index => decision.Choices.Single(choice => choice.Index == index).Label)) + ". Reset to repeat.");
            } else if (reply.Action == ReplyAction.Allocate && decision.Kind == "allocate") {
                var amounts = reply.Amounts ?? [];
                bool valid = amounts.Length == decision.Choices.Length && amounts.Select(value => (long)value).Sum() == decision.Amount;
                for (int index = 0; valid && index < amounts.Length; index++) {
                    var choice = decision.Choices[index];
                    valid = amounts[index] >= (choice.Minimum ?? 0) && amounts[index] <= (choice.Maximum ?? decision.Amount);
                }
                if (valid && decision.Choices.Where((choice, index) => choice.IsDefender && amounts[index] > 0).Any())
                    valid = decision.Choices.Select((choice, index) => amounts[index] >= (choice.Lethal ?? 0)).All(value => value);
                if (!valid) throw new EngineCommandRejectedException("The scripted allocation must satisfy the displayed total and recipient thresholds.");
                Complete("Scripted allocation recorded: " + string.Join(", ", amounts) + ". No combat rules were simulated. Reset to repeat.");
            } else if (reply.Action == ReplyAction.Confirm && decision.Intent == DecisionIntent.Selection) {
                int selected = cards.Count(card => card.Highlighted);
                if (selected < decision.Min || selected > decision.Max) throw new EngineCommandRejectedException("Choose one or two cards in this scripted selection.");
                Complete("Scripted selection recorded: " + selected + " cards. No cards were discarded. Reset to repeat.");
            } else if (reply.Action == ReplyAction.Confirm && decision.Intent == DecisionIntent.Priority
                && (Scenario.Id is "trigger-search.v1" or "stack-priority.v1") && (StageIndex == 0 || Scenario.Id == "stack-priority.v1" && StageIndex == 2)) {
                StageIndex++; State = Stamp(Stages[StageIndex].Snapshot);
            } else if (reply.Action == ReplyAction.Confirm && decision.Intent == DecisionIntent.Mana && Scenario.Id == "mana-payment.v1" && StageIndex == 0) {
                StageIndex = 1; State = Stamp(Stages[1].Snapshot);
            } else if (reply.Action == ReplyAction.Cancel) State = Stamp(Stage.Snapshot);
            else throw Unsupported();
            return State;
        }
    }
    private void Complete(string message) => State = Stamp(State with { ActionClass = "", PriorityPlayerId = null, Decision = new() {
        Kind = "input", Intent = DecisionIntent.General, Message = message, OkEnabled = false
    } });
    private static EngineCommandRejectedException Unsupported() => new("This is a scripted presentation snapshot. Inspect cards or use Previous, Next, Reset and Scenarios; start a real game for rules-driven play.");
}
