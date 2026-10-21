using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private static readonly Color ActionAccent = new(115, 220, 255);
    private void DrawArena()
    {
        attachmentBadges.Clear(); attachmentConnections.Clear();
        battlefieldLabels.Clear(); denseStatsBounds.Clear(); counterAnchors.Clear();
        choiceOrderBadges.Clear();
        stackRows.Clear(); prioritySeats.Clear();
        changeCalloutBounds = null; highlightedChangeTargets.Clear(); highlightedChangeSource = null;
        var state = match!;
        priorityView = PriorityPresentation.From(state);
        DrawTableSurface();
        // Queue the player's faces first; slow remote downloads must not bury the hand.
        foreach (var card in state.Viewer?.Zone("Hand").Cards ?? []) resources.Card(card);
        foreach (var card in state.Viewer?.Zone("Command").Cards ?? []) resources.Card(card);
        var opponents = TableLayout.Opponents(state);
        foreach (int i in new[] { 1, 0, 2 }) if (i < opponents.Length) DrawOpponent(opponents[i], i);
        if (state.Viewer is { } viewer) DrawLocal(viewer);
        for (int i = 0; i < opponents.Length && i < 3; i++) DrawHero(opponents[i], TableLayout.Hero(i), false, i);
        DrawCombatConnections(); DrawAttachmentConnections(); DrawStack(); DrawSeatTransfers();
        DrawChangeHighlights();
        if (!BattlefieldFocused) DrawEffectTransitions();
        DrawHand(); DrawCardEffects(); DrawDepartures();
        if (!BattlefieldFocused) DrawLiftedCards();
        // Your seat stays in front of the fan and readable during card travel.
        if (state.Viewer is { } local) DrawHero(local, TableLayout.Hero(3), true, 3);
        DrawPriorityHandoff();
        foreach (var item in feedback) {
            float age = (float)(1.5 - (item.Until - now));
            canvas.Text(item.Text, (int)item.Position.X + 35, (int)item.Position.Y - 20 - (int)(age * 30), item.Color * Math.Min(1, (float)(item.Until - now)), 1.6f, 100, 1, true);
        }
        DrawGameHeader(); DrawCombatBar(); DrawActionCallout();
        DrawTurnCallout();
        if (dragging && dragSource?.Card is { } heldCard) {
            bool legal = LegalDrop(HitAt(pointer));
            if (dragSource.Zone == "Hand" && state.Decision?.Intent == DecisionIntent.Priority) {
                canvas.Panel(new(286, 166, 1028, 609), Color.Transparent, (legal ? Blue : Muted) * .45f, 12);
            }
            if (state.Decision?.Intent is DecisionIntent.Attack or DecisionIntent.Block && cardPositions.TryGetValue(heldCard.CombatId, out var origin))
                DrawCombatAim(origin, legal ? Blue : Muted);
            else {
                // Keep the held occurrence in the scene, so release continues
                // from the pointer instead of respawning it at the library.
                var heldPose = cardPresentation.Hold(CardIdentity(heldCard), new(pointer.ToVector2(), new(154, 216), -.04f, Elevation: 46));
                PaintInteractionCard(heldCard, heldPose, true, false);
            }
        }
        hovered = !dragging ? HitAt(pointer)?.Card : null;
        if (!OverlayOpen) DrawReaderControls();
        // Decision text and its controls have priority over card readers and
        // held cards. Their input regions are registered last for the same reason.
        if (!BattlefieldFocused && !OverlayOpen && !AbilityActionPending) { DrawDecisionDock(); DrawQuietPriorityControl(); }
        if (!BattlefieldFocused) DrawChangeCallout();
        if (!BattlefieldFocused) DrawAbilityActions();
        DrawOverlays();
        if (BattlefieldFocused) DrawEffectTransitions();
        DrawLiftedCards();
        if (BattlefieldFocused && !DecisionModal && !AbilityActionPending) { DrawDecisionDock(); DrawQuietPriorityControl(); DrawChangeCallout(); }
        if (BattlefieldFocused) DrawAbilityActions();
        if (ReadingGalleryOpen || BattlefieldFocused) DrawReaderControls();
        if (ReadingGalleryOpen) hovered = HitAt(pointer)?.Card;
        DrawExperienceLabHeader();
    }
    private void DrawGameHeader()
    {
        var state = match!;
        canvas.Gradient(new(0, 0, 1600, 54), new Color(8, 12, 18) * .8f, Color.Transparent);
        if (ExperienceLabOpen) return;
        canvas.Text("MANA TABLE", 24, 17, Gold, .9f, 180, 1, true);

        var active = state.Players.FirstOrDefault(p => p.Id == state.ActivePlayerId);
        string label = state.Turn == 0 ? "Opening hands" : state.ActivePlayerId == state.ViewerId ? "Your turn" : active?.Name ?? "Waiting";
        canvas.Text(label, 220, 9, Ink, .85f, 410, 1, true);
        canvas.Text($"Turn {state.Turn}  ·  {state.Phase}", 220, 32, Muted, .6f, 410, 1);
        Button("battlefields", "Battlefields", 650, 12, 178, () => OpenZone(state.ViewerId, "Battlefield"), height: 35);
        DrawTableStates(state);
        DrawChangesControl();
        Button("history", "History", 1190, 12, 110, () => { overlay = "history"; historyPage = 0; }, height: 35);
        Button("settings", "Settings", 1310, 12, 112, () => overlay = "settings", height: 35);
        bool departure = session.NeedsRecovery || state.Viewer?.Eliminated == true || state.Status == "error";
        Button(departure ? "leave-table" : "concede", departure ? "Leave table" : "Concede", 1432, 12, 143,
            () => overlay = departure ? "leave-table" : "concede", !busy && state.Status != "finished", height: 35);

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
        if (match.Viewer is { } viewer) {
            int placements = HandPlacements.AllOptions(match).Sum(a => a.HandPlacementLimit);
            string landState = string.Join(" · ", new[] { HandPlacements.LandPlayState(viewer), placements > 0
                ? placements + (placements == 1 ? " placement available" : " placements available") : "" }.Where(s => s.Length > 0));
            if (landState.Length > 0) {
            canvas.Panel(new(650, 864, 480, 31), new Color(12, 19, 27) * .95f, new Color(62, 78, 90), 4);
            canvas.Text(landState, 662, 873, Ink, .63f, 456, 1);
            }
        }
        if (hand.Total > 11) {
            Button("handprev", "‹", 254, 789, 34, () => handPage = hand.Page - 1, hand.Page > 0, height: 34);
            Button("handnext", "›", 1203, 789, 34, () => handPage = hand.Page + 1, (hand.Page + 1) * 11 < hand.Total, height: 34);
        }
    }
    private static string CardIdentity(Card card) => CardViews.Identity(card);
    private void DrawSceneCard(SceneCard node)
    {
        var card = node.Card;
        var rest = node.Zone == "Battlefield" && !node.Dense && (card.PhasedOut || CardPresentation.HasAbility(card, "flying"))
            ? CardMotion.Airborne(node.Pose, now, preferences.ReducedMotion, node.Id) : node.Pose;
        var pose = DrawTableCard(node.Id, card, rest, node.Zone, node.Owner,
            "card:" + card.Key + ":" + card.VisualId, () => CardClick(card),
            (displayed, focus) => PaintSceneCard(card, displayed, node.Zone, node.Owner, focus, node.Compact, node.GroupFront, node.GroupCount, node.Dense), node.Entrance, node.EntranceDelay);
        if (!string.IsNullOrEmpty(card.CombatId)) cardPositions[card.CombatId] = pose;
    }
    private void PaintSceneCard(Card card, Pose pose, string zone, int owner, bool focus, bool compact = false, bool groupFront = true, int groupCount = 1, bool dense = false)
    {
        int? choice = match == null ? null : TableChoices.Index(match, card);
        bool selected = choice is { } index ? selections.Contains(index) : card.Highlighted || card.CombatId.Length > 0 && selectedCombat == card.CombatId;
        bool legal = IsCardActionable(card);
        if (zone == "Battlefield") PaintAbilityEffects(card, pose);
        PaintCard(card, pose, selected, legal, focus, zone == "Battlefield" && card.PhasedOut && !focus ? .42f : 1);
        if (zone == "Battlefield" && card.PhasedOut) PaintPhasedOutline(pose);
        if (zone == "Command") {
            PaintCommanderTax(card, pose);
            if (focus && pose.Size.X >= 300) PaintCommanderTaxDetails(card, pose);
        }
        if (choice != null && selected) {
            var badge = new Rectangle((int)pose.Center.X - 39, pose.Bounds.Top + 8, 78, 24);
            ExtendCardHit(card, badge);
            if (match!.Decision!.Ordered) PaintChoiceOrder(choice.Value, badge, .48f);
            else {
                var mark = pose.Point(.85f, .12f);
                canvas.Circle(mark, 9, new(19, 33, 35));
                canvas.Line(mark + new Vector2(-4, 0), mark + new Vector2(-1, 3), Gold, 2);
                canvas.Line(mark + new Vector2(-1, 3), mark + new Vector2(5, -4), Gold, 2);
            }
        }
        if (CombatTarget(card)) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(10) }, Blue, 4);
        if (zone == "Battlefield" && (groupFront || focus)) {
            if (pose.Size.X < 300) PaintBattlefieldLabels(card, pose, owner, groupCount);
            else {
                PaintAttachedCardState(card, pose, owner);
                if (focus) PaintStateDetails(card, pose);
            }
        }
        if (zone is "Hand" or "Command" && pose.Size.X >= 300) PaintAttachedCardState(card, pose);
        // Paint the relationship marker after combat stats so the small corner
        // indicator remains visible on densely packed projected card faces.
        if (zone == "Battlefield" && (groupFront || focus)) PaintAttachmentBadge(card, pose, owner, compact && !focus);
    }
    private void PaintCard(Card card, Pose pose, bool selected, bool legal, bool focus = false, float opacity = 1)
    {
        if (selected) canvas.Glow(pose.Center, pose.Size * 1.2f, Gold * .18f);
        canvas.Surface(null, pose with { Center = pose.Center + new Vector2(4 + pose.Elevation * .18f, (pose.IsProjected ? 5 : 10) + pose.Elevation), Size = pose.Size + new Vector2(7) }, Color.Black * ((pose.Elevation > 0 ? .27f : .4f) * opacity));
        // The backing must fade too, otherwise the face only darkens against an opaque slab.
        canvas.Surface(null, pose with { Size = pose.Size + new Vector2(4) }, new Color(9, 13, 18) * opacity);
        if (card.FaceDown) PaintBack(pose, opacity);
        else if (resources.Card(card) is { } image) canvas.Surface(image, pose, Color.White * opacity);
        else {
            canvas.Surface(null, pose, new Color(53, 63, 71) * opacity);
            canvas.CardFrame(pose with { Size = pose.Size - new Vector2(7) }, Muted * (.25f * opacity));
            var r = pose.Bounds;
            if (pose.Size.X >= 300) {
                bool actionSpace = focus && (match?.Decision?.Intent == DecisionIntent.Priority && IsCardActionable(card)
                    || AbilityActionPending && match?.Decision is { } decision
                        && InteractionCards.Source(match, decision) is { } source && SameVisibleCard(card, source));
                int typeTop = actionSpace ? (int)(r.Height * .53f) : 121;
                canvas.Text(card.Name, r.X + 20, r.Y + 20, Ink * opacity, 1.05f, r.Width - 40, 2, true);
                canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, r.X + 20, r.Y + 82, Gold * opacity, .8f, r.Width - 40, 1);
                canvas.Text(card.Type, r.X + 20, r.Y + typeTop, Gold * opacity, .75f, r.Width - 40, 2);
                canvas.Line(new(r.X + 20, r.Y + typeTop + 55), new(r.Right - 20, r.Y + typeTop + 55), Muted * (.4f * opacity));
                canvas.Text(card.Text, r.X + 20, r.Y + typeTop + 74, Ink * opacity, .78f, r.Width - 40, Math.Max(1, (r.Height - typeTop - 99) / 27));
            } else {
                canvas.Text(card.Name, r.X + 5, r.Y + 5, Ink * opacity, pose.Size.X > 100 ? .67f : .47f, r.Width - 10, pose.Size.X > 100 ? 3 : 2, true);
                canvas.Text(card.ManaCost == "no cost" ? "" : card.ManaCost, r.X + 5, r.Bottom - 27, Gold * opacity, .54f, r.Width - 10, 1);
                if (pose.Size.X > 130) canvas.Text(card.Type, r.X + 6, r.Y + (int)(r.Height * .62f), Muted * opacity, .5f, r.Width - 12, 2);
            }
        }
        if (legal || selected) {
            float glow = !preferences.ReducedMotion && legal && match?.Decision?.Intent == DecisionIntent.Priority
                ? .85f + .15f * MathF.Sin((float)now * 2.4f) : 1;
            if (selected) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(6) }, Gold, 4);
            else canvas.CardAction(pose, ActionAccent, glow);
        }
        if (card.Attacking || card.Blocking) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(4) }, card.Attacking ? Red : Blue, 2);
        if (pose.Elevation > 4) {
            float light = Math.Min(.4f, pose.Elevation / 100) * opacity;
            canvas.Line(pose.Point(.03f, .01f), pose.Point(.97f, .01f), Ink * light, 1);
            canvas.Line(pose.Point(.99f, .03f), pose.Point(.99f, .5f), Ink * light * .5f, 1);
        }
        if (focus) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(4) }, Ink * .5f, 2);
    }
    private void PaintBack(Pose pose, float opacity = 1)
    {
        if (resources.CardBack != null) { canvas.Surface(resources.CardBack, pose, Color.White * opacity); return; }
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
        // Choosing an ability has not put it on the stack. Its current card
        // remains the source on the table; do not manufacture a stack copy.
        if (state.Stack.Length == 0 && AbilityActionPending) return;
        if (state.Stack.Length == 0 && state.Decision?.SourceCard == null) return;
        canvas.Glow(new(807, 485), new(430, 165), Blue * .17f);
        var resting = TableWorld.Card(new(6, 108), new(245, 126));
        canvas.CardFrame(resting, Gold * .12f, 1);
        DrawStackFlow();
        bool payingSource = state.Decision is { Intent: DecisionIntent.Mana, SourceCard: not null }
            || state.Decision is { Intent: DecisionIntent.Selection, SourceCard: not null, PlayerChoices.Length: > 0 };
        var stackCards = state.Stack.Take(3).Select(s => s.Card ?? new Card { Name = s.Name, Text = s.Text, Type = s.Ability ? "Ability" : "Spell" }).ToArray();
        if (payingSource) stackCards = [state.Decision!.SourceCard!];
        Pose? topPose = null;
        if (stackCards.Length == 0 && state.Decision?.SourceCard is { } source) stackCards = [source];
        for (int i = stackCards.Length - 1; i >= 0; i--) {
            var reported = stackCards[i];
            var card = state.Decision is { } sourceDecision && (payingSource || state.Stack.Length == 0)
                ? InteractionCards.Source(state, sourceDecision) ?? stackCards[i] : InteractionCards.Current(state, stackCards[i]);
            bool alsoInZone = state.Players.Any(p => p.Zones.Any(z => z.Cards.Any(c => CardIdentity(c) == CardIdentity(card))));
            string identity = payingSource ? "payment:" + state.Decision!.Id : alsoInZone ? "stack:" + (state.Stack.ElementAtOrDefault(i)?.Id ?? "source") : CardIdentity(card);
            if (identity.Length == 0) identity = "stack:" + (state.Stack.ElementAtOrDefault(i)?.Id ?? card.Name);
            Vector2 entrance = new(800, 520);
            if (card.VisualId.Length > 0) {
                if (cardPresentation.TryGetPose(card.VisualId, out var sourcePose)) entrance = sourcePose.Center;
                else if (state.Activity.LastOrDefault(a => a.CardId == card.VisualId && a.PlayerId != null) is { PlayerId: { } actor })
                    entrance = TableLayout.SeatHand(actor == state.ViewerId ? 3 : TableLayout.SeatIndex(state, actor));
            }
            int stackIndex = i;
            var spellPose = TableWorld.Spell(i);
            if (state.Stack.Length > 0 || payingSource) spellPose = spellPose with { Center = spellPose.Center + new Vector2(32, 14), Size = new(124, 174) };
            var stackPose = DrawTableCard(identity, card, spellPose, "Stack", null, "stack-item:" + i, () => {
                if (payingSource) Inspect(card);
                else if (IsCardActionable(reported)) CardClick(reported);
                else if (state.Stack.Length > stackIndex) OpenStack(stackIndex);
                else Inspect(card);
            }, (displayed, focus) => PaintInteractionCard(card, displayed, stackIndex == 0, false, focus), entrance);
            if (i == 0) topPose = stackPose;
        }
        if (!payingSource && topPose is { } origin && state.Stack.FirstOrDefault() is { } top) DrawStackTargets(top, origin);
        if (state.Stack.Length == 0 && !payingSource) {
            canvas.Text(stackCards[0].Name, 549, 400, Ink, .62f, 154, 3, true);
            Button("stack", "Inspect source", 549, 462, 154, () => { if (state.Decision?.SourceCard is { } source) Inspect(source); }, height: 29);
        } else if (!payingSource && topPose is { } topCard) {
            var next = topCard.Point(.92f, .08f);
            canvas.Circle(next, 15, Gold);
            canvas.CenterText("1", new((int)next.X - 15, (int)next.Y - 15, 30, 30), new(16, 26, 31), .7f, true);
        }
        if (state.Stack.Length > 0) {
            Button("action-details", "Details", 871, 465, 101, () => OpenStack(), height: 28);
            canvas.Text("After everyone passes", 717, 520, Muted, .46f, 196, 1);
        }
    }
    private void DrawDecisionDock()
    {
        var state = match!; var d = state.Decision;
        decisionTextBounds = Rectangle.Empty; decisionText = ""; decisionTextOverflow = false;
        decisionPanelBounds = new(1240, 712, 360, 188);
        canvas.Gradient(new(1240, 712, 360, 188), Color.Transparent, new Color(10, 16, 21) * .95f);
        if (DrawRecoveryDock(state)) return;
        if (state.Status is "finished" or "error") {
            canvas.Text(state.Result ?? "Game stopped", 1267, 730, Gold, 1.17f, 295, 1, true);
            canvas.Text(state.Error ?? "You can still inspect the final battlefield.", 1267, 767, Muted, .63f, 294, 2);
            if (localMatch || state.Status == "finished") Button("return", IsWorkshopMatch ? "Return to tune" : localMatch ? "Return to setup" : "Return to lobby", 1266, 828, 296, ReturnFromMatch, !busy, true, 47);
            else Button("error-leave", "Leave table", 1266, 828, 296, () => overlay = "leave-table", !busy, true, 47);
            return;
        }
        if (state.Viewer?.Eliminated == true) {
            canvas.Text("WATCHING THE TABLE", 1266, 727, Gold, .69f, 298, 1, true);
            canvas.Text("You have been eliminated. You can inspect the table while the remaining players finish.", 1266, 754, Ink, .78f, 295, 3);
            Button("eliminated-leave", "Leave table", 1266, 828, 296, () => overlay = "leave-table", !busy, height: 47);
            return;
        }
        bool autoFollowing = TurnGuide.CanAutoPass(state, automatic, held, preferences.Stops, false);
        bool recipientChoice = d?.Intent == DecisionIntent.Selection && StateClarity.Recipients(state).Length > 0;
        bool following = busy || d == null || autoFollowing;
        string status = !busy && autoFollowing ? "AUTO · PASSING PRIORITY" : !busy && recipientChoice ? "CHOOSE A PLAYER" : StateClarity.DecisionStatus(state, busy);
        var abilities = PriorityAbilityOptions();
        string instruction = !busy && (recipientChoice || d?.Intent == DecisionIntent.Priority && state.Stack.Length == 0)
            ? "" : StateClarity.DecisionInstruction(state, busy, pendingActionLabel);
        var textLayout = PlanDecisionText(status, instruction, abilities.Length > 0);
        if (!recipientChoice) {
            canvas.Panel(new(1254, decisionPanelBounds.Top, 320, 795 - decisionPanelBounds.Top), new(12, 22, 30), new(57, 75, 88), 5);
            canvas.Text(status, 1266, textLayout.StatusY, following ? Muted : Gold, .7f, decisionTextOverflow ? 180 : 298, 1, true);
            canvas.Text(instruction, 1266, textLayout.TextY, Ink, .75f, 295, textLayout.Lines);
            hits.Add(new("instructions", ControlScope("instructions"), new(1262, textLayout.StatusY, 304, 789 - textLayout.StatusY), () => { overlay = "help"; inspectPage = 0; }));
            if (decisionTextOverflow) Button("decision-read-all", "Read all", 1456, textLayout.StatusY - 1, 106, () => { overlay = "help"; inspectPage = 0; }, height: 23);
        } else decisionPanelBounds = new(1240, 852, 360, 48);
        if (abilities.Length > 0) DrawPriorityAbilityControl(abilities);
        if (following) {
            canvas.Panel(new(1266, 799, 296, 47), new(22, 31, 39), new Color(66, 82, 94), 4);
            canvas.CenterText(busy ? "Waiting for confirmation…" : autoFollowing ? "Auto is passing priority" : StateClarity.WaitingTitle(state), new(1275, 800, 278, 45), Muted, .76f);
        } else if (recipientChoice) {
            // The named recipient controls beside the source own this choice.
        } else if (TableChoice) {
            Button("confirm", selections.Count == 0 && d!.Min == 0 ? "Choose none" : $"Confirm ({selections.Count}/{d!.Max})", 1266, 799, 296,
                () => Send(new(state.Id, d.Id, ReplyAction.Choose, Choices: selections.ToArray())), !busy && TableChoices.CanConfirm(state, selections), true, 47);
            Button("choicesclear", "Clear", 1459, 852, 103, () => selections.Clear(), !busy && selections.Count > 0, height: 27);
        } else if (d?.Kind == "input") {
            Button("confirm", TurnGuide.Confirm(state), 1266, 799, 296, () => Send(new(state.Id, d.Id, ReplyAction.Confirm)), CombatGuide.CanConfirm(state) && !busy, true, 47);
            Button("cancel", d.Intent == DecisionIntent.Priority && d.Cancel == "End Turn" ? "Pass turn" : d.Cancel, 1459, 852, 103, () => Send(new(state.Id, d.Id, ReplyAction.Cancel)), d.CancelEnabled && !busy, height: 27);
        } else if (d != null) Button("open-decision", "Make a choice", 1266, 799, 296, () => collapsedDecision = false, !busy, true, 47);
        else { canvas.CenterText(state.Status == "starting" ? "Starting game…" : "Resolving…", new(1266, 797, 296, 45), Muted, .9f); }
        Button("auto", automatic ? "Auto" : preferences.TextScale > 1.1f ? "Manual" : "Full control", 1266, 852, 111, () => UseAutomaticControl(!automatic), true, automatic, 27);
        Button("hold", held ? "Held" : "Hold", 1383, 852, 70, ToggleHoldControl, true, held, 27);
        DrawPhaseStrip();
    }
    private void DrawPhaseStrip()
    {
        int x = 27;
        foreach (var phase in TurnGuide.Stops) {
            bool current = phase.Key == match!.PhaseKey || phase.Key == "COMBAT_BEGIN" && match.PhaseKey.StartsWith("COMBAT");
            string key = phase.Key, id = "phase-stop:" + key;
            var bounds = new Rectangle(x, 863, 36, 32);
            var r = new Rectangle(x, 870, 36, 25);
            PaintControlSurface(id, bounds, current ? new Color(80, 78, 62) : new Color(25, 33, 44),
                current ? Gold * .6f : new Color(51, 64, 75));
            canvas.CenterText(phase.Label switch { "Upkeep" => "UP", "Draw" => "DR", "Main 1" => "M1", "Combat" => "ATK", "Main 2" => "M2", _ => "END" }, r, current ? Gold : Muted, .43f, true);
            if (preferences.Stops.Contains(phase.Key)) canvas.Circle(new(x + 18, 866), 2.5f, Blue);
            hits.Add(new(id, ControlScope(id), bounds, () => { if (!preferences.Stops.Remove(key)) preferences.Stops.Add(key); SavePreferences(); }));
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
