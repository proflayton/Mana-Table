using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table.Interaction;

internal abstract class DecisionViewState { internal bool Collapsed { get; set; } }
internal abstract class GalleryState : DecisionViewState
{
    internal int Page { get; set; }
    internal string Filter { get; set; } = "";
    internal bool FilterFocused { get; set; }
}
internal sealed class ChoiceDraft : GalleryState
{
    internal ChoiceSelection Selection { get; }
    internal bool Ordered { get; }
    internal HandReductionContext? HandReduction { get; }
    internal bool Keeping => HandReduction is { } reduction && reduction.MaximumHandSize < reduction.HandSize - reduction.MaximumHandSize;
    internal int SelectionLimit => Keeping ? HandReduction!.MaximumHandSize : Selection.Range.Max;
    internal bool ShowAllCards { get; set; }
    internal bool Includes(int? index) => ShowAllCards || index.HasValue;
    internal List<int> Selected { get; } = [];
    internal IReadOnlyList<int> View { get; }
    internal ChoiceDraft(ChoiceSelection selection, bool ordered, HandReductionContext? handReduction = null)
    {
        Selection = selection; Ordered = ordered; HandReduction = handReduction; View = Selected.AsReadOnly();
    }
}
internal sealed class RevealDraft : GalleryState { }
internal sealed class ValueDraft(string initial) : DecisionViewState { internal string Text { get; set; } = initial; }
internal abstract class AllocationInspection { internal int RulesPage { get; set; } }
internal sealed class AllocationSourceInspection(string id) : AllocationInspection { internal string Id { get; } = id; }
internal sealed class AllocationRecipientInspection(int index, string id) : AllocationInspection
{
    internal int Index { get; } = index;
    internal string Id { get; } = id;
    internal bool Editing { get; set; }
    internal string Text { get; set; } = "";
}
internal sealed class AllocationDraft : DecisionViewState
{
    internal AllocationPayload Payload { get; }
    internal int[] Amounts { get; }
    internal IReadOnlyList<int> View { get; }
    internal int Page { get; set; }
    internal AllocationInspection? Inspection { get; set; }
    internal AllocationDraft(AllocationPayload payload)
    {
        Payload = payload; Amounts = payload.Recipients.Select(recipient => recipient.Minimum).ToArray();
        View = Array.AsReadOnly(Amounts);
    }
}

// One scoped owner retains only the draft applicable to the current payload.
// Reading/page changes never produce a command or extend an engine permission.
internal sealed class DecisionSession
{
    private (string Game, int Viewer, string Decision)? scope;
    private (string VisualId, string Key, bool FaceDown)[]? reductionHand;
    private (int Count, int? Maximum, bool Current)? reductionState;
    internal Decision? Current { get; private set; }
    internal DecisionPayload? Payload => Current?.Payload;
    internal DecisionViewState? Draft { get; private set; }
    internal DecisionDockView? Presentation { get; set; }
    internal ChoiceDraft? Choice => Draft as ChoiceDraft;
    internal AllocationDraft? Allocation => Draft as AllocationDraft;
    internal GalleryState? Gallery => Draft as GalleryState;
    internal IReadOnlyList<int> Selections => Choice?.View ?? Array.Empty<int>();
    // Local keep marks are never sent as discard choices. Build the complement
    // in the current offer's order, retaining each physical occurrence's index.
    internal IReadOnlyList<int> SubmissionSelections => Choice is { Keeping: true } keeping
        ? Array.AsReadOnly(keeping.Selection.Choices.Where(option => !keeping.Selected.Contains(option.Index)).Select(option => option.Index).ToArray())
        : Selections;
    internal IReadOnlyList<int> Allocations => Allocation?.View ?? Array.Empty<int>();
    internal bool Collapsed { get => Draft?.Collapsed == true; set { if (Draft != null) Draft.Collapsed = value; } }
    internal int Page { get => Draft switch { GalleryState gallery => gallery.Page, AllocationDraft allocation => allocation.Page, _ => 0 }; set { if (Gallery != null) Gallery.Page = value; else if (Allocation != null) Allocation.Page = value; } }
    internal string Filter { get => Gallery?.Filter ?? ""; set { if (Gallery != null) Gallery.Filter = value; } }
    internal bool Filtering { get => Gallery?.FilterFocused == true; set { if (Gallery != null) Gallery.FilterFocused = value; } }
    internal string Input { get => Draft is ValueDraft text ? text.Text : Allocation?.Inspection is AllocationRecipientInspection recipient ? recipient.Text : ""; set { if (Draft is ValueDraft text) text.Text = value; else if (Allocation?.Inspection is AllocationRecipientInspection recipient) recipient.Text = value; } }

