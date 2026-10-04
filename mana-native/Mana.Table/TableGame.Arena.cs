using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private static readonly Color ActionAccent = new(115, 220, 255);
    private void DrawArena()
    {
        var state = match!;
        DrawTableSurface();
        // Queue the player's faces first; slow remote downloads must not bury the hand.
        foreach (var card in state.Viewer?.Zone("Hand").Cards ?? []) art.Get(card);
        foreach (var card in state.Viewer?.Zone("Command").Cards ?? []) art.Get(card);
        var opponents = TableLayout.Opponents(state);
        foreach (int i in new[] { 1, 0, 2 }) if (i < opponents.Length) DrawOpponent(opponents[i], i);
        if (state.Viewer is { } viewer) DrawLocal(viewer);
        for (int i = 0; i < opponents.Length && i < 3; i++) DrawHero(opponents[i], TableLayout.Hero(i), false, i);
        DrawCombatConnections(); DrawStack(); DrawSeatTransfers();
        DrawHand(); DrawCardEffects(); DrawDepartures(); DrawLiftedCards();
        // Your seat stays in front of the fan and readable during card travel.
        if (state.Viewer is { } local) DrawHero(local, TableLayout.Hero(3), true, 3);
        foreach (var item in feedback) {
            float age = (float)(1.5 - (item.Until - now));
            canvas.Text(item.Text, (int)item.Position.X + 35, (int)item.Position.Y - 20 - (int)(age * 30), item.Color * Math.Min(1, (float)(item.Until - now)), 1.6f, 100, 1, true);
        }
        DrawDecisionDock(); DrawGameHeader(); DrawCombatBar(); DrawActionCallout();
        DrawTurnCallout();
        if (dragging && dragSource?.Card is { } heldCard) {
            bool legal = LegalDrop(HitAt(pointer));
            if (state.Decision?.Intent is DecisionIntent.Attack or DecisionIntent.Block && cardPositions.TryGetValue(heldCard.CombatId, out var origin))
                DrawCombatAim(origin, legal ? Blue : Muted);
            else {
                // Keep the held occurrence in the scene, so release continues
                // from the pointer instead of respawning it at the library.
                var heldPose = motion.Hold(CardIdentity(heldCard), new(pointer.ToVector2(), new(154, 216), -.04f, Elevation: 46));
                PaintCard(heldCard, heldPose, true, false);
            }
            string hint = legal ? "Release to " + (state.Decision?.Intent == DecisionIntent.Attack ? "assign attacker" : state.Decision?.Intent == DecisionIntent.Block ? "assign blocker" : "play card") : "Choose a highlighted target · Escape to cancel";
            int hintX = Math.Clamp(pointer.X - 192, 20, 1190), hintY = Math.Clamp(pointer.Y + 31, 70, 846);
            canvas.Panel(new(hintX, hintY, 388, 32), new(16, 24, 33), legal ? Blue : Muted, 5);
            canvas.CenterText(hint, new(hintX + 6, hintY, 376, 32), Ink, .62f);
        }
        hovered = !dragging ? HitAt(pointer)?.Card : null;
        DrawOverlays();
        DrawLiftedCards();
        if (ReadingGalleryOpen) hovered = HitAt(pointer)?.Card;
        readingTransitions.RemoveWhere(id => !readingCards.Any(c => c.Id == id));
    }
    private void DrawGameHeader()
    {
        var state = match!;
        canvas.Gradient(new(0, 0, 1600, 54), new Color(8, 12, 18) * .8f, Color.Transparent);
        canvas.Text("MANA TABLE", 24, 17, Gold, .9f, 180, 1, true);

        var active = state.Players.FirstOrDefault(p => p.Id == state.ActivePlayerId);
        string label = state.Turn == 0 ? "Opening hands" : state.ActivePlayerId == state.ViewerId ? "Your turn" : active?.Name ?? "Waiting";
        canvas.Text(label, 220, 9, Ink, .85f, 600, 1, true);
        canvas.Text($"Turn {state.Turn}  ·  {state.Phase}", 220, 32, Muted, .6f, 600, 1);
        Button("history", "History", 1190, 12, 110, () => { overlay = "history"; historyPage = 0; }, height: 35);
        Button("settings", "Settings", 1310, 12, 112, () => overlay = "settings", height: 35);
        Button("concede", "Concede", 1432, 12, 143, () => {
            overlay = "concede";
        }, !busy && state.Status is not ("finished" or "error"), height: 35);

    }
    private void DrawHand()
    {
        var hand = tableScene!.Hand;
        canvas.Gradient(new(244, 785, 970, 115), Color.Transparent, new Color(8, 12, 20) * .85f);
        foreach (var node in hand.Cards) DrawSceneCard(node);
        if (hand.Total == 0) canvas.CenterText("Your hand is empty", new(455, 815, 690, 65), Muted * .6f, .86f);
        string limit = match!.Viewer?.MaxHandSize is { } maximum ? $"Limit {maximum} at cleanup" : "No hand limit";
        canvas.Panel(new(263, 864, 373, 31), new Color(12, 19, 27) * .95f, new Color(62, 78, 90), 4);
        canvas.Text($"{hand.Total} cards in hand · {limit}", 274, 873, Ink, .59f, 351, 1);
        if (hand.Total > 11) {
            Button("handprev", "‹", 254, 789, 34, () => handPage = hand.Page - 1, hand.Page > 0, height: 34);
            Button("handnext", "›", 1203, 789, 34, () => handPage = hand.Page + 1, (hand.Page + 1) * 11 < hand.Total, height: 34);
        }
    }
    private static string CardIdentity(Card card) => CardViews.Identity(card);
    private void DrawSceneCard(SceneCard node)
    {
        var card = node.Card;
        var rest = node.Zone == "Battlefield" && CardPresentation.HasAbility(card, "flying")
            ? CardMotion.Airborne(node.Pose, now, preferences.ReducedMotion, node.Id) : node.Pose;
        var pose = DrawTableCard(node.Id, card, rest, node.Zone, node.Owner,
            "card:" + card.Key + ":" + card.VisualId, () => CardClick(card),
            (displayed, focus) => PaintSceneCard(card, displayed, node.Zone, node.Owner, focus), node.Entrance, node.EntranceDelay);
        if (!string.IsNullOrEmpty(card.CombatId)) cardPositions[card.CombatId] = pose;
    }
    private void PaintSceneCard(Card card, Pose pose, string zone, int owner, bool focus)
    {
        int? choice = match == null ? null : TableChoices.Index(match, card);
        bool selected = choice is { } index ? selections.Contains(index) : card.Highlighted || card.CombatId.Length > 0 && selectedCombat == card.CombatId;
        bool legal = IsCardActionable(card);
        if (zone == "Battlefield") PaintAbilityEffects(card, pose);
        PaintCard(card, pose, selected, legal, focus);
        if (zone == "Battlefield" && legal && CardActions.CanActivate(match!, card)) {
            var badge = new Rectangle((int)pose.Center.X - 39, pose.Bounds.Top + 6, 78, 21);
            canvas.Panel(badge, new(15, 41, 54), ActionAccent, 4);
            canvas.CenterText("ACTIVATE", badge, ActionAccent, .46f, true);
        }
        if (zone == "Battlefield" && focus && pose.Size.X >= 300) {
            var hint = new Rectangle(Math.Clamp(pose.Bounds.X, 8, 1174), Math.Min(843, pose.Bounds.Bottom + 6), 418, 43);
            canvas.Panel(hint, new(16, 25, 34), legal ? ActionAccent : Muted, 5);
            canvas.CenterText(CardActions.Hint(match!, card), new(hint.X + 5, hint.Y + 2, 408, 21), legal ? ActionAccent : Ink, .64f, true);
            canvas.CenterText("Right-click for rules and ability controls", new(hint.X + 5, hint.Y + 22, 408, 19), Muted, .53f);
        }
        if (choice != null && selected) {
            var badge = new Rectangle((int)pose.Center.X - 39, pose.Bounds.Top + 8, 78, 24);
            canvas.Panel(badge, new(34, 48, 49), Gold, 4);
            canvas.CenterText(match!.Decision!.Ordered ? "ORDER " + (selections.IndexOf(choice.Value) + 1) : "SELECTED", badge, Gold, .48f, true);
        }
        if (CombatTarget(card)) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(10) }, Blue, 4);
        bool creature = CardPresentation.HasCombatStats(card);
        if (zone == "Battlefield" && (creature || card.Counters.Count > 0 || card.Damage > 0 || card.CombatKeywords.Length > 0)) {
            int x = (int)pose.Center.X, y = pose.Bounds.Bottom - 14;
            int seat = owner == match!.ViewerId ? 3 : TableLayout.SeatIndex(match, owner);
            // Side seats read toward the center, keeping badges out of the next
            // card's footprint while their faces still lie in the seat's plane.
            if (seat is 0 or 2) { x = seat == 0 ? pose.Bounds.Right + 21 : pose.Bounds.Left - 21; y = (int)pose.Center.Y - 12; }
            if (creature) {
                canvas.Panel(new(x - 29, y, 58, 25), card.Attacking ? new Color(98, 39, 40) : new Color(23, 31, 42), selected ? Blue : new Color(136, 144, 148), 5);
                canvas.CenterText($"{card.Power}/{card.Toughness}", new(x - 29, y, 58, 25), Ink, .7f, true);
            }
            if (card.Damage > 0) { canvas.Circle(new(x + 39, y + 10), 14, Red); canvas.CenterText(card.Damage.ToString(), new(x + 25, y - 4, 28, 28), Color.Black, .61f, true); }
            PaintStateMarkers(card, pose, x, y, seat, owner);
            if (focus && pose.Size.X >= 300) PaintStateDetails(card, pose);
        }
    }
    private void PaintCard(Card card, Pose pose, bool selected, bool legal, bool focus = false)
    {
        if (legal || selected) canvas.Glow(pose.Center, pose.Size * 1.6f, (selected ? Gold : ActionAccent) * .6f);
        canvas.Surface(null, pose with { Center = pose.Center + new Vector2(4 + pose.Elevation * .18f, (pose.IsProjected ? 5 : 10) + pose.Elevation), Size = pose.Size + new Vector2(7) }, Color.Black * (pose.Elevation > 0 ? .27f : .4f));
        canvas.Surface(null, pose with { Size = pose.Size + new Vector2(4) }, new Color(9, 13, 18));
        if (card.FaceDown) PaintBack(pose);
        else if (art.Get(card) is { } image) canvas.Surface(image, pose, Color.White);
        else {
            canvas.Surface(null, pose, new Color(53, 63, 71));
            canvas.CardFrame(pose with { Size = pose.Size - new Vector2(7) }, Muted * .25f);
            var r = pose.Bounds;
            if (pose.Size.X >= 300) {
                canvas.Text(card.Name, r.X + 20, r.Y + 20, Ink, 1.05f, r.Width - 40, 2, true);
                canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, r.X + 20, r.Y + 82, Gold, .8f, r.Width - 40, 1);
                canvas.Text(card.Type, r.X + 20, r.Y + 121, Gold, .75f, r.Width - 40, 2);
                canvas.Line(new(r.X + 20, r.Y + 176), new(r.Right - 20, r.Y + 176), Muted * .4f);
                canvas.Text(card.Text, r.X + 20, r.Y + 195, Ink, .78f, r.Width - 40, Math.Max(1, (r.Height - 220) / 27));
            } else {
                canvas.Text(card.Name, r.X + 5, r.Y + 5, Ink, pose.Size.X > 100 ? .67f : .47f, r.Width - 10, pose.Size.X > 100 ? 3 : 2, true);
                canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, r.X + 5, r.Bottom - 27, Gold, .54f, r.Width - 10, 1);
                if (pose.Size.X > 130) canvas.Text(card.Type, r.X + 6, r.Y + (int)(r.Height * .62f), Muted, .5f, r.Width - 12, 2);
            }
        }
        if (legal || selected) {
            float glow = !preferences.ReducedMotion && legal && match?.Decision?.Intent == DecisionIntent.Priority
                ? .85f + .15f * MathF.Sin((float)now * 2.4f) : 1;
            canvas.CardAction(pose, selected ? Gold : ActionAccent, glow);
        }
        if (card.Attacking || card.Blocking) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(4) }, card.Attacking ? Red : Blue, 2);
        if (pose.Elevation > 4) {
            float light = Math.Min(.4f, pose.Elevation / 100);
            canvas.Line(pose.Point(.03f, .01f), pose.Point(.97f, .01f), Ink * light, 1);
            canvas.Line(pose.Point(.99f, .03f), pose.Point(.99f, .5f), Ink * light * .5f, 1);
        }
        if (focus) canvas.CardFocus(pose, HoverAccent);
    }
    private void PaintBack(Pose pose, float opacity = 1)
    {
        if (assets.CardBack != null) { canvas.Surface(assets.CardBack, pose, Color.White * opacity); return; }
        canvas.Surface(null, pose, new Color(27, 41, 49) * opacity);
        canvas.CardFrame(pose with { Size = pose.Size - new Vector2(5) }, new Color(166, 151, 120) * opacity, 1);
        var diamond = new[] { pose.Point(.5f, .27f), pose.Point(.76f, .5f), pose.Point(.5f, .73f), pose.Point(.24f, .5f) };
        for (int i = 0; i < 4; i++) canvas.Line(diamond[i], diamond[(i + 1) % 4], new Color(145, 140, 119) * opacity, 1);
        canvas.Circle(pose.Center, 2, Gold * opacity);
    }
    private void DrawCombatConnections()
    {
        if (match!.Combat is not { } combat) return;
        foreach (var attack in combat.Attackers) {
            if (!cardPositions.TryGetValue(attack.CardId, out var attacker)) continue;
            Vector2? destination = null;
            if (attack.Defender is { Kind: "card" } defender && cardPositions.TryGetValue(defender.Id, out var target)) destination = target.EdgeToward(attacker.Center, 6);
            else if (heroPositions.TryGetValue(attack.DefendingPlayerId, out var player)) {
                var delta = attacker.Center - player;
                destination = player + (delta.LengthSquared() > 0 ? Vector2.Normalize(delta) : Vector2.UnitY) * (attack.DefendingPlayerId == match.ViewerId ? 51 : 40);
            }
            string? focus = CombatSource ?? hovered?.CombatId;
            float alpha = string.IsNullOrEmpty(focus) || attack.CardId == focus || attack.BlockerIds.Contains(focus) ? .85f : .22f;
            if (destination is { } end) canvas.Arrow(attacker.EdgeToward(end, 4), end, Red * alpha);
            foreach (string id in attack.BlockerIds) if (cardPositions.TryGetValue(id, out var blocker)) canvas.Arrow(blocker.EdgeToward(attacker.Center, 4), attacker.EdgeToward(blocker.Center, 4), Blue * alpha);
        }
        if (selectedCombat != null && cardPositions.TryGetValue(selectedCombat, out var selected) && !dragging)
            DrawCombatAim(selected, Blue * .8f);
    }
    private void DrawCombatAim(Pose source, Color color)
    {
        var target = pointer.ToVector2();
        if (!source.Contains(pointer)) canvas.Arrow(source.EdgeToward(target, 4), target, color, 12);
    }
    private void DrawStack()
    {
        var state = match!;
        if (state.Stack.Length == 0 && state.Decision?.SourceCard == null) return;
        canvas.Glow(new(807, 485), new(430, 165), Blue * .17f);
        var resting = TableWorld.Card(new(6, 108), new(245, 126));
        canvas.CardFrame(resting, Gold * .12f, 1);
        canvas.Text(state.Stack.Length > 0 ? "ON THE STACK  ·  " + state.Stack.Length : "CURRENT ACTION", 549, 379, Gold, .49f, 154, 1, true);
        var stackCards = state.Stack.Take(3).Select(s => s.Card ?? new Card { Name = s.Name, Text = s.Text, Type = s.Ability ? "Ability" : "Spell" }).ToArray();
        Pose? topPose = null;
        if (stackCards.Length == 0 && state.Decision?.SourceCard is { } source) stackCards = [source];
        for (int i = stackCards.Length - 1; i >= 0; i--) {
            var card = stackCards[i];
            bool alsoInZone = state.Players.Any(p => p.Zones.Any(z => z.Cards.Any(c => CardIdentity(c) == CardIdentity(card))));
            string identity = alsoInZone ? "stack:" + (state.Stack.ElementAtOrDefault(i)?.Id ?? "source") : CardIdentity(card);
            if (identity.Length == 0) identity = "stack:" + (state.Stack.ElementAtOrDefault(i)?.Id ?? card.Name);
            Vector2 entrance = new(800, 520);
            if (card.VisualId.Length > 0) {
                if (motion.TryGet(card.VisualId, out var sourcePose)) entrance = sourcePose.Center;
                else if (state.Activity.LastOrDefault(a => a.CardId == card.VisualId && a.PlayerId != null) is { PlayerId: { } actor })
                    entrance = TableLayout.SeatHand(actor == state.ViewerId ? 3 : TableLayout.SeatIndex(state, actor));
            }
            int stackIndex = i;
            var stackPose = DrawTableCard(identity, card, TableWorld.Spell(i), "Stack", null, "stack-item:" + i, () => {
                if (IsCardActionable(card)) CardClick(card);
                else if (state.Stack.Length > stackIndex) OpenStack(stackIndex);
                else Inspect(card);
            }, (displayed, focus) => PaintCard(card, displayed, stackIndex == 0, false, focus), entrance);
            if (i == 0) topPose = stackPose;
        }
        if (topPose is { } origin && state.Stack.FirstOrDefault() is { } top) DrawStackTargets(top, origin);
        canvas.Text(stackCards[0].Name, 549, 400, Ink, .62f, 154, 3, true);
        Button("stack", state.Stack.Length > 0 ? "Inspect stack" : "Inspect source", 549, 462, 154, () => { if (state.Stack.Length > 0) OpenStack(); else if (state.Decision?.SourceCard is { } source) Inspect(source); }, height: 29);
    }
    private void DrawDecisionDock()
    {
        var state = match!; var d = state.Decision;
        canvas.Gradient(new(1240, 712, 360, 188), Color.Transparent, new Color(10, 16, 21) * .95f);
        if (state.Status is "finished" or "error") {
            canvas.Text(state.Result ?? "Game stopped", 1267, 730, Gold, 1.17f, 295, 1, true);
            canvas.Text(state.Error ?? "You can still inspect the final battlefield.", 1267, 767, Muted, .63f, 294, 2);
            if (localMatch || state.Status == "finished") Button("return", localMatch ? "Return to setup" : "Return to lobby", 1266, 828, 296, ReturnFromMatch, !busy, true, 47);
            return;
        }
        bool following = busy || d == null || TurnGuide.CanAutoPass(state, automatic, held, preferences.Stops, false);
        string status = following ? "FOLLOWING THE TABLE" : d!.Intent == DecisionIntent.Block ? "CHOOSE YOUR BLOCKERS"
            : d.Intent == DecisionIntent.Priority ? "YOUR PRIORITY" : "YOUR DECISION";
        canvas.Text(status, 1266, 727, following ? Muted : Gold, .54f, 298, 1, true);
        canvas.Text(following ? "Waiting for your next action. Use Hold to pause Auto." : TurnGuide.Instruction(state), 1266, 741, Ink, .66f, 295, 3);
        hits.Add(new("instructions", ControlScope("instructions"), new(1262, 724, 304, 65), () => { overlay = "help"; inspectPage = 0; }));
        if (following) {
            canvas.Panel(new(1266, 799, 296, 47), new(22, 31, 39), new Color(66, 82, 94), 4);
            canvas.CenterText("Table in progress", new(1275, 800, 278, 45), Muted, .76f);
        } else if (TableChoice) {
            Button("confirm", selections.Count == 0 && d!.Min == 0 ? "Choose none" : $"Confirm ({selections.Count}/{d!.Max})", 1266, 799, 296,
                () => Send(new(state.Id, d.Id, ReplyAction.Choose, Choices: selections.ToArray())), !busy && TableChoices.CanConfirm(state, selections), true, 47);
            Button("choicesclear", "Clear", 1459, 852, 103, () => selections.Clear(), !busy && selections.Count > 0, height: 27);
            canvas.Panel(new(350, 714, 370, 50), new Color(12, 22, 31) * .96f, ActionAccent, 5);
            canvas.CenterText(d.Ordered ? "Click cards in order" : "Click highlighted cards", new(356, 716, 358, 22), ActionAccent, .65f);
            canvas.CenterText($"Choose {d.Min}–{d.Max} · {selections.Count} selected", new(356, 739, 358, 20), Ink, .57f);
        } else if (d?.Kind == "input") {
            Button("confirm", TurnGuide.Confirm(state), 1266, 799, 296, () => Send(new(state.Id, d.Id, ReplyAction.Confirm)), CombatGuide.CanConfirm(state) && !busy, true, 47);
            Button("cancel", d.Intent == DecisionIntent.Priority && d.Cancel == "End Turn" ? "Pass turn" : d.Cancel, 1459, 852, 103, () => Send(new(state.Id, d.Id, ReplyAction.Cancel)), d.CancelEnabled && !busy, height: 27);
        } else if (d != null) Button("open-decision", "Make a choice", 1266, 799, 296, () => collapsedDecision = false, !busy, true, 47);
        else { canvas.CenterText(state.Status == "starting" ? "Starting game…" : "Resolving…", new(1266, 797, 296, 45), Muted, .9f); }
        Button("auto", automatic ? "Auto" : "Full control", 1266, 852, 111, () => SetAutomatic(!automatic), true, automatic, 27);
        Button("hold", held ? "Held" : "Hold", 1383, 852, 70, () => { held = !held; priority.Reset(); }, true, held, 27);
        DrawPhaseStrip();
    }
    private void DrawPhaseStrip()
    {
        int x = 27;
        foreach (var phase in TurnGuide.Stops) {
            bool current = phase.Key == match!.PhaseKey || phase.Key == "COMBAT_BEGIN" && match.PhaseKey.StartsWith("COMBAT");
            var r = new Rectangle(x, 870, 36, 25);
            canvas.Rounded(r, current ? new Color(80, 78, 62) : new Color(25, 33, 44), 4);
            canvas.CenterText(phase.Label switch { "Upkeep" => "UP", "Draw" => "DR", "Main 1" => "M1", "Combat" => "ATK", "Main 2" => "M2", _ => "END" }, r, current ? Gold : Muted, .43f, true);
            if (preferences.Stops.Contains(phase.Key)) canvas.Circle(new(x + 18, 866), 2.5f, Blue);
            string key = phase.Key;
            hits.Add(new("phase-stop:" + key, ControlScope("phase-stop:" + key), new(x, 863, 36, 32), () => { if (!preferences.Stops.Remove(key)) preferences.Stops.Add(key); SavePreferences(); }));
            x += 38;
        }
    }
    private void ManaOrb(string name, int amount, Vector2 point)
    {
        Color color = name switch { "W" => new(232, 222, 185), "U" => new(103, 169, 224), "B" => new(167, 152, 181), "R" => new(224, 130, 101), "G" => new(134, 186, 142), _ => new(191, 197, 203) };
        canvas.Circle(point, 15, color); canvas.CenterText(name, new((int)point.X - 15, (int)point.Y - 16, 30, 31), new(20, 26, 34), .62f, true);
        if (amount > 1) canvas.Text(amount.ToString(), (int)point.X + 9, (int)point.Y + 6, Ink, .57f, 30, 1, true);
    }
}
