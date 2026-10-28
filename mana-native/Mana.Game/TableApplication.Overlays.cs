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
        var drawer = SystemDrawer("Play controls", "settings-", new MatchIntent.CloseView(), 810, "close-overlay");
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
        MenuAction("motion", "Reduced motion  " + (preferences.ReducedMotion ? "On" : "Off"), new(x, y + 499, 500, 40), new MatchIntent.ToggleReducedMotion(), selected: preferences.ReducedMotion, size: .78f);
        DrawAudioSettings(x, y + 551);
        MenuAction("text-size", $"Text size  {preferences.TextScale:P0}", new(x, y + 681, 500, 40), new MatchIntent.CycleTextScale(), size: .78f);
        MenuAction("updates-settings", "Updates", new(x, y + 751, 240, 40), new ShellIntent.OpenUpdates(), size: .75f);
        canvas.Text("F11  Fullscreen", x + 293, y + 764, Muted, .65f, 207, 1);
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
}
