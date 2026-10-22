using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

public sealed record ExperienceStage(string Name, string Description, GameSnapshot Snapshot);
public sealed record ExperienceScenario(string Id, string Name, string Description, Func<IReadOnlyList<ExperienceStage>> Create);

/// <summary>Versioned, authored public projections. These exercise presentation; Forge remains the rules authority.</summary>
public static partial class ExperienceScenarios
{
    public const string DefaultId = "crowded-counters.v1";
    public static IReadOnlyList<ExperienceScenario> All { get; } = Array.AsReadOnly(new ExperienceScenario[] {
        new(DefaultId, "480 permanents · Toph + Felidar", "Four crowded battlefields, an earthbent land, then 82 exact landfall recipients.", Crowd),
        new("card-reading.v1", "Card reading and selection", "Stable hand slots, inspectable battlefield cards and a hand selection.", Reading),
        new("ability-choice.v1", "Llanowar Elves · make green mana", "Start before an activation, choose its ability, and see the tapped source and green mana.", AbilityFlow),
        new("ability-targeting.v1", "Walking Ballista · costs and targets", "Two on-card abilities, cancellable targeting, a pending stack ability and its authored result.", BallistaFlow),
        new(CounterDamageScenario.Id, "Counters → damage", "Chandra's counter increment, a numbered damage choice, its pending effect and an authored result.", CounterDamageScenario.Create),
        new(DamageResultsScenario.Id, "Damage · actual results", "Surviving recipients, death between observations and base versus actual prevented damage.", DamageResultsScenario.Create),
        new(DecisionFlowScenario.Id, "Four players · decisions and priority", "Authored land, priority handoffs, private-choice ownership, a typed target and pending effect, then its public result.", DecisionFlowScenario.Create),
        new("ordered-choice.v1", "Ordered card choices", "Same-name cards, explicit selection numbers and a complete ordered draft.", Ordered),
        new("mana-payment.v1", "Mana and payment", "Colored unspent mana, a payment decision and a scripted pool transition.", Mana),
        new("combat-allocation.v1", "Combat damage allocation", "Current stats, marked damage, anonymous blockers and defender thresholds.", Allocation),
        new("public-state.v1", "Public state and privacy", "Qualified abilities, generic player resources, day/night and visibility revocation.", PublicStates),
        new("stack-priority.v1", "Stack and priority", "A trigger, another player's response, your response, resolution and the resulting battlefield.", StackPriority),
        new("trigger-search.v1", "Trigger and library search", "Public priority hands off to required recipient and private library choices, then the local hand.", TriggerSearch),
        new("turn-orientation.v1", "Turn orientation · ordinary table", "A turn handoff, off-turn response, multiple defenders, target choice and extra turn.", TurnOrientationScenario.Ordinary),
        new("turn-orientation-crowded.v1", "Turn orientation · crowded table", "The same authored turn sequence over four crowded battlefields; compare stationary context.", TurnOrientationScenario.Crowded),
        new(FourPlayerTableScenario.Id, "Four players · asymmetric table", "Ordinary boards, one crowded opponent, then a cross-seat ability and its authored current result.", FourPlayerTableScenario.Create)
    });

    public static ExperienceScenario Find(string id) => All.FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException("Unknown experience scenario '" + id + "'. Available: " + string.Join(", ", All.Select(item => item.Id)));

    private static IReadOnlyList<ExperienceStage> Crowd()
    {
        var initial = ClutteredChangesScenario.Initial();
        var earthbend = ClutteredChangesScenario.Earthbend(initial);
        return new[] {
            new ExperienceStage("Initial battlefield", "480 public permanents. Click to read; right-click to inspect.", initial),
            new ExperienceStage("Toph · earthbend 2", "The reported Toph trigger animates one land and adds exactly two counters.", earthbend),
            new ExperienceStage("Felidar Retreat · counter mode", "One landfall trigger adds one counter and vigilance to each of 82 local creatures.", ClutteredChangesScenario.Retreat(earthbend))
        };
    }

