using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiCostPaymentRegression
{
    private static readonly DecisionReply No = new("Required selection", false);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var intent in new[] { DecisionIntent.Tap, DecisionIntent.Sacrifice, DecisionIntent.Exile }) {
            var frame = Frame(intent);
            var answer = await Evaluate(frame);
            check(answer.Proposal is SelectCard { Card: "token-key" } && frame.Allows(answer.Proposal),
                $"A required {intent} cost selects a nonmana creature instead of consuming the only producer for its parent's remaining generic bill");
        }

        var alternative = Frame(backup: true);
        check((await Evaluate(alternative)).Proposal is SelectCard { Card: "producer-key" },
            "A separate finite producer permits spending the cheaper mana creature instead of treating one funding witness as permanently reserved");
        var staged = Frame(backup: true, stagedBackup: true);
        check((await Evaluate(staged)).Proposal is SelectCard { Card: "token-key" },
            "An already staged cost selection cannot also fund the parent's remaining bill");
        var sourceConsumes = Frame(parentFunding: true);
        check((await Evaluate(sourceConsumes)).Proposal is SelectCard { Card: "token-key" },
            "A parent source consumed by its remaining tap cost cannot replace the selected creature as available mana funding");

        var paid = Frame(paid: true);
        check((await Evaluate(paid)).Proposal is SelectCard { Card: "producer-key" },
            "An already paid empty mana bill does not reserve a producer against the current nonmana cost");

        ImmutableArray<ActionCost> pendingTapCosts = [new(ActionCostKind.Mana, 1, false, ""),
            new(ActionCostKind.Tap, 1, true, ""), new(ActionCostKind.Tap, 1, false, "")];
        var pendingTap = Frame(selectableParent: true, remainingCosts: pendingTapCosts, currentCostIndex: 2);
        check((await Evaluate(pendingTap)).Proposal is SelectCard { Card: "token-key" },
            "A current typed tap preserves both the distinct pending source tap and the sole remaining mana producer");
        var reorderedTap = Frame(selectableParent: true,
            remainingCosts: [pendingTapCosts[2], pendingTapCosts[0], pendingTapCosts[1]], currentCostIndex: 0);
        check((await Evaluate(reorderedTap)).Proposal is SelectCard { Card: "token-key" },
            "The current cost part is identified by its actual index rather than an assumed mana or source payment order");
        var paidSourceTap = Frame(selectableParent: true,
            remainingCosts: [pendingTapCosts[0], pendingTapCosts[2]], currentCostIndex: 1);
        check((await Evaluate(paidSourceTap)).Proposal is SelectCard { Card: "origin-key" },
            "A fresh legal source candidate is no longer reserved after its separate source tap has left the remaining costs");
        var unknownCurrentPart = Frame(selectableParent: true, remainingCosts: pendingTapCosts);
        check((await Evaluate(unknownCurrentPart)).Proposal is SelectCard { Card: "origin-key" },
            "Legacy cost metadata without the actual current part index does not invent a pending source collision");
        var currentSourceTap = Frame(selectableParent: true,
            remainingCosts: [pendingTapCosts[0], pendingTapCosts[1]], currentCostIndex: 1);
        check((await Evaluate(currentSourceTap)).Proposal is SelectCard { Card: "origin-key" },
            "The genuine current source tap does not conflict with itself as though it were a second unpaid source tap");
        var tapThenSacrifice = Frame(selectableParent: true,
            remainingCosts: [pendingTapCosts[0], pendingTapCosts[2], new(ActionCostKind.Sacrifice, 1, true, "")], currentCostIndex: 1);
        check((await Evaluate(tapThenSacrifice)).Proposal is SelectCard { Card: "origin-key" },
            "A legal current tap followed by a pending source sacrifice can use the same physical resource");
        var sacrificeBeforeTap = Frame(DecisionIntent.Sacrifice, selectableParent: true,
            remainingCosts: [pendingTapCosts[0], pendingTapCosts[1], new(ActionCostKind.Sacrifice, 1, false, "")], currentCostIndex: 2);
        check((await Evaluate(sacrificeBeforeTap)).Proposal is SelectCard { Card: "token-key" },
            "A source cannot be removed by the current sacrifice before its distinct unpaid source tap");
        foreach (var uncertain in new[] {
            pendingTapCosts.Add(new(ActionCostKind.Untap, 1, true, "")),
            pendingTapCosts.Add(new(ActionCostKind.Other, null, false, "")),
            pendingTapCosts.Add(new(ActionCostKind.Other, 0, false, ""))
        }) {
            var unknownCoupling = Frame(selectableParent: true, remainingCosts: uncertain, currentCostIndex: 2);
            check((await Evaluate(unknownCoupling)).Proposal is SelectCard { Card: "origin-key" },
                "An intervening source untap or unknown cost coupling retains the legal fallback instead of claiming a proven source collision");
        }
        var current = Frame();
        var payment = current.Semantics.CostPayment!;
        foreach (var facts in new CostPaymentFunding?[] {
            null,
            payment with { ManaCostKnowledge = SemanticKnowledge.Unsupported },
            payment with { Funding = new(SemanticKnowledge.Unknown, Mana(), []) },
            payment with { Funding = new(SemanticKnowledge.Known, Mana(), []) }
        }) {
            var fallback = new AiDecisionFrame(current.State, semantics: current.Semantics with { CostPayment = facts });
            var answer = await Evaluate(fallback);
            check(answer.Proposal is SelectCard { Card: "producer-key" } && fallback.Allows(answer.Proposal),
                "Missing, unsupported or unwitnessed funding facts keep the ordinary required-selection fallback rather than inventing an affordable alternative or cancelling");
        }

        var discard = Frame(DecisionIntent.Discard);
        check((await Evaluate(discard)).Proposal is SelectCard { Card: "hand-small-key" },
            "A discard cost chooses among the actual offered hand cards without confusing them with separately observed battlefield producers");

        bool Rejected(DecisionSemantics semantics) {
            try { _ = new AiDecisionFrame(current.State, semantics: semantics); return false; }
            catch (ArgumentException) { return true; }
        }
        check(Rejected(current.Semantics with { CostPayment = payment with { PayerId = 99 } })
                && Rejected(current.Semantics with { CostPayment = payment with { PayerId = 1 } })
                && Rejected(current.Semantics with { SelectionIsCost = false })
                && Rejected(current.Semantics with { CostPayment = payment with { SourceCardId = "invented-source" } }),
            "Cost funding rejects invented payers, another payer's borrowed resources, noncost decisions and a mismatched parent source");
        check(Rejected(current.Semantics with { CostPayment = payment with { CurrentCostIndex = -1 } })
                && Rejected(current.Semantics with { CostPayment = payment with { CurrentCostIndex = payment.RemainingCosts.Length } }),
            "A declared current cost part must index the current remaining cost list and cannot address a paid or invented part");
        var departedPortrait = Card("departed-parent", 1, 1, selectable: false) with { Key = "" };
        var departed = current.State with { Decision = current.State.Decision! with {
            Context = new(Source: new("", departedPortrait, 0, "Command")) } };
        bool portraitRejected = false;
        try { _ = new AiDecisionFrame(departed, semantics: current.Semantics with {
            CostPayment = payment with { SourceCardId = departedPortrait.VisualId } }); }
        catch (ArgumentException) { portraitRejected = true; }
        check(portraitRejected && CardViews.Source(departed, departed.Decision!) == departedPortrait
                && CardViews.FindZoneOccurrence(departed, departedPortrait.VisualId) == null,
            "A readable historical cost portrait cannot establish a current parent occurrence for remaining-cost funding metadata");
        var foreign = new AiDecisionFrame(current.State, semantics: current.Semantics with { CostPayment = payment with {
            PayerId = 1, Funding = new(SemanticKnowledge.Known, Mana(), [Resource("foreign-producer")]) } });
        var foreignAnswer = await Evaluate(foreign);
        check(foreignAnswer.Proposal is SelectCard { Card: "producer-key" } && foreign.Allows(foreignAnswer.Proposal)
                && !foreign.Allows(new UseOfferedAction(current.State.Id, current.State.Decision!.Id, "foreign-key", "invented-mana"))
                && !current.Allows(new UseOfferedAction(current.State.Id, current.State.Decision!.Id, "producer-key", "invented-mana")),
            "A real foreign payer's read-only funding is scoped to that payer and grants no mana action permission or fabricated own-resource reservation");
    }

    private static Task<AiDecisionReplayResult> Evaluate(AiDecisionFrame frame)
        => AiDecisionReplay.EvaluateAsync(new(1, "current-cost-funding", "casual-commander.v3", frame.State,
            frame.Domain, frame.Semantics, new([], []), 17), new CasualCommanderPolicy());

    private static AiDecisionFrame Frame(DecisionIntent intent = DecisionIntent.Tap, bool backup = false,
        bool stagedBackup = false, bool parentFunding = false, bool paid = false, bool selectableParent = false,
        ImmutableArray<ActionCost>? remainingCosts = null, int? currentCostIndex = null)
    {
        var parent = Card("origin", selectableParent ? 1 : 2, 2, selectable: selectableParent);
        var producer = Card("producer", 0, 1, selectable: intent != DecisionIntent.Discard);
        var token = Card("token", 3, 3, selectable: intent != DecisionIntent.Discard) with { GamePieceType = "token" };
        var battlefield = ImmutableArray.Create(parent, producer, token);
        var resources = ImmutableArray.Create(Resource(producer.VisualId));
        if (backup) {
            var extra = Card("backup", 1, 1, selectable: stagedBackup) with { Highlighted = stagedBackup };
            battlefield = battlefield.Add(extra);
            resources = resources.Add(Resource(extra.VisualId));
        }
        if (parentFunding) resources = resources.Add(Resource(parent.VisualId));
        var hand = intent == DecisionIntent.Discard
            ? ImmutableArray.Create(Card("hand-small", 0, 1), Card("hand-large", 3, 3)) : [];
        var state = new GameSnapshot("cost-funding", "playing", null, null, 0, 7, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, Mana(), [new("Battlefield", battlefield.Length, battlefield, null),
                new("Hand", hand.Length, hand, null)]),
             new(1, "Other payer", 40, false, false, Mana(),
                [new("Battlefield", 1, [Card("foreign-producer", 1, 1, selectable: false)], null)])], [], null,
            new("cost-selection", new(Source: new(parent.VisualId,
                parent with { Key = "", Selectable = false, Highlighted = false }, 0, "Battlefield")),
                new EntitySelectionPayload(No, No, [], new(stagedBackup ? 2 : 1, stagedBackup ? 2 : 1)))) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0 };
        var costs = remainingCosts ?? (paid ? ImmutableArray<ActionCost>.Empty : [new(ActionCostKind.Mana, 1, false, "")]);
        if (parentFunding) costs = costs.Add(new(ActionCostKind.Tap, 1, true, ""));
        return new(state, semantics: new(intent) { SelectionIsCost = true,
            CostPayment = new(parent.VisualId, 0, costs, new(SemanticKnowledge.Known, Mana(), resources)) {
                ManaCostKnowledge = SemanticKnowledge.Known, CurrentCostIndex = currentCostIndex } });
    }

    private static Card Card(string id, int power, int toughness, bool selectable = true)
        => new() { Name = "Untrusted display name", VisualId = id, CombatId = id, Key = id + "-key",
            Type = "Creature", Power = power, Toughness = toughness, Selectable = selectable };
    private static ManaFundingSource Resource(string id)
        => new(id, [new([new(ActionCostKind.Tap, 1, true, "")],
            new(SemanticKnowledge.Known, [new(Mana(("G", 1)))]), SemanticKnowledge.Known)]);
    private static ImmutableDictionary<string, int> Mana(params (string Color, int Count)[] entries)
        => entries.ToImmutableDictionary(entry => entry.Color, entry => entry.Count);
}
