using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private TurnProgressView? turnProgress;

    private void DrawOpponentPhase(Player player, Vector2 hero, int seat)
    {
        if (player.Id != match!.ActivePlayerId || match.Turn == 0 || turnProgress == null || seat == 1) return;
        // Outward from the side portrait: this pocket contains neither its
        // inward-facing mana nor the command-zone card below its name.
        var center = hero + new Vector2(seat == 2 ? 59 : -59, 0);
        var fill = new Color(17, 26, 30);
        canvas.Circle(center, 15, Gold * .8f);
        canvas.Circle(center, 13, fill);
        if (turnProgress.PhaseKey.Length > 0) PaintPhaseGlyph(turnProgress.PhaseKey, center, Gold, fill);
        else canvas.Ring(center, 5, Gold, 1.5f);
        if (Vector2.DistanceSquared(pointer.ToVector2(), center) <= 16 * 16)
            DrawTurnHint(player.Name + " · " + turnProgress.PhaseLabel, center);
    }

    private void DrawPortraitTurnControls()
    {
        var state = match!;
        turnProgress ??= TurnProgressPresentation.From(state, preferences.Stops);
        if (state.Viewer == null || state.Turn == 0) return;
        var layout = TurnControlLayout.For(SeatHero(state.ViewerId));
        var markers = turnProgress.Markers;
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
            hits.Add(new(id, ControlScope(id), bounds, () => {
                if (!preferences.Stops.Remove(key)) preferences.Stops.Add(key);
                SavePreferences();
            }, Shape: point => Vector2.DistanceSquared(point.ToVector2(), center) <= 18 * 18));
        }
        if (!SelectingActionTarget) {
            TurnControlIcon("auto", layout.AutoCenter, automatic, () => UseAutomaticControl(!automatic));
            TurnControlIcon("hold", layout.HoldCenter, held, ToggleHoldControl, pause: true);
        }
        for (int i = 0; i < markers.Length; i++)
            if (IsControlHovered("phase-stop:" + markers[i].Key)) DrawTurnHint(markers[i].Hint, layout.Markers[i]);
        if (IsControlHovered("auto")) {
            string detail = automatic ? "Use full control (F)" : "Use automatic passing (F)";
            if (decisionDock?.PauseReason is DecisionDockPauseReason.Inspection or DecisionDockPauseReason.Presentation)
                detail = decisionDock.Caption + " · " + detail;
            DrawTurnHint(detail, layout.AutoCenter);
        }
        if (IsControlHovered("hold")) DrawTurnHint(held ? "Stop holding priority (Ctrl)" : "Hold priority this turn (Ctrl)", layout.HoldCenter);
    }

    private void DrawPortraitPriority(DecisionDockView view)
    {
        var state = match!; var d = state.Decision;
        var layout = TurnControlLayout.For(SeatHero(state.ViewerId));
        var area = layout.PriorityArea;
        // Resource values keep their measured width. The quiet portrait action
        // can grow into the former decision area without covering mana.
        int resourceRight = manaPoolBadges.GetValueOrDefault(state.ViewerId)?.Bounds.Right ?? 0;
        area.X = Math.Clamp(Math.Max(area.X, resourceRight + 14), area.X, 1550 - area.Width);
        decisionPanelBounds = area; decisionTextBounds = Rectangle.Empty; decisionText = ""; decisionTextOverflow = false;
        bool stopped = view.PauseReason == DecisionDockPauseReason.PhaseStop;
        var accent = stopped || held ? Gold : view.Status == DecisionDockStatus.AutoPassing || QuietPriorityArmed ? Blue : PriorityAccent;
        var statusPoint = new Vector2(area.X + 8, area.Y + 10);
        DrawDecisionRole(view, statusPoint, accent);
        string heading = QuietPriorityArmed ? view.PauseReason is DecisionDockPauseReason.None ? "Passing for now" : "Passing paused"
            : stopped ? view.Caption : view.Headline;
        canvas.Text(heading, area.X + 28, area.Y - 2, accent, .62f, stopped ? area.Width - 62 : area.Width - 28, 2, true);
        if (Vector2.DistanceSquared(pointer.ToVector2(), statusPoint) <= 16 * 16)
            DrawTurnHint(view.Headline + (view.Caption.Length > 0 ? " · " + view.Caption : ""), statusPoint);
        if (stopped) {
            string id = "resume-phase-stop", key = state.PhaseKey;
            var bounds = new Rectangle(area.Right - 28, area.Y - 4, 28, 27);
            canvas.CenterText("×", bounds, IsControlHovered(id) ? Ink : Gold, .85f, true);
            hits.Add(new(id, ControlScope(id), bounds, () => { preferences.Stops.Remove(key); SavePreferences(); }));
            if (IsControlHovered(id)) DrawTurnHint("Remove my " + turnProgress!.PhaseLabel.ToLowerInvariant() + " stop", bounds.Center.ToVector2());
        }
        bool following = busy || d == null || view.Status == DecisionDockStatus.AutoPassing;
        var action = new Rectangle(area.X, area.Bottom - 40, area.Width, 40);
        if (QuietPriorityArmed || !following && CanOfferQuietPriority) {
            Button(QuietPriorityArmed ? "quiet-priority-stop" : "quiet-priority-arm", QuietPriorityArmed ? "Take control" : "Pass for now",
                action.X, action.Y, action.Width - 48, QuietPriorityArmed ? StopQuietPriority : ArmQuietPriority, !busy || QuietPriorityArmed, true, action.Height);
            if (!busy && d is { Intent: DecisionIntent.Priority, OkEnabled: true }) {
                var center = new Vector2(action.Right - 19, action.Center.Y);
                TurnControlIcon("confirm", center, false, () => Send(new PassPriority(state.Id, d.Id)));
                if (IsControlHovered("confirm")) DrawTurnHint("Pass priority once", center);
            }
        } else if (!following && d is { OkEnabled: true }) {
            Button("confirm", view.ActionLabel, action.X, action.Y, action.Width,
                () => Send(DecisionCommands.Confirm(state)), CombatGuide.CanConfirm(state), true, action.Height);
        } else if (view.Status == DecisionDockStatus.AutoPassing) {
            PaintTurnAdvance(new(action.X + 14, action.Center.Y), Blue);
            canvas.Text("Passing", action.X + 38, action.Y + 8, Muted, .66f, action.Width - 45, 1);
        }
        if (!following && d?.CancelEnabled == true)
            Button("cancel", d.Cancel == "End Turn" ? "Pass turn" : d.Cancel,
                area.Right + 12, action.Y, Math.Min(130, 1580 - area.Right - 12),
                () => Send(DecisionCommands.Cancel(state)), height: action.Height);
    }

    private void TurnControlIcon(string id, Vector2 center, bool selected, Action action, bool pause = false)
    {
        bool hover = IsControlHovered(id), down = hover && pressed?.Id == id && CurrentHit(pressed);
        Color color = hover ? Ink : selected ? id == "hold" ? Gold : Blue : Muted;
        canvas.Circle(center + new Vector2(0, 2), 17, Color.Black * .8f);
        canvas.Circle(center, 16, hover ? Blue : selected ? color : new Color(65, 78, 82));
        canvas.Circle(center, 14, down ? new(35, 60, 69) : new(13, 24, 30));
        if (pause) PaintTurnPause(center, color, 6); else PaintTurnAdvance(center, color);
        var bounds = new Rectangle((int)center.X - 18, (int)center.Y - 18, 36, 36);
        hits.Add(new(id, ControlScope(id), bounds, action,
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
