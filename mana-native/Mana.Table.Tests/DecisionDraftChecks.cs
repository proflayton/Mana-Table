using System.Reflection;
using Mana.Contracts;
using Mana.Table.Interaction;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static class DecisionDraftChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var choice = State(new() { Id = "order", Kind = "choice", Ordered = true, Min = 3, Max = 3,
            Choices = [new(7, "First", null), new(19, "Second", null), new(41, "Third", null)] });
        var draft = new DecisionDraft();
        check(draft.Observe(choice, false) && draft.Selections.SequenceEqual([7, 19, 41]),
            "An ordered gallery starts with the provider's exact choice indices rather than array positions");
        draft.ClearSelection(); draft.Toggle(choice, 41); draft.Toggle(choice, 7); draft.Toggle(choice, 19);
        var ordered = draft.Selections;
        check(!draft.Observe(choice with { Revision = 50 }, false) && draft.Selections.SequenceEqual([41, 7, 19])
            && draft.Position(41) == 1 && draft.Position(7) == 2 && draft.Position(19) == 3,
            "Repeated observations preserve the player's chosen order and its visible position numbers");
        bool collectionProtected = true;
        if (ordered is ICollection<int> collection) {
            try { collection.Add(999); collectionProtected = false; } catch (NotSupportedException) { }
        }
        check(collectionProtected && draft.Selections.SequenceEqual([41, 7, 19]),
            "A published draft selection cannot be changed by a presentation consumer");

        var direct = new DecisionDraft(); direct.Observe(choice, true);
        check(direct.Selections.Count == 0, "An ordered direct battlefield choice waits for explicit player selections");
        var optional = State(new() { Id = "optional", Kind = "choice", Max = 2,
            Choices = [new(7, "First", null), new(19, "Second", null), new(41, "Third", null)] });
        draft.Observe(optional, false);
        check(!draft.Toggle(optional, 1) && !draft.Toggle(optional, -1) && draft.Toggle(optional, 19)
            && draft.Toggle(optional, 7) && !draft.Toggle(optional, 41) && draft.Selections.SequenceEqual([19, 7]),
            "Choice staging admits only published indices and never exceeds the current maximum");
        var nextPrompt = optional with { Decision = optional.Decision! with { Id = "next-prompt" } };
        check(draft.Observe(nextPrompt, false) && draft.Selections.Count == 0 && !draft.Toggle(optional, 7),
            "Replacing a prompt revokes its previous selections and rejects edits from the retired prompt");
        draft.Toggle(nextPrompt, 7);
        var otherGame = nextPrompt with { Id = "another-game" };
        check(draft.Observe(otherGame, false) && draft.Selections.Count == 0 && !draft.Toggle(nextPrompt, 19),
            "A different game cannot inherit a draft merely because it reuses the decision identifier");
        draft.Toggle(otherGame, 19);
        var otherViewer = otherGame with { ViewerId = 1 };
        check(draft.Observe(otherViewer, false) && draft.Selections.Count == 0 && !draft.Toggle(otherGame, 7),
            "Changing the viewer retires the preceding seat's draft even within the same game and decision");
        draft.Toggle(otherViewer, 7);
        check(draft.Observe(otherViewer with { Decision = null }, false) && draft.Selections.Count == 0,
            "Leaving a required decision removes its unsubmitted local choices");

        var single = optional with { Decision = optional.Decision! with { Id = "single", Max = 1 } };
        draft.Observe(single, false); draft.Toggle(single, 7); draft.Toggle(single, 41);
        check(draft.Selections.SequenceEqual([41]), "Choosing a different single option replaces the previous local selection");
        var library = State(new() { Id = "library", Kind = "choice", Max = 1,
            LibraryCards = [new(null, "Visible reference", new() { Name = "Forest" }), new(83, "Offered choice", new() { Name = "Forest" })] });
        draft.Observe(library, false);
        check(!draft.Toggle(library, 0) && !draft.Toggle(library, 1) && draft.Toggle(library, 83)
            && draft.Selections.SequenceEqual([83]),
            "A library gallery accepts the offered choice handle while reference faces and array slots grant no selection authority");
        var allocation = State(new() { Id = "allocation", Kind = "allocate", Amount = 5,
            Choices = [new(7, "Recipient A", null) { Minimum = 1, Maximum = 3 },
                new(19, "Recipient B", null) { Minimum = 0, Maximum = 4 }] });
        draft.Observe(allocation, false);
        check(draft.Allocations.SequenceEqual([1, 0]) && !draft.SetAllocation(allocation, -1, 1)
            && !draft.SetAllocation(allocation, 2, 1) && !draft.SetAllocation(allocation, 0, 0)
            && !draft.SetAllocation(allocation, 0, 4) && draft.AllocationReply(allocation) == null,
            "Allocation staging starts from engine minima and rejects invalid slots, under-minimum and over-maximum amounts");
        draft.AssignRemaining(allocation, 0); draft.AssignRemaining(allocation, 1);
        var reply = draft.AllocationReply(allocation) as AllocateAmounts;
        check(reply?.Amounts.SequenceEqual([3, 2]) == true && GameCommandPermissions.Allows(allocation, reply),
            "Assign remaining respects each recipient cap and emits only a complete permitted allocation");
        draft.AdjustAllocation(allocation, 1, 1);
        check(draft.Allocations.SequenceEqual([3, 2]), "Incrementing a full allocation cannot spend the available amount twice");
        draft.SetAllocation(allocation, 1, 4);
        check(draft.AllocationReply(allocation) == null, "An over-allocated editable draft cannot become an engine command");
        draft.ResetAllocation(allocation);
        check(draft.Allocations.SequenceEqual([1, 0]) && draft.AllocationReply(allocation with { ViewerId = 1 }) == null,
            "Reset preserves provider minima while a different viewer cannot submit this allocation");
        var combat = allocation with { Decision = allocation.Decision! with { Id = "combat", AllocationKind = "combat",
            Choices = [new(7, "Blocker", null) { Lethal = 3, Maximum = 5 }, new(19, "Defender", null) { IsDefender = true, Maximum = 5 }] } };
        draft.Observe(combat, false); draft.SetAllocation(combat, 0, 2); draft.SetAllocation(combat, 1, 3);
        check(draft.AllocationReply(combat) == null, "Combat damage cannot reach the defender before the engine's reported lethal threshold");
        draft.SetAllocation(combat, 0, 3); draft.SetAllocation(combat, 1, 2);
        check(draft.AllocationReply(combat) is AllocateAmounts permitted && GameCommandPermissions.Allows(combat, permitted),
            "Meeting the reported threshold enables the exact drafted combat allocation without predicting damage results");
        var expanded = combat with { Decision = combat.Decision! with { Choices = [.. combat.Decision.Choices,
            new(41, "Another recipient", null) { Id = "third", Minimum = 1, Maximum = 5 }] } };
        check(draft.Observe(expanded, false) && draft.Allocations.SequenceEqual([0, 0, 1])
            && !draft.SetAllocation(combat, 0, 4),
            "Changing allocation slots under the same decision identity rebuilds minima and revokes edits from the old shape");
        var tightened = expanded with { Decision = expanded.Decision! with { Amount = 4, Choices = expanded.Decision.Choices
            .SetItem(0, expanded.Decision.Choices[0] with { Lethal = 4, Maximum = 4 }) } };
        draft.SetAllocation(expanded, 0, 3);
        check(draft.Observe(tightened, false) && draft.Allocations.SequenceEqual([0, 0, 1])
            && !draft.SetAllocation(expanded, 0, 5),
            "Changed totals, caps and combat thresholds invalidate the previous draft even when slot count and decision identity are unchanged");
        draft.SetAllocation(tightened, 0, 3);
        var differentRecipient = tightened with { Decision = tightened.Decision! with { Choices = tightened.Decision.Choices
            .SetItem(0, tightened.Decision.Choices[0] with { Id = "replacement", CardId = "another-occurrence" }) } };
        check(draft.Observe(differentRecipient, false) && draft.Allocations.SequenceEqual([0, 0, 1])
            && !draft.SetAllocation(tightened, 0, 2),
            "Replacing a recipient cannot inherit amounts staged for a different occurrence in the same slot");
        draft.Clear();
        check(draft.Selections.Count == 0 && draft.Allocations.Count == 0 && draft.AllocationReply(combat) == null,
            "Resetting the match clears both choice and allocation authority together");

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
