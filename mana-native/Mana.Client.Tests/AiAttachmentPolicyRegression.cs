using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiAttachmentPolicyRegression
{
    private static readonly AttachmentModifier Pump = new(SemanticKnowledge.Known, 2, 0, ["trample"]);
    private static readonly DecisionReply Yes = new("Allowed", true), No = new("Unavailable", false);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = State();
        var positive = Estimate(state, Pump, "own");
        check(positive is { Status: CasualAssessmentStatus.Estimated, Value: > 0 }
                && Math.Abs(positive.Value - 2.5) < .00001,
            "A known attachment prices only its +2 power and newly granted trample on the current recipient");
        check(Estimate(state, Pump, "enemy") is { Status: CasualAssessmentStatus.Estimated, Value: < 0 },
            "The same beneficial attachment has negative utility on an opposing recipient instead of pricing the Aura body");
        check(Estimate(Replace(state, "aura", card => card with { Power = 100, Toughness = 100 }), Pump, "own") is { Value: 2.5 },
            "The attachment's source characteristics cannot add a second permanent body to recipient utility");
        foreach (string keyword in new[] { "flying", "trample", "reach", "vigilance", "lifelink", "deathtouch", "first strike", "double strike", "indestructible" }) {
            var modifier = new AttachmentModifier(SemanticKnowledge.Known, 0, 0, [keyword]);
            var existing = Replace(state, "own", card => card with { States = [Ability(keyword)] });
            check(Estimate(state, modifier, "own") is { Status: CasualAssessmentStatus.Estimated, Value: > 0 }
                    && Estimate(existing, modifier, "own") is { Status: CasualAssessmentStatus.Estimated, Value: 0 },
                $"Granting {keyword} has marginal value only when the recipient does not already have that effective ability");
        }
        var stalePrinted = Replace(state, "own", card => card with { CombatKeywords = ["trample"], States = [] });
        check(Estimate(stalePrinted, Pump, "own") is { Value: 2.5 },
            "Authoritatively absent effective trample overrides a stale printed keyword when evaluating its new grant");
        var curse = new AttachmentModifier(SemanticKnowledge.Known, -2, -2, []);
        check(Estimate(state, curse, "own") is { Value: < 0 } && Estimate(state, curse, "enemy") is { Value: > 0 },
            "Signed negative attachment modifiers distinguish harming our creature from weakening an opponent's creature");
        var mixed = new AttachmentModifier(SemanticKnowledge.Known, 10, -2, []);
        check(Estimate(state, mixed, "own") is { Value: < 0 },
            "A large power increase cannot make reducing our creature to zero toughness appear beneficial");
        var indestructible = Replace(state, "own", card => card with { States = [Ability("indestructible")] });
        check(Estimate(indestructible, mixed, "own") is { Value: < 0 },
            "Existing indestructible does not turn zero resulting toughness into a surviving positive body estimate");
        var marked = Replace(state, "own", card => card with { Power = 6, Toughness = 5, Damage = 4 });
        check(Estimate(marked, mixed, "own") is { Value: < 0 }
                && Estimate(marked, mixed with { GrantedKeywords = ["indestructible"] }, "own") is { Value: > 0 },
            "The nominal attachment estimate accounts for marked lethal damage and a simultaneously granted indestructible ability");
        check(Estimate(state, mixed with { GrantedKeywords = ["indestructible"] }, "own") is { Value: < 0 },
            "Newly granted indestructible still cannot rescue zero toughness in the nominal recipient estimate");
        check(Estimate(Replace(state, "own", card => card with { Power = null }), Pump, "own").Status == CasualAssessmentStatus.Incomplete
                && Estimate(Replace(state, "own", card => card with { Power = int.MaxValue }), Pump, "own").Status == CasualAssessmentStatus.Incomplete,
            "Missing or overflowing current characteristics remain unassessed rather than inventing a recipient body");

        foreach (long seed in new long[] { 0, 1, 17, 91 })
        foreach (bool reverse in new[] { false, true }) {
            var reordered = reverse ? state with { Players = state.Players.Reverse().Select(player => player with {
                Name = "Unrelated seat prose", Zones = player.Zones.Select(zone => zone with {
                    Cards = zone.Cards.Reverse().Select(card => card with { Name = "Different printed name", Text = "Unrelated translated text" }).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray() } : state;
            check(await Answer(reordered, Semantics(reordered, Pump), seed) is SelectCard { Card: "own:current" },
                $"Beneficial attachment targeting selects our current creature across seed {seed} and enumeration/prose changes ({reverse})");
            check(await Answer(reordered, Semantics(reordered, curse), seed) is SelectCard { Card: "enemy:current" },
                $"Harmful attachment targeting selects an opposing current creature across seed {seed} and enumeration/prose changes ({reverse})");
        }
        var duplicate = Replace(state, "enemy", card => card with { Power = 30, Toughness = 30 });
        duplicate = duplicate with { Players = duplicate.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Count = 2, Cards = [zone.Cards[0] with { States = [Ability("trample")] }, Body("new-recipient", 1, 1)]
            }).ToImmutableArray() }).ToImmutableArray() };
        check(await Answer(duplicate, Semantics(duplicate, Pump)) is SelectCard { Card: "new-recipient:current" },
            "Recipient utility prefers adding a new effective keyword rather than choosing a larger already-equipped body");
        await PartiallyAssessedRecipients(check);
        var enemyOnly = Without(state, "own");
        check(await Answer(enemyOnly, Semantics(enemyOnly, Pump)) is CancelSelection,
            "A known beneficial attachment cancels when every current legal recipient would benefit an opponent");
        var friendlyOnly = Without(state, "enemy");
        check(await Answer(friendlyOnly, Semantics(friendlyOnly, curse)) is CancelSelection,
            "A known harmful attachment cancels rather than sacrificing our only current target for required completion");
        var forced = WithReplies(enemyOnly, cancel: false);
        check(await Answer(forced, Semantics(forced, Pump)) is SelectCard { Card: "enemy:current" },
            "A genuinely required no-cancel target remains an authorized legal answer even when its known utility is negative");
        var unknown = new DecisionSemantics();
        check(await Answer(state, unknown) is CancelSelection,
            "An unassessed non-cost target uses the current cancellation permission instead of a seeded legal recipient");
        check(await Answer(state, Semantics(state, AttachmentModifier.Unknown) with { Intent = DecisionIntent.Benefit }) is CancelSelection,
            "A legacy benefit label cannot turn an explicitly unknown attachment descriptor into understood recipient utility");
        var unknownForced = WithReplies(state, cancel: false);
        var forcedAnswer = await Answer(unknownForced, unknown);
        check(forcedAnswer is SelectCard && new AiDecisionFrame(unknownForced).Allows(forcedAnswer),
            "An unassessed required target without cancellation still progresses using exact current legal authority");
        var optional = WithReplies(state, complete: true);
        check(await Answer(optional, unknown) is CompleteSelection,
            "An unknown optional target completes the already legal selection instead of cancelling or adding a recipient");
        check(await Answer(state, new DecisionSemantics { SelectionIsCost = true }) is SelectCard { Card: "own:current" },
            "A selection cost retains its opportunity-cost policy rather than inheriting the unknown-effect cancellation guard");

        var allied = Without(state, "own") with { Players = Without(state, "own").Players.Select(player => player with { TeamId = 7 }).ToImmutableArray() };
        check(await Answer(allied, Semantics(allied, Pump)) is SelectCard { Card: "enemy:current" },
            "The attachment estimate supports a rules-owned ally rather than inferring hostility from the seat number");
        var unknownRelation = allied with { Players = allied.Players.Select(player => player.Id == 1 ? player with { TeamId = null } : player).ToImmutableArray() };
        check(await Answer(unknownRelation, Semantics(unknownRelation, Pump)) is CancelSelection,
            "An unknown team relationship cannot establish a positive attachment contribution");

        await Initiation(check);
        var oldFrame = new AiDecisionFrame(state, semantics: Semantics(state, Pump));
        var chosen = await Answer(state, Semantics(state, Pump));
        var fresh = state with { Decision = state.Decision! with { Id = "replacement-input" }, Revision = 2 };
        check(chosen != null && oldFrame.Allows(chosen) && !new AiDecisionFrame(fresh, semantics: Semantics(fresh, Pump)).Allows(chosen),
            "Attachment semantics cannot make a proposal from a retired input executable in its replacement scope");
        check(CasualActionAssessment.Evaluate(state, Graph(state, Pump), 1).Status == CasualAssessmentStatus.BudgetExceeded,
            "Attachment comparison preserves finite work accounting rather than falling through to an invented estimate");
    }

    private static async Task PartiallyAssessedRecipients(Action<bool, string> check)
    {
        var reach = new AttachmentModifier(SemanticKnowledge.Known, 0, 0, ["reach"]);
        foreach (bool faceDown in new[] { false, true })
        foreach (var intent in new[] { DecisionIntent.Unknown, DecisionIntent.Benefit })
        foreach (bool cancel in new[] { false, true })
        foreach (bool reverse in new[] { false, true }) {
            var state = State();
            var unassessed = Body("unassessed", 20, 20) with { FaceDown = faceDown, Power = faceDown ? 20 : null };
            state = state with { Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Count = 2, Cards = reverse ? [unassessed, ..zone.Cards] : [..zone.Cards, unassessed]
                }).ToImmutableArray()
            }).ToImmutableArray() };
            state = WithReplies(state, cancel: cancel);
            var answer = await Answer(state, Semantics(state, reach) with { Intent = intent });
            check(answer is SelectCard { Card: "own:current" } && new AiDecisionFrame(state, semantics: Semantics(state, reach)).Allows(answer),
                $"A known reach grant worth 0.3 outranks an unassessed recipient's larger fallback score (faceDown {faceDown}, intent {intent}, cancel {cancel}, reversed {reverse})");
        }
        var unknownOnly = Replace(Without(State(), "enemy"), "own", card => card with { Power = null });
        check(await Answer(unknownOnly, Semantics(unknownOnly, reach)) is CancelSelection,
            "With no assessed attachment recipient, cancellation remains available rather than inventing positive utility");
        unknownOnly = WithReplies(unknownOnly, cancel: false);
        check(await Answer(unknownOnly, Semantics(unknownOnly, reach)) is SelectCard { Card: "own:current" },
            "With no assessed recipient and no cancellation, recognized attachment targeting retains its current legal fallback");
    }

    private static async Task Initiation(Action<bool, string> check)
    {
        var state = State() with { Decision = new("attachment-priority", new(), new PriorityPayload(Yes)) };
        check(await Offer(state, ActionEffectKind.Attach, Graph(state, Pump)) is UseOfferedAction,
            "A supported attachment with a positive legal recipient remains a useful offered cast");
        var enemyOnly = Without(state, "own");
        check(await Offer(enemyOnly, ActionEffectKind.Attach, Graph(enemyOnly, Pump)) is PassPriority,
            "Attachment initiation evaluates actual recipients instead of granting every cast a positive exploration value");
        foreach (var kind in new[] { ActionEffectKind.Unknown, ActionEffectKind.Attach }) {
            var unmodeled = Graph(state, AttachmentModifier.Unknown);
            check(await Offer(state, kind, unmodeled) is PassPriority,
                $"An unmodeled targeted {kind} cast cannot receive generic exploration value before its recipient is understood");
        }
        check(await Offer(state, ActionEffectKind.Unknown, ActionOperationGraph.Unknown, targeted: false) is UseOfferedAction,
            "Ordinary untargeted incomplete cast exploration remains available");
        check(await Offer(state, ActionEffectKind.Damage, ActionOperationGraph.Unknown) is UseOfferedAction,
            "The unknown-attachment guard preserves the existing known damage-direction cast fallback");
    }

    private static CasualActionEstimate Estimate(GameSnapshot state, AttachmentModifier modifier, string recipient)
    {
        var graph = Graph(state, modifier);
        graph = graph with { Operations = [graph.Operations[0] with {
            Targets = new(SemanticKnowledge.Known, 1, 1, [Entity(state, recipient)])
        }] };
        return CasualActionAssessment.Evaluate(state, graph, 512, CardViews.FindZoneOccurrence(state, "aura")!.Value.Card);
    }

    private static DecisionSemantics Semantics(GameSnapshot state, AttachmentModifier modifier)
        => new() { OperationId = 0, OperationGraph = Graph(state, modifier) };

    private static ActionOperationGraph Graph(GameSnapshot state, AttachmentModifier modifier)
    {
        var candidates = state.Players.SelectMany(player => player.Zone("Battlefield").Cards.Select(card =>
            new SemanticEntity(card.VisualId, SemanticEntityKind.Card, card.VisualId, ControllerId: player.Id, OwnerId: player.Id))).ToImmutableArray();
        return new(modifier.Knowledge, 0, [new(0, ActionEffectKind.Attach) {
            Knowledge = modifier.Knowledge, Attachment = modifier, ActorPlayerId = 0,
            Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null, [Entity(state, "aura")]),
            Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []),
            Targets = new(SemanticKnowledge.Known, 1, 1, candidates)
        }]);
    }

    private static SemanticEntity Entity(GameSnapshot state, string id)
    {
        var occurrence = CardViews.FindZoneOccurrence(state, id)!.Value;
        return new(id, SemanticEntityKind.Card, id, ControllerId: occurrence.PlayerId, OwnerId: occurrence.PlayerId);
    }

    private static async Task<DecisionCommand?> Offer(GameSnapshot state, ActionEffectKind kind, ActionOperationGraph graph, bool targeted = true)
    {
        var source = CardViews.FindZoneOccurrence(state, "aura")!.Value.Card;
        var action = new SourceAction("attachment-route", "cast", "Untrusted display prose");
        var semantic = new ActionSemantics(action.Id, kind, [kind], [], ActionAffordability.Payable) {
            StableId = "stable-attachment-route", Targeted = targeted, OperationGraph = graph
        };
        var domain = new DecisionActionDomain(1, ActionDomainCoverage.Complete,
            [new(state.Id, state.Decision!.Id, source.Key, source.VisualId, [action])], [semantic]);
        return await Evaluate(new(state, domain: domain));
    }

    private static Task<DecisionCommand?> Answer(GameSnapshot state, DecisionSemantics semantics, long seed = 17)
        => Evaluate(new(state, semantics: semantics), seed);

    private static async Task<DecisionCommand?> Evaluate(AiDecisionFrame frame, long seed = 17)
    {
        using var session = new AiSeatSession(frame.State.Id, 0, new CasualCommanderPolicy(), seed, new(TimeSpan.FromSeconds(5), 4096));
        session.Observe(frame); await session.EvaluateAsync(); return session.TakeCommand()?.Command;
    }

    private static PublicState Ability(string keyword) => new(keyword, keyword, "ability", 1, null, "", keyword);
    private static Card Body(string id, int power, int toughness) => new() {
        VisualId = id, CombatId = id, Key = id + ":current", Name = "Public creature", Type = "Creature",
        Power = power, Toughness = toughness, Selectable = true, States = []
    };
    private static GameSnapshot Replace(GameSnapshot state, string id, Func<Card, Card> change)
        => state with { Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.VisualId == id ? change(card) : card).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
    private static GameSnapshot Without(GameSnapshot state, string id)
        => state with { Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Where(card => card.VisualId != id).ToImmutableArray(),
                Count = zone.Cards.Count(card => card.VisualId != id) }).ToImmutableArray()
        }).ToImmutableArray() };
    private static GameSnapshot WithReplies(GameSnapshot state, bool complete = false, bool cancel = true)
        => state with { Decision = state.Decision! with { Payload = new TargetSelectionPayload(complete ? Yes : No, cancel ? Yes : No, new(complete ? 0 : 1, 1), []) } };
    private static GameSnapshot State()
    {
        var aura = new Card { VisualId = "aura", CombatId = "aura", Key = "aura:current", Name = "Unfamiliar attachment", Type = "Enchantment Aura" };
        return new("attachment-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            [new(0, "Acting seat", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", 1, [aura], null), new("Battlefield", 1, [Body("own", 2, 2)], null)]),
             new(1, "Other seat", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [Body("enemy", 20, 20)], null)])], [], null,
            new("attachment-target", new(Source: new("aura", aura with { Key = "" }, 0, "Hand")),
                new TargetSelectionPayload(No, Yes, new(1, 1), []))) { Revision = 1, BoardRevision = 1, DecisionOwnerId = 0 };
    }
}
