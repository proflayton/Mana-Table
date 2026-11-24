using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class AiLibraryAcquisitionRegression
{
    private static readonly CasualCommanderOptions Candidate = new(LibraryAcquisitionEvaluation: true);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        // The retained natural turn-22 replay (65aa325d-f3dc-4f97-9d83-fa56b27faf04,
        // SHA256 09AC0E77E4DFD70CF3AC3D04001467923304CBC9A1F8E3AE444477AF2C3BAF66)
        // offered 28 keyless basic-land faces, range 0..2, with Unknown intent.
        // These authored cases preserve that decisive boundary and add explicit
        // mechanical context; they are not re-labelled historical provider truth.
        var bundle = Case(Context(LibraryAcquisitionStage.SelectBundle), maximum: 2);
        var baseline = await Evaluate(bundle);
        check(baseline.Proposal is ChooseOptions { Choices.IsEmpty: true },
            "Frozen v6 reproduces the empty optional basic-land bundle choice despite newly supplied acquisition facts");
        var candidate = await Evaluate(bundle, Candidate);
        check(candidate.Proposal is ChooseOptions { Choices.Length: 2 } useful && useful.Choices.All(index => index is 7 or 19),
            "The acquisition candidate chooses both currently offered basic lands from the certified self-library bundle");

        check(new CasualCommanderPolicy().Descriptor.Id == "casual-commander.v6"
                && new CasualCommanderPolicy(new(false)).Descriptor.Id == "casual-commander.v6.attachment-ablation.v1"
                && new CasualCommanderPolicy(Candidate).Descriptor.Id == "casual-commander.v7.library-acquisition.v1",
            "The library candidate has an explicit descriptor while default v6 and attachment ablation retain their identities");
        Reject(check, () => _ = new CasualCommanderPolicy(new(false, true)),
            "The direct constructor rejects an unversioned combined acquisition and attachment ablation");

        foreach (var context in new[] { Context(), Context(destination: "Hand"),
            Context(LibraryAcquisitionStage.SelectBundle), Context(LibraryAcquisitionStage.DistributeBundle),
            Context(LibraryAcquisitionStage.DistributeBundle, "Hand") })
        foreach (long seed in new[] { long.MinValue, 17L, 9007199254740993L })
        foreach (bool reverse in new[] { false, true }) {
            int maximum = context.Stage == LibraryAcquisitionStage.SelectBundle ? 2 : 1;
            var recorded = Case(context, maximum: maximum, seed: seed);
            var offered = ((OptionsPayload)recorded.State.Decision!.Payload).Selection.Choices;
            if (reverse) offered = offered.Reverse().Select(choice => choice with {
                Label = "Misleading translated payment label", Card = choice.Card! with { Name = "Unrelated renamed card" }
            }).ToImmutableArray();
            recorded = WithChoices(recorded, offered);
            string before = JsonSerializer.Serialize(recorded);
            var result = await Evaluate(recorded, Candidate);
            check(result.Status == AiSeatStatus.Ready && result.Proposal is ChooseOptions chosen
                    && chosen.Choices.Length == maximum && chosen.Choices.All(index => index is 7 or 19)
                    && Frame(recorded).Allows(chosen) && result.Evidence?.Seed == seed,
                $"Certified {context.Stage}/{context.CurrentDestination} selects current basic lands regardless of names, labels or order (seed {seed}, reversed {reverse})");
            check(before == JsonSerializer.Serialize(recorded) && recorded.RemainingWork == 4096,
                "Acquisition evaluation preserves the source frame, empty diagnostic history, exact seed and captured allowance");
        }

        await UnsupportedAndSafety(check);
        await CurrentOffersAndBounds(check);
    }

    private static async Task UnsupportedAndSafety(Action<bool, string> check)
    {
        var owned = Case(Context());
        foreach (var recorded in new[] {
            Case(null, maximum: 2),
            owned with { State = owned.State with { DecisionOwnerId = null } },
            Case(Context(actor: 29)),
            Case(Context(), semantics: new() { SelectionIsCost = true }),
            Case(Context(), semantics: new(DecisionIntent.Discard)),
            Case(Context(), semantics: new(DecisionIntent.Sacrifice)),
            Case(Context(), semantics: new(DecisionIntent.Harm)),
            Case(Context(), semantics: new(DecisionIntent.StartingPlayer))
        }) {
            var old = await Evaluate(recorded);
            var candidate = await Evaluate(recorded, Candidate);
            check(Equivalent(old.Proposal, candidate.Proposal) && old.Evidence?.ReasonCode == candidate.Evidence?.ReasonCode
                    && old.Evidence?.RemainingWork == candidate.Evidence?.RemainingWork,
                "Missing, foreign-actor or conflicting acquisition facts retain the exact generic proposal and work accounting");
        }

        foreach (var invalid in new[] {
            owned with { State = owned.State with { DecisionOwnerId = 29 } },
            owned with { State = owned.State with { ViewerId = 37 } }
        }) {
            bool oldRejected = false, candidateRejected = false;
            try { await Evaluate(invalid); } catch (ArgumentException) { oldRejected = true; }
            try { await Evaluate(invalid, Candidate); } catch (ArgumentException) { candidateRejected = true; }
            check(oldRejected && candidateRejected,
                "Foreign decision ownership and an absent viewing seat retain identical frame-admission rejection in both policies");
        }

        ImmutableArray<Choice> ineligible = [
            new(5, "Forest basic land", Face("Land - Forest Plains")),
            new(13, "Basic Land", Face("Creature - Basic Land")),
            new(23, "Ordinary basic land", Face() with { FaceDown = true }),
            new(37, "Huge benefit", Face("Creature") with { Power = 100, Toughness = 100 })
        ];
        var noLand = Case(Context(maximum: 2), maximum: 2, choices: ineligible);
        check((await Evaluate(noLand, Candidate)).Proposal is ChooseOptions { Choices.IsEmpty: true },
            "Nonbasic cards, misleading subtype text, face-down cards and valuable creatures do not fill optional acquisition slots");
        var readable = (OptionsPayload)noLand.State.Decision!.Payload;
        noLand = noLand with { State = noLand.State with { Decision = noLand.State.Decision with {
            Payload = readable with { Selection = new(readable.Selection.Choices, readable.Selection.Range,
                [new(null, "Readable basic land without an offer", Face())]) }
        } } };
        check((await Evaluate(noLand, Candidate)).Proposal is ChooseOptions { Choices.IsEmpty: true },
            "A readable basic-land disclosure without a current index never creates a selectable candidate");

        var mandatory = Case(Context(maximum: 2), minimum: 2, maximum: 2,
            choices: [new(7, "Current land", Face()), new(19, "Required nonbasic", Face("Land"))]);
        var fallback = await Evaluate(mandatory, Candidate);
        check(fallback.Proposal is ChooseOptions { Choices.Length: 2 } answer && Frame(mandatory).Allows(answer)
                && fallback.Evidence?.ReasonCode == "choose-offered-effect",
            "When basic lands cannot meet the legal minimum, the unchanged mandatory path answers current offers without claiming acquisition utility");

        // These cannot become candidate inputs: the immutable context and option
        // contracts reject them before a policy may interpret them as free gains.
        Reject(check, () => _ = new LibraryAcquisitionDestination("Exile", 1), "Exile acquisitions are outside the admitted capability");
        Reject(check, () => _ = new LibraryAcquisitionDestination("Library", 1), "Tucking a card is outside the admitted capability");
        Reject(check, () => _ = new LibraryAcquisitionContext(11, 11, 11, LibraryAcquisitionStage.Direct,
            "Hand", false, [new("Hand", 1)]), "Unknown additional costs cannot be represented as a certified free acquisition");
        Reject(check, () => _ = new LibraryAcquisitionContext(11, 29, 11, LibraryAcquisitionStage.Direct,
            "Hand", true, [new("Hand", 1)]), "Another player's library cannot become a self-acquisition context");
        Reject(check, () => _ = new LibraryAcquisitionContext(11, 11, 29, LibraryAcquisitionStage.Direct,
            "Hand", true, [new("Hand", 1)]), "Another recipient cannot become a self-acquisition context");
        var payment = (OptionsPayload)Case(Context()).State.Decision!.Payload;
        Reject(check, () => _ = payment with { ManaPaymentContext = new(11, [new("{G}", 1)], null) },
            "A certified acquisition cannot also claim to be a mana-payment choice");

        var unknownConfirmation = Case(null);
        unknownConfirmation = unknownConfirmation with { State = unknownConfirmation.State with {
            Decision = unknownConfirmation.State.Decision! with { Payload = new ConfirmationPayload(new("Accept", true), new("Decline", true)) }
        } };
        var a = await Evaluate(unknownConfirmation);
        var b = await Evaluate(unknownConfirmation, Candidate);
        check(a.Proposal is AcceptConfirmation && Equivalent(a.Proposal, b.Proposal)
                && a.Evidence?.RemainingWork == b.Evidence?.RemainingWork,
            "The acquisition opt-in does not globally reinterpret unknown confirmations or change cancellation behavior");
    }

    private static async Task CurrentOffersAndBounds(Action<bool, string> check)
    {
        var captured = Case(Context(LibraryAcquisitionStage.SelectBundle), maximum: 2);
        var previous = await Evaluate(captured, Candidate);
        var payload = (OptionsPayload)captured.State.Decision!.Payload;
        var refreshed = WithChoices(captured, payload.Selection.Choices.Select(choice => choice with { Index = choice.Index + 100 }).ToImmutableArray());
        refreshed = refreshed with { State = refreshed.State with { Revision = 2, BoardRevision = 2 } };
        var fresh = await Evaluate(refreshed, Candidate);
        check(previous.Proposal is ChooseOptions old && !Frame(refreshed).Allows(old)
                && fresh.Proposal is ChooseOptions current && current.Choices.Length == 2
                && current.Choices.All(index => index is 107 or 119) && Frame(refreshed).Allows(current),
            "A refreshed offer uses only its current indices, and old indices cannot select the new faces");
        var newDecision = refreshed with { State = refreshed.State with { Decision = refreshed.State.Decision! with { Id = "renewed-library-input" } } };
        check(!Frame(newDecision).Allows(fresh.Proposal!) && Frame(newDecision).Allows((await Evaluate(newDecision, Candidate)).Proposal!),
            "A renewed decision never inherits the old input's command authority");

        var reduced = Case(Context(maximum: 2), maximum: 1);
        check((await Evaluate(reduced, Candidate)).Proposal is ChooseOptions { Choices.Length: 1 },
            "A destination's nominal upper bound never enlarges the smaller current selection range");
        check((await Evaluate(Case(Context(), maximum: 0), Candidate)).Proposal is ChooseOptions { Choices.IsEmpty: true },
            "A current zero upper bound remains an empty answer despite certified acquisition purpose");
        foreach (int work in new[] { 1, 5, 6, 7, 8 }) {
            var limited = Case(Context(LibraryAcquisitionStage.SelectBundle), maximum: 2, work: work);
            var result = await Evaluate(limited, Candidate);
            check(result.Status == AiSeatStatus.Ready && result.Proposal is ChooseOptions selected && Frame(limited).Allows(selected)
                    && result.Evidence is { RemainingWork: >= 0 } && result.Evidence.RemainingWork <= work
                    && selected.Choices.All(index => index is 7 or 19),
                $"A {work}-unit allowance retains an admissible answer from completed comparisons without enlarging shared work");
        }
        var partial = await Evaluate(Case(Context(LibraryAcquisitionStage.SelectBundle), maximum: 2, work: 6), Candidate);
        check(partial.Proposal is ChooseOptions { Choices.Length: 1 } && partial.Evidence?.RemainingWork == 4,
            "A useful basic-land answer survives a local optimization cutoff while the four-unit mechanical reserve remains intact");
        var many = Case(Context(LibraryAcquisitionStage.SelectBundle), maximum: 2,
            choices: Enumerable.Range(0, 513).Select(index => new Choice(index, "Same visible face", Face())).ToImmutableArray());
        var oldMany = await Evaluate(many);
        var newMany = await Evaluate(many, Candidate);
        check(Equivalent(oldMany.Proposal, newMany.Proposal) && oldMany.Evidence?.RemainingWork == newMany.Evidence?.RemainingWork,
            "An over-512 option set falls through without an unbounded new scan or altered generic work accounting");
    }

    private static AiDecisionReplayCase WithChoices(AiDecisionReplayCase recorded, ImmutableArray<Choice> choices)
    {
        var payload = (OptionsPayload)recorded.State.Decision!.Payload;
        return recorded with { State = recorded.State with { Decision = recorded.State.Decision with {
            Payload = payload with { Selection = new(choices, payload.Selection.Range, payload.Selection.Disclosures) }
        } } };
    }

    private static AiDecisionFrame Frame(AiDecisionReplayCase recorded)
        => new(recorded.State, domain: recorded.Domain, semantics: recorded.Semantics);

    private static bool Equivalent(DecisionCommand? a, DecisionCommand? b)
        => a?.GetType() == b?.GetType() && (a == null || JsonSerializer.Serialize(a, a.GetType()) == JsonSerializer.Serialize(b, b!.GetType()));

    private static void Reject(Action<bool, string> check, Action action, string description)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        check(rejected, description);
    }

    private static Task<AiDecisionReplayResult> Evaluate(AiDecisionReplayCase captured, CasualCommanderOptions? options = null)
        => AiDecisionReplay.EvaluateAsync(captured, new CasualCommanderPolicy(options ?? new()));

    private static LibraryAcquisitionContext Context(LibraryAcquisitionStage stage = LibraryAcquisitionStage.Direct,
        string destination = "Battlefield", int actor = 11, int maximum = 1)
        => new(actor, actor, actor, stage, stage == LibraryAcquisitionStage.SelectBundle ? "Library" : destination, true,
            stage == LibraryAcquisitionStage.SelectBundle
                ? [new("Hand", 1), new("Battlefield", 1)] : [new(destination, maximum)]);

    private static Card Face(string type = "Basic Land - Forest") => new() {
        Name = "Arbitrary disclosed face", Type = type, States = [], CombatKeywords = []
    };

    private static AiDecisionReplayCase Case(LibraryAcquisitionContext? context, int minimum = 0, int maximum = 1,
        ImmutableArray<Choice> choices = default, long seed = 17, int work = 4096, DecisionSemantics? semantics = null)
    {
        if (choices.IsDefault) choices = [new(7, "Unrelated first label", Face()), new(19, "Unrelated second label", Face("Snow Basic Land - Plains")),
            new(31, "Misleading basic-land label", Face("Land - Forest Plains"))];
        var payload = new OptionsPayload(new(choices, new(minimum, maximum))) { LibraryAcquisition = context };
        var state = new GameSnapshot("library-choice-game", "playing", null, null, 11, 22, "Main", "MAIN1", 11, [
            new(11, "Acting seat", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", 0, [], null), new("Battlefield", 0, [], null), new("Library", 28, [], null)]),
            new(29, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        ], [], null, new("current-library-choice", new(), payload)) { Revision = 1, BoardRevision = 1, DecisionOwnerId = 11 };
        return new(1, "authored-certified-library-acquisition", "casual-commander.v6", state,
            DecisionActionDomain.None, semantics ?? new(), new([], []), seed) {
                RemainingTime = TimeSpan.FromSeconds(10), RemainingWork = work
            };
    }
}