    private static Card Body(string id, string name, string type = "Creature", int? power = 2, int? toughness = 2) => new() {
        VisualId = id, CombatId = id, Name = name, Type = type, Power = power, Toughness = toughness
    };
    private static GameSnapshot Table(string id, Card[] battlefield, Card[]? hand = null, Card[]? opposing = null)
    {
        hand ??= [];
        var players = Enumerable.Range(0, 4).Select(seat => new Player(seat, seat == 0 ? "You" : "Opponent " + seat, 40, seat == 0, false, ImmutableDictionary<string, int>.Empty, [
            new("Battlefield", seat == 0 ? battlefield.Length : seat == 1 ? opposing?.Length ?? 0 : 0, (seat == 0 ? battlefield : seat == 1 ? opposing ?? [] : []).ToImmutableArray(), null),
            new("Hand", seat == 0 ? hand.Length : 7, (seat == 0 ? hand : []).ToImmutableArray(), null),
            new("Command", 0, [], null), new("Library", 80, [], null), new("Graveyard", 0, [], null), new("Exile", 0, [], null)
        ])).ToArray();
        return new(id, "playing", null, null, 0, 4, "Main phase", "MAIN1", 0, players.ToImmutableArray(), [], null,
            new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = false, Message = "Scripted snapshot. Inspect cards, or use the stage controls." });
    }

    private static Card ManaElf() => Body("reader-elf", "Llanowar Elves", power: 1, toughness: 1)
        with { Selectable = true, Text = "{T}: Add {G}." };

    private static IReadOnlyList<ExperienceStage> AbilityFlow()
    {
        var initial = Table("ability-choice", [ManaElf()]);
        initial = initial with { Decision = initial.Decision! with { Message = "Make one green mana with Llanowar Elves." } };
        var finished = initial with {
            Players = initial.Players.Select(player => player.Id != initial.ViewerId ? player : player with {
                Mana = ImmutableDictionary<string, int>.Empty.Add("G", 1), Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card with { Tapped = true, Selectable = false }).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray(),
            Decision = initial.Decision! with { Message = "One green mana is in your pool." }
        };
        return [
            new("Make one green mana", "Use Llanowar Elves' mana ability.", initial),
            new("Green mana added", "The Elf is tapped and one green mana is in your pool.", finished)
        ];
    }

    private static IReadOnlyList<ExperienceStage> Reading()
    {
        var elf = ManaElf();
        var hand = new[] { Body("reader-hand-a", "Grizzly Bears") with { Text = "A simple creature for checking hand geometry." },
            Body("reader-hand-b", "Serra Angel", power: 4, toughness: 4) with { CombatKeywords = ["flying", "vigilance"] },
            Body("reader-hand-c", "Forest", "Basic Land", null, null) };
        var state = Table("reading", [elf, Body("reader-body", "Wall of Blossoms", power: 0, toughness: 4)], hand);
        var countedElf = elf with { Power = 3, Toughness = 3, Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2) };
        var counterLand = Body("reader-counter-land", "Blast Zone", "Land", null, null) with {
            Counters = ImmutableDictionary<string, int>.Empty.Add("Charge", 3).Add("Stun", 1)
        };
        var counterSupport = Body("reader-counter-support", "Everflowing Chalice", "Artifact", null, null) with {
            Counters = ImmutableDictionary<string, int>.Empty.Add("Charge", 2).Add("Shield", 1)
        };
        // These are authored current characteristics, not effects calculated by
        // this fixture. Counters must never be added to already reported P/T.
        var counted = state with { Players = state.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Count = 4, Cards = [countedElf, zone.Cards.Single(card => card.VisualId == "reader-body"), counterLand, counterSupport]
            }).ToImmutableArray()
        }).ToImmutableArray(), Decision = state.Decision! with { OkEnabled = true,
            Message = "You have priority." } };
        var modified = counted with { Players = counted.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = zone.Cards.Select(card => card.VisualId != elf.VisualId ? card : card with { Power = 6, Toughness = 6 }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray(), Decision = new() {
            Kind = "choice", Purpose = DecisionPurpose.AbilitySelection, Title = "Activate Llanowar Elves", Min = 0, Max = 1,
            // Deliberately older, disclosed prompt face. The exact public source
            // occurrence must supply its current characteristics for display.
            SourceCard = elf, SourceCardId = elf.VisualId, SourceControllerId = 0, SourceZone = "Battlefield",
            Message = "Choose an ability. Costs and any targets come next.",
            Choices = [new(0, "{T}: Add {G}.", null)]
        } };
        return new[] {
            new ExperienceStage("Read and collapse", "Click a hand card, click its neighbor, then click again to collapse. The Elf activation is a scripted UI demonstration.", state),
            new ExperienceStage("Choose from your hand", "Select and deselect cards without moving their original hand slots.", state with {
                Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                        Cards = zone.Cards.Select(card => card with { Selectable = true }).ToImmutableArray()
                    }).ToImmutableArray()
                }).ToImmutableArray(), Decision = new() {
                Kind = "input", Intent = DecisionIntent.Selection, Min = 1, Max = 2, OkEnabled = true,
                Message = "Scripted hand selection: choose one or two cards, then confirm. No real cards are discarded."
            } }),
            new ExperienceStage("Counters on current permanents", "The printed 1/1 Elf is currently 3/3 with two +1/+1 counters. A land and artifact carry different counter types. Read or inspect each public occurrence; these are authored snapshots, not simulated effects.", counted),
            new ExperienceStage("Current stats in an activation choice", "The same Elf is currently 6/6 with the same two +1/+1 counters. The chooser retains an older disclosed source face and an exact source ID, so display must use current public characteristics. No rules are simulated.", modified)
        };
    }
    private static IReadOnlyList<ExperienceStage> Ordered()
    {
        var cards = new[] { Body("order-a", "Grizzly Bears"), Body("order-b", "Grizzly Bears"), Body("order-c", "Serra Angel", power: 4, toughness: 4) };
        var state = Table("ordered", cards);
        return new[] { new ExperienceStage("Choose the complete order", "Select every occurrence. Numbers record its position; confirm records only this scripted draft.", state with {
            Decision = new() { Kind = "choice", Title = "Order these cards", Message = "Scripted order preview. Select all three cards in your chosen order.",
                Ordered = true, Min = 3, Max = 3, Choices = cards.Select((card, index) => new Choice(index, card.Name,
                    card with { Key = "", VisualId = "", CombatId = "" }) { CardId = card.VisualId, Id = "order-choice-" + index }).ToImmutableArray() }
        }) };
    }
    private static IReadOnlyList<ExperienceStage> Mana()
    {
        var elf = Body("mana-elf", "Llanowar Elves", power: 1, toughness: 1) with { Selectable = true, Text = "{T}: Add {G}." };
        var state = Table("mana", [elf, Body("mana-forest", "Forest", "Basic Land", null, null)]);
        GameSnapshot Pool(ImmutableDictionary<string, int> pool) => state with { Players = state.Players.Select(player => player.Id != 0 ? player : player with { Mana = pool }).ToImmutableArray() };
        var paying = Pool(ImmutableDictionary<string, int>.Empty.Add("C", 2).Add("G", 1).Add("U", 2)) with { Decision = new() {
            Kind = "input", Intent = DecisionIntent.Mana, OkEnabled = true, CancelEnabled = true,
            Message = "Pay {2}{G}.", SourceCard = Body("paying-cultivate", "Cultivate", "Sorcery", null, null) with { ManaCost = "{2}{G}" },
            SourceCardId = "paying-cultivate", SourceControllerId = 0, SourceZone = "Hand"
        } };
        return new[] {
            new ExperienceStage("Unspent mana and payment", "Five unspent mana are shown by color around your portrait. Inspect the source or view the next stage.", paying),
            new ExperienceStage("After payment", "The authored result spends two colorless and one green, leaving two blue mana.", Pool(ImmutableDictionary<string, int>.Empty.Add("U", 2))),
            new ExperienceStage("Pool emptied", "The authored transition removes all unspent mana.", Pool(ImmutableDictionary<string, int>.Empty))
        };
    }
    private static IReadOnlyList<ExperienceStage> Allocation()
    {
        var source = Body("damage-source", "Ancient Stoneguard", power: 1, toughness: 10) with { CombatKeywords = ["trample"],
            Text = "Scripted current characteristics: this effect assigns 10 damage. The amount comes from the snapshot." };
        var marked = Body("damage-marked", "Soldier", power: 2, toughness: 5) with { Damage = 3, CombatKeywords = ["indestructible"] };
        var second = Body("damage-second", "Soldier", power: 1, toughness: 1) with { CombatKeywords = ["deathtouch"] };
        var anonymous = Body("", "Face-down card") with { CombatId = "damage-anonymous", FaceDown = true, Text = "" };
        var state = Table("allocation", [source], opposing: [marked, second, anonymous]);
        Choice Recipient(int index, Card card, int lethal) => new(index, card.Name, card) { Id = "damage-choice-" + index,
            CardId = card.CombatId, ControllerId = 1, OwnerId = 1, Minimum = 0, Maximum = 10, Lethal = lethal };
        return new[] { new ExperienceStage("Assign damage", "Inspect all current stats, stage required blocker amounts, then assign the rest to the defender.", state with {
            Phase = "Combat damage", PhaseKey = "COMBAT_DAMAGE", Decision = new() {
                Kind = "allocate", Title = "Assign combat damage", AllocationKind = "combat", AmountLabel = "combat damage", Amount = 10,
                SourceCard = source, SourceCardId = source.VisualId, SourceControllerId = 0, OverrideOrder = true,
                Message = "Scripted allocation preview: 10 damage, with engine-shaped blocker thresholds. This does not simulate combat rules.",
                Choices = [Recipient(0, marked, 2), Recipient(1, second, 1), Recipient(2, anonymous, 2),
                    new(3, "Opponent 1", null) { Id = "damage-defender", PlayerId = 1, IsDefender = true, Minimum = 0, Maximum = 10 }]
            }
        }) };
    }
    private static IReadOnlyList<ExperienceStage> PublicStates()
    {
        var visible = Body("public-state-body", "Public ward creature") with { States = [
            new("ward-two", "Ward {2}", "ability", 2, null, "Two effective instances of this public ability.", "ward"),
            new("protection-blue", "Protection from blue", "ability", 1, null, "The color qualifier is part of this public ability.", "protection")
        ] };
        var anonymous = Body("", "Face-down card") with { CombatId = "state-anonymous", FaceDown = true, Text = "", States = [
            new("public-ward", "Ward {3}", "ability", 1, null, "Only a current public characteristic is shown.", "ward")
        ] };
        var hand = Body("state-hand", "Public hand preview") with { Selectable = true };
        var state = Table("public-state", [visible], [hand], [anonymous]) with {
            States = [new("day-night", "Day", "status", null, null, "It is day for the whole table.", "day-night")]
        };
        state = state with { Players = state.Players.Select(player => player.Id != 0 ? player : player with {
            Mana = ImmutableDictionary<string, int>.Empty.Add("C", 2), States = [new("speed", "Speed", "progress", 4, 4, "The public engine-declared maximum."),
                new("future-track", "Momentum", "progress", 6, 6, "An unfamiliar resource with its own declared maximum.")]
        }).ToImmutableArray() };
        var night = state with { States = [new("day-night", "Night", "status", null, null, "It is night for the whole table.", "day-night")],
            Players = state.Players.Select(player => player.Id != 0 ? player : player with { Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone
                : zone with { Count = 0, Cards = [] }).ToImmutableArray(), Mana = ImmutableDictionary<string, int>.Empty.Add("U", 3) }).ToImmutableArray() };
        return new[] {
            new ExperienceStage("Qualified public state", "Inspect repeated Ward, protection, an anonymous permanent, maximum Speed and an unfamiliar maximum of six.", state),
            new ExperienceStage("Night and visibility revoked", "The formerly visible hand occurrence disappears. Opponents' private hands were never supplied.", night),
            new ExperienceStage("Public state ended", "Removed table and player state must leave no stale labels or controls.", night with {
                States = [], Players = night.Players.Select(player => player with { States = [] }).ToImmutableArray()
            })
        };
    }
    private static IReadOnlyList<ExperienceStage> StackPriority()
    {
        var sage = Body("stack-sage", "Reclamation Sage", power: 2, toughness: 1);
        var ring = Body("stack-ring", "Sol Ring", "Artifact", null, null);
        var bear = Body("stack-bear", "Grizzly Bears");
        var growth = Body("stack-growth-card", "Giant Growth", "Instant", null, null);
        var intervention = Body("stack-intervention-card", "Heroic Intervention", "Instant", null, null);
        var state = Table("stack-priority", [sage], opposing: [ring, bear]) with {
            PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            Decision = new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true, CanAutoPass = false,
                Message = "Scripted trigger window: you have priority. Next previews the opponent's response." }
        };
        var trigger = new StackItem("Reclamation Sage", "Destroy target artifact or enchantment.", true, sage) {
            Id = "stack-sage-trigger", ControllerId = 0, Targets = [new("card", ring.VisualId, ring.Name, 1)]
        };
        var response = new StackItem("Giant Growth", "Target creature gets +3/+3 until end of turn.", false, growth) {
            Id = "stack-opponent-response", ControllerId = 1, Targets = [new("card", bear.VisualId, bear.Name, 1)]
        };
        var local = new StackItem("Heroic Intervention", "Permanents you control gain hexproof and indestructible until end of turn.", false, intervention) {
            Id = "stack-local-response", ControllerId = 0, Targets = []
        };
        var single = state with { Stack = [trigger] };
        var opponent = state with { Stack = [response, trigger], PriorityPlayerId = 1, DecisionOwnerId = 1, Decision = null };
        var own = state with { Stack = [local, response, trigger], Decision = state.Decision! with {
            Message = "Scripted response window: your Heroic Intervention is on top. You have priority; Next previews resolution."
        } };
        var resolving = state with { Status = "resolving", Stack = [response, trigger], PriorityPlayerId = null,
            DecisionOwnerId = null, Decision = null, ActionClass = "resolving", Activity = [
                new(1, 4, "MAIN1", "resolved", 0, "Heroic Intervention resolved.", intervention.VisualId, intervention.Name) {
                    Outcome = "resolved", Detail = local.Text
                }
            ] };
        var completed = state with { Stack = [], Decision = state.Decision! with { Message = "Scripted sequence complete. Inspect the changed battlefield; Reset replays this example." },
            Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name switch {
                "Battlefield" => zone with { Count = player.Id == 0 ? 1 : player.Id == 1 ? 1 : 0,
                    Cards = player.Id == 0 ? [sage with { CombatKeywords = ["hexproof", "indestructible"] }]
                        : player.Id == 1 ? [bear with { Power = 5, Toughness = 5 }] : [] },
                "Graveyard" => zone with { Count = player.Id == 0 ? 1 : player.Id == 1 ? 2 : 0,
                    Cards = player.Id == 0 ? [intervention] : player.Id == 1 ? [growth, ring] : [] },
                _ => zone
            }).ToImmutableArray() }).ToImmutableArray(),
            Activity = [.. resolving.Activity,
                new(2, 4, "MAIN1", "resolved", 1, "Giant Growth resolved.", growth.VisualId, growth.Name) { Outcome = "resolved", Detail = response.Text },
                new(3, 4, "MAIN1", "resolved", 0, "Reclamation Sage's ability resolved.", sage.VisualId, sage.Name) { Outcome = "resolved", Detail = trigger.Text }]
        };
        return new[] {
            new ExperienceStage("Trigger · your priority", "One public trigger targets the opponent's Sol Ring. You currently hold priority.", single),
            new ExperienceStage("Opponent's response and priority", "Giant Growth is on top; the opponent holds priority. Their private decision is absent.", opponent),
            new ExperienceStage("Your response on top", "Your Heroic Intervention is newest. The stack resolves from its first item downward.", own),
            new ExperienceStage("Resolving", "The first spell has resolved. No player priority or private decision is reported during this authored transition.", resolving),
            new ExperienceStage("Completed", "The stack is empty; the Bear is 5/5, your Sage is protected, and Sol Ring is in its owner's graveyard.", completed)
        };
    }
    private static IReadOnlyList<ExperienceStage> TriggerSearch()
    {
        var source = Body("search-source", "Search trigger (scripted)", "Enchantment", null, null) with {
            Text = "Authored presentation fixture for a public trigger, response windows, required recipient selection and a private library search. It does not define a Magic card's rules or timing."
        };
        var state = Table("trigger-search", [source]) with {
            ActivePlayerId = 2, PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority", Decision = new() {
                Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true,
                Message = "Scripted public trigger: passing priority is separate from completing a required search choice. Use Next to inspect each handoff."
            }
        };
        var trigger = new StackItem(source.Name, "A public trigger is pending. Required recipient and library decisions are separate from priority.", true, source) {
            Id = "search-pending-trigger", ControllerId = 0
        };
        var pending = state with { Stack = [trigger] };
        var other = pending with { PriorityPlayerId = 1, DecisionOwnerId = 1, Decision = null };
        var recipient = state with { PriorityPlayerId = null, DecisionOwnerId = 0, ActionClass = "selection", Decision = new() {
            Kind = "input", Intent = DecisionIntent.Selection, PlayerChoices = [0, 1, 2, 3], SourceCard = source,
            SourceCardId = source.VisualId, SourceZone = "Battlefield", SourceControllerId = 0,
            Message = "Choose a player. That player searches their library for a card."
        } };
        Card[] library = [Body("", "Forest", "Basic Land", null, null), Body("", "Island", "Basic Land", null, null), Body("", "Plains", "Basic Land", null, null)];
        var search = state with { PriorityPlayerId = null, DecisionOwnerId = 0, ActionClass = "choice", Decision = new() {
            Kind = "choice", Title = "Search your library", Min = 1, Max = 1,
            SourceCard = source, SourceCardId = source.VisualId, SourceZone = "Battlefield", SourceControllerId = 0,
            Message = "Choose a card to put into your hand.",
            Choices = library.Select((card, index) => new Choice(index, card.Name, card)).ToImmutableArray(),
            LibraryCards = library.Select((card, index) => new LibraryChoice(index, card.Name, card)).ToImmutableArray()
        } };
        var completed = state with { Players = state.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name switch {
                "Hand" => zone with { Count = 1, Cards = [library[0] with { VisualId = "searched-card", CombatId = "searched-card" }] },
                "Library" => zone with { Count = zone.Count - 1 }, _ => zone
            }).ToImmutableArray()
        }).ToImmutableArray(), Decision = state.Decision! with { Message = "Scripted search complete. The selected card is now in your hand. No real deck was changed." },
            Activity = [new(1, 4, "MAIN1", "moved", 0, "You moved a card from library to hand.", null, null)] };
        return new[] {
            new ExperienceStage("Public trigger pending", "This generic authored trigger demonstrates the UI handoff; individual Magic timing is owned by Forge.", pending),
            new ExperienceStage("Another player has priority", "No private prompt is exposed while the opponent holds priority.", other),
            new ExperienceStage("Required recipient selection", "Passing priority cannot substitute for this required choice. Choose You or inspect the opponent privacy path.", recipient),
            new ExperienceStage("Required library choice", "Select one permitted face. Search cards have prompt indices, without persistent library handles.", search),
            new ExperienceStage("Selected card in hand", "The authored search result is visible only in your own hand.", completed)
        };
    }
}
