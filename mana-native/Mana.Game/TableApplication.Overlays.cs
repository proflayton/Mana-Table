using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private int playerDamagePage;
    internal sealed record ChoiceOrderBadge(int Index, int Position, Rectangle Bounds, string Text, int MeasuredWidth, int AvailableWidth);
    private readonly Dictionary<int, ChoiceOrderBadge> choiceOrderBadges = [];
    private void PaintChoiceOrder(int index, Rectangle bounds, float fontSize = .56f)
    {
        int position = decisionDraft.Position(index);
        if (position == 0) return;
        string caption = "ORDER " + position;
        float size = fontSize;
        int available = bounds.Width - 8;
        while (size > .45f && canvas.TextWidth(caption, size, true) > available) size = Math.Max(.45f, size - .025f);
        if (canvas.TextWidth(caption, size, true) > available) {
            caption = position.ToString();
            size = fontSize;
            while (size > .45f && canvas.TextWidth(caption, size, true) > available) size = Math.Max(.45f, size - .025f);
        }
        canvas.Panel(bounds, new(34, 48, 49), Gold, 4);
        canvas.CenterText(caption, new(bounds.X + 4, bounds.Y, available, bounds.Height), Gold, size, true);
        if (Automated) choiceOrderBadges[index] = new(index, position, bounds, caption, canvas.TextWidth(caption, size, true), available);
    }
    private void ModalFrame(string title, UiIntent close, string closeLabel = "Close", int height = 706)
    {
        // Modal hit regions replace the table's regions, including invisible cards.
        hits.Clear(); hovered = null; ClearCardComposition();
        canvas.Fill(new(0, 60, 1600, 840), Color.Black * .72f);
        canvas.Panel(new(166, 94, 1268, height), new(23, 30, 40), new(98, 113, 130), 14);
        int closeWidth = Math.Clamp(canvas.TextWidth(closeLabel, .76f) + 24, 154, 400);
        int closeX = 1407 - closeWidth;
        canvas.Text(title, 192, 115, Ink, 1.12f, Math.Min(990, closeX - 212), 2, true);
        Button("close-overlay", closeLabel, closeX, 112, closeWidth, close, height: 34);
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
                case "mana": DrawManaDetails(); break;
                case "table-state": DrawTableStateDetails(); break;
                case "changes": DrawChangeDetails(); break;
                case "experience-lab": DrawExperienceLabGallery(); break;
                case "concede": DrawConcede(); break;
                case "leave-table": DrawLeaveTable(); break;
                case "help": DrawHelp(); break;
                case "ability-auto-pass": DrawAbilityAutoPass(); break;
                case "hand-placement": DrawHandPlacement(); break;
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
    private int ReadingPageSize(int characters) => Math.Max(100, (int)(characters / (preferences.TextScale * preferences.TextScale)));
    private Card InspectorFace(Card original) => inspectBack && original.OtherFace is { } back
        ? original with { Name = back.Name, Type = back.Type, ManaCost = back.ManaCost, Text = back.OracleText, Power = back.Power, Toughness = back.Toughness, ArtName = back.ArtName, ArtFace = back.ArtFace } : original;
    private string[] InspectorPages(Card original, Card card)
    {
        bool onBattlefield = match!.Players.Any(p => p.Zone("Battlefield").Cards.Any(c => SameVisibleCard(c, original)));
        string stateText = onBattlefield ? string.Join(" · ", CardPresentation.StateLabels(card)) : "";
        if (CardPresentation.CommanderTaxDetails(card) is { Length: > 0 } taxDetails) stateText = taxDetails + (stateText.Length > 0 ? "\n" + stateText : "");
        if (CardPresentation.Counters(card) is { Length: > 0 } counters) stateText += "\nCounters: " + string.Join("; ", counters.Select(c => c.Key + " × " + c.Value));
        string effectiveDetails = onBattlefield ? string.Join("\n", CardPresentation.AbilityDetails(card)) : "";
        return TextPages((stateText.Length > 0 ? stateText + "\n\n" : "") + card.Text
            + (effectiveDetails.Length > 0 ? "\n\nCurrent abilities\n" + effectiveDetails : ""), ReadingPageSize(1050));
    }
    private void DrawInspector()
    {
        if (inspected == null) return;
        var original = inspected;
        var card = InspectorFace(original);
        ModalFrame(card.Name, new MatchIntent.CloseInspector());
        var inspectedPose = new Pose(new(447, 460), new(380, 532));
        PaintCard(card, inspectedPose, false, false);
        if (!inspectBack) PaintAttachedCardState(CardViews.Current(match, original), inspectedPose);
        canvas.Text(card.Type, 686, 191, Gold, .9f, 690, 2, true);
        canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, 686, 248, Ink, 1, 675, 1);
        string values = card.Type.Contains("Creature") ? $"Power / toughness: {card.Power}/{card.Toughness}" : "";
        if (card.Damage > 0) values += "   Damage marked: " + card.Damage;
        canvas.Text(values, 686, 287, Ink, .78f, 674, 3);
        var paragraphs = matchReadingPages; int page = inspectPage;
        canvas.Text(paragraphs[page], 686, 378, Ink, .8f, 660, 17);
        if (paragraphs.Length > 1) {
            Button("inspect-prev", "Previous", 686, 727, 135, new MatchIntent.SetPage(MatchPage.Inspect, page - 1), page > 0);
            Button("inspect-next", "More rules", 831, 727, 145, new MatchIntent.SetPage(MatchPage.Inspect, page + 1), page + 1 < paragraphs.Length);
            canvas.Text($"{page + 1} / {paragraphs.Length}", 995, 738, Muted, .7f);
        }
        if (original.OtherFace != null) Button("other-face", inspectBack ? "View current face" : "View other face", 274, 737, 347, new MatchIntent.FlipInspector(), accent: true);
    }
    private void DrawSettings()
    {
        var drawer = SystemDrawer("Play controls", "settings-", new MatchIntent.CloseView(), 760, "close-overlay");
        int x = drawer.X + 28, y = drawer.Y;
        canvas.Text("Priority", x, y + 108, Gold, .8f, 500, 1, true);
        MenuAction("settings-auto", "Auto", new(x, y + 148, 236, 42), new MatchIntent.SetAutomatic(true), selected: automatic);
        MenuAction("settings-manual", "Full control", new(x + 264, y + 148, 236, 42), new MatchIntent.SetAutomatic(false), selected: !automatic);
        MenuAction("settings-hold", held ? "Held for this turn" : "Hold priority this turn", new(x, y + 210, 500, 42), new MatchIntent.ToggleHold(), selected: held, size: .78f);
        canvas.Text("Stop on your turn", x, y + 289, Gold, .78f, 500, 1, true);
        for (int i = 0; i < TurnGuide.Stops.Length; i++) {
            var stop = TurnGuide.Stops[i]; bool selected = preferences.Stops.Contains(stop.Key);
            MenuAction("stop:" + stop.Key, stop.Label, new(x + i % 2 * 264, y + 329 + i / 2 * 46, 236, 40), new MatchIntent.TogglePhaseStop(stop.Key), selected: selected, size: .75f);
        }
        canvas.Line(new(x, y + 485), new(x + 500, y + 485), LobbyTrim);
        MenuAction("motion", "Reduced motion  " + (preferences.ReducedMotion ? "On" : "Off"), new(x, y + 505, 500, 40), new MatchIntent.ToggleReducedMotion(), selected: preferences.ReducedMotion, size: .78f);
        MenuAction("sound", "Sound  " + (preferences.Sounds ? "On" : "Off"), new(x, y + 555, 500, 40), new MatchIntent.ToggleSounds(), selected: preferences.Sounds, size: .78f);
        MenuAction("text-size", $"Text size  {preferences.TextScale:P0}", new(x, y + 605, 500, 40), new MatchIntent.CycleTextScale(), size: .78f);
        MenuAction("updates-settings", "Updates", new(x, y + 687, 240, 40), new ShellIntent.OpenUpdates(), size: .75f);
        canvas.Text("F11  Fullscreen", x + 293, y + 700, Muted, .65f, 207, 1);
    }
    private void DrawHistory()
    {
        ModalFrame("Match history", new MatchIntent.CloseView());
        var events = match!.Notices.Reverse().Select(message => (Label: "NOTICE", Message: message))
            .Concat(match.Activity.Reverse().Select(entry => (Label: "TURN " + entry.Turn, Message: entry.Message + (entry.Detail.Length > 0 ? " " + entry.Detail : "")))).ToArray(); int count = 10;
        int page = Math.Clamp(historyPage, 0, Math.Max(0, (events.Length - 1) / count));
        int row = 0;
        foreach (var entry in events.Skip(page * count).Take(count)) {
            int y = 189 + row++ * 49;
            canvas.Text(entry.Label, 204, y + 1, Gold, .58f, 96, 1, true);
            canvas.Text(entry.Message, 317, y, Ink, .75f, 1032, 2);
            canvas.Line(new(204, y + 40), new(1370, y + 40), Muted * .12f);
        }
        if (events.Length == 0) canvas.Text("No events have been reported for this game yet.", 206, 214, Muted, .9f);
        Button("history-prev", "Newer", 205, 739, 145, new MatchIntent.SetPage(MatchPage.History, page - 1), page > 0);
        Button("history-next", "Older", 362, 739, 145, new MatchIntent.SetPage(MatchPage.History, page + 1), (page + 1) * count < events.Length);
        canvas.Text("Only events visible to your seat are shown.", 790, 752, Muted, .65f, 574, 1);
    }
    private void DrawPlayerDetails()
    {
        var player = match!.Players.FirstOrDefault(p => p.Id == zonePlayer);
        if (player == null) return;
        ModalFrame(player.Name, new MatchIntent.CloseView());
        Button("player-mana", "Mana pool", 1130, 211, 220, new MatchIntent.OpenManaPool(player.Id), height: 36);
        canvas.Text(player.Life + " LIFE", 212, 202, Ink, 2, 1090, 1, true);
        string handLimit = player.MaxHandSize is { } maximum ? $"Maximum hand size: {maximum} · Discard during this player's cleanup" : "No maximum hand size";
        canvas.Text($"{player.Zone("Hand").Count} cards in hand · {handLimit}", 213, 258, Muted, .7f, 1090, 1);
        DrawPlayerCounterDetails(player, 213, 290, 1090);
        canvas.Text("COMMANDER DAMAGE RECEIVED", 213, 375, Gold, .76f, 1000, 1, true);
        int y = 414;
        const int count = 6;
        int page = Math.Clamp(playerDamagePage, 0, Math.Max(0, (player.CommanderDamage.Length - 1) / count));
        foreach (var entry in player.CommanderDamage.Skip(page * count).Take(count)) {
            canvas.Text(entry.Name, 216, y, Ink, .89f, 815, 1, true);
            canvas.Text(entry.Owner, 216, y + 28, Muted, .66f, 815, 1);
            canvas.Text(entry.Damage + " / 21", 1121, y, entry.Damage >= 18 ? Red : Gold, 1.2f, 200, 1, true);
            y += 47;
        }
        canvas.Text("Each opposing commander is tracked separately.", 216, 710, Muted, .75f, 970, 1);
        if (player.CommanderDamage.Length > count) {
            Button("commander-damage-prev", "Previous", 216, 745, 150, new MatchIntent.SetPage(MatchPage.PlayerDamage, page - 1), page > 0, height: 32);
            Button("commander-damage-next", "More commanders", 380, 745, 230, new MatchIntent.SetPage(MatchPage.PlayerDamage, page + 1), (page + 1) * count < player.CommanderDamage.Length, height: 32);
        }
    }
    private void DrawConcede()
    {
        ModalFrame("Concede this game?", new MatchIntent.CloseView(), "Keep playing");
        canvas.Text("Your seat will lose this game. This cannot be undone.", 244, 263, Ink, 1.2f, 1090, 3);
        canvas.Text(localMatch ? "The solo game ends when you concede." : "The remaining players can continue playing.", 244, 353, Muted, .92f, 1090, 3);
        string id = match!.Id;
        Button("confirm-concede", "Concede game", 1010, 690, 323, new MatchIntent.Concede(), !busy, true, 57);
    }
    private void DrawHelp()
    {
        ModalFrame("Current decision", new MatchIntent.CloseView());
        var text = matchReadingPages;
        int page = Math.Clamp(inspectPage, 0, text.Length - 1);
        helpPageText = text[page];
        canvas.Text(helpPageText, 211, 200, Ink, .93f, 1155, 80);
        Button("help-prev", "Previous", 211, 736, 144, new MatchIntent.SetPage(MatchPage.Inspect, page - 1), page > 0);
        Button("help-next", "More", 368, 736, 144, new MatchIntent.SetPage(MatchPage.Inspect, page + 1), page + 1 < text.Length);
    }
    private void DrawZone()
    {
        if (zoneName == "Stack") { DrawStackDetails(); return; }
        if (zoneName == "Battlefield") { DrawFocusedBattlefield(); return; }
        var player = match!.Players.FirstOrDefault(p => p.Id == zonePlayer);
        var zone = player?.Zone(zoneName!);
        var cards = zone?.Cards ?? [];
        ModalFrame(zoneName + (zoneName == "Stack" ? "" : " · " + player?.Name), new MatchIntent.CloseZone());
        int count = 7; int page = Math.Clamp(zonePage, 0, Math.Max(0, (cards.Length - 1) / count));
        canvas.Text(cards.Length + " visible" + (zoneName == "Stack" ? "" : " / " + (zone?.Count ?? 0) + " total"), 205, 183, Muted, .72f, 650, 1);
        if (player != null && AvailableZoneCount(player, zoneName!) is > 0 and var available)
            canvas.Text(available + " available · Use a card below to see its play and costs.", 205, 220, ActionAccent, .77f, 1170, 2, true);
        string displayedZone = zoneName!;
        int index = 0;
        foreach (var card in cards.Skip(page * count).Take(count)) {
            int x = 210 + index++ * 169;
            var pose = new Pose(new(x + 74, 409), new(148, 207));
            DrawTableCard("zone:" + zonePlayer + ":" + displayedZone + ":" + CardIdentity(card),
                card, pose, displayedZone, zonePlayer, "card:" + card.Key + ":" + card.VisualId, new MatchIntent.InspectCard(card),
                (displayed, focused) => PaintCard(card, displayed, card.Highlighted, IsCardActionable(card), focused));
            if (zoneName == "Command") PaintCommanderTax(card, pose);
            canvas.Text(card.Name, x, 533, Ink, .68f, 155, 3, true);
            canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, x, 600, Gold, .62f, 156, 2);
            if (pose.Contains(pointer)) hovered = card;
        }
        if (cards.Length == 0) canvas.CenterText("No cards are visible in this zone.", new(230, 333, 1120, 100), Muted, 1.05f);
        Button("zoneprev", "Previous", 208, 734, 160, new MatchIntent.SetPage(MatchPage.Zone, page - 1), page > 0);
        Button("zonenext", "Next", 380, 734, 160, new MatchIntent.SetPage(MatchPage.Zone, page + 1), (page + 1) * count < cards.Length);
        canvas.Text("Right-click a card for its complete rules.", 917, 746, Muted, .66f, 474, 1);
    }
    private void DrawStackDetails()
    {
        var entries = match!.Stack;
        ModalFrame("The stack", new MatchIntent.CloseZone());
        if (entries.Length == 0) { canvas.Text("The stack is empty.", 219, 222, Muted, 1); return; }
        int page = Math.Clamp(zonePage, 0, entries.Length - 1);
        if (!string.IsNullOrEmpty(inspectedStack)) {
            int current = entries.IndexOf(entries.FirstOrDefault(s => s.Id == inspectedStack)!);
            if (current < 0) {
                canvas.Text("The inspected spell or ability has left the stack.", 219, 235, Ink, 1.05f, 1100, 2);
                Button("stack-current", "Inspect next to resolve", 220, 345, 412, new MatchIntent.OpenStack(), accent: true, height: 51);
                return;
            }
            page = current;
        }
        var entry = entries[page];
        canvas.Text($"{page + 1} / {entries.Length}   " + (page == 0 ? "Next to resolve" : "Waiting on the stack"), 217, 190, Gold, .8f, 1100, 1, true);
        if (entry.Card is { } card) {
            var pose = new Pose(new(408, 459), new(304, 426));
            bool target = IsCardActionable(card);
            DrawTableCard("stack-detail:" + entry.Id, card, pose, "Stack", entry.ControllerId,
                target ? "stack-target:" + page : "stack-inspect:" + entry.Id,
                target ? new MatchIntent.UseCard(card, false, true) : new MatchIntent.InspectCard(card),
                (displayed, focused) => PaintInteractionCard(card, displayed, false, target, focused));
            if (pose.Contains(pointer)) hovered = card;
        }
        canvas.Text(entry.Name, 630, 246, Ink, 1.05f, 716, 3, true);
        canvas.Text((entry.Ability ? "ABILITY" : "SPELL") + " · " + StateClarity.StackController(match, entry), 631, 328, Gold, .74f, 700, 1, true);
        var text = matchReadingPages; int textPage = Math.Clamp(inspectPage, 0, text.Length - 1);
        canvas.Text(text[textPage], 632, 369, Ink, .8f, 714, 15);
        Button("stack-newer", "Above", 216, 734, 130, new MatchIntent.OpenStack(page - 1), page > 0);
        Button("stack-older", "Below", 359, 734, 130, new MatchIntent.OpenStack(page + 1), page + 1 < entries.Length);
        if (text.Length > 1) {
            Button("stack-text-prev", "Previous text", 1002, 734, 171, new MatchIntent.SetPage(MatchPage.Inspect, textPage - 1), textPage > 0);
            Button("stack-text-next", "More text", 1187, 734, 171, new MatchIntent.SetPage(MatchPage.Inspect, textPage + 1), textPage + 1 < text.Length);
        }
    }
    private void DrawDecisionOverlay()
    {
        var state = match!; var d = state.Decision!;
        if (d.Task == DecisionTask.Allocation) { DrawAllocation(state, d); return; }
        bool cards = d.LibraryCards.Length > 0 || d.Choices.Length > 0 && d.Choices.All(choice => choice.Card != null);
        string title = d.Title.Length > 0 ? d.Title : d.Task == DecisionTask.Reveal ? "Revealed cards" : d.Ordered ? "Choose an order"
            : cards ? d.Max == 1 ? "Choose a card" : "Choose cards" : "Make your choice";
        ModalFrame(title, new MatchIntent.ViewBattlefield(), "View battlefield", d.Task is DecisionTask.Options or DecisionTask.Ordering or DecisionTask.AbilityChoice or DecisionTask.Reveal ? 790 : 706);
        canvas.Text(d.Message, 204, 181, Ink, .77f, 1185, 3);
        if (d.Task is DecisionTask.Number or DecisionTask.Text) { DrawValueDecision(state, d); return; }
        DrawChoices(state, d);
    }
    private void ToggleChoice(Decision d, int index)
    {
        if (match?.Decision == d && decisionDraft.Toggle(match, index)) CollapseCardReading();
    }
    private void DrawChoices(GameSnapshot state, Decision d)
    {
        var items = d.LibraryCards.Length > 0
            ? d.LibraryCards.Select(c => (c.Index, c.Label, Card: (Card?)c.Card, Detail: "", Mana: "")).ToArray()
            : d.Choices.Select(c => (Index: (int?)c.Index, c.Label, Card: CardViews.Choice(state, c), c.Detail, c.Mana)).ToArray();
        var filtered = items.Select((item, slot) => (Item: item, Slot: slot))
            .Where(c => c.Item.Label.Contains(choiceFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool portraits = items.Any(c => c.Card != null); int capacity = portraits ? 7 : 6;
        bool sparseSource = portraits && items.Length <= 4;
        var sourcePreview = !portraits || sparseSource ? CardViews.Source(state, d) : null;
        if (sourcePreview != null) {
            int sourceX = sparseSource ? 199 : 1185;
            var sourcePose = new Pose(new(sourceX + 83, 435), new(166, 232));
            if (sparseSource) canvas.Text("SOURCE", sourceX, 294, Gold, .55f, 166, 1, true);
            PaintInteractionCard(sourcePreview, sourcePose, false, false);
            canvas.Text(sourcePreview.FaceDown ? "Face-down card" : sourcePreview.Name, sourceX, 562, Ink, .68f, 166, 3, true);
            if (!sourcePreview.FaceDown && VisibleCards().Any(card => SameVisibleCard(card, sourcePreview))) {
                var sourceBounds = new Rectangle(sourceX, 319, 166, 298);
                if (sourceBounds.Contains(pointer)) canvas.CardFocus(sourcePose, HoverAccent);
                hits.Add(new("choice-source-inspect", Scope, sourceBounds, new MatchIntent.InspectCard(sourcePreview), sourcePreview));
            }
        }
        bool cardChoices = items.Length > 0 && items.All(c => c.Card != null);
        int page = Math.Clamp(choicePage, 0, Math.Max(0, (filtered.Length - 1) / capacity));
        string instruction = d.Task == DecisionTask.Reveal ? "" : d.Ordered
            ? $"Choose {d.Min}–{d.Max} in order. Selected: {decisionDraft.Selections.Count}."
            : d.Min == 1 && d.Max == 1 ? decisionDraft.Selections.Count == 0
                ? cardChoices ? "Choose one card" : "Choose one option"
                : cardChoices ? "One card selected" : "One option selected"
            : $"Choose {d.Min}–{d.Max}. Selected: {decisionDraft.Selections.Count}.";
        canvas.Text(instruction, 206, 251, Gold, .72f, 1160, 2);
        var pageItems = filtered.Skip(page * capacity).Take(capacity).ToArray();
        int expanded = Array.FindIndex(pageItems, e => readingId == "choice-face:" + Scope + ":" + e.Slot);
        bool sparseCards = portraits && items.Length <= capacity;
        int rowWidth = expanded < 0 ? 148 + Math.Max(0, pageItems.Length - 1) * 169 : 410 + Math.Max(0, pageItems.Length - 1) * 128;
        int rowLeft = sparseCards ? sparseSource && sourcePreview != null ? 403 + (991 - rowWidth) / 2 : (1600 - rowWidth) / 2
            : expanded < 0 ? 206 : 190;
        int row = 0;
        foreach (var entry in pageItems) {
            var item = entry.Item;
            int index = row++; bool selected = item.Index is { } id && decisionDraft.Selections.Contains(id);
            bool available = d.Task != DecisionTask.Reveal && item.Index != null && !busy;
            string buttonId = item.Index is { } cardIndex ? "choice:" + cardIndex : "reveal:" + entry.Slot;
            var captured = item;
            if (portraits) {
                // Make room for the original face as it grows, so it cannot
                // completely bury an adjacent revealed card or choice.
                int column = expanded < 0 ? 148 : index == expanded ? 410 : 112;
                int x = expanded < 0 ? rowLeft + index * 169 : rowLeft + index * 128 + (index > expanded ? 298 : 0);
                int faceWidth = expanded >= 0 && index != expanded ? 112 : 148;
                var pose = new Pose(new(x + column / 2, 408), new(faceWidth, faceWidth * 1.4f));
                if (item.Card != null) DrawTableCard("choice-face:" + Scope + ":" + entry.Slot, item.Card, pose, "Choice", null,
                    buttonId, available ? new MatchIntent.SelectChoice(captured.Index!.Value) : UiIntent.None,
                    (displayed, focus) => {
                        PaintInteractionCard(item.Card, displayed, selected, false, focus);
                        // Availability stays visible, while the chosen face owns
                        // the strong gold frame and glow instead of competing halos.
                        if (available && !selected) canvas.CardFrame(displayed, ActionAccent * .65f, 1);
                    },
                    choiceSlot: entry.Slot, activationBounds: new(x, 304, column, 329));
                else canvas.Panel(new(x, 304, 148, 207), Panel, Muted);
                canvas.Text(item.Label, x + 1, 526, Ink, .66f, column, 3, true);
                var badgeBounds = new Rectangle(x, 605, column, 28);
                if (selected && d.Ordered) PaintChoiceOrder(item.Index!.Value, badgeBounds, .65f);
                else if (selected) {
                    var center = new Vector2(badgeBounds.Center.X, badgeBounds.Center.Y);
                    canvas.Line(center + new Vector2(-8, 0), center + new Vector2(-2, 6), Gold, 3);
                    canvas.Line(center + new Vector2(-2, 6), center + new Vector2(9, -7), Gold, 3);
                }
                if (available && item.Card == null) hits.Add(new(buttonId, Scope, new(x, 304, 150, 329), new MatchIntent.SelectChoice(captured.Index!.Value)));
            } else {
                int columnWidth = sourcePreview != null ? 465 : 585;
                int x = 206 + index % 2 * (columnWidth + 17), y = 302 + index / 2 * 116;
                var bounds = new Rectangle(x, y, columnWidth, 103);
                PaintControlSurface(buttonId, bounds, selected ? new Color(36, 63, 79) : Panel,
                    selected ? Gold : new Color(77, 90, 105), available);
                bool numbered = selected && d.Ordered;
                int offset = (numbered ? 99 : 16) + (item.Mana.Length > 0 ? 47 : 0);
                if (numbered) PaintChoiceOrder(item.Index!.Value, new(x + 12, y + 12, 76, 33), .65f);
                if (item.Mana.Length > 0) ManaOrb(item.Mana.Trim('{', '}'), 1, new(x + offset - 30, y + 48));
                canvas.Text((selected && !d.Ordered ? "✓  " : "") + item.Label, x + offset, y + 12, Ink, .83f, columnWidth - 22 - offset, 3, true);
                if (item.Detail.Length > 0) canvas.Text(item.Detail, x + offset, y + 69, Muted, .65f, columnWidth - 22 - offset, 1);
                if (available) hits.Add(new(buttonId, Scope, bounds, new MatchIntent.SelectChoice(captured.Index!.Value)));
            }
        }
        if (filtered.Length == 0) canvas.CenterText("No matching cards or choices.", new(209, 356, 1177, 90), Muted, 1);
        if (filtered.Length > capacity) {
            Button("choicesprev", "‹", 206, 754, 45, new MatchIntent.SetPage(MatchPage.Choice, page - 1), page > 0, height: 34);
            Button("choicesnext", "›", 260, 754, 45, new MatchIntent.SetPage(MatchPage.Choice, page + 1), (page + 1) * capacity < filtered.Length, height: 34);
            canvas.Text($"Page {page + 1} / {(filtered.Length + capacity - 1) / capacity}", 325, 761, Muted, .66f, 350, 1);
        }
        if (items.Length > capacity) {
            Button("choicesfilter", choiceFilter.Length == 0 ? "Search choices…" : choiceFilter, 808, 754, 587, new MatchIntent.FocusInput(true), accent: textFocus && filterInput, height: 34);
            if (textFocus && filterInput) canvas.Text("Type to filter. Backspace edits. Escape closes the gallery.", 810, 794, Muted, .56f, 580, 1);
        }
        if (decisionDraft.Selections.Count > 0 && d.Task != DecisionTask.Reveal)
            Button("choicesclear", d.Ordered ? "Reset order" : "Clear", 206, 819, 183, new MatchIntent.ClearSelection(), !busy);
        if (d.Min == 0 && d.Task != DecisionTask.Reveal) Button("choice-cancel", "Choose none", 405, 819, 182, new MatchIntent.ChooseNone(), !busy);
        Button("choicesdone", d.Task == DecisionTask.Reveal ? "Continue" : d.Ordered ? "Confirm order" : cardChoices && decisionDraft.Selections.Count > 0
            ? decisionDraft.Selections.Count == 1 ? "Confirm card" : "Confirm cards" : "Confirm selection", 1088, 814, 307, new MatchIntent.ConfirmSelection(), !busy && (d.Task == DecisionTask.Reveal || decisionDraft.Selections.Count >= d.Min && decisionDraft.Selections.Count <= d.Max), true, 47);
    }
    private void DrawValueDecision(GameSnapshot state, Decision d)
    {
        bool numeric = d.Task == DecisionTask.Number || d.Numeric;
        canvas.Text(numeric && d.Task == DecisionTask.Number ? $"Choose a number from {d.Min} to {d.Max}." : "Enter your answer.", 228, 315, Gold, .94f, 1127, 2);
        Button("text-input", inputText.Length == 0 ? "Click here and type…" : inputText, 230, 399, 1100, new MatchIntent.FocusInput(false), accent: textFocus, height: 66);
        if (d.Task == DecisionTask.Number) {
            Button("number-less", "−", 232, 490, 65, new MatchIntent.AdjustNumber(-1), height: 44);
            Button("number-more", "+", 310, 490, 65, new MatchIntent.AdjustNumber(1), height: 44);
        }
        bool valid = !numeric || int.TryParse(inputText, out int number) && (d.Task != DecisionTask.Number || number >= d.Min && number <= d.Max);
        if (!valid) canvas.Text("Enter a whole number within the permitted range.", 231, 559, Red, .78f, 1090, 2);
        Button("value", "Confirm", 1090, 718, 266, new MatchIntent.ConfirmValue(), valid && !busy, true, 54);
    }
}
