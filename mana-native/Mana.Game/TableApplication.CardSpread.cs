using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private CardCollectionTarget? spreadCollection;
    private string? spreadGame, spreadDecision;
    private (string Game, int Viewer, string Decision)? cleanupHandSpread;
    private string? spreadId => spreadCollection?.Id;
    private int spreadViewer;
    private BoardView? spreadTarget;
    private readonly BoardViewMotion spreadCamera = new();
    private BoardView spreadView = BoardView.Identity;
    private bool spreadCameraUsed, spreadPanning, spreadKeyboardPanning;
    private readonly Dictionary<string, Pose> spreadWorldTargets = [];
    private Point spreadPanStart;
    private BoardView spreadPanView;
    private string spreadTitle = "", spreadInstruction = "";
    private int spreadInstructionY, spreadAreaTop = 148;
    private bool CardCollectionOpen => spreadId != null;
    private bool AccessCollectionAllowed => match != null && !session.NeedsRecovery && (match.Decision?.Payload is null or PriorityPayload);

    private bool HandCollectionAllowed => match != null && decisions.Presentation is { } presentation
        && (presentation.Payload is null or PriorityPayload or OpeningHandPayload
            || presentation.Payload is OptionsPayload or OrderingPayload && presentation.Reading.HasFlag(DecisionReading.HandSelectionInspection)
            || presentation.Payload is EntitySelectionPayload && match.Viewer?.Zone("Hand").Cards.Any(card => card.Selectable) == true);

    private bool CollectionAllowed(CardCollectionTarget target) => target switch {
        CardCollectionTarget.HandCollection => HandCollectionAllowed,
        CardCollectionTarget.PublicZone zone => match?.Players.Any(player => player.Id == zone.PlayerId) == true,
        _ => AccessCollectionAllowed
    };

    private void ExpandCardCollection(CardCollectionTarget target)
    {
        if (dragging || match == null || session.NeedsRecovery && target is not CardCollectionTarget.PublicZone
            || ActivityOpen || updatesOpen || ExperienceOpen || overlay.Length > 0 || zoneName != null) return;
        if (!CollectionAllowed(target)) return;
        if (target is CardCollectionTarget.HandCollection && match.Viewer?.Zone("Hand").Cards.Length is not > 0) return;
        if (target is CardCollectionTarget.AccessCollection or CardCollectionTarget.AccessPacket
            && tableScene?.PlayAccess.Groups.Any(group => target is CardCollectionTarget.AccessCollection
                || target is CardCollectionTarget.AccessPacket packet && group.Id == packet.PacketId) != true) return;
        if (spreadCollection == target) { CollapseCardReading(); FoldCardCollection(); return; }
        CollapseCardReading();
        FoldCardCollection();
        CancelReleasedPlayIntent();
        spreadCollection = target; spreadGame = match.Id; spreadViewer = match.ViewerId; spreadDecision = match.Decision?.Id;
        spreadWorldTargets.Clear();
        spreadTarget = null; spreadCameraUsed = false; spreadPanning = false; spreadCamera.Reset();
        session.CancelAdvance();
    }

    private void FoldCardCollection()
    {
        if (CardCollectionOpen) CollapseCardReading();
        QueuePublicCollectionReturn();
        spreadCollection = null; spreadTarget = null; spreadPanning = false; spreadCameraUsed = false;
        cleanupHandSpread = null;
        spreadWorldTargets.Clear();
        spreadCamera.Reset();
    }

    private void ReconcileCardCollection()
    {
        ReconcilePublicCollectionReturns();
        if (spreadId == null) return;
        if (cleanupHandSpread != null && cleanupHandSpread != CurrentCleanupHandScope()) {
            FoldCardCollection();
            return;
        }
        if (match is not { } state || state.Id != spreadGame || state.ViewerId != spreadViewer
            || session.NeedsRecovery && spreadCollection is not CardCollectionTarget.PublicZone
            || ActivityOpen || updatesOpen || ExperienceOpen || overlay.Length > 0 || zoneName != null
            || !CollectionAllowed(spreadCollection!)
            || spreadCollection is CardCollectionTarget.PublicZone && state.Decision?.Payload is not (null or PriorityPayload)
                && state.Decision?.Id != spreadDecision)
        {
            CollapseCardReading();
            FoldCardCollection();
        }
    }

    private (string Game, int Viewer, string Decision)? CurrentCleanupHandScope()
        => match?.Decision is { } decision && decisions.Choice is { Keeping: true } && HandReductionFacts.IsCurrent(match)
            ? (match.Id, match.ViewerId, decision.Id) : null;

    private void ExpandCleanupHandCollection()
    {
        var current = CurrentCleanupHandScope();
        if (cleanupHandSpread != null && cleanupHandSpread != current) FoldCardCollection();
        if (current == null || spreadCollection == CardCollectionTarget.Hand) return;
        ExpandCardCollection(CardCollectionTarget.Hand);
        if (spreadCollection == CardCollectionTarget.Hand) cleanupHandSpread = current;
    }

    private void PrepareCardCollection()
    {
        if (tableScene?.Spread is not { } plan) {
            if (spreadId != null) FoldCardCollection();
            return;
        }
        if (spreadWorldTargets.Count > 0 && (spreadWorldTargets.Count != plan.WorldCards.Count
            || plan.WorldCards.Any(card => !spreadWorldTargets.TryGetValue(card.Id, out var previous) || previous != card.Pose)))
            spreadCameraUsed = false;
        // Retain geometry only. A revoked disclosure cannot survive in this
        // continuity check as an old card face, action key or route.
        spreadWorldTargets.Clear();
        foreach (var card in plan.WorldCards) spreadWorldTargets[card.Id] = card.Pose;
        bool initial = spreadTarget == null;
        spreadTarget = plan.View;
        spreadView = spreadCamera.Navigate(match!.Id, match.ViewerId, plan.View, now,
            preferences.ReducedMotion || initial, dragging || pressed != null || readingId != null,
            direct: spreadPanning || spreadKeyboardPanning);
    }

    private bool SpreadContains(string id) => CardCollectionOpen && tableScene?.Spread?.Contains(id) == true;

    private void PrepareCardCollectionHeader()
    {
        if (!CardCollectionOpen) return;
        spreadTitle = spreadCollection is CardCollectionTarget.PublicZone zone
            ? zone.ZoneName + " · " + match!.Players.First(player => player.Id == zone.PlayerId).Name
            : spreadId == CardSpreadPlan.HandId ? "Your hand"
            : string.Join(" · ", match!.PlayAccesses.Where(access => spreadId == CardSpreadPlan.AccessId
                || tableScene?.PlayAccess.Groups.Any(group => group.Id == spreadId && group.Cards.Any(item => item.Access.CardId == access.CardId)) == true)
                .Select(access => access.Zone).Distinct());
        spreadInstruction = "";
        if (spreadCollection is CardCollectionTarget.PublicZone && match?.Decision?.Payload is not (null or PriorityPayload))
            spreadInstruction = "Inspecting · return to your choice with Escape";
        if (spreadId == CardSpreadPlan.HandId && decisions.Presentation?.Payload is not (null or PriorityPayload)) {
            spreadTitle = decisions.Presentation!.Headline;
            spreadInstruction = decisions.Presentation.Instruction;
            if (decisions.Presentation.Payload is OpeningHandPayload { Consequence: { } hand })
                spreadInstruction = hand.FreeMulligan ? "Free mulligan" : $"Mulligan → keep {hand.NextHandSize}";
            if (decisions.Choice is { HandReduction: { } reduction } draft) {
                int discards = reduction.HandSize - reduction.MaximumHandSize;
                spreadTitle = draft.Keeping ? $"Choose {reduction.MaximumHandSize} cards to keep" : $"Choose {discards} cards to discard";
                spreadInstruction = draft.Keeping
                    ? $"Keep {decisions.Selections.Count} / {reduction.MaximumHandSize} · The other {discards} cards will be discarded together."
                    : $"Discard {decisions.Selections.Count} / {discards} · Keep {reduction.MaximumHandSize} cards.";
            }
        }
        spreadInstructionY = 72 + canvas.TextSize(spreadTitle, .85f, 1120, 80, true).Y + 8;
        spreadAreaTop = Math.Max(CardSpreadPlan.Viewport.Top, spreadInstructionY
            + (spreadInstruction.Length == 0 ? 0 : canvas.TextSize(spreadInstruction, .62f, 1140, 80).Y + 14));
    }

    private int MeasureSpreadPacketHeader(CardSpreadPacket packet, int width)
        => 24 + canvas.TextSize(packet.Title, .68f, Math.Max(1, width - 52), 80, true).Y
            + canvas.TextSize(packet.Context, .53f, Math.Max(1, width - 52), 80).Y + 8;

    private void DrawCardCollection()
    {
        if (!CardCollectionOpen || tableScene?.Spread is not { } plan) return;
        if (plan.WorldCards.Count == 0 && plan.Target is CardCollectionTarget.PublicZone empty) {
            DrawEmptyPublicCollection(empty);
            return;
        }
        // This is the table's presentation space, not another zone browser. The
        // ordinary hand and source fans keep their fixed anchors below it.
        canvas.Fill(new(0, 54, 1600, 724), Color.Black * .57f);
        hits.RemoveAll(hit => hit.Bounds.Intersects(new(0, 54, 1600, 724)));
        var fold = new MatchIntent.FoldCardCollection();
        hits.Add(new("card-spread-table", ScopeFor(fold), new(0, 54, 1600, 724), fold, ShowPointer: false));
        canvas.Gradient(new(0, 54, 1600, plan.Area.Top - 54), new Color(7, 14, 20) * .8f, new Color(7, 14, 20) * .2f);
        string title = spreadTitle;
        if (plan.Target is not (CardCollectionTarget.HandCollection or CardCollectionTarget.PublicZone) && spreadView.Scale >= .85f) {
            var worldCenter = spreadView.Inverse(plan.Area.Center.ToVector2());
            var section = plan.Sections.FirstOrDefault(section => section.Bounds.Contains(worldCenter.ToPoint()))
                ?? plan.Sections.OrderBy(section => Vector2.DistanceSquared(section.Bounds.Center.ToVector2(), worldCenter)).First();
            if (!plan.CameraArea.Contains(spreadView.Apply(section.Heading).ToPoint()))
                title = section.Packet.Title + " · " + section.Packet.Context;
        }
        canvas.Text(title, 94, 72, Gold, .85f, 1120, 80, true);
        if (spreadInstruction.Length > 0) canvas.Text(spreadInstruction, 94, spreadInstructionY, Ink, .62f, 1140, 80);
        DrawSpreadSources(plan);
        foreach (var world in plan.WorldCards) {
            if (dragging && dragSource?.Card is { } held && CardViews.Same(held, world.Card)) continue;
            var pose = spreadView.Apply(world.Pose);
            DrawTableCard(world.Id, world.Card, pose, world.Zone, world.Owner,
                "card:" + world.Card.Key + ":" + world.Card.VisualId, new MatchIntent.UseCard(world.Card),
                (displayed, focused) => PaintSceneCard(world.Card, displayed, world.Zone, world.Owner, focused),
                entrance: world.Entrance, clip: plan.CameraArea,
                directPlacement: spreadCameraUsed && (spreadPanning || spreadKeyboardPanning || spreadView != plan.View)
                    && readingId != world.Id);
        }
        DrawLiftedCards();
        DrawReaderControls();
        if (plan.Id == CardSpreadPlan.HandId && match?.Decision is { } required) {
            if (required.Payload is OpeningHandPayload opening) DrawOpeningHand(match, required, opening);
            else if (required.Payload is OptionsPayload or OrderingPayload && decisions.Presentation?.RequiredSurface == DecisionRequiredSurface.Table) {
                DrawSelectionCompletion(new(1150, 730, 358, 44), new(1050, 737, 84, 30));
            }
        }
        // The control belongs to this open collection; Home and a middle click
        // on a card offer the same overview/focus journey without cycling pages.
        var overview = new Rectangle(1390, 70, 44, 32);
        var close = new Rectangle(1464, 70, 44, 32);
        var accent = IsControlHovered("card-spread-overview") ? Color.White : Muted;
        canvas.Line(new(1401, 77), new(1398, 77), accent, 2); canvas.Line(new(1398, 77), new(1398, 83), accent, 2);
        canvas.Line(new(1424, 77), new(1427, 77), accent, 2); canvas.Line(new(1427, 77), new(1427, 83), accent, 2);
        canvas.Line(new(1401, 95), new(1398, 95), accent, 2); canvas.Line(new(1398, 95), new(1398, 89), accent, 2);
        canvas.Line(new(1424, 95), new(1427, 95), accent, 2); canvas.Line(new(1427, 95), new(1427, 89), accent, 2);
        var reset = new MatchIntent.ResetCardCollectionView();
        hits.Add(new("card-spread-overview", ScopeFor(reset), overview, reset));
        if (IsControlHovered("card-spread-overview")) canvas.Text("Overview · Home", 1285, 106, Ink, .53f, 240, 1);
        accent = IsControlHovered("card-spread-fold") ? Color.White : Muted;
        canvas.Line(new(1479, 79), new(1493, 93), accent, 2); canvas.Line(new(1479, 93), new(1493, 79), accent, 2);
        hits.Add(new("card-spread-fold", ScopeFor(fold), close, fold));
        if (IsControlHovered("card-spread-fold")) canvas.Text("Fold · Escape", 1395, 106, Ink, .53f, 130, 1);
    }

    private void DrawSpreadSources(CardSpreadPlan plan)
    {
        canvas.BeginClip(plan.CameraArea);
        foreach (var section in plan.Sections.Where(section => section.Packet.Title.Length > 0)) {
            var at = spreadView.Apply(section.Heading);
            float scale = spreadView.Scale;
            var source = section.Packet.SourceIds.Select(id => CardViews.FindZoneOccurrence(match!, id)?.Card)
                .FirstOrDefault(card => card is { FaceDown: false });
            var center = at + new Vector2(18, 18) * scale;
            canvas.Circle(center, 19 * scale, Gold * .85f);
            canvas.Circle(center, 17 * scale, new(16, 27, 33));
            if (source != null && resources.Card(source) is { } art) canvas.Portrait(art, center, 17 * scale, Color.White);
            int x = (int)(at.X + 52 * scale), width = Math.Max(1, (int)((section.Bounds.Width - 96) * scale));
            int titleHeight = canvas.TextSize(section.Packet.Title, .68f * scale, width, 80, true).Y;
            canvas.Text(section.Packet.Title, x, (int)at.Y, Ink, .68f * scale, width, 80, true);
            canvas.Text(section.Packet.Context, x, (int)at.Y + titleHeight + (int)(8 * scale), Gold, .53f * scale, width, 80);
        }
        canvas.EndClip();
    }

    private void FocusCardCollection(string cardId)
    {
        if (tableScene?.Spread is not { } plan || readingId != null || dragging || pressed != null) return;
        spreadTarget = plan.Focus(cardId); spreadCameraUsed = true;
    }

    private void ResetCardCollectionView()
    {
        if (tableScene?.Spread is not { } plan || dragging) return;
        CollapseCardReading(); spreadTarget = plan.Overview; spreadCameraUsed = true;
    }

    private bool ZoomCardCollection(int direction)
    {
        if (!CardCollectionOpen || tableScene?.Spread is not { } plan || !plan.CameraArea.Contains(pointer)) return false;
        if (plan.WorldCards.Count == 0) return true;
        if (dragging || pressed != null || readingId != null) return true;
        spreadTarget = plan.Zoom(pointer.ToVector2(), direction > 0 ? 1.24f : 1 / 1.24f); spreadCameraUsed = true;
        return true;
    }

    private void UpdateCardCollectionPointer(MouseState mouse, KeyboardState keys, TimeSpan elapsed, bool keyboardAvailable)
    {
        if (!CardCollectionOpen && !OverlayOpen && !textFocus && !chatFocus && !dragging && pressed == null) {
            bool middle = mouse.MiddleButton == ButtonState.Pressed && previousMouse.MiddleButton == ButtonState.Released;
            bool handKey = keyboardAvailable && keys.IsKeyDown(Keys.H) && previousKeys.IsKeyUp(Keys.H);
            if (handKey && HandCollectionAllowed) { ExpandCardCollection(CardCollectionTarget.Hand); return; }
            if (middle && HitAt(pointer)?.Card is { } card) {
                if (match?.Viewer?.Zone("Hand").Cards.Any(current => CardViews.Same(current, card)) == true && HandCollectionAllowed)
                    ExpandCardCollection(CardCollectionTarget.Hand);
                else if (tableScene?.PlayAccess.Groups.FirstOrDefault(group => group.Cards.Any(item => CardViews.Same(item.Scene.Card, card))) is { } group)
                    ExpandCardCollection(new CardCollectionTarget.AccessPacket(group.Id));
                return;
            }
        }
        if (!CardCollectionOpen || tableScene?.Spread is not { WorldCards.Count: > 0 } plan) return;
        if (textFocus || chatFocus) { spreadPanning = false; return; }
        if (keyboardAvailable && keys.IsKeyDown(Keys.Home) && previousKeys.IsKeyUp(Keys.Home)) { ResetCardCollectionView(); return; }
        if (keyboardAvailable && keys.IsKeyDown(Keys.Z) && previousKeys.IsKeyUp(Keys.Z)
            && HitAt(pointer)?.Card is { } pointed && plan.Contains(CardViews.Identity(pointed))) FocusCardCollection(CardViews.Identity(pointed));
        if (dragging || pressed != null || readingId != null || mouse.LeftButton == ButtonState.Pressed) { spreadPanning = false; return; }
        if (keyboardAvailable && !spreadPanning
            && mouse.LeftButton == ButtonState.Released && mouse.MiddleButton == ButtonState.Released) {
            var delta = CameraKeyboardPan.Offset(keys, elapsed);
            if (delta != Vector2.Zero) {
                spreadTarget = plan.Pan(delta, spreadView); spreadCameraUsed = true; spreadKeyboardPanning = true;
                session.CancelAdvance();
            }
        }
        if (mouse.MiddleButton == ButtonState.Pressed && previousMouse.MiddleButton == ButtonState.Released
            && plan.CameraArea.Contains(pointer) && readingId == null && !dragging && pressed == null) {
            spreadPanning = true; spreadPanStart = pointer; spreadPanView = spreadView;
        }
        if (spreadPanning && mouse.MiddleButton == ButtonState.Pressed) {
            var delta = pointer.ToVector2() - spreadPanStart.ToVector2();
            if (delta.LengthSquared() > 16) {
                spreadTarget = plan.Pan(delta, spreadPanView);
                spreadCameraUsed = true;
            }
        }
        if (spreadPanning && mouse.MiddleButton == ButtonState.Released) {
            if (Vector2.DistanceSquared(pointer.ToVector2(), spreadPanStart.ToVector2()) <= 16
                && HitAt(pointer)?.Card is { } card && plan.Contains(CardViews.Identity(card))) FocusCardCollection(CardViews.Identity(card));
            spreadPanning = false;
        }
    }
}
