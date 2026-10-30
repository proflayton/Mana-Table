using System.Reflection;
using Mana.Contracts;
using Mana.Table.Interaction;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static class DecisionSessionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        ChoiceSelection Options(int min, int max) => new([new(7, "First", null), new(19, "Second", null), new(41, "Third", null)], new(min, max));
        var choice = State(new("order", new(), new OrderingPayload(Options(3, 3))));
        var draft = new DecisionSession();
        check(draft.Observe(choice, false) && draft.Selections.SequenceEqual([7, 19, 41]) && draft.Allocations.Count == 0,
            "An ordered gallery starts with exact choice indices without creating an allocation draft");
        draft.ClearSelection(); draft.Toggle(choice, 41); draft.Toggle(choice, 7); draft.Toggle(choice, 19);
        var ordered = draft.Selections;
        check(!draft.Observe(choice with { Revision = 50 }, false) && draft.Selections.SequenceEqual([41, 7, 19])
            && draft.Position(41) == 1 && draft.Position(7) == 2 && draft.Position(19) == 3,
            "Repeated observations preserve the chosen order and visible positions");
        bool protectedSelection = true;
        if (ordered is ICollection<int> collection) {
            try { collection.Add(999); protectedSelection = false; } catch (NotSupportedException) { }
        }
        check(protectedSelection && draft.Selections.SequenceEqual([41, 7, 19]),
            "A published selection cannot be changed by its presentation consumer");
        var direct = new DecisionSession(); direct.Observe(choice, true);
        check(direct.Selections.Count == 0, "Direct ordering waits for explicit selection");
        var optional = State(new("optional", new(), new OptionsPayload(Options(0, 2))));
        draft.Observe(optional, false);
        check(!draft.Toggle(optional, 1) && !draft.Toggle(optional, -1) && draft.Toggle(optional, 19)
            && draft.Toggle(optional, 7) && !draft.Toggle(optional, 41) && draft.Selections.SequenceEqual([19, 7]),
            "Choice staging accepts published indices and current bounds only");
        var nextPrompt = optional with { Decision = optional.Decision! with { Id = "next-prompt" } };
        check(draft.Observe(nextPrompt, false) && draft.Selections.Count == 0 && !draft.Toggle(optional, 7),
            "A new prompt retires the previous choices and editing scope");
        draft.Toggle(nextPrompt, 7);
        var otherGame = nextPrompt with { Id = "another-game" };
        check(draft.Observe(otherGame, false) && draft.Selections.Count == 0 && !draft.Toggle(nextPrompt, 19),
            "A new game cannot inherit a reused decision identifier");
        draft.Toggle(otherGame, 19);
        var otherViewer = otherGame with { ViewerId = 1 };
        check(draft.Observe(otherViewer, false) && draft.Selections.Count == 0 && !draft.Toggle(otherGame, 7),
            "Changing viewers retires the old seat's draft");
        draft.Toggle(otherViewer, 7);
        check(draft.Observe(otherViewer with { Decision = null }, false) && draft.Selections.Count == 0,
            "Leaving a decision clears unsubmitted choices");
        var single = optional with { Decision = optional.Decision! with { Id = "single", Payload = new OptionsPayload(Options(0, 1)) } };
        draft.Observe(single, false); draft.Toggle(single, 7); draft.Toggle(single, 41);
        check(draft.Selections.SequenceEqual([41]), "Another single choice replaces the previous local selection");
        var library = State(new("library", new(), new OptionsPayload(new(
            [new(83, "Offered choice", null)], new(0, 1),
            [new(null, "Visible reference", new() { Name = "Forest" }), new(83, "Offered choice", new() { Name = "Forest" })]))));
        draft.Observe(library, false);
        check(!draft.Toggle(library, 0) && !draft.Toggle(library, 1) && draft.Toggle(library, 83) && draft.Selections.SequenceEqual([83]),
            "Disclosed reference faces and array slots never grant a selection handle");
        var changedBounds = single with { Decision = single.Decision! with { Payload = new OptionsPayload(Options(0, 2)) } };
        draft.Observe(single, false); draft.Toggle(single, 7);
        check(draft.Observe(changedBounds, false) && draft.Selections.Count == 0 && !draft.Toggle(single, 19),
            "Changed constraints under the same identifier retire the preceding choice draft");
        var replacedPayload = changedBounds with { Decision = changedBounds.Decision! with { Payload = new NumberPayload("12") } };
        check(draft.Observe(replacedPayload, false) && draft.Input == "12" && draft.Selections.Count == 0 && draft.Allocations.Count == 0
            && !draft.Toggle(changedBounds, 7), "A same-ID payload switch owns only its applicable value draft");
        draft.Input = "42";
        check(!draft.Observe(replacedPayload with { Revision = 100 }, false) && draft.Input == "42",
            "An unchanged numeric prompt preserves the player's unfinished entry across observations");
        var bounded = replacedPayload with { Decision = replacedPayload.Decision! with { Payload = new NumberPayload("12", new(0, 20)) } };
        check(draft.Observe(bounded, false) && draft.Input == "12",
            "A same-ID numeric range change retires an entry created under the preceding bounds");
        draft.Input = "15";
        var newInitial = bounded with { Decision = bounded.Decision! with { Payload = new NumberPayload("3", new(0, 20)) } };
        check(draft.Observe(newInitial, false) && draft.Input == "3",
            "A same-ID numeric initial-value replacement starts with the newly offered value");
        check(draft.AdjustNumber(1) && draft.Input == "4", "A number step uses the engine's explicit initial value");
        draft.Input = "";
        check(draft.AdjustNumber(1) && draft.Input == "1", "A deliberate first plus establishes one from an empty numeric draft");
        draft.Input = "";
        check(draft.AdjustNumber(-1) && draft.Input == "0", "A first minus from empty respects the current zero minimum");
        foreach (string invalid in new[] { "-", " " }) {
            draft.Input = invalid;
            check(!draft.AdjustNumber(1) && draft.Input == invalid, "A number step preserves nonempty invalid intermediate text: [" + invalid + "]");
        }
        var narrowNumber = newInitial with { Decision = newInitial.Decision! with { Payload = new NumberPayload("", new(2, 5)) } };
        draft.Observe(narrowNumber, false);
        check(draft.AdjustNumber(1) && draft.Input == "2", "A first numeric step clamps to a nonzero engine minimum");
        draft.Input = "5";
        check(draft.AdjustNumber(1) && draft.Input == "5", "A number step cannot exceed the current explicit maximum");
        var unboundedNumber = newInitial with { Decision = newInitial.Decision! with { Payload = new NumberPayload("") } };
        draft.Observe(unboundedNumber, false); draft.Input = int.MaxValue.ToString();
        bool upperClamped = draft.AdjustNumber(1) && draft.Input == int.MaxValue.ToString();
        draft.Input = int.MinValue.ToString();
        check(draft.AdjustNumber(-1) && draft.Input == int.MinValue.ToString() && upperClamped,
            "Number steps retain widened arithmetic and clamp both integer extremes without overflow");
        var text = newInitial with { Decision = newInitial.Decision! with { Payload = new TextPayload("First answer") } };
        draft.Observe(text, false); draft.Input = "Unfinished answer";
        check(!draft.Observe(text with { Revision = 101 }, false) && draft.Input == "Unfinished answer",
            "Unchanged text input retains the local draft");
        check(!draft.AdjustNumber(1) && draft.Input == "Unfinished answer", "Numeric stepping cannot edit an ordinary text payload");
        var newText = text with { Decision = text.Decision! with { Payload = new TextPayload("Replacement answer") } };
        check(draft.Observe(newText, false) && draft.Input == "Replacement answer",
            "A same-ID text replacement cannot inherit the preceding answer");
        var reveal = State(new("reveal", new(), new RevealPayload([new(null, "Forest", new() { Name = "Forest", VisualId = "first-face" })])));
        draft.Observe(reveal, false); draft.Filter = "Forest"; draft.Page = 2; draft.Collapsed = true;
        check(!draft.Observe(reveal with { Revision = 102 }, false) && draft.Filter == "Forest" && draft.Page == 2 && draft.Collapsed,
            "Repeated observations preserve navigation within the same disclosed reveal");
        var concealed = reveal with { Decision = reveal.Decision! with { Payload = new RevealPayload([new(null, "Face-down card", new() { FaceDown = true })]) } };
        check(draft.Observe(concealed, false) && draft.Filter == "" && draft.Page == 0 && !draft.Collapsed,
            "A same-ID disclosure replacement retires the former face's reading workspace and navigation");
        draft.Observe(library, false); draft.Toggle(library, 83); draft.Filter = "Forest";
        var changedDisclosure = library with { Decision = library.Decision! with { Payload = new OptionsPayload(new(
            [new(83, "Offered choice", null)], new(0, 1),
            [new(null, "Visible reference", new() { Name = "Island" }), new(83, "Offered choice", new() { Name = "Forest" })])) } };
        check(draft.Observe(changedDisclosure, false) && draft.Selections.Count == 0 && draft.Filter == "" && !draft.Toggle(library, 83),
            "Replacing a same-ID choice disclosure resets its draft and rejects edits through the former face set");

        AllocationRecipient Recipient(int index, string label, int minimum, int maximum,
            string id = "", string cardId = "", int? lethal = null, bool defender = false)
            => new(index, id, label, null, cardId, null, null, null, minimum, maximum, lethal, defender);
        var allocation = State(new("allocation", new(), new AllocationPayload(5, AllocationKind.Generic, "", false,
            [Recipient(7, "Recipient A", 1, 3), Recipient(19, "Recipient B", 0, 4)])));
        draft.Observe(allocation, false);
        check(draft.Allocations.SequenceEqual([1, 0]) && draft.Selections.Count == 0 && !draft.SetAllocation(allocation, -1, 1)
            && !draft.SetAllocation(allocation, 2, 1) && !draft.SetAllocation(allocation, 0, 0)
            && !draft.SetAllocation(allocation, 0, 4) && draft.AllocationReply(allocation) == null,
            "Only an allocation draft owns engine minima, limits and current recipient slots");
        bool protectedAmounts = true;
        if (draft.Allocations is IList<int> amounts) {
            try { amounts[0] = 3; protectedAmounts = false; } catch (NotSupportedException) { }
        }
        check(protectedAmounts && draft.Allocations.SequenceEqual([1, 0]), "Published amounts cannot be mutated by a consumer");
        draft.AssignRemaining(allocation, 0); draft.AssignRemaining(allocation, 1);
        var reply = draft.AllocationReply(allocation) as AllocateAmounts;
        check(reply?.Amounts.SequenceEqual([3, 2]) == true && GameCommandPermissions.Allows(allocation, reply),
            "Assign remaining respects recipient caps and emits a complete permitted allocation");
        draft.AdjustAllocation(allocation, 1, 1);
        check(draft.Allocations.SequenceEqual([3, 2]), "A full draft cannot spend its amount twice");
        draft.SetAllocation(allocation, 1, 4);
        check(draft.AllocationReply(allocation) == null, "An over-allocated draft cannot submit");
        draft.ResetAllocation(allocation);
        check(draft.Allocations.SequenceEqual([1, 0]) && draft.AllocationReply(allocation with { ViewerId = 1 }) == null,
            "Reset retains minima while the previous viewer's authority remains scoped");
        var combat = allocation with { Decision = allocation.Decision! with { Id = "combat", Payload = new AllocationPayload(5, AllocationKind.Combat, "", false,
            [Recipient(7, "Blocker", 0, 5, lethal: 3), Recipient(19, "Defender", 0, 5, defender: true)]) } };
        draft.Observe(combat, false); draft.SetAllocation(combat, 0, 2); draft.SetAllocation(combat, 1, 3);
        check(draft.AllocationReply(combat) == null, "Combat allocation retains the engine's reported lethal constraint");
        draft.SetAllocation(combat, 0, 3); draft.SetAllocation(combat, 1, 2);
        check(draft.AllocationReply(combat) is AllocateAmounts permitted && GameCommandPermissions.Allows(combat, permitted),
            "Meeting the engine threshold enables the exact draft without predicting results");
        var expanded = combat with { Decision = combat.Decision! with { Payload = new AllocationPayload(5, AllocationKind.Combat, "", false,
            [Recipient(7, "Blocker", 0, 5, lethal: 3), Recipient(19, "Defender", 0, 5, defender: true), Recipient(41, "Another recipient", 1, 5, "third")]) } };
        check(draft.Observe(expanded, false) && draft.Allocations.SequenceEqual([0, 0, 1]) && !draft.SetAllocation(combat, 0, 4),
            "Changed slots under the same ID rebuild minima and revoke edits from the old shape");
        var tightened = expanded with { Decision = expanded.Decision! with { Payload = new AllocationPayload(4, AllocationKind.Combat, "", false,
            [Recipient(7, "Blocker", 0, 4, lethal: 4), Recipient(19, "Defender", 0, 4, defender: true), Recipient(41, "Another recipient", 1, 4, "third")]) } };
        draft.SetAllocation(expanded, 0, 3);
        check(draft.Observe(tightened, false) && draft.Allocations.SequenceEqual([0, 0, 1]) && !draft.SetAllocation(expanded, 0, 5),
            "Changed totals, caps and thresholds retire old allocations without requiring a new identifier");
        var differentRecipient = tightened with { Decision = tightened.Decision! with { Payload = new AllocationPayload(4, AllocationKind.Combat, "", false,
            [Recipient(7, "Replacement", 0, 4, "replacement", "another-occurrence", 4), Recipient(19, "Defender", 0, 4, defender: true), Recipient(41, "Another recipient", 1, 4, "third")]) } };
        draft.SetAllocation(tightened, 0, 3);
        check(draft.Observe(differentRecipient, false) && draft.Allocations.SequenceEqual([0, 0, 1]) && !draft.SetAllocation(tightened, 0, 2),
            "A replacement recipient cannot inherit another occurrence's amounts");
        draft.Clear();
        check(draft.Selections.Count == 0 && draft.Allocations.Count == 0 && draft.AllocationReply(combat) == null && draft.Input.Length == 0,
            "Reset clears every draft and its submission authority");
        CheckDataOnlyInteractions(check);
    }

    private static GameSnapshot State(Decision decision) => new("game", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0,
        [], [], null, decision);

    private static void CheckDataOnlyInteractions(Action<bool, string> check)
    {
        var types = typeof(UiIntent).Assembly.GetTypes().Where(type => typeof(UiIntent).IsAssignableFrom(type))
            .Append(typeof(TableHit)).Append(typeof(CardPresentationRequest)).ToArray();
        var callbacks = new List<string>();
        foreach (var type in types) {
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                if (type == typeof(TableHit) && property.Name == nameof(TableHit.Shape)) continue;
                if (CarriesDelegate(property.PropertyType)) callbacks.Add(type.Name + "." + property.Name);
            }
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                if (type == typeof(TableHit) && field.Name == "<Shape>k__BackingField") continue;
                if (CarriesDelegate(field.FieldType)) callbacks.Add(type.Name + "." + field.Name);
            }
        }
        check(callbacks.Count == 0, "Published interaction intents, hit payloads and card requests retain no executable callbacks: " + string.Join(", ", callbacks));
        check(typeof(TableHit).GetProperty(nameof(TableHit.Intent))?.PropertyType == typeof(UiIntent)
            && typeof(CardPresentationRequest).GetProperty(nameof(CardPresentationRequest.Activate))?.PropertyType == typeof(UiIntent),
            "Both ordinary controls and presented card gestures cross the same typed intent boundary");
        static bool CarriesDelegate(Type type) => typeof(Delegate).IsAssignableFrom(type)
            || type.HasElementType && type.GetElementType() is { } element && CarriesDelegate(element)
            || type.IsGenericType && type.GetGenericArguments().Any(CarriesDelegate);
    }
}
