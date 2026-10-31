using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class CleanupHandReductionRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = State(35, 7);
        var options = (OptionsPayload)state.Decision!.Payload;
        var discarded = options.Selection.Choices.Take(28).Select(choice => choice.Index).ToImmutableArray();
        var answer = new ChooseOptions(state.Id, state.Decision.Id, discarded);
        check(HandReductionFacts.IsCurrent(state) && GameCommandPermissions.Allows(state, answer),
            "Thirty-five current hand occurrences can be discarded to seven in one exact scoped option answer");
        check(!GameCommandPermissions.Allows(state, answer with { Choices = discarded.RemoveAt(0) })
            && !GameCommandPermissions.Allows(state, answer with { Choices = discarded.Add(999) })
            && !GameCommandPermissions.Allows(state, answer with { Choices = discarded.SetItem(1, discarded[0]) })
            && !GameCommandPermissions.Allows(state, answer with { DecisionId = "retired-cleanup" })
            && !GameCommandPermissions.Allows(state, new SelectCard(state.Id, state.Decision.Id, "invented-hand-key")),
            "Cleanup keeps exact discard cardinality, distinct current indices and decision scope without granting card selection handles");
        var roundTrip = JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(state))!;
        check(HandReductionFacts.IsCurrent(roundTrip) && roundTrip.Decision!.Payload is OptionsPayload { HandReduction.MaximumHandSize: 7 }
            && GameCommandPermissions.Allows(roundTrip, answer),
            "Immutable cleanup facts and duplicate-named physical hand occurrences survive the native snapshot round trip");
        foreach (var changed in new[] {
            state with { ViewerId = 1 }, state with { DecisionOwnerId = 1 }, state with { Status = "resolving" },
            ChangeViewer(state, viewer => viewer with { MaxHandSize = null }),
            ChangeViewer(state, viewer => viewer with { MaxHandSize = 8 }),
            ChangeHand(state, hand => hand with { Count = 34 }),
            ChangeHand(state, hand => hand with { Cards = hand.Cards.RemoveAt(0) }),
            ChangeHand(state, hand => hand with { Cards = hand.Cards.SetItem(0, hand.Cards[0] with { VisualId = "new-occurrence", CombatId = "new-occurrence" }) }),
            ChangeHand(state, hand => hand with { Cards = hand.Cards.SetItem(0, hand.Cards[1]) })
        }) {
            bool rejected = false; try { _ = new AiDecisionFrame(changed); } catch (ArgumentException) { rejected = true; }
            check(!HandReductionFacts.IsCurrent(changed) && !GameCommandPermissions.Allows(changed, answer) && rejected,
                "Changed ownership, limit, count or opaque hand occurrence membership revokes cleanup complements and AI submission authority");
        }
        foreach (var create in new Action[] {
            () => _ = new HandReductionContext(-1, 35, 7), () => _ = new HandReductionContext(0, 7, 7),
            () => _ = new HandReductionContext(0, 35, -1),
            () => _ = options with { Selection = new(options.Selection.Choices, new(27, 28)) },
            () => _ = options with { Selection = new(options.Selection.Choices.RemoveAt(0), new(28, 28)) },
            () => _ = options with { Selection = new(options.Selection.Choices.SetItem(0, options.Selection.Choices[0] with { CardId = options.Selection.Choices[1].CardId }), new(28, 28)) },
            () => _ = options with { Selection = new(options.Selection.Choices.SetItem(0, options.Selection.Choices[0] with { Card = options.Selection.Choices[0].Card! with { Key = "unauthorized-key" } }), new(28, 28)) },
            () => _ = options with { Selection = new(options.Selection.Choices.SetItem(0, options.Selection.Choices[0] with { Card = options.Selection.Choices[0].Card! with { Selectable = true } }), new(28, 28)) },
            () => _ = options with { Selection = new(options.Selection.Choices, new(28, 28), [new(null, "Extra disclosure", new Card())]) }
        }) {
            bool rejected = false; try { create(); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Hand-reduction contracts reject contradictory counts, duplicate occurrence links, action keys and unrelated disclosures");
        }

        foreach (long seed in new long[] { 0, 17 }) {
            var proposal = await Evaluate(state, seed, 512);
            var chosen = (ChooseOptions)proposal.Command;
            var keepers = options.Selection.Choices.Where(choice => !chosen.Choices.Contains(choice.Index)).ToArray();
            check(chosen.Choices.Length == 28 && keepers.Length == 7 && keepers.Count(choice => choice.Card!.Type == "Basic Land") == 3
                && keepers.Where(choice => choice.Card!.Type == "Creature").All(choice => choice.Card!.Power == 4)
                && proposal.RemainingWork >= 270 && GameCommandPermissions.Allows(state, chosen),
                "Bulk cleanup with unknown intent retains development lands and useful bodies, discards the complement, and stays within a bounded work allowance");
            var reverse = state with { Decision = state.Decision with { Payload = options with {
                Selection = new(options.Selection.Choices.Reverse().ToImmutableArray(), options.Selection.Range) } } };
            check(((ChooseOptions)(await Evaluate(reverse, seed, 512)).Command).Choices.Order().SequenceEqual(chosen.Choices.Order()),
                "Cleanup preferences follow occurrence identities and offered indices rather than duplicate card names or wire enumeration order");
        }
        var tiny = await Evaluate(state, 17, 8);
        check(tiny.Command is ChooseOptions tinyAnswer && tinyAnswer.Choices.Length == 28
            && GameCommandPermissions.Allows(state, tinyAnswer) && tiny.ReasonCode == "cleanup-required-bounded-candidate",
            "A small cleanup optimization allowance still produces a complete legal required answer without claiming all choices were assessed");
        var small = State(8, 7);
        var smallAnswer = await Evaluate(small, 17, 128);
        check(smallAnswer.Command is ChooseOptions { Choices.Length: 1 } one
            && ((OptionsPayload)small.Decision!.Payload).Selection.Choices.Single(choice => choice.Index == one.Choices[0]).Card!.Power == 1,
            "A one-card cleanup uses discard semantics from typed hand facts even when generic decision intent is unknown");
        var balanced = State(14, 7, landCount: 4);
        var balancedAnswer = (ChooseOptions)(await Evaluate(balanced, 17, 256)).Command;
        check(((OptionsPayload)balanced.Decision!.Payload).Selection.Choices.Where(choice => !balancedAnswer.Choices.Contains(choice.Index))
            .Count(choice => choice.Card!.Type == "Basic Land") == 3,
            "Discard-side cleanup updates the remaining land count after each choice instead of treating every initially abundant land as independently disposable");
        var none = State(8, 0);
        check((await Evaluate(none, 17, 8)).Command is ChooseOptions { Choices.Length: 8 } all
            && GameCommandPermissions.Allows(none, all),
            "An engine-owned maximum of zero answers the full required discard without a keeper optimization loop");
        var masked = ChangeHand(state, hand => hand with { Cards = hand.Cards.SetItem(0, hand.Cards[0] with {
            FaceDown = true, Name = "Face-down card", Type = "", Power = null, Toughness = null }) });
        var maskedOptions = options with { Selection = new(options.Selection.Choices.SetItem(0, options.Selection.Choices[0] with {
            Card = masked.Viewer!.Zone("Hand").Cards[0] }), options.Selection.Range) };
        masked = masked with { Decision = masked.Decision! with { Payload = maskedOptions } };
        check(HandReductionFacts.IsCurrent(masked) && (await Evaluate(masked, 17, 512)).Command is ChooseOptions maskedAnswer
            && GameCommandPermissions.Allows(masked, maskedAnswer),
            "A currently published anonymous hand occurrence remains answerable without exposing its hidden printed face");
    }

    private sealed record Evaluation(DecisionCommand Command, string ReasonCode, int RemainingWork);
    private static async Task<Evaluation> Evaluate(GameSnapshot state, long seed, int units)
    {
        using var session = new AiSeatSession(state.Id, state.ViewerId, new CasualCommanderPolicy(), seed,
            new(TimeSpan.FromSeconds(5), workUnits: units));
        session.Observe(new(state)); await session.EvaluateAsync();
        var submission = session.TakeCommand() ?? throw new InvalidOperationException("Cleanup policy did not return a required legal answer.");
        return new(submission.Command, submission.ReasonCode,
            session.History.Decisions.Last(record => record.Outcome == AiDecisionOutcome.Submitted).RemainingWork);
    }
    private static GameSnapshot ChangeViewer(GameSnapshot state, Func<Player, Player> change) => state with {
        Players = state.Players.Select(player => player.Id == 0 ? change(player) : player).ToImmutableArray()
    };
    private static GameSnapshot ChangeHand(GameSnapshot state, Func<Zone, Zone> change) => ChangeViewer(state, player => player with {
        Zones = player.Zones.Select(zone => zone.Name == "Hand" ? change(zone) : zone).ToImmutableArray()
    });
    private static GameSnapshot State(int count, int maximum, int landCount = 10)
    {
        int lands = count > 10 ? landCount : 0;
        var cards = Enumerable.Range(0, count).Select(index => new Card { VisualId = "hand-" + index, CombatId = "hand-" + index,
            Name = index < lands ? "Duplicate land" : "Duplicate creature",
            Type = index < lands ? "Basic Land" : "Creature",
            Power = index < lands ? null : count > 10 ? index < 20 ? 1 : 4 : index == 0 ? 1 : 4,
            Toughness = index < lands ? null : 2 }).ToImmutableArray();
        var choices = cards.Select((card, index) => new Choice(index * 3 + 5, card.Name, card) { CardId = card.VisualId }).ToImmutableArray();
        return new("cleanup-regression", "playing", null, null, 0, 4, "Cleanup", "CLEANUP", 0,
            [new(0, "Acting seat", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", count, cards, null), new("Battlefield", 0, [], null)]) { MaxHandSize = maximum },
             new(1, "Other seat", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("cleanup-scope", new(), new OptionsPayload(new(choices, new(count - maximum, count - maximum))) {
                HandReduction = new(0, count, maximum) })) { Revision = 1, BoardRevision = 1, DecisionOwnerId = 0 };
    }
}
