using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private ControlCue? guidanceCue;
    private Rectangle? guidanceBounds;
    private sealed record GuidanceAttempt(string Key, string GameId, string DecisionId, long Revision);
    private GuidanceAttempt? guidanceAttempt;

    private void LearnControl(string key)
    {
        if (experience.LearnedControls.Contains(key)) return;
        experience = experience with { LearnedControls = [.. experience.LearnedControls, key] };
        SaveExperience();
    }

    private void ResetControlHints()
    {
        experience = experience with { LearnedControls = [] };
        SaveExperience();
    }

    private string? GuidanceForReply(DecisionReply reply)
    {
        if (match == null) return null;
        if (reply.Action == ReplyAction.AssignAttack) return "attack";
        if (reply.Action == ReplyAction.AssignBlock) return "block";
        if (reply.Action == ReplyAction.SelectPlayer || reply.Action == ReplyAction.Choose && TableChoices.IsDirect(match)) return "choose-target";
        if (reply.Action != ReplyAction.SelectCard) return null;
        var located = CardViews.Located(match).FirstOrDefault(c => c.Card.Key == reply.Card);
        var cue = located.Card == null ? null : ControlGuidance.Card(match, located.Card, located.Zone);
        return cue?.Key is "hand-play" or "commander-play" or "activate-ability" or "choose-target" ? cue.Key : null;
    }

    private void FinishGuidanceAttempt()
    {
        var attempt = guidanceAttempt; guidanceAttempt = null;
        if (attempt != null && error.Length == 0 && match is { } state && state.Id == attempt.GameId
            && (state.Revision > attempt.Revision || state.Decision?.Id != attempt.DecisionId)) LearnControl(attempt.Key);
    }

    private void UseAutomaticControl(bool value) { SetAutomatic(value); LearnControl("priority-controls"); }
    private void ToggleHoldControl() { held = !held; priority.Reset(); LearnControl("priority-controls"); }
    private bool Unlearned(ControlCue? cue) => cue != null && !experience.LearnedControls.Contains(cue.Key);

    private void DrawContextGuidance()
    {
        guidanceCue = null; guidanceBounds = null;
        if (match is not { Status: "playing" } state || busy || OverlayOpen || dragging || chatOpen) return;
        var hit = HitAt(pointer);
        ControlCue? cue;
        if (hit?.Id is "auto" or "hold") {
            cue = new("priority-controls", held ? "Priority held this turn" : automatic ? "Auto is following the table" : "You have full control",
                held ? "Click Hold again to resume. It resets next turn." : automatic ? "Auto passes empty priority. Hold keeps this turn open." : "You choose each pass. Auto skips empty priority.");
            if (Unlearned(cue)) PaintGuidance(cue, new(1266, 724, 296, 66), true);
            return;
        }
        if (state.Decision?.Intent is DecisionIntent.Attack or DecisionIntent.Block or DecisionIntent.Selection || TableChoices.IsDirect(state)) {
            cue = ControlGuidance.Decision(state, selectedCombat);
            if (Unlearned(cue)) {
                var target = selectedCombat != null
                    ? hits.FirstOrDefault(h => h.Card is { } card && CombatTarget(card) || h.PlayerId is { } player && IsPlayerTarget(player))
                    : hits.FirstOrDefault(h => h.Card is { } card && IsCardActionable(card) || h.PlayerId is { } player && IsPlayerTarget(player));
                if (target != null && GuidancePlacement(target.Bounds, target.Zone) is { } targetBeside) PaintAnchoredGuidance(cue!, target, targetBeside);
                else PaintGuidanceHeader(cue!);
            }
            return;
        }
        Hit? anchor = hit?.Card == null ? null : hit;
        if (anchor?.Card is { } focused) {
            bool staged = pendingPlay is { } pending && pending.Id == anchor.Id && pending.Scope == Scope && now - pending.Time <= .5;
            cue = ControlGuidance.Card(state, focused, anchor.Zone ?? "", staged);
        } else {
            if (TurnGuide.CanAutoPass(state, automatic, held, preferences.Stops, false)) return;
            cue = null;
            // Point to an actual, visible play rather than teaching an abstract control.
            foreach (string zone in new[] { "Hand", "Command", "Battlefield" }) {
                foreach (var candidate in hits.Where(h => h.Zone == zone && h.Card != null)) {
                    var suggestion = ControlGuidance.Card(state, candidate.Card!, zone);
                    if (suggestion?.Key is "hand-play" or "commander-play" or "activate-ability" && Unlearned(suggestion)) {
                        cue = suggestion; anchor = candidate; break;
                    }
                }
                if (cue != null) break;
            }
        }
        if (!Unlearned(cue)) return;
        var placement = anchor == null ? null : GuidancePlacement(anchor.Bounds, anchor.Zone);
        if (placement is { } beside) {
            PaintAnchoredGuidance(cue!, anchor!, beside);
        } else PaintGuidance(cue!, new(1266, 724, 296, 66), true);
    }

    private void PaintAnchoredGuidance(ControlCue cue, Hit anchor, Rectangle bounds)
    {
        var from = Vector2.Clamp(bounds.Center.ToVector2(), new(anchor.Bounds.Left, anchor.Bounds.Top), new(anchor.Bounds.Right, anchor.Bounds.Bottom));
        var to = Vector2.Clamp(from, new(bounds.Left + 10, bounds.Top + 5), new(bounds.Right - 10, bounds.Bottom - 5));
        canvas.Line(from, to, ActionAccent * .55f, 1);
        PaintGuidance(cue, bounds);
    }

    private Rectangle? GuidancePlacement(Rectangle anchor, string? zone)
    {
        const int width = 370, height = 66;
        int x = Math.Clamp(anchor.Center.X - width / 2, 14, 1218);
        var candidates = new[] {
            new Rectangle(x, anchor.Bottom + 9, width, height), new Rectangle(x, anchor.Top - height - 9, width, height),
            new Rectangle(anchor.Right + 12, Math.Clamp(anchor.Center.Y - height / 2, 65, 824), width, height),
            new Rectangle(anchor.Left - width - 12, Math.Clamp(anchor.Center.Y - height / 2, 65, 824), width, height),
            zone == "Hand" ? new Rectangle(340, 706, width, height) : Rectangle.Empty
        };
        foreach (var bounds in candidates) {
            if (bounds.IsEmpty || bounds.Left < 14 || bounds.Top < 64 || bounds.Right > 1586 || bounds.Bottom > 850) continue;
            if (bounds.Intersects(TableLayout.LocalSeatBounds)) continue;
            if (hits.Any(h => h.Bounds.Intersects(bounds))) continue;
            return bounds;
        }
        return null;
    }

    private void PaintGuidance(ControlCue cue, Rectangle bounds, bool dock = false)
    {
        if (dock && match?.Decision is { Intent: not DecisionIntent.Priority }) {
            PaintGuidanceHeader(cue);
            return;
        }
        guidanceCue = cue; guidanceBounds = bounds;
        canvas.Panel(bounds, new(14, 28, 38), ActionAccent * .7f, 5);
        canvas.Text(cue.Title, bounds.X + 10, bounds.Y + 7, ActionAccent, dock ? .62f : .72f, bounds.Width - 20, 1, true);
        canvas.Text(cue.Text, bounds.X + 10, bounds.Y + 28, Ink, dock ? .62f : .68f, bounds.Width - 20, 2);
        // Guidance paints no hit region: the real card, target and controls retain input.
    }

    private void PaintGuidanceHeader(ControlCue cue)
    {
        // Every dock fallback leaves the engine's reason for a required choice
        // visible, including targets, mana costs and combat restrictions.
        guidanceCue = cue; guidanceBounds = new(1266, 723, 296, 17);
        canvas.Fill(guidanceBounds.Value, new Color(14, 28, 38));
        canvas.Text(cue.Key == "choose-target" ? cue.Text : cue.Title, 1266, 725, ActionAccent, .56f, 296, 1, true);
    }
}
