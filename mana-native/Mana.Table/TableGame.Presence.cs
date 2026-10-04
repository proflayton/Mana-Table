using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void DrawSeatTransfers()
    {
        if (preferences.ReducedMotion) return;
        foreach (var cue in presence.Cues.Where(c => c.Kind == SeatCueKind.Draw)) {
            int seat = TableLayout.SeatIndex(match!, cue.PlayerId);
            var player = match!.Players.FirstOrDefault(p => p.Id == cue.PlayerId);
            if (seat is < 0 or > 2 || player == null) continue;
            int count = Math.Min(7, player.Zone("Hand").Count);
            for (int i = 0; i < cue.Amount; i++) {
                double age = now - cue.Started - i * TablePresence.DrawStagger;
                if (age < 0 || age >= TablePresence.DrawDuration) continue;
                var destination = TableLayout.HiddenHand(seat, Math.Max(0, count - cue.Amount + i), count);
                var pose = TablePresence.DrawPose(TableWorld.Pile(seat, 0), destination, age);
                canvas.Surface(null, pose with { Center = pose.Center + new Vector2(4, 5 + pose.Elevation), Size = pose.Size + new Vector2(5) }, Color.Black * .26f);
                PaintBack(pose);
                canvas.Line(pose.Point(0, 0), pose.Point(1, 0), Ink * .45f);
            }
        }
    }
    private void DrawSeatImpact(int player, Vector2 center, float radius)
    {
        if (preferences.ReducedMotion) return;
        var cue = presence.Cues.LastOrDefault(c => c.PlayerId == player && c.Kind != SeatCueKind.Draw);
        if (cue == null) return;
        float age = (float)(now - cue.Started);
        if (age < 0 || age > .9f) return;
        float fade = 1 - age / .9f;
        var color = cue.Kind == SeatCueKind.LifeLoss ? Red : Blue;
        canvas.Glow(center, new(radius * 4.5f), color * fade * .55f);
        canvas.Ring(center, radius + 7 + age * 28, color * fade, 2);
    }
    private void DrawTurnCallout()
    {
        var state = match!;
        if (preferences.ReducedMotion || OverlayOpen || state.Turn == 0 || state.Stack.Length > 0 || now >= turnBannerUntil || state.Status == "finished") return;
        float remaining = (float)(turnBannerUntil - now);
        float alpha = Math.Min(1, Math.Min((1.5f - remaining) * 6, remaining * 3));
        bool own = state.ActivePlayerId == state.ViewerId;
        string player = state.Players.FirstOrDefault(p => p.Id == state.ActivePlayerId)?.Name ?? "Next player";
        canvas.Glow(new(800, 422), new(670, 180), (own ? Gold : Blue) * alpha * .13f);
        canvas.Line(new(554, 405), new(670, 405), Gold * alpha * .45f);
        canvas.Line(new(930, 405), new(1046, 405), Gold * alpha * .45f);
        canvas.CenterText(own ? "YOUR TURN" : player.ToUpperInvariant(), new(515, 361, 570, 71), Ink * alpha, own ? 1.55f : 1.2f, false, true);
        canvas.CenterText("TURN " + state.Turn, new(590, 436, 420, 27), Gold * alpha, .6f);
    }
}
