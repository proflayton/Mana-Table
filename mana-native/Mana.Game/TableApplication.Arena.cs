using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
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
        battlefieldLabels.Clear(); battlefieldStatsBounds.Clear(); counterAnchors.Clear();
        choiceOrderBadges.Clear();
        stackRows.Clear(); prioritySeats.Clear();
        changeCalloutBounds = null; highlightedChangeTargets.Clear(); highlightedChangeSource = null;
        var state = match!;
        DrawTableSurface();
        // Queue the player's faces first; slow remote downloads must not bury the hand.
        foreach (var card in state.Viewer?.Zone("Hand").Cards ?? []) resources.Card(card);
        foreach (var card in state.Viewer?.Zone("Command").Cards ?? []) resources.Card(card);
        foreach (var card in tableScene!.PlayAccess.Groups.SelectMany(group => group.Cards).Select(item => item.Scene.Card)) resources.Card(card);
        var opponents = TableLayout.Opponents(state);
        foreach (int i in new[] { 1, 0, 2 }) if (i < opponents.Length) DrawOpponent(opponents[i], i);
        if (state.Viewer is { } viewer) DrawLocal(viewer);
        if (TableStudyEnabled) {
            canvas.BeginClip(BoardViewport);
            foreach (var node in tableScene!.BattlefieldDrawOrder) DrawSceneCard(node);
            canvas.EndClip();
        }
        for (int i = 0; i < opponents.Length && i < 3; i++) DrawHero(opponents[i], SeatHero(opponents[i].Id), false, SeatDisplayIndex(opponents[i].Id));
        DrawCombatConnections(); DrawAttachmentConnections(); DrawStack(); DrawSeatTransfers();
        DrawChangeHighlights();
        if (!BattlefieldFocused) DrawEffectTransitions();
        DrawHand(); DrawPlayAccess(); DrawPublicCollectionReturns(); DrawCardEffects(); DrawDepartures();
        if (!BattlefieldFocused && !CardCollectionOpen) DrawLiftedCards();
        // Your seat stays in front of the fan and readable during card travel.
        if (state.Viewer is { } local) DrawHero(local, SeatHero(local.Id), true, 3);
        DrawPriorityHandoff();
        DrawGameHeader(); DrawCombatBar(); DrawActionCallout();
        DrawTurnCallout();
        if (dragging && dragSource?.Card is { } heldCard) {
            bool legal = LegalDrop(HitAt(pointer));
            if (state.Decision?.Payload is AttackPayload or BlockPayload && cardPositions.TryGetValue(heldCard.CombatId, out var origin))
                DrawCombatAim(origin, legal ? Blue : Muted);
            else {
                // Keep the held occurrence in the scene, so release continues
                // from the pointer instead of respawning it at the library.
                var heldPose = new Pose(pointer.ToVector2() + new Vector2(0, -72), new(132, 185), -.025f, Elevation: 18);
                DrawTableCard(CardIdentity(heldCard), heldCard, heldPose, dragSource!.Zone!, dragSource.PlayerId,
                    dragSource.Id, new MatchIntent.UseCard(heldCard), (pose, _) => PaintInteractionCard(heldCard, pose, false, legal),
                    directPlacement: true);
            }
        }
        hovered = !dragging ? HitAt(pointer)?.Card : null;
        if (!OverlayOpen && !CardCollectionOpen) DrawReaderControls();
        // Decision text and its controls have priority over card readers and
        // held cards. Their input regions are registered last for the same reason.
        if (!BattlefieldFocused && !OverlayOpen && !AbilityActionPending && (!CardCollectionOpen || EmptyPublicCollection)) { DrawDecisionDock(); }
        if (!BattlefieldFocused) DrawChangeCallout();
        if (!BattlefieldFocused) { DrawAbilityActions(); DrawSourceTargeting(); }
        if (!CardCollectionOpen) DrawOverlays();
        if (BattlefieldFocused) DrawEffectTransitions();
        DrawLiftedCards();
        DrawActualImpacts();
        if (BattlefieldFocused && !DecisionModal && !AbilityActionPending && !CardCollectionOpen) { DrawDecisionDock(); DrawChangeCallout(); }
        if (BattlefieldFocused) { DrawAbilityActions(); DrawSourceTargeting(); }
        if (!CardCollectionOpen && (ReadingGalleryOpen || BattlefieldFocused || ZoneReadingOpen)) DrawReaderControls();
        if (ReadingGalleryOpen) hovered = HitAt(pointer)?.Card;
        DrawPendingReferencePreview();
        DrawLinkedSpellPreview();
        DrawStudyBattlefieldName();
        DrawStudyFocusNavigation();
        DrawCardCollection();
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
        var turn = MatchHeaderLayout.Turn;
        var phase = MatchHeaderLayout.Phase;
        canvas.Text(label, turn.X, turn.Y, Ink, .85f, turn.Width, 1, true);
        canvas.Text($"Turn {state.Turn}  ·  {state.Phase}", phase.X, phase.Y, Muted, .6f, phase.Width, 1);
        Button("battlefields", "Battlefields", MatchHeaderLayout.Battlefields, new MatchIntent.OpenBattlefields());
        DrawTableStates(state);
        DrawChangesControl();
        Button("history", "History", MatchHeaderLayout.History, new MatchIntent.OpenView(MatchView.History));
        Button("settings", "Settings", MatchHeaderLayout.Settings, new MatchIntent.OpenView(MatchView.Settings));
        bool departure = session.NeedsRecovery || state.Viewer?.Eliminated == true || state.Status == "error";
        Button(departure ? "leave-table" : "concede", departure ? "Leave table" : "Concede", MatchHeaderLayout.Departure,
            new MatchIntent.OpenView(departure ? MatchView.LeaveTable : MatchView.Concede), !busy && state.Status != "finished");

    }
    private void DrawHand()
    {
        var hand = tableScene!.Hand;
        canvas.Gradient(new(244, 785, 970, 115), Color.Transparent, new Color(8, 12, 20) * .85f);
        foreach (var node in hand.Cards) if (!SpreadContains(node.Id)) DrawSceneCard(node);
        if (hand.Total == 0) canvas.CenterText("Your hand is empty", new(455, 815, 690, 65), Muted * .6f, .86f);
    }
    private static string CardIdentity(Card card) => CardViews.Identity(card);
    private void DrawSceneCard(SceneCard node)
    {
        var card = node.Card;
        if (node.Zone != "Battlefield" && dragging && dragSource is { Card: { } held }
            && dragSource.Zone == node.Zone && CardViews.Same(card, held)) return;
        // The study reads a copy while the current battlefield occurrence stays
        // in place. The presentation controller includes this same anchor in
        // its scoped hit geometry, so a second click can close inspection.
        if (node.ScanFace && readingId == node.Id && !card.FaceDown) {
            var anchor = boardView.Apply(node.Pose);
            if (node.Attachment != null) PaintBattlefieldAttachment(node, anchor, false, false);
            else PaintBattlefieldScanFace(card, anchor, node.Owner, false, false, false, node.GroupFront, node.GroupCount);
            canvas.CardFrame(anchor, HoverAccent * .85f, 2);
        }
        var rest = node.Zone == "Battlefield" && !node.ScanFace && (card.PhasedOut || CardPresentation.HasAbility(card, "flying"))
            ? CardMotion.Airborne(node.Pose, now, preferences.ReducedMotion, node.Id) : node.Pose;
        var pose = DrawTableCard(node.Id, card, rest, node.Zone, node.Owner,
            "card:" + card.Key + ":" + card.VisualId, new MatchIntent.UseCard(card),
            (displayed, focus) => {
                if (node.Attachment != null && !focus && displayed.Size.X < 300) PaintBattlefieldAttachment(node, displayed,
                    card.Highlighted || card.CombatId.Length > 0 && selectedCombat == card.CombatId || TableChoices.Index(match!, card) is { } choice && decisions.Selections.Contains(choice), IsCardActionable(card));
                else PaintSceneCard(card, displayed, node.Zone, node.Owner, focus, node.Compact, node.GroupFront, node.GroupCount);
            }, node.Entrance, node.EntranceDelay, readingAnchor: node.Attachment?.Edge(node.Pose));
        if (!string.IsNullOrEmpty(card.CombatId)) {
            var anchor = node.ScanFace && readingId == node.Id ? boardView.Apply(node.Pose) : pose;
            if (node.Attachment is { } attachment && !node.Card.FaceDown) {
                var edge = attachment.Edge(anchor);
                anchor = new(edge.Center.ToVector2(), edge.Size.ToVector2());
            }
            cardPositions[card.CombatId] = anchor;
        }
    }
    private void PaintSceneCard(Card card, Pose pose, string zone, int owner, bool focus, bool compact = false, bool groupFront = true, int groupCount = 1)
    {
        int? choice = match == null ? null : TableChoices.Index(match, card);
        bool selected = choice is { } index ? decisions.Selections.Contains(index) : card.Highlighted || card.CombatId.Length > 0 && selectedCombat == card.CombatId;
        bool legal = IsCardActionable(card);
        bool scanFace = zone == "Battlefield" && !focus && !pose.IsProjected && pose.Size.X < 300
            && tableScene?.BattlefieldCard(owner, CardViews.Identity(card)) is { ScanFace: true };
        if (zone == "Battlefield") PaintAbilityEffects(card, pose);
        if (scanFace) PaintBattlefieldScanFace(card, pose, owner, selected, legal, focus, groupFront || focus, groupCount);
        else PaintCard(card, pose, selected, legal, focus, zone == "Battlefield" && card.PhasedOut && !focus ? .42f : 1);
        if (zone == "Battlefield" && card.PhasedOut) PaintPhasedOutline(pose);
        if (zone == "Command") {
            PaintCommanderTax(card, pose);
            if (focus && pose.Size.X >= 300) PaintCommanderTaxDetails(card, pose);
        }
        if (choice != null && selected) {
            var badge = new Rectangle((int)pose.Center.X - 39, pose.Bounds.Top + 8, 78, 24);
            ExtendCardHit(card, badge);
            if (decisions.Payload is OrderingPayload) PaintChoiceOrder(choice.Value, badge, .48f);
            else {
                var mark = pose.Point(.85f, .12f);
                canvas.Circle(mark, 9, new(19, 33, 35));
                canvas.Line(mark + new Vector2(-4, 0), mark + new Vector2(-1, 3), Gold, 2);
                canvas.Line(mark + new Vector2(-1, 3), mark + new Vector2(5, -4), Gold, 2);
            }
        }
        if (CombatTarget(card)) canvas.CardFrame(pose with { Size = pose.Size + new Vector2(10) }, Blue, 4);
        if (zone == "Battlefield" && (groupFront || focus) && !scanFace) {
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
        if (zone == "Battlefield" && !scanFace && (groupFront || focus)) PaintPreparedMarker(card, pose, owner);
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
                bool actionSpace = focus && (match?.Decision?.Payload is PriorityPayload && IsCardActionable(card)
                    || AbilityActionPending && match?.Decision is { } decision
                        && CardViews.Source(match, decision) is { } source && SameVisibleCard(card, source));
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
            float glow = !preferences.ReducedMotion && legal && match?.Decision?.Payload is PriorityPayload
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
            if (attack.Defender is { Kind: "card" } defender) {
                if (cardPositions.TryGetValue(defender.Id, out var target)) destination = target.EdgeToward(attacker.Center, 6);
            } else if (heroPositions.TryGetValue(attack.DefendingPlayerId, out var player)) {
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
        var decision = decisions.Presentation?.Payload == null ? null : state.Decision;
        // A source reference is context, not a stack object. In particular a
        // priority snapshot can retain the source of the preceding interaction.
        // Only real stack entries belong in the stack presentation.
        if (state.Stack.Length == 0 && (decision?.Payload is PriorityPayload
            || decision?.Context.Source?.Zone == "Battlefield")) return;
        // These journeys point to the existing source and actual library piles.
        // Do not create another card copy in the center of the table.
        if (DecisionLayout.UsesSourceWorkspace(decision)) {
            if (state.Stack.Length > 0) { DrawStackFlow(); DrawPendingSpellCards(); }
            return;
        }
        if (ValuedTargetDecision) { DrawPendingSpellCards(); return; }
        // Choosing an ability has not put it on the stack. Its current card
        // remains the source on the table; do not manufacture a stack copy.
        if (state.Stack.Length == 0 && (AbilityActionPending || SelectingActionTarget)) return;
        if (state.Stack.Length == 0 && decision?.Context.Source?.Card == null) return;
        bool payingSource = decision is { Payload: PaymentPayload, Context.Source.Card: not null }
            || !SelectingActionTarget && decision is { Payload: EntitySelectionPayload { Players.Length: > 0 }, Context.Source.Card: not null };
        if (state.Stack.Length > 0 && !payingSource) { DrawStackFlow(); DrawPendingSpellCards(); return; }
        canvas.Glow(new(807, 485), new(430, 165), Blue * .17f);
        var resting = TableWorld.Card(new(6, 108), new(245, 126));
        canvas.CardFrame(resting, Gold * .12f, 1);
        var stackCards = state.Stack.Take(3).Select(s => s.Card ?? new Card { Name = s.Name, Text = s.Text, Type = s.Ability ? "Ability" : "Spell" }).ToArray();
        if (payingSource) stackCards = [decision!.Context.Source?.Card!];
        if (stackCards.Length == 0 && decision?.Context.Source?.Card is { } source) stackCards = [source];
        for (int i = stackCards.Length - 1; i >= 0; i--) {
            var reported = stackCards[i];
            var card = decision is { } sourceDecision && (payingSource || state.Stack.Length == 0)
                ? CardViews.Source(state, sourceDecision) ?? stackCards[i] : CardViews.Current(state, stackCards[i]);
            bool alsoInZone = state.Players.Any(p => p.Zones.Any(z => z.Cards.Any(c => CardIdentity(c) == CardIdentity(card))));
            string identity = payingSource ? "payment:" + decision!.Id : alsoInZone ? "stack:" + (state.Stack.ElementAtOrDefault(i)?.Id ?? "source") : CardIdentity(card);
            if (identity.Length == 0) identity = "stack:" + (state.Stack.ElementAtOrDefault(i)?.Id ?? card.Name);
            Vector2 entrance = new(800, 520);
            if (card.VisualId.Length > 0) {
                if (matchPresentation.Cards.TryGetPose(card.VisualId, out var sourcePose)) entrance = sourcePose.Center;
                else if (state.Activity.LastOrDefault(a => a.CardId == card.VisualId && a.PlayerId != null) is { PlayerId: { } actor })
                    entrance = SeatHand(actor);
            }
            int stackIndex = i;
            var spellPose = TableWorld.Spell(i);
            if (state.Stack.Length > 0 || payingSource) spellPose = spellPose with { Center = spellPose.Center + new Vector2(32, 14), Size = new(124, 174) };
            DrawTableCard(identity, card, spellPose, "Stack", null, "stack-item:" + i, new MatchIntent.UseStackCard(card, reported, stackIndex, payingSource), (displayed, focus) => PaintInteractionCard(card, displayed, stackIndex == 0, false, focus), entrance);
        }
        if (state.Stack.Length == 0 && !payingSource) {
            canvas.Text(stackCards[0].Name, 549, 400, Ink, .62f, 154, 3, true);
            Button("stack", "Inspect source", 549, 462, 154, new MatchIntent.InspectDecisionSource(), height: 29);
        }
        if (state.Stack.Length > 0) DrawPendingSpellCards();
    }
    private void ManaOrb(string name, int amount, Vector2 point)
    {
        Color color = name switch { "W" => new(232, 222, 185), "U" => new(103, 169, 224), "B" => new(167, 152, 181), "R" => new(224, 130, 101), "G" => new(134, 186, 142), _ => new(191, 197, 203) };
        canvas.Circle(point, 15, color); canvas.CenterText(name, new((int)point.X - 15, (int)point.Y - 16, 30, 31), new(20, 26, 34), .62f, true);
        if (amount > 1) canvas.Text(amount.ToString(), (int)point.X + 9, (int)point.Y + 6, Ink, .57f, 30, 1, true);
    }
}
