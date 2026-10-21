using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Renderer;

public enum SeatCueKind { Draw, LifeLoss, LifeGain }
public sealed record SeatCue(int PlayerId, SeatCueKind Kind, int Amount, double Started);

// Public engine draw events and life deltas only. An opponent's draw has no card identity,
// art, or cached hidden object. These cues never delay an authoritative update.
public sealed class TablePresence
{
    public const double DrawDuration = .62, DrawStagger = .09;
    private readonly List<SeatCue> cues = [];
    private string? gameId;
    private long lastActivityId;
    public IReadOnlyList<SeatCue> Cues => cues;
    public void Clear() { cues.Clear(); gameId = null; lastActivityId = 0; }
    public void Observe(GameSnapshot? previous, GameSnapshot current, double now)
    {
        cues.RemoveAll(c => now - c.Started > 1.5 || !current.Players.Any(p => p.Id == c.PlayerId && !p.Eliminated));
        if (previous == null || previous.Id != current.Id || current.Status == "finished") {
            Clear(); gameId = current.Id; lastActivityId = current.Activity.Select(e => e.Id).DefaultIfEmpty().Max(); return;
        }
        if (gameId != current.Id) { gameId = current.Id; lastActivityId = previous.Activity.Select(e => e.Id).DefaultIfEmpty().Max(); }
        var draws = current.Activity.Where(e => e.Id > lastActivityId && e.Kind == "draw" && e.Count > 0 && e.PlayerId != null)
            .GroupBy(e => e.PlayerId!.Value).ToDictionary(g => g.Key, g => (int)Math.Min(int.MaxValue, g.Sum(e => (long)e.Count)));
        lastActivityId = Math.Max(lastActivityId, current.Activity.Select(e => e.Id).DefaultIfEmpty().Max());
        foreach (var player in current.Players) {
            var before = previous.Players.FirstOrDefault(p => p.Id == player.Id);
            if (before == null || player.Eliminated) continue;
            int life = player.Life - before.Life;
            if (life != 0) {
                cues.RemoveAll(c => c.PlayerId == player.Id && c.Kind != SeatCueKind.Draw);
                cues.Add(new(player.Id, life < 0 ? SeatCueKind.LifeLoss : SeatCueKind.LifeGain, Math.Abs(life), now));
            }
            if (player.Id == current.ViewerId) continue; // The local hand uses visible-card motion.
            if (draws.TryGetValue(player.Id, out int drawn)) {
                cues.RemoveAll(c => c.PlayerId == player.Id && c.Kind == SeatCueKind.Draw);
                cues.Add(new(player.Id, SeatCueKind.Draw, drawn, now));
            }
        }
    }
    public int PendingDraws(int player, double now) => cues.Where(c => c.PlayerId == player && c.Kind == SeatCueKind.Draw)
        .Sum(c => Enumerable.Range(0, Math.Min(7, c.Amount)).Count(i => now - c.Started < DrawDuration + i * DrawStagger));

    public static Pose DrawPose(Pose library, Pose hand, double elapsed)
    {
        float t = Math.Clamp((float)(elapsed / DrawDuration), 0, 1);
        float ease = t * t * (3 - 2 * t), lift = MathF.Sin(t * MathF.PI) * 55;
        return new(Vector2.Lerp(library.Center, hand.Center, ease) - new Vector2(0, lift),
            Vector2.Lerp(library.Size, hand.Size, ease),
            library.Angle + MathHelper.WrapAngle(hand.Angle - library.Angle) * ease,
            MathHelper.Lerp(library.Tilt, hand.Tilt, ease), MathHelper.Lerp(library.Perspective, hand.Perspective, ease),
            MathHelper.Lerp(library.Shear, hand.Shear, ease), lift);
    }
}
