using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private TurnProgressView? turnProgress;
    private TurnControlLayout LocalTurnControls => match is { } state
        ? tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == state.ViewerId)?.Geometry.TurnControls
            ?? TurnControlLayout.For(SeatHero(state.ViewerId))
        : TurnControlLayout.For(TableLayout.Hero(3));
    private TableScene? studyHudScene;
    private double studyHudTime = double.NaN;
    private IReadOnlyList<Rectangle> studyHudObstacles = [];
    private IReadOnlyList<Rectangle> studyAttentionObstacles = [];
    private IReadOnlyList<Rectangle> studyFixedHudObstacles = [];
    private sealed record PortraitPriorityPlan(Rectangle Area, int HeadingHeight, int UndoHeight, string Source, string Progress);
    private PortraitPriorityPlan? portraitPriorityPlan;

    private IReadOnlyList<Rectangle> StudyAttentionObstacles {
        get { _ = StudyHudObstacles; return studyAttentionObstacles; }
    }

    // One copied plan is shared by context placement, card readers and timing
    // controls for the completed scene. Drawing order cannot change its inputs.
    private IReadOnlyList<Rectangle> StudyHudObstacles {
        get {
            if (ReferenceEquals(studyHudScene, tableScene) && studyHudTime == now) return studyHudObstacles;
            studyHudScene = tableScene; studyHudTime = now;
            if (match is not { } state || tableScene == null) return studyAttentionObstacles = studyHudObstacles = [];
            var occupied = new List<Rectangle>();
            void AddCard(Pose pose) {
                var body = new CardPaintGeometry(pose);
                occupied.Add(body.Backing.Bounds); occupied.Add(body.Shadow.Bounds);
            }
            foreach (var card in tableScene.BattlefieldDrawOrder) AddCard(boardView.Apply(card.Pose));
            int backgroundCount = occupied.Count;
            foreach (var card in tableScene.Hand.Cards) AddCard(card.Pose);
            foreach (var card in tableScene.PlayAccess.Groups.SelectMany(group => group.Cards)) AddCard(card.Scene.Pose);
            foreach (var seat in tableScene.Seats) {
                var player = state.Players.Single(player => player.Id == seat.PlayerId);
                bool own = seat.PlayerId == state.ViewerId;
                var hero = seat.Geometry.Hero;
                int radius = own ? 47 : 41;
                occupied.Add(new((int)hero.X - radius, (int)hero.Y - radius, radius * 2, radius * 2 + 6));
                var mana = PlanManaHalo(player, seat.Index);
                occupied.AddRange(mana.Parts.Select(part => part.Bounds));
                if (!own) {
                    if (!seatReadoutPlans.ContainsKey(seat.PlayerId))
                        occupied.Add(SeatNameplateArea(hero, false, seat.Index, mana));
                    foreach (var hand in OpponentHandPoses(player, seat.Index)) {
                        occupied.Add(hand.Bounds);
                        occupied.Add((hand with { Center = hand.Center + new Vector2(3, 6) }).Bounds);
                    }
                    bool far = state.Players.Length > 4 ? seat.Geometry.Frame.IsFar : seat.Index == 1;
                    if (!far && StateClarity.PlayerStates(player).Length == 0)
                        occupied.Add(new((int)seat.Geometry.Hand.X - 65, (int)seat.Geometry.Hand.Y + 35, 130, 22));
                    if (OpponentPhaseCenter(player, hero, seat.Index) is { } phase) {
                        occupied.Add(new((int)phase.X - 15, (int)phase.Y - 15, 30, 30));
                    }
                    if (seatReadoutPlans.TryGetValue(player.Id, out var readout)) occupied.AddRange(readout.Pieces);
                } else {
                    occupied.AddRange(seat.Geometry.TurnControls.PhaseBounds);
                    occupied.Add(seat.Geometry.TurnControls.AutoBounds);
                    occupied.Add(seat.Geometry.TurnControls.HoldBounds);
                    occupied.Add(seat.Geometry.TurnControls.AlertBounds);
                    if (StateClarity.PlayerStates(player).Length > 0 && seat.Geometry.PublicCounterArea is { } counters) occupied.Add(counters);
                    if (seat.Geometry.LandReadoutArea is { } landArea) occupied.Add(landArea);
                }
                foreach (var commander in seat.Geometry.Commanders) {
                    var pose = commander.Pose with { Size = commander.Pose.Size + new Vector2(13) };
                    AddCard(commander.TableAnchored ? boardView.Apply(pose) : pose);
                }
                occupied.Add(DisplayCommandLabel(seat.Geometry));
                if (AvailableZoneCount(player, "Command") > 0)
                    occupied.Add(DisplayCommandActions(seat.Geometry));
                foreach (var pile in seat.Geometry.Piles) {
                    var paint = DisplayPileGeometry(pile, player.Zone(pile.Zone).Count);
                    occupied.Add(paint.Shadow.Bounds); occupied.Add(paint.Top.Bounds); occupied.AddRange(paint.Layers.Select(layer => layer.Bounds));
                    occupied.Add(paint.Label);
                    if (!paint.Count.IsEmpty) occupied.Add(paint.Count);
                }
            }
            foreach (var card in matchPresentation.Cards.Cards.Where(card => card.Focused
                || card.Zone != "Stack" && (card.Layer is CardPresentationLayer.Returning or CardPresentationLayer.Playing))) AddCard(card.Display);
            if (matchPresentation.Cards.Pinned?.ReaderTarget is { } reader) AddCard(reader);
            if (!sourceReaderActionsBounds.IsEmpty) occupied.Add(sourceReaderActionsBounds);
            if (!battlefieldReaderStateBounds.IsEmpty) occupied.Add(battlefieldReaderStateBounds);
            if (manaJourneyPaint is { } bill) { occupied.Add(bill.Bounds); occupied.Add(bill.SourceBounds); }
            occupied.AddRange(manaSourcePaint.SelectMany(source => new[] { source.Bounds, source.OutputBounds }));
            // The attention area owns these unpinned stack destinations. Its
            // moving spell cannot also repel that destination every frame.
            // Other HUD consumers still avoid the complete physical journey.
            studyAttentionObstacles = Array.AsReadOnly(occupied.Where(bounds => !bounds.IsEmpty).ToArray());
            studyFixedHudObstacles = Array.AsReadOnly(occupied.Skip(backgroundCount).Where(bounds => !bounds.IsEmpty).ToArray());
            foreach (var card in matchPresentation.Cards.Cards.Where(card => !card.Focused && card.Zone == "Stack"
                && (card.Layer is CardPresentationLayer.Returning or CardPresentationLayer.Playing))) AddCard(card.Display);
            return studyHudObstacles = Array.AsReadOnly(occupied.Where(bounds => !bounds.IsEmpty).ToArray());
        }
    }

    private bool PortraitTurnControlsCanPaint => DecisionDockCanPaint && !session.NeedsRecovery
        && match is { Status: not ("finished" or "error"), Viewer: { Eliminated: false }, Turn: > 0 }
        && turnProgress != null;

    private bool PortraitPriorityCanPaint => PortraitTurnControlsCanPaint && (turnPassRequest != null || finiteResolutionIntent != null
        || !SelectingActionTarget && (QuietPriorityArmed || ResolvingCapturedStack)
        || !SelectingActionTarget && match?.Decision?.Payload is null or PriorityPayload
            && (decisions.Presentation?.Surface == DecisionDockSurface.Portrait
                || automatic && !held && decisions.Presentation?.Surface == DecisionDockSurface.None));

    private IReadOnlyList<Rectangle> ReaderControlReservations {
        get {
            if (!PortraitTurnControlsCanPaint) return [];
            var layout = LocalTurnControls;
            var phases = layout.PhaseBounds.Take(turnProgress!.Markers.Length).ToArray();
            return SelectingActionTarget ? phases
                : [.. phases, layout.AutoBounds, layout.HoldBounds, layout.AlertBounds, AbilityPassingControlBounds(),
                    HudPaint(portraitPriorityPlan?.Area ?? Rectangle.Empty), HudPaint(combatBarArea)];
        }
    }

    private Vector2? OpponentPhaseCenter(Player player, Vector2 hero, int seat)
    {
        if (player.Id != match!.ActivePlayerId || match.Turn == 0 || turnProgress == null) return null;
        bool extended = match.Players.Length > 4;
        if (extended ? SeatFrameFor(player.Id).IsFar : seat == 1) return null;
        return hero + (extended ? new Vector2(0, -50) : new Vector2(seat == 2 ? 59 : -59, 0));
    }

    private void DrawOpponentPhase(Player player, Vector2 hero, int seat)
    {
        if (OpponentPhaseCenter(player, hero, seat) is not { } center) return;
        // The marker occupies the pocket between mat and portrait in a larger
        // table, or the approved outward side pocket in the legacy layout.
        var progress = turnProgress!;
        var fill = new Color(17, 26, 30);
        canvas.Circle(center, 15, Gold * .8f);
        canvas.Circle(center, 13, fill);
        if (progress.PhaseKey.Length > 0) PaintPhaseGlyph(progress.PhaseKey, center, Gold, fill);
        else canvas.Ring(center, 5, Gold, 1.5f);
        if (Vector2.DistanceSquared(pointer.ToVector2(), center) <= 16 * 16)
            DrawTurnHint(player.Name + " · " + progress.PhaseLabel, center);
    }

    private void DrawPortraitTurnControls()
    {
        if (!PortraitTurnControlsCanPaint) return;
        var state = match!;
        var layout = LocalTurnControls;
        var markers = turnProgress!.Markers;
        for (int i = 1; i < markers.Length; i++)
            canvas.Line(layout.Markers[i - 1], layout.Markers[i], Gold * .22f, 2);
        for (int i = 0; i < markers.Length; i++) {
            var phase = markers[i]; var center = layout.Markers[i];
            string id = "phase-stop:" + phase.Key;
            var color = phase.Current ? Gold : phase.Stop ? Blue : Muted * .8f;
            bool hover = IsControlHovered(id);
            if (phase.Current || hover) canvas.Glow(center, new(49), (hover ? Blue : Gold) * .24f);
            canvas.Circle(center + new Vector2(0, 2), 17, Color.Black * .7f);
            canvas.Circle(center, 16, hover ? Blue : phase.Current ? Gold : new Color(75, 81, 78));
            var fill = phase.Current ? new Color(62, 53, 32) : new Color(15, 25, 30);
            canvas.Circle(center, 14, fill);
            PaintPhaseGlyph(phase.Key, center, color, fill);
            if (phase.Stop) {
                var pause = center + new Vector2(10, 11);
                canvas.Circle(pause, 8, new(12, 30, 38));
                PaintTurnPause(pause, Blue, 4);
            }
            var bounds = new Rectangle((int)center.X - 18, (int)center.Y - 18, 36, 36);
            string key = phase.Key;
            hits.Add(new(id, ScopeFor(new MatchIntent.TogglePhaseStop(key)), bounds, new MatchIntent.TogglePhaseStop(key), Shape: point => Vector2.DistanceSquared(point.ToVector2(), center) <= 18 * 18));
        }
        if (!SelectingActionTarget) {
            TurnControlIcon("auto", layout.AutoCenter, automatic, new MatchIntent.SetAutomatic(!automatic));
            TurnControlIcon("hold", layout.HoldCenter, held, new MatchIntent.ToggleHold(), pause: true);
            DrawPriorityAlertControl(layout);
        }
        for (int i = 0; i < markers.Length; i++)
            if (IsControlHovered("phase-stop:" + markers[i].Key)) DrawTurnHint(markers[i].Hint, layout.Markers[i]);
        if (IsControlHovered("auto")) {
            string detail = automatic ? "Auto: skip quiet windows, stop for actions · Full control (F)" : "Enable Auto: wait for actions (F)";
            if (decisionDock?.PauseReason is DecisionDockPauseReason.Inspection or DecisionDockPauseReason.Presentation)
                detail = decisionDock.Caption + " · " + detail;
            DrawTurnHint(detail, layout.AutoCenter);
        }
        if (IsControlHovered("hold")) DrawTurnHint(held ? "Stop holding priority (Ctrl)" : "Hold priority this turn (Ctrl)", layout.HoldCenter);
    }

    private int PriorityWaitWidth => Math.Max(150, Math.Max(canvas.TextWidth("Wait for action", .76f),
        canvas.TextWidth("Take control", .76f, true)) + 24);

    private Rectangle PortraitPriorityArea()
    {
        var layout = LocalTurnControls;
        var area = layout.PriorityArea;
        bool extended = match!.Players.Length > 4;
        if (extended) area.Width = 340;
        int passWidth = new[] { "Continue damage", "Go to end step", "Go to combat", "Pass once" }
            .Max(label => canvas.TextWidth(label, .76f, true)) + 24;
        area.Width = Math.Max(area.Width, passWidth + PriorityWaitWidth + 8);
        if (turnPassRequest != null) {
            int labelWidth = Math.Max(canvas.TextWidth("Keep playing", .76f, true), canvas.TextWidth("Pass anyway", .76f));
            area.Width = Math.Max(area.Width, (labelWidth + 24) * 2 + 8);
        }
        if (extended) { area.X = 1592 - area.Width; area.Y = 810; }
        return area;
    }

    private PortraitPriorityPlan MeasurePortraitPriority()
    {
        var area = PortraitPriorityArea();
        var state = match!;
        string source = finiteResolutionIntent?.SourceName ?? "";
        if (finiteResolutionIntent is { DeclinesOptionalPayments: false } resolving)
            source = state.Executions?.Occurrences.FirstOrDefault(item => resolving.CapturedExecutionIds.Contains(item.Id)
                    && item.Step is ExecutionStep.Choice or ExecutionStep.Cost or ExecutionStep.Resolution)?.SourceName
                ?? state.Stack.FirstOrDefault(item => resolving.CapturedExecutionIds.Contains(item.ExecutionId))?.Name ?? source;
        string progress = finiteResolutionIntent != null && finiteProgressProjection is { } finite
            ? (finite.DeclineProgress is { } declined ? $"Source: {declined.CompletedCount} / {declined.CapturedCount} completed\nQueue: " : "")
                + $"{finite.Progress.CompletedCount} / {finite.Progress.CapturedCount} completed · {finite.Progress.RemainingCount} remaining" : "";
        int heading = Math.Max(22, canvas.TextSize("Your response · Auto paused", .62f, area.Width - 28, 1, true).Y);
        int details = progress.Length == 0 ? 0 : (source.Length == 0 ? 0 : canvas.TextSize(source, .62f, area.Width - 28, 80, true).Y + 5)
            + canvas.TextSize(progress, .6f, area.Width - 28, 80).Y + 8;
        int undo = UndoControlHeight(area.Width);
        bool following = busy || state.Decision == null || decisions.Presentation?.Status == DecisionDockStatus.AutoPassing;
        int actions = !following && !QuietPriorityArmed && !ResolvingCapturedStack && !CanOfferQuietPriority && CanOfferStackResolution ? 88 : 40;
        int height = Math.Max(area.Height, heading + 8 + details + (undo > 0 ? undo + 8 : 0) + actions);
        area.Y -= height - area.Height; area.Height = height;
        return new(area, heading, undo, source, progress);
    }

    private Rectangle PlacePortraitPriorityArea(Rectangle area)
    {
        // Both automatic waiting and its explicit continuation use the same
        // portrait action position, including enlarged type and resource pips.
        var mana = match is { Viewer: { } viewer } ? PlanManaHalo(viewer, 3).Bounds : Rectangle.Empty;
        int resourceRight = mana.IsEmpty ? 0 : mana.Right;
        bool extended = match!.Players.Length > 4;
        var access = tableScene?.PlayAccess;
        if (TableStudyEnabled) {
            if (!extended) area = access?.PreferredPriorityArea(area) ?? area;
            area.X = Math.Min(Math.Max(area.X, resourceRight + 14), 1592 - area.Width);
            return PlaceStudyHud(area, [], new(8, 58, 1584, 834));
        }
        int right = extended ? 1592 : 1550;
        area.X = Math.Clamp(Math.Max(area.X, resourceRight + 14), Math.Min(area.X, right - area.Width), right - area.Width);
        var readers = matchPresentation.Cards.Cards.Where(card => card.Focused
                || card.Layer is Presentation.CardPresentationLayer.Returning or Presentation.CardPresentationLayer.Playing)
            .Select(card => card.Display.Bounds).Append(sourceReaderActionsBounds);
        // The legacy table keeps its accepted adjacent-fan position. Larger
        // tables have already placed these controls against every owner's
        // furniture and the access fan; only reader occlusion remains here.
        if (!extended) area = access?.PreferredPriorityArea(area) ?? area;
        return access?.ReservePriority(area, readers) ?? area;
    }

    private void DrawAutomaticPassingControl()
    {
        if (!automatic || held || !PortraitTurnControlsCanPaint) return;
        var area = portraitPriorityPlan?.Area ?? PlacePortraitPriorityArea(PortraitPriorityArea());
        if (area.IsEmpty) return;
        decisionPanelBounds = area;
        string heading = decisionDock?.PauseReason switch {
            DecisionDockPauseReason.Inspection => "Auto paused · Reading",
            DecisionDockPauseReason.Presentation => "Auto · Watching play",
            DecisionDockPauseReason.Chat => "Auto paused · Chat",
            DecisionDockPauseReason.PhaseStop => "Auto paused · Phase stop",
            DecisionDockPauseReason.Interaction or DecisionDockPauseReason.Overlay => "Auto paused",
            _ when match?.Status == "resolving" => "Auto · Watching play",
            _ => "Auto · Waiting for action"
        };
        canvas.Text(heading, area.X + 28, area.Y - 2, Blue, .62f, area.Width - 28, 1, true);
        PaintTurnAdvance(new(area.X + 8, area.Y + 10), Blue);
        var action = new Rectangle(area.Right - PriorityWaitWidth, area.Bottom - 40, PriorityWaitWidth, 40);
        // This local stop remains usable while a submitted pass reconciles.
        // It cannot retract that pass or fabricate a new engine response.
        Button("priority-take-control", "Take control", action, new MatchIntent.TakePriorityControl(), true, true);
        if (IsControlHovered("priority-take-control"))
            DrawTurnHint("Pause Auto for this turn (Ctrl)", action.Center.ToVector2());
    }

    private void DrawPortraitPriority(DecisionDockView view)
    {
        var state = match!; var d = state.Decision;
        bool following = busy || d == null || view.Status == DecisionDockStatus.AutoPassing;
        if (following && automatic && !held && !QuietPriorityArmed && !ResolvingCapturedStack && turnPassRequest == null) {
            DrawAutomaticPassingControl();
            return;
        }
        var plan = portraitPriorityPlan ?? MeasurePortraitPriority();
        var area = plan.Area;
        int waitWidth = PriorityWaitWidth;
        string finiteSource = plan.Source, finiteProgress = plan.Progress;
        int undoHeight = plan.UndoHeight;
        if (portraitPriorityPlan == null) area = PlacePortraitPriorityArea(area);
        decisionPanelBounds = area; decisionTextBounds = Rectangle.Empty; decisionText = ""; decisionTextOverflow = false;
        if (area.IsEmpty) return;
        if (DrawTurnPassConfirmation(area)) return;
        bool stopped = view.PauseReason == DecisionDockPauseReason.PhaseStop;
        var accent = stopped || held ? Gold : QuietPriorityArmed || ResolvingCapturedStack ? Blue : PriorityAccent;
        var statusPoint = new Vector2(area.X + 8, area.Y + 10);
        if (QuietPriorityArmed || ResolvingCapturedStack) PaintTurnAdvance(statusPoint, accent);
        else DrawDecisionRole(view, statusPoint, accent);
        string heading = finiteResolutionIntent is { } batch ? batch.DeclinesOptionalPayments ? "Decline costs \u00b7 resolve queue" : "Resolving captured effects"
            : ResolvingCapturedStack ? "Resolving · " + CapturedStackRemaining + " remaining"
            : QuietPriorityArmed ? automatic ? "Auto · Waiting for action" : "Waiting for action"
            : stopped ? view.Caption : held ? "Your control · This turn"
            : view.Status == DecisionDockStatus.Priority && automatic && priorityAttention.NeedsResponse && state.Stack.Length > 0
                ? "Your response · Auto paused" : view.Headline;
        canvas.Text(heading, area.X + 28, area.Y, accent, .62f, stopped ? area.Width - 62 : area.Width - 28, 1, true);
        if (undoHeight > 0) DrawUndoControl(new(area.X, area.Y + plan.HeadingHeight + 4, area.Width, undoHeight));
        if (finiteProgress.Length > 0) {
            int progressY = area.Y + plan.HeadingHeight + 4 + (undoHeight > 0 ? undoHeight + 8 : 0);
            if (finiteSource.Length > 0) {
                canvas.Text(finiteSource, area.X + 28, progressY, Ink, .62f, area.Width - 28, 80, true);
                progressY += canvas.TextSize(finiteSource, .62f, area.Width - 28, 80, true).Y + 5;
            }
            canvas.Text(finiteProgress, area.X + 28, progressY, Muted, .6f, area.Width - 28, 80);
        }
        if (Vector2.DistanceSquared(pointer.ToVector2(), statusPoint) <= 16 * 16)
            DrawTurnHint(QuietPriorityArmed ? "Waiting this turn · Stops for new actions · Take control to stop now"
                : view.Headline + (view.Caption.Length > 0 ? " · " + view.Caption : ""), statusPoint);
        if (stopped) {
            string id = "resume-phase-stop", key = state.PhaseKey;
            var bounds = new Rectangle(area.Right - 28, area.Y - 4, 28, 27);
            canvas.CenterText("×", bounds, IsControlHovered(id) ? Ink : Gold, .85f, true);
            hits.Add(new(id, ScopeFor(new MatchIntent.TogglePhaseStop(key, true)), bounds, new MatchIntent.TogglePhaseStop(key, true)));
            if (IsControlHovered(id)) DrawTurnHint("Remove my " + turnProgress!.PhaseLabel.ToLowerInvariant() + " stop", bounds.Center.ToVector2());
        }
        var action = new Rectangle(area.X, area.Bottom - 40, area.Width, 40);
        if (ResolvingCapturedStack) {
            Button("stack-resolution-stop", "Take control", action, new MatchIntent.SetStackResolution(false), true, true);
        } else if (QuietPriorityArmed || !following && CanOfferQuietPriority) {
            var wait = new Rectangle(action.Right - waitWidth, action.Y, waitWidth, action.Height);
            Button(QuietPriorityArmed ? "quiet-priority-stop" : "quiet-priority-arm", QuietPriorityArmed ? "Take control" : "Wait for action",
                wait, new MatchIntent.SetQuietPriority(!QuietPriorityArmed), !busy || QuietPriorityArmed, QuietPriorityArmed);
            if (IsControlHovered("quiet-priority-arm")) DrawTurnHint("Wait this turn · Stop for spells, abilities, combat and choices", wait.Center.ToVector2());
            if (!QuietPriorityArmed && !busy && d is { Payload: PriorityPayload { Pass.Enabled: true } }) {
                Button("confirm", view.ActionLabel, new(action.X, action.Y, action.Width - waitWidth - 8, action.Height),
                    new MatchIntent.ConfirmDecision(), CombatGuide.CanConfirm(state), true);
            }
        } else if (!following && d?.Payload is PriorityPayload { Pass.Enabled: true }) {
            if (CanOfferStackResolution) {
                Button("confirm", view.ActionLabel, action.X, action.Y - 48, action.Width,
                    new MatchIntent.ConfirmDecision(), CombatGuide.CanConfirm(state), true, action.Height);
                Button("stack-resolution-arm", "Resolve these", action, new MatchIntent.SetStackResolution(true), true);
            } else Button("confirm", view.ActionLabel, action.X, action.Y, action.Width,
                new MatchIntent.ConfirmDecision(), CombatGuide.CanConfirm(state), true, action.Height);
        }
    }

    private void TurnControlIcon(string id, Vector2 center, bool selected, UiIntent action, bool pause = false)
    {
        bool hover = IsControlHovered(id), down = hover && pressed?.Id == id && CurrentHit(pressed);
        Color color = hover ? Ink : selected ? id == "hold" ? Gold : Blue : Muted;
        canvas.Circle(center + new Vector2(0, 2), 17, Color.Black * .8f);
        canvas.Circle(center, 16, hover ? Blue : selected ? color : new Color(65, 78, 82));
        canvas.Circle(center, 14, down ? new(35, 60, 69) : new(13, 24, 30));
        if (pause) PaintTurnPause(center, color, 6); else PaintTurnAdvance(center, color);
        var bounds = new Rectangle((int)center.X - 18, (int)center.Y - 18, 36, 36);
        hits.Add(new(id, ScopeFor(action), bounds, action,
            Shape: point => Vector2.DistanceSquared(point.ToVector2(), center) <= 18 * 18));
    }

    private void DrawTurnHint(string label, Vector2 origin)
    {
        int width = Math.Min(510, canvas.TextWidth(label, .65f, true) + 24);
        int height = canvas.TextSize(label, .65f, width - 24, 4, true).Y + 16;
        var bounds = new Rectangle(Math.Clamp((int)origin.X - width / 2, 12, 1588 - width), (int)origin.Y - height - 24, width, height);
        canvas.Panel(bounds, new(9, 18, 23), Gold * .5f, 5);
        canvas.Text(label, bounds.X + 12, bounds.Y + 8, Ink, .65f, width - 24, 4, true);
    }

    private void PaintTurnPause(Vector2 center, Color color, int radius)
    {
        canvas.Rounded(new((int)center.X - 5, (int)center.Y - radius, 3, radius * 2), color, 1);
        canvas.Rounded(new((int)center.X + 2, (int)center.Y - radius, 3, radius * 2), color, 1);
    }
    private void PaintTurnAdvance(Vector2 center, Color color)
    {
        canvas.Polygon([center + new Vector2(-5, -7), center + new Vector2(5, 0), center + new Vector2(-5, 7)], color);
        canvas.Line(center + new Vector2(7, -7), center + new Vector2(7, 7), color, 2);
    }
    private void PaintPhaseGlyph(string key, Vector2 c, Color color, Color fill)
    {
        if (key == "UPKEEP") {
            canvas.Ring(c, 4, color, 1.5f);
            for (int i = 0; i < 8; i++) {
                var direction = new Vector2(MathF.Cos(i * MathF.PI / 4), MathF.Sin(i * MathF.PI / 4));
                canvas.Line(c + direction * 6, c + direction * 9, color, 1.5f);
            }
        } else if (key == "DRAW") {
            canvas.Border(new((int)c.X - 4, (int)c.Y - 8, 11, 15), color, 2);
            canvas.Line(c + new Vector2(-7, -5), c + new Vector2(-7, 9), color, 1.5f);
            canvas.Line(c + new Vector2(-7, 9), c + new Vector2(3, 9), color, 1.5f);
        } else if (key == "COMBAT_BEGIN") {
            foreach (int sign in new[] { -1, 1 }) {
                canvas.Line(c + new Vector2(-7 * sign, 8), c + new Vector2(7 * sign, -8), color, 2);
                canvas.Line(c + new Vector2(-8 * sign, 2), c + new Vector2(-1 * sign, 8), color, 2);
            }
        } else if (key == "END_OF_TURN") {
            canvas.Circle(c, 8, color); canvas.Circle(c + new Vector2(4, -3), 7, fill);
        } else {
            var points = new[] { c + new Vector2(0, -9), c + new Vector2(7, 0), c + new Vector2(0, 9), c + new Vector2(-7, 0) };
            for (int i = 0; i < points.Length; i++) canvas.Line(points[i], points[(i + 1) % points.Length], color, 1.5f);
            if (key == "MAIN2") canvas.Line(c + new Vector2(-2, -3), c + new Vector2(-2, 3), color, 1.5f);
            canvas.Line(c + new Vector2(key == "MAIN2" ? 2 : 0, -3), c + new Vector2(key == "MAIN2" ? 2 : 0, 3), color, 1.5f);
        }
    }
}