    internal bool Observe(GameSnapshot? state, bool direct)
    {
        var current = state?.Decision is { } decision ? (state.Id, state.ViewerId, decision.Id) : ((string, int, string)?)null;
        bool changed = scope != current || Current?.Payload.GetType() != state?.Decision?.Payload.GetType()
            || !SameSelection(Current?.Payload, state?.Decision?.Payload)
            || !SameHandReduction(state)
            || !SameAllocation(Current?.Payload as AllocationPayload, state?.Decision?.Payload as AllocationPayload)
            || !SameEntry(Current?.Payload, state?.Decision?.Payload)
            || !SameReveal(Current?.Payload as RevealPayload, state?.Decision?.Payload as RevealPayload);
        Current = state?.Decision;
        if (!changed) return false;
        scope = current;
        reductionHand = HandReductionIdentity(state);
        reductionState = HandReductionState(state);
        Draft = Current?.Payload switch {
            OptionsPayload options => new ChoiceDraft(options.Selection, false,
                state != null && HandReductionFacts.IsCurrent(state) ? options.HandReduction : null),
            OrderingPayload ordering => new ChoiceDraft(ordering.Selection, true),
            AbilityChoicePayload ability => new ChoiceDraft(ability.Selection, false),
            RevealPayload => new RevealDraft(),
            NumberPayload number => new ValueDraft(number.Initial),
            TextPayload text => new ValueDraft(text.Initial),
            AllocationPayload allocation => new AllocationDraft(allocation),
            null or PriorityPayload or OpeningHandPayload or PaymentPayload or AttackPayload or BlockPayload
                or TargetSelectionPayload or EntitySelectionPayload or ConfirmationPayload => null,
            _ => throw new InvalidOperationException("No decision state owns " + Current?.Payload.GetType().Name)
        };
        if (!direct && Choice is { Ordered: true } ordered && ordered.Selection.Range.Min == ordered.Selection.Choices.Length)
            ordered.Selected.AddRange(ordered.Selection.Choices.Select(option => option.Index));
        return true;
    }

    internal void Clear() { scope = null; reductionHand = null; reductionState = null; Current = null; Draft = null; Presentation = null; }
    internal bool AdjustNumber(int delta)
    {
        if (Payload is not NumberPayload number || Draft is not ValueDraft draft) return false;
        if (!int.TryParse(draft.Text, out int value) && draft.Text.Length != 0) return false;
        draft.Text = Math.Clamp((long)value + delta, number.Range?.Min ?? int.MinValue, number.Range?.Max ?? int.MaxValue).ToString();
        return true;
    }
    internal int Position(int index) => Choice?.Selected.IndexOf(index) + 1 ?? 0;
    internal void ClearSelection() => Choice?.Selected.Clear();
    internal bool Toggle(GameSnapshot state, int index)
    {
        if (!Matches(state) || Choice is not { } draft || !draft.Selection.Choices.Any(option => option.Index == index)) return false;
        if (draft.Selected.Remove(index)) return true;
        if (draft.SelectionLimit == 1) draft.Selected.Clear();
        if (draft.Selected.Count >= draft.SelectionLimit) return false;
        draft.Selected.Add(index); return true;
    }

    internal DecisionCommand? SelectionReply(GameSnapshot state)
    {
        if (!Matches(state) || state.Decision?.Payload is OptionsPayload { HandReduction: not null } && !HandReductionFacts.IsCurrent(state)) return null;
        return DecisionCommands.Choose(state, SubmissionSelections);
    }

