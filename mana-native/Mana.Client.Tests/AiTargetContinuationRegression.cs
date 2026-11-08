using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiTargetContinuationRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = Targets("current-targets", 1, productive: true, complete: true);
        var semantics = new DecisionSemantics(DecisionIntent.Exile) { EpisodeId = "actual-target-input" };
        var frame = new AiDecisionFrame(state, semantics: semantics);
        var selected = state.Players[1].Zone("Battlefield").Cards.Single(card => card.Highlighted);
        check(frame.Allows(new SelectCard(state.Id, state.Decision!.Id, selected.Key))
                && (await Evaluate(frame)).Command is SelectCard { Card: "current-targets:productive" },
            "The target policy preserves the engine's selected-card undo authority but adds only an unselected current productive target");
        check((await Evaluate(frame)).Command is not CompleteSelection,
            "An enabled target completion does not discard another productive optional target merely because the minimum is already satisfied");
        var renamed = state with { Decision = state.Decision! with { Context = new(Message: "Translated unrelated description") },
            Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card with { Name = "Different printed title", Text = "Different rules prose" }).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray() };
        check((await Evaluate(new(renamed, semantics: semantics))).Command is SelectCard { Card: "current-targets:productive" },
            "Current target continuation follows typed admissibility rather than card names or rejection prose");
        var exhausted = Targets("fresh-completion-scope", 2, productive: false, complete: true);
        var completedFrame = new AiDecisionFrame(exhausted, semantics: semantics);
        var completion = await Evaluate(completedFrame);
        check(completion.Command is CompleteSelection complete && completedFrame.Allows(complete)
                && !completedFrame.Allows(complete with { DecisionId = state.Decision.Id }),
            "A valid target subset completes its fresh scope when only selected undo candidates remain, even below the maximum target count");
        var locked = Targets("locked-current-scope", 3, productive: false, complete: false);
        check((await Evaluate(new(locked, semantics: semantics))).Status == AiSeatStatus.Unavailable,
            "Exhausting current target additions cannot invent disabled completion or toggle an already selected target");

        using var bounded = new AiSeatSession(state.Id, 0, new CasualCommanderPolicy(), 17,
            new(TimeSpan.FromSeconds(10), workUnits: 8));
        int accepted = 0;
        for (int step = 0; step < 12; step++) {
            // A transport acceptance need not change selection. This models
            // the observed lifecycle failure without inventing engine success.
            var noProgress = Targets("refreshed-target-scope-" + step, step + 1, productive: true, complete: false);
            bounded.Observe(new(noProgress, semantics: semantics));
            if ((await bounded.EvaluateAsync()).Status == AiSeatStatus.BudgetExceeded) break;
            var answer = bounded.TakeCommand();
            if (answer?.Command is not SelectCard) break;
            bounded.Report(answer, AiExecutionReceipt.Accepted); accepted++;
        }
        check(accepted == 8 && bounded.View.Status == AiSeatStatus.BudgetExceeded && bounded.TakeCommand() == null
                && bounded.History.Decisions.Where(record => record.Outcome == AiDecisionOutcome.Accepted)
                    .Select(record => record.Seed).Distinct().Count() == 1,
            "Fresh target prompt IDs cannot reset the actual input episode's finite work or deterministic seed after repeated accepted no-progress attempts");
        var newInput = Targets("genuinely-new-target-input", 30, productive: true, complete: false);
        bounded.Observe(new(newInput, semantics: semantics with { EpisodeId = "different-actual-target-input" }));
        check((await bounded.EvaluateAsync()).Status == AiSeatStatus.Ready,
            "A distinct target input receives its own allowance while refreshed scopes of the prior input remain bounded");
        await UnknownOptionalSelections(check);
    }

    private static async Task UnknownOptionalSelections(Action<bool, string> check)
    {
        foreach (bool entities in new[] { false, true })
        foreach (int selected in new[] { 0, 42 })
        foreach (bool reordered in new[] { false, true })
        foreach (string counter in new[] { "", "P1P1" })
        foreach (long seed in new[] { 17L, 71L }) {
            var state = Selection(entities, 57, selected, complete: true, reordered: reordered);
            var semantic = new DecisionSemantics(DecisionIntent.Unknown) { CounterType = counter };
            var frame = new AiDecisionFrame(state, semantics: semantic);
            var eligible = state.Players.SelectMany(player => player.Zone("Battlefield").Cards).ToArray();
            var candidate = eligible.First(card => !card.Highlighted);
            var answer = await Evaluate(frame, seed);
            check(eligible.Length == 57 && eligible.Count(card => card.Highlighted) == selected
                    && frame.Allows(new SelectCard(state.Id, state.Decision!.Id, candidate.Key))
                    && answer.Command is CompleteSelection complete && frame.Allows(complete),
                $"An optional unknown {(entities ? "entity" : "target")} effect preserves its {selected}/57 selection instead of adding arbitrary cards (reordered {reordered}, counter '{counter}', seed {seed})");
        }
        foreach (bool entities in new[] { false, true })
        foreach (bool staged in new[] { false, true }) {
            var state = Selection(entities, 0, 0, complete: true, players: [0, 1, 2]);
            var semantic = new DecisionSemantics(DecisionIntent.Unknown) {
                SelectedPlayers = staged ? [0] : [], CounterType = "P1P1"
            };
            var frame = new AiDecisionFrame(state, semantics: semantic);
            var answer = await Evaluate(frame);
            check(frame.Allows(new SelectPlayer(state.Id, state.Decision!.Id, 1))
                    && answer.Command is CompleteSelection complete && frame.Allows(complete),
                $"Unknown optional {(entities ? "entity" : "target")} player choices cannot turn seeded ties or counter labels into benefit (already staged {staged})");
        }
        foreach (bool entities in new[] { false, true }) {
            var required = Selection(entities, 57, 42, complete: false);
            var requiredFrame = new AiDecisionFrame(required, semantics: new(DecisionIntent.Unknown));
            var answer = await Evaluate(requiredFrame);
            check(answer.Command is SelectCard card && requiredFrame.Allows(card)
                    && required.Players.SelectMany(player => player.Zone("Battlefield").Cards)
                        .Any(candidate => candidate.Key == card.Card && !candidate.Highlighted),
                $"Unknown required {(entities ? "entity" : "target")} additions still use an unselected exact current card when completion is disabled");
            var requiredPlayers = Selection(entities, 0, 0, complete: false, players: [0, 1, 2]);
            var playerFrame = new AiDecisionFrame(requiredPlayers, semantics: new(DecisionIntent.Unknown) { SelectedPlayers = [0] });
            var playerAnswer = await Evaluate(playerFrame);
            check(playerAnswer.Command is SelectPlayer { Player: not 0 } player && playerFrame.Allows(player),
                $"Unknown required {(entities ? "entity" : "target")} player choices remain legal without toggling the staged player");
        }
        var cost = Selection(entities: true, 3, 0, complete: true);
        var costFrame = new AiDecisionFrame(cost, semantics: new(DecisionIntent.Unknown) { SelectionIsCost = true });
        check((await Evaluate(costFrame)).Command is SelectCard { Card: "selection-key:0" } payment && costFrame.Allows(payment),
            "The optional-effect confidence rule does not replace an engine-offered selection cost with an unknown-effect decline");

        foreach (bool entities in new[] { false, true }) {
            var state = Selection(entities, 3, 0, complete: true);
            var candidates = state.Players.SelectMany(player => player.Zone("Battlefield").Cards.Select(card =>
                new SemanticEntity(card.VisualId, SemanticEntityKind.Card, card.VisualId, ControllerId: player.Id))).ToImmutableArray();
            var operation = new ActionOperation(0, ActionEffectKind.AddCounters) {
                Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0, Amount = 1,
                AmountKnowledge = SemanticKnowledge.Known, CounterType = "P1P1",
                Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
                    [new("selection-source", SemanticEntityKind.Card, "selection-card:0", ControllerId: 0)]),
                Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []),
                Targets = new(SemanticKnowledge.Known, 0, 3, candidates)
            };
            var semantic = new DecisionSemantics(DecisionIntent.Unknown) {
                CounterType = "P1P1", OperationId = 0, OperationGraph = new(SemanticKnowledge.Known, 0, [operation])
            };
            var knownFrame = new AiDecisionFrame(state, semantics: semantic);
            check((await Evaluate(knownFrame)).Command is SelectCard { Card: "selection-key:0" } productive
                    && knownFrame.Allows(productive),
                $"A completely known positive counter graph still continues an optional {(entities ? "entity" : "target")} selection despite an unknown scalar intent");
            var zeroFrame = new AiDecisionFrame(state, semantics: semantic with {
                OperationGraph = semantic.OperationGraph with { Operations = [operation with { Amount = 0 }] }
            });
            check((await Evaluate(zeroFrame)).Command is CompleteSelection zero && zeroFrame.Allows(zero),
                $"A fully known zero-value counter graph cannot turn optional {(entities ? "entity" : "target")} ties into a productive addition");
            var partial = new AiDecisionFrame(state, semantics: semantic with {
                OperationGraph = semantic.OperationGraph with { Knowledge = SemanticKnowledge.Unknown }
            });
            check((await Evaluate(partial)).Command is CompleteSelection complete && partial.Allows(complete),
                $"A known counter node inside an unknown graph cannot alone justify optional {(entities ? "entity" : "target")} growth");
            var unknownDamage = semantic with { CounterType = "", OperationGraph = new(SemanticKnowledge.Unknown, 0, [
                operation with { Effect = ActionEffectKind.Damage, Knowledge = SemanticKnowledge.Unknown,
                    Amount = null, AmountKnowledge = SemanticKnowledge.Unknown, CounterType = "" }
            ]) };
            var unknownDamageFrame = new AiDecisionFrame(state, semantics: unknownDamage);
            check((await Evaluate(unknownDamageFrame)).Command is CompleteSelection decline && unknownDamageFrame.Allows(decline),
                $"An unmodeled damage node cannot bypass the unknown optional {(entities ? "entity" : "target")} guard through public threat jitter");
            var partialDamage = semantic with { CounterType = "", OperationGraph = new(SemanticKnowledge.Unknown, 0, [
                operation with { Effect = ActionEffectKind.Damage, Amount = 5, CounterType = "", Links = [new(1, OperationLinkKind.Sequence)] },
                new(1, ActionEffectKind.Unknown) { Knowledge = SemanticKnowledge.Unknown, ActorPlayerId = 0,
                    Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null,
                        [new("acting-seat", SemanticEntityKind.Player, PlayerId: 0)]) }
            ]) };
            var partialDamageFrame = new AiDecisionFrame(state, semantics: partialDamage);
            check((await Evaluate(partialDamageFrame)).Command is SelectCard { Card: not "selection-key:0" } recipient
                    && partialDamageFrame.Allows(recipient),
                $"Known local fixed damage preserves its productive first optional {(entities ? "entity" : "target")} recipient despite an unrelated incomplete follow-up");
            var staged = state with { Players = state.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card =>
                    card.VisualId == "selection-card:1" ? card with { Highlighted = true } : card).ToImmutableArray() }).ToImmutableArray()
            }).ToImmutableArray() };
            var stagedPartial = new AiDecisionFrame(staged, semantics: partialDamage);
            check((await Evaluate(stagedPartial)).Command is CompleteSelection finish && stagedPartial.Allows(finish),
                $"An already staged partial-damage {(entities ? "entity" : "target")} bundle still completes instead of adding a separately positive unmodeled recipient");
        }
    }

    private static GameSnapshot Selection(bool entities, int eligible, int selected, bool complete,
        bool reordered = false, ImmutableArray<int> players = default)
    {
        var cards = Enumerable.Range(0, eligible).Select(index => new Card {
            Key = "selection-key:" + index, VisualId = "selection-card:" + index, CombatId = "selection-card:" + index,
            Name = reordered ? "Renamed public face" : "Public candidate", Text = reordered ? "Unrelated translated prose" : "",
            Type = "Creature", Power = 2, Toughness = 2, Selectable = true, Highlighted = index < selected
        }).ToArray();
        var seats = Enumerable.Range(0, 3).Select(id => {
            var owned = cards.Where((_, index) => index % 3 == id);
            if (reordered) owned = owned.Reverse();
            var faces = owned.ToImmutableArray();
            return new Player(id, reordered ? "Renamed seat" : "Seat " + id, 40, false, false,
                ImmutableDictionary<string, int>.Empty, [new("Battlefield", faces.Length, faces, null)]);
        }).ToImmutableArray();
        if (reordered) seats = seats.Reverse().ToImmutableArray();
        players = players.IsDefault ? [] : players;
        DecisionPayload payload = entities
            ? new EntitySelectionPayload(new("Done", complete), new("Cancel", true), players, new(complete ? 0 : selected + 1, eligible + players.Length))
            : new TargetSelectionPayload(new("Done", complete), new("Cancel", true), new(complete ? 0 : selected + 1, eligible + players.Length), players);
        return new("selection-confidence-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            seats, [], null, new("current-selection", new(Message: reordered ? "Other description" : "Current choice"), payload)) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0
            };
    }

    private static async Task<(AiSeatStatus Status, DecisionCommand? Command)> Evaluate(AiDecisionFrame frame, long seed = 17)
    {
        using var session = new AiSeatSession(frame.State.Id, 0, new CasualCommanderPolicy(), seed,
            new(TimeSpan.FromSeconds(5), workUnits: 1024));
        session.Observe(frame);
        var result = await session.EvaluateAsync();
        return (result.Status, session.TakeCommand()?.Command);
    }

    private static GameSnapshot Targets(string scope, long revision, bool productive, bool complete)
    {
        Card Face(string id, bool selectable, bool highlighted, int power = 3) => new() {
            Key = scope + ":" + id, VisualId = id, CombatId = id, Name = "Printed target",
            Type = "Creature", Power = power, Toughness = power, Selectable = selectable, Highlighted = highlighted
        };
        var players = ImmutableArray.Create(
            new Player(0, "You", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 0, [], null)]),
            new Player(1, "First opponent", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 2, [Face("selected-undo", true, true), Face("unproductive", false, false, 99)], null)]),
            new Player(2, "Second opponent", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [Face("productive", productive, false)], null)]));
        return new("target-continuation-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            players, [], null, new(scope, new(Message: "Current selection"),
                new TargetSelectionPayload(new("Done", complete), new("Cancel", false), new(0, 3), []))) {
                    Revision = revision, BoardRevision = revision, DecisionOwnerId = 0
                };
    }
}
