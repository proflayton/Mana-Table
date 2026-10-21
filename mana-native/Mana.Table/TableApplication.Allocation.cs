using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string allocationScope = "", allocationDetailId = "";
    // -2 is the overview; -1 is the source; other values are original array slots.
    private int allocationDetail = -2, allocationRulesPage;
    private bool allocationEditing;

    private void EnsureAllocation(Decision d)
    {
        if (allocationScope == Scope && allocations.Length == d.Choices.Length) return;
        allocationScope = Scope;
        allocations = d.Choices.Select((_, index) => AllocationGuide.Minimum(d, index)).ToArray();
        CloseAllocationDetail();
    }

    private void CloseAllocationDetail()
    {
        allocationDetail = -2; allocationDetailId = ""; allocationEditing = false;
        allocationRulesPage = 0; textFocus = false;
    }

    private void OpenAllocationDetail(int index, bool edit = false)
    {
        if (match?.Decision is not { Kind: "allocate" } d || allocationScope != Scope
            || index < -1 || index >= d.Choices.Length) return;
        allocationDetail = index;
        allocationDetailId = index < 0 ? d.SourceCardId : d.Choices[index].Id;
        allocationRulesPage = 0; allocationEditing = edit; textFocus = edit; filterInput = false;
        if (index >= 0) inputText = allocations[index].ToString();
    }

    private bool InspectAllocationAtPointer()
    {
        if (!DecisionModal || match?.Decision?.Kind != "allocate") return false;
        string? id = HitAt(pointer)?.Id;
        if (id == "alloc-inspect-source") { OpenAllocationDetail(-1); return true; }
        const string prefix = "alloc-inspect:";
        if (id?.StartsWith(prefix, StringComparison.Ordinal) == true && int.TryParse(id[prefix.Length..], out int index)) {
            OpenAllocationDetail(index); return true;
        }
        return false;
    }

    private void DrawAllocation(GameSnapshot state, Decision d)
    {
        EnsureAllocation(d);
        if (allocationDetail >= -1) {
            bool current = allocationDetail < 0 ? allocationDetailId == d.SourceCardId
                : allocationDetail < d.Choices.Length && allocationDetailId == d.Choices[allocationDetail].Id;
            if (current) { DrawAllocationDetail(state, d); return; }
            CloseAllocationDetail();
        }
        string unit = AllocationGuide.Unit(d);
        ModalFrame(d.Title.Length > 0 ? d.Title : "Assign " + unit, () => {
            CloseAllocationDetail(); collapsedDecision = true;
        }, "View battlefield", 790);
        DrawAllocationHeader(state, d);
        const int capacity = 6;
        choicePage = Math.Clamp(choicePage, 0, Math.Max(0, (d.Choices.Length - 1) / capacity));
        for (int slot = 0; slot < capacity; slot++) {
            int index = choicePage * capacity + slot;
            if (index >= d.Choices.Length) break;
            DrawAllocationRecipient(state, d, index, 201 + slot % 2 * 606, 331 + slot / 2 * 150);
        }
        if (d.Choices.Length == 0) canvas.Text("The engine has not supplied any recipients.", 220, 400, Muted, .9f, 1130, 2);
        canvas.Line(new(201, 788), new(1398, 788), Muted * .25f);
        string? problem = AllocationGuide.Problem(d, allocations);
        canvas.Text(problem ?? "All assigned. Confirm when you are ready.", 204, 795, problem == null ? ActionAccent : Gold, .67f, 1180, 2, true);
        Button("alloc-prev", "‹", 202, 834, 44, () => choicePage--, choicePage > 0, height: 34);
        Button("alloc-next", "›", 255, 834, 44, () => choicePage++, (choicePage + 1) * capacity < d.Choices.Length, height: 34);
        canvas.Text($"{choicePage + 1} / {Math.Max(1, (d.Choices.Length + capacity - 1) / capacity)} · {d.Choices.Length} recipients", 315, 842, Muted, .65f, 280, 1);
        Button("alloc-reset", "Reset", 602, 834, 100, () => {
            allocations = d.Choices.Select((_, index) => AllocationGuide.Minimum(d, index)).ToArray();
        }, !busy, height: 34);
        if (d.MaySkip) Button("skip", "Assign later", 716, 834, 170, () => Send(new(state.Id, d.Id, ReplyAction.Skip)), !busy, height: 34);
        Button("allocate", "Confirm assignment", 1090, 834, 307, () => {
            if (match is { } current && AllocationGuide.Reply(current, allocations) is { } reply) Send(reply);
        }, !busy && problem == null, true, 37);
    }

    private void DrawAllocationHeader(GameSnapshot state, Decision d)
    {
        var source = InteractionCards.Source(state, d);
        if (source != null) {
            var pose = new Pose(new(250, 242), new(83, 116));
            PaintInteractionCard(source, pose, false, false);
            AllocationArtHit("alloc-inspect-source", source, pose, () => OpenAllocationDetail(-1));
        } else {
            canvas.Panel(new(209, 184, 83, 116), Panel, Muted * .4f);
            canvas.CenterText("SOURCE", new(211, 225, 79, 35), Muted, .55f);
        }
        string seat = d.SourceControllerId is { } controller
            ? AllocationGuide.Seat(state, new(0, "", null) { ControllerId = controller }) : "";
        canvas.Text(source?.Name ?? "Allocation", 310, 185, Ink, .91f, 690, 1, true);
        canvas.Text(seat, 311, 214, Muted, .62f, 680, 1);
        string sourceState = string.Join(" · ", new[] {
            AllocationGuide.Stats(source), AllocationGuide.MarkedDamage(source), AllocationGuide.Counters(source), AllocationGuide.Abilities(source)
        }.Where(value => value.Length > 0));
        canvas.Text(sourceState, 311, 237, Gold, .69f, 685, 2, true);
        canvas.Text(d.Message, 311, 275, Muted, .64f, 685, 2);
        long total = AllocationGuide.Total(allocations), remaining = d.Amount - total;
        canvas.Text("ASSIGN " + d.Amount + " " + AllocationGuide.Unit(d).ToUpperInvariant(), 1024, 185, Gold, .69f, 366, 2, true);
        canvas.Text(total + " assigned", 1024, 230, Ink, 1.12f, 366, 1, true);
        canvas.Text(remaining >= 0 ? remaining + " remaining" : -remaining + " over assigned", 1025, 266,
            remaining == 0 ? ActionAccent : remaining < 0 ? Red : Gold, .82f, 365, 1, true);
        if (d.AllocationKind == "combat" && d.Choices.Any(c => c.IsDefender) && d.Choices.Any(c => c.Lethal.HasValue))
            canvas.Text("Assign required amounts to blockers before the defender.", 312, 305, Muted, .57f, 1060, 1);
        canvas.Line(new(202, 324), new(1398, 324), Muted * .25f);
    }

    private void DrawAllocationRecipient(GameSnapshot state, Decision d, int index, int x, int y)
    {
        var choice = d.Choices[index];
        var card = InteractionCards.Choice(state, choice);
        int value = allocations[index], minimum = AllocationGuide.Minimum(d, index), maximum = AllocationGuide.Maximum(d, index);
        long remaining = d.Amount - AllocationGuide.Total(allocations);
        canvas.Panel(new(x, y, 588, 143), value > 0 ? new Color(29, 47, 59) : Panel,
            value > 0 ? new Color(102, 139, 152) : new Color(68, 82, 98), 7);
        if (card != null) {
            var pose = new Pose(new(x + 52, y + 68), new(80, 112));
            PaintInteractionCard(card, pose, false, false);
            AllocationArtHit("alloc-inspect:" + index, card, pose, () => OpenAllocationDetail(index));
            canvas.CenterText("Inspect", new(x + 10, y + 123, 84, 16), Muted, .49f);
        } else {
            canvas.Panel(new(x + 12, y + 12, 80, 112), new(28, 40, 51), Muted * .35f, 5);
            var player = state.Players.FirstOrDefault(p => p.Id == choice.PlayerId);
            if (player == null) canvas.CenterText("AMOUNT", new(x + 16, y + 45, 72, 38), Muted, .56f, true);
            else {
                canvas.CenterText(player.Life.ToString(), new(x + 16, y + 37, 72, 39), Ink, 1.0f, true);
                canvas.CenterText("LIFE", new(x + 16, y + 78, 72, 21), Muted, .54f);
            }
        }
        canvas.Text((index + 1) + ". " + AllocationGuide.Name(choice, index), x + 106, y + 8, Ink, .78f, 465, 1, true);
        string seat = AllocationGuide.Seat(state, choice);
        canvas.Text(seat.Length == 0 ? choice.IsDefender ? "Defender" : "Recipient" : seat + (choice.IsDefender ? " · Defender" : ""), x + 107, y + 33, Muted, .58f, 460, 1);
        string stats = AllocationGuide.Stats(card);
        string marked = AllocationGuide.MarkedDamage(card);
        string stateLine = string.Join(" · ", new[] { stats.Length > 0 ? "Current " + stats : "", marked }.Where(value => value.Length > 0));
        if (stateLine.Length == 0) stateLine = card?.Type ?? (choice.PlayerId != null ? "Player" : AllocationGuide.Unit(d));
        canvas.Text(stateLine, x + 107, y + 52, Ink, .76f, 462, 1, true);
        canvas.Text(AllocationGuide.Counters(card), x + 107, y + 77, Gold, .61f, 462, 1);
        canvas.Text(AllocationGuide.Abilities(card), x + 107, y + 95, Muted, .61f, 462, 1);
        Button("alloc-fill:" + index, "Remaining", x + 106, y + 114, 117, () => {
            allocations[index] = (int)Math.Min(maximum, Math.Max(value, (long)value + Math.Max(0, d.Amount - AllocationGuide.Total(allocations))));
        }, !busy && remaining > 0 && value < maximum, height: 26);
        if (AllocationGuide.ThresholdLabel(d, choice).Length > 0 && choice.Lethal is { } required)
            Button("alloc-required:" + index, "Required " + required, x + 231, y + 114, 140,
                () => allocations[index] = required, !busy && required >= minimum && required <= maximum && value != required, height: 26);
        Button("amount-less:" + index, "−", x + 385, y + 111, 39, () => allocations[index]--, !busy && value > minimum, height: 29);
        Button("alloc-set:" + index, value.ToString(), x + 430, y + 111, 97, () => OpenAllocationDetail(index, true), !busy, value > 0, 29);
        Button("amount-more:" + index, "+", x + 533, y + 111, 39, () => allocations[index]++, !busy && value < maximum && remaining > 0, height: 29);
    }

    private void AllocationArtHit(string id, Card card, Pose pose, Action inspect)
    {
        if (pose.Contains(pointer)) hovered = card;
        hits.Add(new(id, Scope, pose.Bounds, inspect, card));
    }

    private void DrawAllocationDetail(GameSnapshot state, Decision d)
    {
        int index = allocationDetail;
        Choice? choice = index >= 0 ? d.Choices[index] : null;
        Card? card = choice != null ? InteractionCards.Choice(state, choice) : InteractionCards.Source(state, d);
        string name = choice == null ? card?.Name ?? "Source" : AllocationGuide.Name(choice, index);
        ModalFrame(name, CloseAllocationDetail, "Back", 790);
        // Current characteristics use an exact engine occurrence link. Amounts,
        // thresholds and reply indexes remain on the original current prompt.
        if (card != null) PaintInteractionCard(card, new(new(432, 451), new(330, 462)), false, false);
        else canvas.Panel(new(267, 220, 330, 462), Panel, Muted * .4f);
        string seat = choice == null && d.SourceControllerId is { } controller
            ? AllocationGuide.Seat(state, new(0, "", null) { ControllerId = controller }) : choice == null ? "" : AllocationGuide.Seat(state, choice);
        canvas.Text((index < 0 ? "SOURCE" : "RECIPIENT " + (index + 1)) + (seat.Length > 0 ? " · " + seat : ""), 650, 188, Gold, .7f, 720, 1, true);
        canvas.Text(card?.Type ?? (choice?.PlayerId != null ? "Player" : "Allocation recipient"), 650, 221, Ink, .88f, 720, 2, true);
        canvas.Text(string.Join(" · ", new[] { AllocationGuide.Stats(card), AllocationGuide.MarkedDamage(card) }.Where(value => value.Length > 0)), 651, 275, Gold, 1.05f, 715, 2, true);
        canvas.Text(AllocationGuide.Counters(card), 651, 328, Ink, .75f, 714, 2);
        canvas.Text(AllocationGuide.Abilities(card), 651, 373, Muted, .75f, 714, 2);
        string rules = card?.Text ?? choice?.Detail ?? "";
        var pages = TextPages(rules, 550); allocationRulesPage = Math.Clamp(allocationRulesPage, 0, pages.Length - 1);
        canvas.Text(pages[allocationRulesPage], 651, 423, Ink, .75f, 714, 8);
        if (pages.Length > 1) {
            Button("alloc-rules-prev", "Previous rules", 650, 628, 166, () => allocationRulesPage--, allocationRulesPage > 0, height: 32);
            Button("alloc-rules-next", "More rules", 830, 628, 166, () => allocationRulesPage++, allocationRulesPage + 1 < pages.Length, height: 32);
        }
        if (choice != null) {
            int minimum = AllocationGuide.Minimum(d, index), maximum = AllocationGuide.Maximum(d, index);
            string threshold = AllocationGuide.ThresholdLabel(d, choice);
            canvas.Text($"Assign {AllocationGuide.Unit(d)} · Range {minimum}–{maximum}" + (threshold.Length > 0 ? " · " + threshold : ""), 651, 683, Gold, .69f, 715, 2);
            Button("alloc-input", allocationEditing ? inputText.Length == 0 ? "Type amount…" : inputText : allocations[index].ToString(), 651, 737, 265, () => {
                allocationEditing = true; inputText = allocations[index].ToString(); textFocus = true; filterInput = false;
            }, !busy, allocationEditing, 45);
            bool valid = int.TryParse(inputText, out int amount) && amount >= minimum && amount <= maximum;
            Button("alloc-apply", "Set amount", 932, 737, 206, () => {
                allocations[index] = amount; CloseAllocationDetail();
            }, !busy && allocationEditing && valid, true, 45);
            if (allocationEditing && !valid) canvas.Text($"Enter a whole number from {minimum} to {maximum}.", 651, 792, Red, .68f, 714, 2);
            else canvas.Text($"Draft: {allocations[index]} · {d.Amount - AllocationGuide.Total(allocations)} remaining overall. The assignment is not submitted here.", 651, 792, Muted, .66f, 714, 2);
        }
        Button("alloc-back", "Back to assignment", 267, 818, 330, CloseAllocationDetail, accent: true, height: 43);
    }
}