    internal void ResetAllocation(GameSnapshot state)
    {
        if (!Matches(state) || Allocation is not { } draft) return;
        for (int index = 0; index < draft.Amounts.Length; index++) draft.Amounts[index] = draft.Payload.Recipients[index].Minimum;
    }
    internal bool SetAllocation(GameSnapshot state, int index, int amount)
    {
        if (!Matches(state) || Allocation is not { } draft || index < 0 || index >= draft.Amounts.Length
            || amount < draft.Payload.Recipients[index].Minimum || amount > draft.Payload.Recipients[index].Maximum) return false;
        draft.Amounts[index] = amount; return true;
    }
    internal void AdjustAllocation(GameSnapshot state, int index, int delta)
    {
        if (!Matches(state) || Allocation is not { } draft || index < 0 || index >= draft.Amounts.Length) return;
        if (delta > 0 && AllocationGuide.Total(draft.Amounts) >= draft.Payload.Amount) return;
        long amount = (long)draft.Amounts[index] + delta;
        if (amount is >= int.MinValue and <= int.MaxValue) SetAllocation(state, index, (int)amount);
    }
    internal void AssignRemaining(GameSnapshot state, int index)
    {
        if (!Matches(state) || Allocation is not { } draft || index < 0 || index >= draft.Amounts.Length) return;
        long amount = Math.Min(draft.Payload.Recipients[index].Maximum, Math.Max(draft.Amounts[index],
            (long)draft.Amounts[index] + Math.Max(0, draft.Payload.Amount - AllocationGuide.Total(draft.Amounts))));
        SetAllocation(state, index, (int)amount);
    }
    internal DecisionCommand? AllocationReply(GameSnapshot state) => Matches(state) ? AllocationGuide.Reply(state, Allocations) : null;
    private bool Matches(GameSnapshot state) => state.Decision != null && scope == (state.Id, state.ViewerId, state.Decision.Id)
        && Current?.Payload.GetType() == state.Decision.Payload.GetType()
        && (state.Decision.Payload is not OptionsPayload { HandReduction: not null } || HandReductionFacts.IsCurrent(state))
        && SameSelection(Current?.Payload, state.Decision.Payload)
        && SameHandReduction(state)
        && SameAllocation(Current?.Payload as AllocationPayload, state.Decision.Payload as AllocationPayload)
        && SameEntry(Current?.Payload, state.Decision.Payload)
        && SameReveal(Current?.Payload as RevealPayload, state.Decision.Payload as RevealPayload);
    private static bool SameSelection(DecisionPayload? before, DecisionPayload? after)
    {
        static ChoiceSelection? Selection(DecisionPayload? payload) => payload switch {
            OptionsPayload options => options.Selection, OrderingPayload ordering => ordering.Selection,
            AbilityChoicePayload ability => ability.Selection, _ => null
        };
        var old = Selection(before); var current = Selection(after);
        if (old == null || current == null) return old == current;
        return (before as OptionsPayload)?.HandReduction == (after as OptionsPayload)?.HandReduction
            && old.Range == current.Range && old.Choices.Select(choice => (choice.Index, choice.CardId, choice.Card?.VisualId))
            .SequenceEqual(current.Choices.Select(choice => (choice.Index, choice.CardId, choice.Card?.VisualId)))
            && SameFaces(old.Disclosures, current.Disclosures);
    }
    private bool SameHandReduction(GameSnapshot? state)
    {
        if (reductionState != HandReductionState(state)) return false;
        var current = HandReductionIdentity(state);
        return reductionHand == null || current == null ? reductionHand == current : reductionHand.SequenceEqual(current);
    }
    private static (string VisualId, string Key, bool FaceDown)[]? HandReductionIdentity(GameSnapshot? state)
        => state?.Decision?.Payload is OptionsPayload { HandReduction: not null }
            ? state.Viewer?.Zone("Hand").Cards.Select(card => (card.VisualId, card.Key, card.FaceDown)).ToArray() ?? [] : null;
    private static (int Count, int? Maximum, bool Current)? HandReductionState(GameSnapshot? state)
        => state?.Decision?.Payload is OptionsPayload { HandReduction: not null } && state.Viewer is { } viewer
            ? (viewer.Zone("Hand").Count, viewer.MaxHandSize, HandReductionFacts.IsCurrent(state)) : null;
    private static bool SameEntry(DecisionPayload? before, DecisionPayload? after) => (before, after) switch {
        (NumberPayload old, NumberPayload current) => old.Initial == current.Initial && old.Range == current.Range,
        (TextPayload old, TextPayload current) => old.Initial == current.Initial,
        (NumberPayload or TextPayload, _) or (_, NumberPayload or TextPayload) => false,
        _ => true
    };
    private static bool SameReveal(RevealPayload? before, RevealPayload? after) => before == null || after == null
        ? before == after : SameFaces(before.Cards, after.Cards);
    private static bool SameFaces(IEnumerable<LibraryChoice> before, IEnumerable<LibraryChoice> after) => before
        .Select(face => (face.Index, face.CardId, face.Card.VisualId, face.Card.Name, face.Card.ArtName, face.Card.Type, face.Card.FaceDown))
        .SequenceEqual(after.Select(face => (face.Index, face.CardId, face.Card.VisualId, face.Card.Name, face.Card.ArtName, face.Card.Type, face.Card.FaceDown)));
    private static bool SameAllocation(AllocationPayload? before, AllocationPayload? after)
    {
        if (before == null || after == null) return before == after;
        if (before.Amount != after.Amount || before.Kind != after.Kind || before.AmountLabel != after.AmountLabel
            || before.MaySkip != after.MaySkip || before.Recipients.Length != after.Recipients.Length) return false;
        for (int index = 0; index < before.Recipients.Length; index++) {
            var old = before.Recipients[index]; var current = after.Recipients[index];
            if (old.Index != current.Index || old.Id != current.Id || old.CardId != current.CardId || old.PlayerId != current.PlayerId
                || old.ControllerId != current.ControllerId || old.OwnerId != current.OwnerId || old.Card?.VisualId != current.Card?.VisualId
                || old.Minimum != current.Minimum || old.Maximum != current.Maximum || old.Lethal != current.Lethal || old.IsDefender != current.IsDefender) return false;
        }
        return true;
    }
}
