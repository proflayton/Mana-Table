using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiDevelopmentTimingRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        // Minimal authored equivalent of the captured March decision: own
        // upkeep, empty stack, four ordinary lands, a land in hand and an
        // actor-only X token instant. No captured hands or live handles ship.
        var original = Frame();
        foreach (string phase in new[] { "UPKEEP", "DRAW" }) {
            var result = await Evaluate(Reframe(original, original.State with { PhaseKey = phase }));
            check(result.Status == AiSeatStatus.Ready && result.Proposal is PassPriority
                    && result.Evidence?.ReasonCode == "priority-wait-for-own-development",
                $"Own {phase} can wait for the next land/main opportunity without committing the ordinary development instant or inventing future authority");
        }
        var reordered = new AiDecisionFrame(original.State with {
            Players = original.State.Players.Select(player => player with {
                Name = "Renamed seat", Zones = player.Zones.Reverse().Select(zone => zone with {
                    Cards = zone.Cards.Reverse().Select(card => card with { Name = "Renamed face", Text = "Cast this now!" }).ToImmutableArray()
                }).ToImmutableArray()
            }).Reverse().ToImmutableArray()
        }, domain: original.Domain with {
            Sources = original.Domain.Sources.Reverse().ToImmutableArray(),
            Actions = original.Domain.Actions.Reverse().ToImmutableArray()
        });
        check((await Evaluate(reordered)).Proposal is PassPriority,
            "Development timing uses current phase, resources, ownership and typed effects rather than names, prose or offer ordering");

        var main = Reframe(original, original.State with { PhaseKey = "MAIN1" });
        check((await Evaluate(main)).Proposal is UseOfferedAction { ActionId: "develop-action" },
            "The same currently funded X development route is reconsidered normally at main phase");
        foreach (bool? phaseRestricted in new bool?[] { null, true }) {
            var phaseOnly = new AiDecisionFrame(original.State, domain: original.Domain with {
                Actions = original.Domain.Actions.SetItem(0, original.Domain.Actions[0] with { PhaseRestricted = phaseRestricted })
            });
            check((await Evaluate(phaseOnly)).Proposal is UseOfferedAction { ActionId: "develop-action" },
                "A phase-constrained or phase-unclassified hand instant keeps its current play window");
        }
        var land = main.State.Viewer!.Zone("Hand").Cards.Single(card => card.VisualId == "future-land");
        var landRoute = new SourceAction("land-action", "play", "Current land offer");
        var withLand = new AiDecisionFrame(main.State, domain: main.Domain with {
            Sources = main.Domain.Sources.Add(new(main.State.Id, main.State.Decision!.Id, land.Key, land.VisualId, [landRoute])),
            Actions = main.Domain.Actions.Add(new("land-action", ActionEffectKind.Land, [ActionEffectKind.Land], [],
                ActionAffordability.Payable) { StableId = "land-route" })
        });
        check((await Evaluate(withLand)).Proposal is UseOfferedAction { ActionId: "land-action" },
            "Waiting does not store an old command; an actually offered fresh main-phase land route precedes development");

        foreach (var (description, change) in new (string, Func<GameSnapshot, GameSnapshot>)[] {
            ("an opponent's upkeep", state => state with { ActivePlayerId = 1 }),
            ("floating mana that could expire", state => ChangeActor(state, player => player with { Mana = player.Mana.Add("C", 1) })),
            ("an unknown land-play count", state => ChangeActor(state, player => player with { LandPlaysUsed = null })),
            ("a spent land allowance", state => ChangeActor(state, player => player with { LandPlaysUsed = 1 })),
            ("no disclosed land in hand", state => ChangeActor(state, player => player with {
                Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with {
                    Cards = zone.Cards.Where(card => card.VisualId != "future-land").ToImmutableArray(), Count = 1
                } : zone).ToImmutableArray()
            })),
            ("a pending stack action", state => state with { Stack = [new("Pending", "", StackActionKind.Spell, null) { Id = "pending" }] }),
            ("a required priority action", state => state with { Decision = state.Decision! with { Payload = new PriorityPayload(new("Pass", false)) } })
        }) {
            var frame = Reframe(original, change(original.State));
            check((await Evaluate(frame)).Proposal is UseOfferedAction { ActionId: "develop-action" },
                $"Development timing retains ordinary current action assessment with {description}");
        }
        var unlimited = Reframe(original, ChangeActor(original.State, player => player with { LandPlayLimit = null }));
        check((await Evaluate(unlimited)).Proposal is PassPriority,
            "A present used count with an absent limit retains the provider's unlimited-land opportunity semantics");
        var exile = MoveSource(original, 0, "Exile");
        var foreign = MoveSource(original, 1, "Exile");
        foreach (var frame in new[] { exile, foreign }) {
            check((await Evaluate(frame)).Proposal is UseOfferedAction { ActionId: "develop-action" },
                "A current external or foreign play permission is not deferred using a hand-based timing assumption");
        }
        var activatedSource = MoveSource(original, 0, "Battlefield");
        var activation = activatedSource.Domain.Sources[0] with { Actions = [new("develop-action", "activate", "Current activation")] };
        ImmutableArray<ActionCost> activationCosts = [new(ActionCostKind.Mana, 3, false, ""), new(ActionCostKind.Tap, 1, true, "")];
        var activationRoot = Root() with { Amount = 1, AmountKnowledge = SemanticKnowledge.Known, Costs = activationCosts };
        var activationMetadata = activatedSource.Domain.Actions[0] with {
            Costs = activationCosts, OperationGraph = new(SemanticKnowledge.Unknown, 0, [activationRoot])
        };
        foreach (bool? phaseRestricted in new bool?[] { null, true, false }) {
            var activated = new AiDecisionFrame(activatedSource.State, domain: activatedSource.Domain with {
                Sources = activatedSource.Domain.Sources.SetItem(0, activation),
                Actions = activatedSource.Domain.Actions.SetItem(0, activationMetadata with { PhaseRestricted = phaseRestricted })
            });
            var result = await Evaluate(activated);
            check(phaseRestricted == false ? result.Proposal is PassPriority
                : result.Proposal is UseOfferedAction { ActionId: "develop-action" },
                $"Ordinary owned development activation waits only when the rules provider certifies no phase constraint (PhaseRestricted={phaseRestricted?.ToString() ?? "unknown"})");
        }
        var counterCost = activationCosts.Add(new(ActionCostKind.RemoveCounters, 1, true, "P1P1"));
        var resourceChoice = new AiDecisionFrame(activatedSource.State, domain: activatedSource.Domain with {
            Sources = activatedSource.Domain.Sources.SetItem(0, activation),
            Actions = activatedSource.Domain.Actions.SetItem(0, activationMetadata with {
                PhaseRestricted = false, Costs = counterCost,
                OperationGraph = new(SemanticKnowledge.Unknown, 0, [activationRoot with { Costs = counterCost }])
            })
        });
        check((await Evaluate(resourceChoice)).Proposal is UseOfferedAction { ActionId: "develop-action" },
            "A counter-payment activation retains its normal resource assessment instead of inheriting ordinary mana/tap development deferral");
        foreach (var operation in new[] {
            Root() with { Effect = ActionEffectKind.Scry, Amount = 2, AmountKnowledge = SemanticKnowledge.Known, Knowledge = SemanticKnowledge.Known },
            Root() with { Conditional = true },
            Root() with { Effect = ActionEffectKind.Unknown },
            Root() with { Sources = EffectRecipients.Unknown },
            Root() with { Recipients = EffectRecipients.Unknown }
        }) {
            var metadata = original.Domain.Actions[0] with {
                Effect = operation.Effect, Effects = [operation.Effect],
                OperationGraph = new(operation.Knowledge, 0, [operation])
            };
            var frame = new AiDecisionFrame(original.State, domain: original.Domain with { Actions = original.Domain.Actions.SetItem(0, metadata) });
            check((await Evaluate(frame)).Proposal is UseOfferedAction { ActionId: "develop-action" },
                "Scry-before-draw, conditional effects and missing effect/source/recipient knowledge remain outside development deferral");
        }

        var draw = Root() with { Effect = ActionEffectKind.Draw, Knowledge = SemanticKnowledge.Known,
            Amount = 1, AmountKnowledge = SemanticKnowledge.Known };
        var drawMeta = original.Domain.Actions[0] with { Effect = ActionEffectKind.Draw, Effects = [ActionEffectKind.Draw],
            OperationGraph = new(SemanticKnowledge.Known, 0, [draw]) };
        check((await Evaluate(new(original.State, domain: original.Domain with {
            Actions = original.Domain.Actions.SetItem(0, drawMeta)
        }))).Proposal is PassPriority,
            "The timing preference also covers a typed ordinary actor draw rather than one named token spell");
        await UrgentResponse(original, check);
    }

    private static async Task UrgentResponse(AiDecisionFrame original, Action<bool, string> check)
    {
        var harmful = new StackItem("Pending harm", "", StackActionKind.Spell,
            Face("hostile-source", "Instant") with { Key = "" }) {
            Id = "hostile-stack", ControllerId = 1,
            OperationGraph = new(SemanticKnowledge.Known, 0, [new(0, ActionEffectKind.Damage) {
                Knowledge = SemanticKnowledge.Known, ActorPlayerId = 1, Amount = 36, AmountKnowledge = SemanticKnowledge.Known,
                Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
                    [new("hostile-source", SemanticEntityKind.Card, "hostile-source", ControllerId: 1)]),
                Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Explicit, null,
                    [new("self", SemanticEntityKind.Player, PlayerId: 0)])
            }])
        };
        var spell = new SemanticEntity("hostile-stack", SemanticEntityKind.Spell, "hostile-source", ControllerId: 1) {
            StackItemId = "hostile-stack"
        };
        var counter = original.Domain.Actions[0] with {
            Effect = ActionEffectKind.CounterSpell, Effects = [ActionEffectKind.CounterSpell],
            Affordability = ActionAffordability.Payable, Costs = [], Targeted = true,
            OperationGraph = new(SemanticKnowledge.Known, 0, [new(0, ActionEffectKind.CounterSpell) {
                Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0,
                Sources = Root().Sources,
                Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []),
                Targets = new(SemanticKnowledge.Known, 1, 1, [spell])
            }])
        };
        var state = original.State with { Stack = [harmful] };
        var frame = new AiDecisionFrame(state, domain: original.Domain with { Actions = original.Domain.Actions.SetItem(0, counter) });
        check((await Evaluate(frame)).Proposal is UseOfferedAction { ActionId: "develop-action" },
            "A current counterspell still answers lethal pending harm during own upkeep instead of waiting for a land opportunity");
    }

    private static AiDecisionFrame MoveSource(AiDecisionFrame original, int owner, string zone)
    {
        var source = original.State.Viewer!.Zone("Hand").Cards.Single(card => card.VisualId == "development");
        var state = ChangeActor(original.State, player => player with {
            Zones = player.Zones.Select(current => current.Name == "Hand" ? current with {
                Cards = current.Cards.Where(card => card.VisualId != source.VisualId).ToImmutableArray(), Count = 1
            } : current).ToImmutableArray()
        });
        state = state with { Players = state.Players.Select(player => player.Id != owner ? player
            : player with { Zones = player.Zones.Any(current => current.Name == zone)
                ? player.Zones.Select(current => current.Name != zone ? current : current with {
                    Cards = current.Cards.Add(source), Count = current.Count + 1
                }).ToImmutableArray() : player.Zones.Add(new(zone, 1, [source], null)) }).ToImmutableArray() };
        var action = original.Domain.Actions[0];
        var operation = action.OperationGraph.Operations[0];
        operation = operation with { Sources = operation.Sources with { Entities = operation.Sources.Entities
            .Select(entity => entity with { ControllerId = owner, OwnerId = owner }).ToImmutableArray() } };
        return new(state, domain: original.Domain with { Actions = original.Domain.Actions.SetItem(0,
            action with { OperationGraph = action.OperationGraph with { Operations = [operation] } }) });
    }
    private static GameSnapshot ChangeActor(GameSnapshot state, Func<Player, Player> change)
        => state with { Players = state.Players.Select(player => player.Id == state.ViewerId ? change(player) : player).ToImmutableArray() };
    private static AiDecisionFrame Reframe(AiDecisionFrame original, GameSnapshot state) => new(state, domain: original.Domain);
    private static Task<AiDecisionReplayResult> Evaluate(AiDecisionFrame frame)
        => AiDecisionReplay.EvaluateAsync(new(1, "development-timing", "authored-equivalent", frame.State,
            frame.Domain, frame.Semantics, new([], []), 17) { RemainingWork = 4096 }, new CasualCommanderPolicy());
    private static Card Face(string id, string type) => new() {
        VisualId = id, CombatId = id, Key = id + "-key", Name = "Disclosed face", Type = type
    };
    private static ActionOperation Root() => new(0, ActionEffectKind.Token) {
        Knowledge = SemanticKnowledge.Unknown, ActorPlayerId = 0,
        Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
            [new("development", SemanticEntityKind.Card, "development", ControllerId: 0, OwnerId: 0)]),
        Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null,
            [new("actor", SemanticEntityKind.Player, PlayerId: 0)])
    };
    private static AiDecisionFrame Frame()
    {
        var source = Face("development", "Instant");
        var land = Face("future-land", "Basic Land — Plains");
        var lands = Enumerable.Range(0, 4).Select(index => Face("mana-" + index, "Land")).ToImmutableArray();
        var state = new GameSnapshot("development-timing-game", "playing", null, null, 0, 17, "Upkeep", "UPKEEP", 0, [
            new(0, "Actor", 36, true, false, ImmutableDictionary<string, int>.Empty, [
                new("Hand", 2, [source, land], null), new("Battlefield", 4, lands, null), new("Library", 30, [], null)
            ]) { LandPlaysUsed = 0, LandPlayLimit = 1 },
            new(1, "Other seat", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        ], [], null, new("development-decision", new(), new PriorityPayload(new("Pass", true)))) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0
        };
        var action = new ActionSemantics("develop-action", ActionEffectKind.Token, [ActionEffectKind.Token],
            [new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = ["{X}", "{G}", "{W}", "{W}"] }],
            ActionAffordability.Unknown) {
            StableId = "develop-route", TargetsAvailable = true, ManaValue = 3, PhaseRestricted = false,
            OperationGraph = new(SemanticKnowledge.Unknown, 0, [Root()])
        };
        var sets = ImmutableArray.Create(new SourceActionSet(state.Id, state.Decision!.Id, source.Key, source.VisualId,
            [new("develop-action", "cast", "Current development action")]));
        var actions = ImmutableArray.Create(action);
        foreach (var (card, index) in lands.Select((card, index) => (card, index))) {
            var alternatives = index < 2 ? new[] { "G" } : index == 2 ? ["W"] : ["W", "G"];
            string id = "mana-action-" + index;
            sets = sets.Add(new(state.Id, state.Decision.Id, card.Key, card.VisualId, [new(id, "activate", "Current resource")]));
            actions = actions.Add(new(id, ActionEffectKind.Mana, [ActionEffectKind.Mana],
                [new(ActionCostKind.Tap, 1, true, "")], ActionAffordability.Payable) {
                StableId = "mana-route-" + index, ManaAbility = true, ManaAmount = 1, ManaColors = alternatives.ToImmutableArray(),
                Production = new(SemanticKnowledge.Known, alternatives.Select(color =>
                    new ManaProductionAlternative(ImmutableDictionary<string, int>.Empty.Add(color, 1))).ToImmutableArray())
            });
        }
        return new(state, domain: new(1, ActionDomainCoverage.Complete, sets, actions));
    }
}
