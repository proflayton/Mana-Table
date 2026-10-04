using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void ModalFrame(string title, Action close, string closeLabel = "Close", int height = 706)
    {
        // Modal hit regions replace the table's regions, including invisible cards.
        hits.Clear(); hovered = null; readingCards.Clear(); liftedCards.Clear();
        canvas.Fill(new(0, 60, 1600, 840), Color.Black * .72f);
        canvas.Panel(new(166, 94, 1268, height), new(23, 30, 40), new(98, 113, 130), 14);
        canvas.Text(title, 192, 115, Ink, 1.12f, 990, 2, true);
        Button("close-overlay", closeLabel, 1253, 112, 154, close, height: 34);
        canvas.Line(new(191, 161), new(1410, 161), Muted * .24f);
    }
    private void DrawOverlays()
    {
        if (overlay.Length > 0) {
            switch (overlay) {
                case "inspect": DrawInspector(); break;
                case "settings": DrawSettings(); break;
                case "history": DrawHistory(); break;
                case "action": DrawActionDetails(); break;
                case "player": DrawPlayerDetails(); break;
                case "concede": DrawConcede(); break;
                case "help": DrawHelp(); break;
                case "combat": DrawCombatReview(); break;
                case "blockers": DrawBlockerReview(); break;
                case "result": DrawResult(); break;
            }
        } else if (zoneName != null) DrawZone();
        else if (DecisionModal) DrawDecisionOverlay();
    }
    private static string[] TextPages(string? value, int size = 1050)
    {
        string rest = value ?? ""; var pages = new List<string>();
        while (rest.Length > size) {
            int end = rest.LastIndexOf(' ', size, size);
            if (end < size / 2) end = size;
            pages.Add(rest[..end]); rest = rest[end..].TrimStart();
        }
        pages.Add(rest); return pages.ToArray();
    }
    private void DrawInspector()
    {
        if (inspected == null) { overlay = ""; return; }
        var original = inspected;
        var card = inspectBack && original.OtherFace is { } back
            ? original with { Name = back.Name, Type = back.Type, ManaCost = back.ManaCost, Text = back.OracleText, Power = back.Power, Toughness = back.Toughness, ArtName = back.ArtName, ArtFace = back.ArtFace }
            : original;
        ModalFrame(card.Name, () => { overlay = ""; inspected = null; });
        PaintCard(card, new(new(447, 460), new(380, 532)), false, false);
        canvas.Text(card.Type, 686, 191, Gold, .9f, 690, 2, true);
        canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, 686, 248, Ink, 1, 675, 1);
        string values = card.Type.Contains("Creature") ? $"Power / toughness: {card.Power}/{card.Toughness}" : "";
        if (card.Damage > 0) values += "   Damage marked: " + card.Damage;
        canvas.Text(values, 686, 287, Ink, .78f, 674, 3);
        bool onBattlefield = match!.Players.Any(p => p.Zone("Battlefield").Cards.Any(c => SameVisibleCard(c, original)));
        string stateText = onBattlefield ? string.Join(" · ", CardPresentation.StateLabels(card)) : "";
        if (CardPresentation.Counters(card) is { Length: > 0 } counters) stateText += "\nCounters: " + string.Join("; ", counters.Select(c => c.Key + " × " + c.Value));
        if (onBattlefield) canvas.Text(CardActions.Hint(match, original), 686, 335, CardActions.CanActivate(match, original) ? ActionAccent : Muted, .77f, 670, 2, true);
        var paragraphs = TextPages((stateText.Length > 0 ? stateText + "\n\n" : "") + card.Text + (onBattlefield ? "\n\n" + CardActions.Help : ""), onBattlefield ? 750 : 1050); inspectPage = Math.Clamp(inspectPage, 0, paragraphs.Length - 1);
        canvas.Text(paragraphs[inspectPage], 686, onBattlefield ? 394 : 378, Ink, .8f, 660, onBattlefield ? 12 : 17);
        if (paragraphs.Length > 1) {
            Button("inspect-prev", "Previous", 686, 727, 135, () => inspectPage--, inspectPage > 0);
            Button("inspect-next", "More rules", 831, 727, 145, () => inspectPage++, inspectPage + 1 < paragraphs.Length);
            canvas.Text($"{inspectPage + 1} / {paragraphs.Length}", 995, 738, Muted, .7f);
        }
        if (original.OtherFace != null) Button("other-face", inspectBack ? "View current face" : "View other face", 274, 737, 347, () => { inspectBack = !inspectBack; inspectPage = 0; }, accent: true);
        if (onBattlefield) Button("activate-ability", "Activate ability", 1080, 727, 280, () => { overlay = ""; CardClick(original); }, !busy && CardActions.CanActivate(match, original), true);
    }
    private void DrawSettings()
    {
        ModalFrame("Play controls", () => { overlay = ""; textFocus = false; });
        canvas.Text("PRIORITY", 201, 196, Gold, .65f, 600, 1, true);
        Button("settings-auto", "Auto", 202, 231, 256, () => SetAutomatic(true), accent: automatic, height: 48);
        Button("settings-manual", "Full control", 472, 231, 256, () => SetAutomatic(false), accent: !automatic, height: 48);
        canvas.Text("Auto lets each visible action play out before passing when you have no response. Lands, affordable commanders, required choices, attacks, and legal blocks still wait for you.", 202, 298, Muted, .81f, 544, 5);
        Button("settings-hold", held ? "Held for this turn" : "Hold priority this turn", 202, 445, 526, () => { held = !held; priority.Reset(); }, accent: held, height: 45);
        canvas.Text("Always stop at these steps on your turn", 805, 196, Gold, .76f, 551, 1, true);
        for (int i = 0; i < TurnGuide.Stops.Length; i++) {
            var stop = TurnGuide.Stops[i]; bool selected = preferences.Stops.Contains(stop.Key);
            Button("stop:" + stop.Key, (selected ? "✓  " : "○  ") + stop.Label, 805 + i % 2 * 285, 237 + i / 2 * 61, 267, () => { if (!preferences.Stops.Remove(stop.Key)) preferences.Stops.Add(stop.Key); SavePreferences(); }, accent: selected, height: 46);
        }
        Button("motion", preferences.ReducedMotion ? "Reduced motion: On" : "Reduced motion: Off", 805, 455, 553, () => { preferences = preferences with { ReducedMotion = !preferences.ReducedMotion }; SavePreferences(); }, accent: preferences.ReducedMotion, height: 45);
        Button("sound", preferences.Sounds ? "Sound cues: On" : "Sound cues: Off", 805, 512, 553, () => { preferences = preferences with { Sounds = !preferences.Sounds }; SavePreferences(); }, accent: preferences.Sounds, height: 45);
        canvas.Line(new(201, 607), new(1370, 607), Muted * .25f);
        canvas.Text("Space  Continue / confirm     F  Auto / full control     Ctrl  Hold this turn\nC  Review combat     Escape  Cancel a drag / close a panel     F11  Fullscreen\nRight-click  Inspect a card     Wheel  Browse crowded ranks or a long hand", 203, 632, Ink, .78f, 1150, 5);
    }
    private void DrawHistory()
    {
        ModalFrame("Match history", () => overlay = "");
        var events = match!.Notices.Reverse().Select(message => (Label: "NOTICE", Message: message))
            .Concat(match.Activity.Reverse().Select(entry => (Label: "TURN " + entry.Turn, Message: entry.Message + (entry.Detail.Length > 0 ? " " + entry.Detail : "")))).ToArray(); int count = 10;
        historyPage = Math.Clamp(historyPage, 0, Math.Max(0, (events.Length - 1) / count));
        int row = 0;
        foreach (var entry in events.Skip(historyPage * count).Take(count)) {
            int y = 189 + row++ * 49;
            canvas.Text(entry.Label, 204, y + 1, Gold, .58f, 96, 1, true);
            canvas.Text(entry.Message, 317, y, Ink, .75f, 1032, 2);
            canvas.Line(new(204, y + 40), new(1370, y + 40), Muted * .12f);
        }
        if (events.Length == 0) canvas.Text("No events have been reported for this game yet.", 206, 214, Muted, .9f);
        Button("history-prev", "Newer", 205, 739, 145, () => historyPage--, historyPage > 0);
        Button("history-next", "Older", 362, 739, 145, () => historyPage++, (historyPage + 1) * count < events.Length);
        canvas.Text("Only events visible to your seat are shown.", 790, 752, Muted, .65f, 574, 1);
    }
    private void DrawPlayerDetails()
    {
        var player = match!.Players.FirstOrDefault(p => p.Id == zonePlayer);
        if (player == null) { overlay = ""; return; }
        ModalFrame(player.Name, () => overlay = "");
        canvas.Text(player.Life + " LIFE", 212, 202, Ink, 2, 1090, 1, true);
        string handLimit = player.MaxHandSize is { } maximum ? $"Maximum hand size: {maximum} · Discard during this player's cleanup" : "No maximum hand size";
        canvas.Text($"{player.Zone("Hand").Count} cards in hand · {handLimit}", 213, 258, Muted, .7f, 1090, 1);
        canvas.Text("COMMANDER DAMAGE RECEIVED", 213, 289, Gold, .76f, 1000, 1, true);
        int y = 343;
        foreach (var entry in player.CommanderDamage.Take(8)) {
            canvas.Text(entry.Name, 216, y, Ink, .89f, 815, 1, true);
            canvas.Text(entry.Owner, 216, y + 28, Muted, .66f, 815, 1);
            canvas.Text(entry.Damage + " / 21", 1121, y, entry.Damage >= 18 ? Red : Gold, 1.2f, 200, 1, true);
            y += 47;
        }
        canvas.Text("Each opposing commander is tracked separately.", 216, 741, Muted, .75f, 970, 1);
    }
    private void DrawConcede()
    {
        ModalFrame("Concede this game?", () => overlay = "", "Keep playing");
        canvas.Text("Your seat will lose this game. This cannot be undone.", 244, 263, Ink, 1.2f, 1090, 3);
        canvas.Text(localMatch ? "The solo game ends when you concede." : "The remaining players can continue playing.", 244, 353, Muted, .92f, 1090, 3);
        string id = match!.Id;
        Button("confirm-concede", "Concede game", 1010, 690, 323, () => { overlay = ""; Work(async () => { await engine.ConcedeAsync(id); var next = await engine.ObserveAsync(); updates.Enqueue(() => Accept(next)); }); }, !busy, true, 57);
    }
    private void DrawHelp()
    {
        ModalFrame("Current decision", () => overlay = "");
        var text = TextPages((match!.Decision?.Message ?? TurnGuide.Instruction(match)) + "\n\n" + (match.Combat?.BlockProblem ?? "") + "\n\n" + CardActions.Help, 2100);
        inspectPage = Math.Clamp(inspectPage, 0, text.Length - 1);
        canvas.Text(text[inspectPage], 211, 200, Ink, .93f, 1155, 21);
        Button("help-prev", "Previous", 211, 736, 144, () => inspectPage--, inspectPage > 0);
        Button("help-next", "More", 368, 736, 144, () => inspectPage++, inspectPage + 1 < text.Length);
    }
    private void DrawZone()
    {
        if (zoneName == "Stack") { DrawStackDetails(); return; }
        var player = match!.Players.FirstOrDefault(p => p.Id == zonePlayer);
        var zone = player?.Zone(zoneName!);
        var cards = zoneName == "Stack" ? match.Stack.Where(s => s.Card != null).Select(s => s.Card!).ToArray() : zone?.Cards ?? [];
        ModalFrame(zoneName + (zoneName == "Stack" ? "" : " · " + player?.Name), () => zoneName = null);
        int count = 7; zonePage = Math.Clamp(zonePage, 0, Math.Max(0, (cards.Length - 1) / count));
        canvas.Text(cards.Length + " visible" + (zoneName == "Stack" ? "" : " / " + (zone?.Count ?? 0) + " total"), 205, 183, Muted, .72f, 650, 1);
        int index = 0;
        foreach (var card in cards.Skip(zonePage * count).Take(count)) {
            int x = 210 + index++ * 169;
            var pose = new Pose(new(x + 74, 409), new(148, 207));
            PaintCard(card, pose, card.Highlighted, IsCardActionable(card));
            canvas.Text(card.Name, x, 533, Ink, .68f, 155, 3, true);
            canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, x, 600, Gold, .62f, 156, 2);
            if (pose.Contains(pointer)) hovered = card;
            hits.Add(new("card:" + card.Key + ":" + card.VisualId, Scope, new(x, 304, 148, 298), () => { if (IsCardActionable(card)) { zoneName = null; CardClick(card); } else Inspect(card); }, card, zoneName, zonePlayer));
        }
        if (cards.Length == 0) canvas.CenterText("No cards are visible in this zone.", new(230, 333, 1120, 100), Muted, 1.05f);
        Button("zoneprev", "Previous", 208, 734, 160, () => zonePage--, zonePage > 0);
        Button("zonenext", "Next", 380, 734, 160, () => zonePage++, (zonePage + 1) * count < cards.Length);
        canvas.Text("Right-click a card for its complete rules.", 917, 746, Muted, .66f, 474, 1);
    }
    private void DrawStackDetails()
    {
        var entries = match!.Stack;
        ModalFrame("The stack", () => zoneName = null);
        if (entries.Length == 0) { canvas.Text("The stack is empty.", 219, 222, Muted, 1); return; }
        zonePage = Math.Clamp(zonePage, 0, entries.Length - 1);
        if (!string.IsNullOrEmpty(inspectedStack)) {
            int current = Array.FindIndex(entries, s => s.Id == inspectedStack);
            if (current < 0) {
                canvas.Text("The inspected spell or ability has left the stack.", 219, 235, Ink, 1.05f, 1100, 2);
                Button("stack-current", "Inspect next to resolve", 220, 345, 412, () => OpenStack(), accent: true, height: 51);
                return;
            }
            zonePage = current;
        }
        var entry = entries[zonePage];
        canvas.Text($"{zonePage + 1} / {entries.Length}   " + (zonePage == 0 ? "Next to resolve" : "Waiting on the stack"), 217, 190, Gold, .8f, 1100, 1, true);
        if (entry.Card is { } card) {
            var pose = new Pose(new(408, 459), new(304, 426));
            PaintCard(card, pose, false, IsCardActionable(card));
            if (pose.Contains(pointer)) hovered = card;
            if (IsCardActionable(card)) hits.Add(new("stack-target:" + zonePage, Scope, pose.Bounds, () => { zoneName = null; CardClick(card); }, card));
        }
        canvas.Text(entry.Name, 630, 246, Ink, 1.05f, 716, 3, true);
        canvas.Text(entry.Ability ? "ABILITY" : "SPELL", 631, 328, Gold, .64f, 700, 1, true);
        var text = TextPages(entry.Text, 1000); inspectPage = Math.Clamp(inspectPage, 0, text.Length - 1);
        canvas.Text(text[inspectPage], 632, 369, Ink, .8f, 714, 15);
        Button("stack-newer", "Above", 216, 734, 130, () => OpenStack(zonePage - 1), zonePage > 0);
        Button("stack-older", "Below", 359, 734, 130, () => OpenStack(zonePage + 1), zonePage + 1 < entries.Length);
        if (text.Length > 1) {
            Button("stack-text-prev", "Previous text", 1002, 734, 171, () => inspectPage--, inspectPage > 0);
            Button("stack-text-next", "More text", 1187, 734, 171, () => inspectPage++, inspectPage + 1 < text.Length);
        }
    }
    private void DrawDecisionOverlay()
    {
        var state = match!; var d = state.Decision!;
        ModalFrame(d.Title.Length > 0 ? d.Title : d.Kind == "reveal" ? "Revealed cards" : d.Kind == "allocate" ? "Assign damage or counters" : "Make your choice", () => { collapsedDecision = true; textFocus = false; }, "View battlefield", d.Kind is "choice" or "reveal" ? 790 : 706);
        canvas.Text(d.Message, 204, 181, Ink, .77f, 1185, 3);
        if (d.Kind is "number" or "text") { DrawValueDecision(state, d); return; }
        if (d.Kind == "allocate") { DrawAllocation(state, d); return; }
        DrawChoices(state, d);
    }
    private void ToggleChoice(Decision d, int index)
    {
        if (selections.Remove(index)) return;
        if (d.Max == 1) selections.Clear();
        if (selections.Count < d.Max) selections.Add(index);
    }
    private void DrawChoices(GameSnapshot state, Decision d)
    {
        var items = d.LibraryCards.Length > 0
            ? d.LibraryCards.Select(c => (c.Index, c.Label, Card: (Card?)c.Card, Detail: "", Mana: "")).ToArray()
            : d.Choices.Select(c => (Index: (int?)c.Index, c.Label, c.Card, c.Detail, c.Mana)).ToArray();
        var filtered = items.Select((item, slot) => (Item: item, Slot: slot))
            .Where(c => c.Item.Label.Contains(choiceFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool portraits = items.Any(c => c.Card != null); int capacity = portraits ? 7 : 6;
        choicePage = Math.Clamp(choicePage, 0, Math.Max(0, (filtered.Length - 1) / capacity));
        string instruction = d.Kind == "reveal" ? "Hover to read these cards, then continue." : d.Ordered ? "Select cards in the order you want them. Numbers show your chosen order." : $"Choose {d.Min}–{d.Max}. Selected: {selections.Count}.";
        canvas.Text(instruction, 206, 251, Gold, .72f, 1160, 2);
        var pageItems = filtered.Skip(choicePage * capacity).Take(capacity).ToArray();
        int expanded = Array.FindIndex(pageItems, e => readingId == "choice-face:" + Scope + ":" + e.Slot);
        int row = 0;
        foreach (var entry in pageItems) {
            var item = entry.Item;
            int index = row++; bool selected = item.Index is { } id && selections.Contains(id);
            bool available = d.Kind != "reveal" && item.Index != null && !busy;
            string buttonId = item.Index is { } cardIndex ? "choice:" + cardIndex : "reveal:" + entry.Slot;
            var captured = item;
            if (portraits) {
                // Make room for the original face as it grows, so it cannot
                // completely bury an adjacent revealed card or choice.
                int column = expanded < 0 ? 148 : index == expanded ? 410 : 112;
                int x = expanded < 0 ? 206 + index * 169 : 190 + index * 128 + (index > expanded ? 298 : 0);
                int faceWidth = expanded >= 0 && index != expanded ? 112 : 148;
                var pose = new Pose(new(x + column / 2, 408), new(faceWidth, faceWidth * 1.4f));
                if (item.Card != null) DrawTableCard("choice-face:" + Scope + ":" + entry.Slot, item.Card, pose, "Choice", null,
                    buttonId, () => { if (available) ToggleChoice(d, captured.Index!.Value); },
                    (displayed, focus) => PaintCard(item.Card, displayed, selected, available, focus),
                    choiceSlot: entry.Slot, activationBounds: new(x, 304, column, 329));
                else canvas.Panel(new(x, 304, 148, 207), Panel, Muted);
                canvas.Text(item.Label, x + 1, 526, Ink, .66f, column, 3, true);
                string badge = selected ? d.Ordered ? "ORDER " + (selections.IndexOf(item.Index!.Value) + 1) : "SELECTED" : !available ? "VIEW ONLY" : "SELECT";
                canvas.CenterText(badge, new(x, 605, column, 28), selected ? Gold : Muted, .56f, true);
                if (available && item.Card == null) hits.Add(new(buttonId, Scope, new(x, 304, 150, 329), () => ToggleChoice(d, captured.Index!.Value)));
            } else {
                int x = 206 + index % 2 * 602, y = 302 + index / 2 * 116;
                var bounds = new Rectangle(x, y, 585, 103);
                canvas.Panel(bounds, selected ? new Color(36, 63, 79) : Panel, selected ? Gold : new Color(77, 90, 105), 7);
                int offset = item.Mana.Length > 0 ? 63 : 16;
                if (item.Mana.Length > 0) ManaOrb(item.Mana.Trim('{', '}'), 1, new(x + 33, y + 48));
                canvas.Text((selected ? "✓  " : "") + item.Label, x + offset, y + 12, Ink, .83f, 563 - offset, 3, true);
                if (item.Detail.Length > 0) canvas.Text(item.Detail, x + offset, y + 69, Muted, .65f, 563 - offset, 1);
                if (available) hits.Add(new(buttonId, Scope, bounds, () => ToggleChoice(d, captured.Index!.Value)));
            }
        }
        if (filtered.Length == 0) canvas.CenterText("No matching cards or choices.", new(209, 356, 1177, 90), Muted, 1);
        Button("choicesprev", "‹", 206, 754, 45, () => choicePage--, choicePage > 0, height: 34);
        Button("choicesnext", "›", 260, 754, 45, () => choicePage++, (choicePage + 1) * capacity < filtered.Length, height: 34);
        canvas.Text($"Page {choicePage + 1} / {Math.Max(1, (filtered.Length + capacity - 1) / capacity)}", 325, 761, Muted, .66f, 350, 1);
        Button("choicesfilter", choiceFilter.Length == 0 ? "Search choices…" : choiceFilter, 808, 754, 587, () => { textFocus = true; filterInput = true; }, accent: textFocus && filterInput, height: 34);
        if (textFocus && filterInput) canvas.Text("Type to filter. Backspace edits. Escape closes the gallery.", 810, 794, Muted, .56f, 580, 1);
        Button("choicesclear", "Clear selection", 206, 819, 183, () => selections.Clear(), !busy && d.Kind != "reveal");
        if (d.Min == 0 && d.Kind != "reveal") Button("choice-cancel", "Choose none", 405, 819, 182, () => Send(new(state.Id, d.Id, ReplyAction.Choose, Choices: [])), !busy);
        Button("choicesdone", d.Kind == "reveal" ? "Continue" : "Confirm selection", 1088, 814, 307, () => { textFocus = false; Send(new(state.Id, d.Id, ReplyAction.Choose, Choices: selections.ToArray())); }, !busy && (d.Kind == "reveal" || selections.Count >= d.Min && selections.Count <= d.Max), true, 47);
    }
    private void DrawValueDecision(GameSnapshot state, Decision d)
    {
        bool numeric = d.Kind == "number" || d.Numeric;
        canvas.Text(numeric && d.Kind == "number" ? $"Choose a number from {d.Min} to {d.Max}." : "Enter your answer.", 228, 315, Gold, .94f, 1127, 2);
        Button("text-input", inputText.Length == 0 ? "Click here and type…" : inputText, 230, 399, 1100, () => { textFocus = true; filterInput = false; }, accent: textFocus, height: 66);
        if (d.Kind == "number") {
            Button("number-less", "−", 232, 490, 65, () => { if (int.TryParse(inputText, out int value)) inputText = Math.Clamp((long)value - 1, d.Min, d.Max).ToString(); }, height: 44);
            Button("number-more", "+", 310, 490, 65, () => { if (int.TryParse(inputText, out int value)) inputText = Math.Clamp((long)value + 1, d.Min, d.Max).ToString(); }, height: 44);
        }
        bool valid = !numeric || int.TryParse(inputText, out int number) && (d.Kind != "number" || number >= d.Min && number <= d.Max);
        if (!valid) canvas.Text("Enter a whole number within the permitted range.", 231, 559, Red, .78f, 1090, 2);
        Button("value", "Confirm", 1090, 718, 266, () => { textFocus = false; Send(new(state.Id, d.Id, ReplyAction.Value, Value: inputText)); }, valid && !busy, true, 54);
    }
    private void DrawAllocation(GameSnapshot state, Decision d)
    {
        long total = allocations.Sum(v => (long)v);
        canvas.Text($"Assign {d.Amount} total. Assigned: {total}. Remaining: {d.Amount - total}.", 213, 265, Gold, .9f, 1135, 2, true);
        int capacity = 5; choicePage = Math.Clamp(choicePage, 0, Math.Max(0, (d.Choices.Length - 1) / capacity));
        int row = 0;
        foreach (var choice in d.Choices.Skip(choicePage * capacity).Take(capacity)) {
            int index = Array.IndexOf(d.Choices, choice), y = 328 + row++ * 68;
            int min = d.AtLeastOne ? 1 : 0, max = d.Limits.Length > index ? d.Limits[index] : d.Amount;
            canvas.Text(choice.Label, 220, y + 9, Ink, .78f, 780, 2);
            Button("amount-less:" + index, "−", 1081, y, 56, () => allocations[index]--, allocations[index] > min, height: 44);
            canvas.CenterText(allocations[index].ToString(), new(1146, y, 104, 44), Gold, 1.1f, true);
            Button("amount-more:" + index, "+", 1260, y, 56, () => allocations[index]++, allocations[index] < max && total < d.Amount, height: 44);
        }
        Button("alloc-prev", "Previous", 218, 720, 145, () => choicePage--, choicePage > 0);
        Button("alloc-next", "Next", 375, 720, 145, () => choicePage++, (choicePage + 1) * capacity < d.Choices.Length);
        if (d.MaySkip) Button("skip", "Assign later", 537, 720, 179, () => Send(new(state.Id, d.Id, ReplyAction.Skip)), !busy);
        Button("allocate", "Confirm assignment", 1082, 713, 291, () => Send(new(state.Id, d.Id, ReplyAction.Allocate, Amounts: allocations)), !busy && total == d.Amount, true, 52);
    }
}
