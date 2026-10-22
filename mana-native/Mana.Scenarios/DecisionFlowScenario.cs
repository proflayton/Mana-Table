using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>
/// Authored four-seat checkpoints for decision ownership and the next local gesture.
/// Next advances presentation data; it does not play lands, choose for another seat,
/// run priority or derive a Magic outcome. A later own-turn checkpoint is explicit.
/// </summary>
public static class DecisionFlowScenario
{
    public const string Id = "decision-flow.v1";
    public const string LandId = "decision-flow-forest";
    public const string ResponseId = "decision-flow-giant-growth";
    public const string TargetContext = "lab-decision-flow-target";

    public static IReadOnlyList<ExperienceStage> Create()
    {
        var sample = CounterDamageScenario.Snapshot(3);
        var viewer = sample.Viewer!;
        var source = viewer.Zone("Battlefield").Cards.Single(card => card.VisualId == CounterDamageScenario.SourceId);
        var chandra = viewer.Zone("Battlefield").Cards.Single(card => card.VisualId == CounterDamageScenario.CounterCardId);
        var target = sample.Players[1].Zone("Battlefield").Cards.Single(card => card.VisualId == CounterDamageScenario.TargetId) with { Selectable = false };
        var forest = Face(LandId, "Forest", "Basic Land — Forest") with { Selectable = true };
        var mountains = Enumerable.Range(0, 3).Select(index => Face("decision-flow-mountain-" + index,
            "Mountain", "Basic Land — Mountain")).ToArray();
        var field = new[] { source }.Concat(mountains).ToArray();
        var first = sample with {
            Id = Id, Turn = 8, ActivePlayerId = 0, Phase = "Main phase", PhaseKey = "MAIN1",
            Status = "playing", PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            Stack = [], Combat = null, Activity = [], Notices = [],
            Players = sample.Players.Select(player => player with {
                Life = 40, Mana = [], Priority = player.Id == 0,
                LandPlaysUsed = player.Id == 0 ? 0 : player.LandPlaysUsed,
                LandPlayLimit = player.Id == 0 ? 1 : player.LandPlayLimit,
                Zones = player.Zones.Select(zone => player.Id == 0 ? zone.Name switch {
                    "Battlefield" => zone with { Count = field.Length, Cards = field, TopCard = null },
                    "Hand" => zone with { Count = 1, Cards = [forest], TopCard = null },
                    "Command" or "Graveyard" or "Exile" => zone with { Count = 0, Cards = [], TopCard = null },
                    _ => zone
                } : zone with { Cards = zone.Cards.Select(card => card with { Selectable = false, Highlighted = false }).ToArray() }).ToArray()
            }).ToArray(),
            Decision = Priority(false)
        };
        var playedForest = forest with { Selectable = false };
        var landPlayed = first with {
            Players = first.Players.Select(player => player.Id != 0 ? player : player with {
                LandPlaysUsed = 1,
                Zones = player.Zones.Select(zone => zone.Name switch {
                    "Battlefield" => zone with { Count = field.Length + 1, Cards = [.. field, playedForest] },
                    "Hand" => zone with { Count = 0, Cards = [] }, _ => zone
                }).ToArray()
            }).ToArray(),
            Decision = Priority(true),
            Activity = [new(1, 8, "MAIN1", "land", 0, "You played Forest.", LandId, forest.Name)]
        };
        var opponent = landPlayed with {
            Turn = 9, ActivePlayerId = 1, PriorityPlayerId = 1, DecisionOwnerId = 1,
            Decision = null,
            Players = landPlayed.Players.Select(player => player with { Priority = player.Id == 1 }).ToArray(),
            Activity = [.. landPlayed.Activity, new(2, 9, "MAIN1", "turn", 1, "Rowan's turn began.", null, null)]
        };
        // Only public ownership/class is supplied. This checkpoint deliberately
        // carries no private options, source inference or fabricated answer.
        var otherChoice = opponent with {
            Status = "resolving", PriorityPlayerId = null, DecisionOwnerId = 2, ActionClass = "selection",
            Players = opponent.Players.Select(player => player with { Priority = false }).ToArray()
        };
        var effect = sample.Decision!.Effect!;
        var counterCause = new PublicEffectCause(chandra.VisualId, chandra.Name, "Loyalty counters added", 0,
            "ability", "decision-flow-counter-origin");
        // This is a later own turn, not an effect attributed to the previous
        // private choice. Current loyalty and the captured +1 are independent facts.
        var choosing = landPlayed with {
            Turn = 12, ActivePlayerId = 0, PriorityPlayerId = null, DecisionOwnerId = 0, ActionClass = "selection",
            Players = landPlayed.Players.Select(player => player with {
                Priority = false,
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Count = player.Id == 0 ? field.Length + 2 : zone.Count,
                    Cards = player.Id == 0 ? [source, chandra, .. mountains.Select(card => card with { Tapped = true }), playedForest with { Tapped = true }]
                        : zone.Cards.Select(card => card.VisualId != target.VisualId ? card : target with { Selectable = true }).ToArray()
                }).ToArray()
            }).ToArray(),
            Decision = sample.Decision with { Context = TargetContext, Message = "Choose a target.", PlayerChoices = [] },
            Activity = [.. opponent.Activity,
                new(3, 12, "MAIN1", "turn", 0, "Your turn began.", null, null),
                new(4, 12, "MAIN1", "arrived", 0, "Chandra, the Firebrand entered the battlefield.", chandra.VisualId, chandra.Name),
                new(5, 12, "MAIN1", "counters", 0, "Added one loyalty counter.", chandra.VisualId, chandra.Name) {
                    Change = new("counters", "Loyalty", 3, 4), Cause = counterCause
                }]
        };
        var pendingAbility = new StackItem(source.Name, "All Will Be One deals 1 damage to Colossal Dreadmaw.", true, source) {
            Id = effect.Id, ControllerId = 0, Effect = effect,
            Targets = [new("card", target.VisualId, target.Name, 1)]
        };
        var pending = choosing with {
            PriorityPlayerId = 0, ActionClass = "priority", Decision = Priority(true), Stack = [pendingAbility],
            Players = ClearSelection(choosing.Players, 0),
            Activity = [.. choosing.Activity,
                new(6, 12, "MAIN1", "ability", 0, "All Will Be One's ability is pending.", source.VisualId, source.Name) {
                    Detail = pendingAbility.Text, Cause = new(source.VisualId, source.Name, pendingAbility.Text, 0, "ability", effect.Id)
                }]
        };
        var growth = Face(ResponseId, "Giant Growth", "Instant") with {
            ManaCost = "{G}", Text = "Target creature gets +3/+3 until end of turn."
        };
        var response = new StackItem(growth.Name, growth.Text, false, growth) {
            Id = "decision-flow-growth-stack", ControllerId = 1,
            Targets = [new("card", target.VisualId, target.Name, 1)]
        };
        var opponentResponse = pending with {
            Stack = [response, pendingAbility], PriorityPlayerId = 1, DecisionOwnerId = 1, Decision = null,
            Players = ClearSelection(pending.Players, 1).Select(player => player.Id != 1 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Count = zone.Count - 1 }).ToArray()
            }).ToArray(),
            Activity = [.. pending.Activity,
                new(7, 12, "MAIN1", "cast", 1, "Rowan cast Giant Growth.", growth.VisualId, growth.Name)]
        };
        var grown = opponentResponse with {
            Stack = [pendingAbility], PriorityPlayerId = 0, DecisionOwnerId = 0, Decision = Priority(true),
            Players = ClearSelection(opponentResponse.Players, 0).Select(player => player.Id != 1 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name switch {
                    "Battlefield" => zone with { Cards = zone.Cards.Select(card => card.VisualId != target.VisualId ? card : card with { Power = 9, Toughness = 9 }).ToArray() },
                    "Graveyard" => zone with { Count = zone.Count + 1, Cards = [.. zone.Cards, growth], TopCard = growth }, _ => zone
                }).ToArray()
            }).ToArray(),
            Activity = [.. opponentResponse.Activity,
                new(8, 12, "MAIN1", "resolved", 1, "Giant Growth resolved.", growth.VisualId, growth.Name) { Outcome = "resolved", Detail = growth.Text }]
        };
        var result = grown with {
            Stack = [], Decision = Priority(true) with { OkEnabled = false },
            Players = grown.Players.Select(player => player.Id != 1 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card.VisualId != target.VisualId ? card : card with { Damage = 1 }).ToArray()
                }).ToArray()
            }).ToArray(),
            Activity = [.. grown.Activity,
                new(9, 12, "MAIN1", "damage", 1, "All Will Be One dealt 1 damage to Colossal Dreadmaw.", target.VisualId, target.Name) {
                    Impact = new("damage", 1, target.VisualId, target.Name, SourceCardId: source.VisualId, SourceName: source.Name, DamageType: "normal")
                },
                new(10, 12, "MAIN1", "resolved", 0, "All Will Be One's ability resolved.", source.VisualId, source.Name) { Outcome = "resolved", Detail = pendingAbility.Text }]
        };
        return [
            new("Last land play", "Authored four-seat presentation: Forest is the sole available play in your first main phase.", first),
            new("No actions remain", "The authored land result reports an empty hand, a used land allowance and engine-authorized automatic priority.", landPlayed),
            new("Rowan has priority", "The next turn checkpoint supplies only the public responder; Rowan's private prompt is absent.", opponent),
            new("Mira is choosing", "A separate public required-choice checkpoint supplies ownership and class without private options or a supplied answer.", otherChoice),
            new("Choose the damage target", "Later own-turn checkpoint: Chandra has four loyalty; the captured increment for All Will Be One is exactly one. No cause is inferred from Mira's private choice.", choosing),
            new("Your ability is pending", "The authored one-damage effect records its source, captured counter increment and chosen creature; it has not resolved.", pending),
            new("Rowan responds", "Giant Growth is above your ability. Rowan holds priority; no private response prompt is exposed.", opponentResponse),
            new("Your ability is next", "The authored response has resolved. Colossal Dreadmaw is currently 9/9; your one-damage ability remains pending.", grown),
            new("Damage dealt; your priority", "An actual-result payload reports one damage. The current 9/9 retains one marked damage, the stack is empty and local priority is available. This fixture does not verify Magic rules.", result)
        ];
    }

    private static Decision Priority(bool canAutoPass) => new() {
        Kind = "input", Intent = DecisionIntent.Priority,
        OkEnabled = true, CanAutoPass = canAutoPass, Message = ""
    };
    private static Card Face(string id, string name, string type) => new() {
        VisualId = id, CombatId = id, Name = name, Type = type
    };
    private static Player[] ClearSelection(Player[] players, int priority) => players.Select(player => player with {
        Priority = player.Id == priority, Zones = player.Zones.Select(zone => zone with {
            Cards = zone.Cards.Select(card => card with { Selectable = false, Highlighted = false }).ToArray()
        }).ToArray()
    }).ToArray();
}
