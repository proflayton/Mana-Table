using Mana.Contracts;

namespace Mana.Renderer;

public sealed record ImpactCue(long ActivityId, PublicImpact Impact, double Started);

/// <summary>Actual public results, consumed once. No association with a neighbouring resolution is inferred.</summary>
public sealed class ImpactFeedback
{
    public const double Duration = 2.4;
    private readonly List<ImpactCue> cues = [];
    private long lastActivityId;
    public IReadOnlyList<ImpactCue> Cues => cues;

    public void Observe(GameSnapshot? before, GameSnapshot after, double now)
    {
        if (before == null || before.Id != after.Id) {
            cues.Clear(); lastActivityId = after.Activity.Select(e => e.Id).DefaultIfEmpty().Max(); return;
        }
        cues.RemoveAll(c => now - c.Started >= Duration || !RecipientVisible(after, c.Impact));
        var entries = after.Activity.Where(e => e.Id > lastActivityId).ToArray();
        var replaced = entries.Where(e => e.Impact is { } impact && RecipientVisible(after, impact))
            .Select(e => (e.Impact!.Kind, e.Impact.CardId, e.Impact.PlayerId)).ToHashSet();
        // A new observed burst replaces the older transient at that recipient.
        // Every new event stays represented; history retains earlier bursts.
        cues.RemoveAll(c => replaced.Contains((c.Impact.Kind, c.Impact.CardId, c.Impact.PlayerId)));
        foreach (var entry in entries) {
            if (entry.Impact is not { } impact || !RecipientVisible(after, impact)) continue;
            if (impact.Kind == "damage" && impact.Amount > 0 || impact.Kind == "life" && impact.Amount != 0)
                cues.Add(new(entry.Id, impact, now));
        }
        // Older hosts still supply authoritative life totals. Preserve a plain
        // life transition for them, without inventing damage or a source.
        foreach (var player in after.Players) {
            var old = before.Players.FirstOrDefault(p => p.Id == player.Id);
            if (old == null || old.Life == player.Life || entries.Any(e => e.Impact is { Kind: "life" } life && life.PlayerId == player.Id)) continue;
            cues.RemoveAll(c => c.Impact.Kind == "life" && c.Impact.PlayerId == player.Id);
            cues.Add(new(0, new("life", (long)player.Life - old.Life, PlayerId: player.Id, Before: old.Life, After: player.Life), now));
        }
        lastActivityId = Math.Max(lastActivityId, after.Activity.Select(e => e.Id).DefaultIfEmpty().Max());
    }

    // Revalidate against the current public occurrence even if it was visible
    // when the historical event was emitted. Never retain a departed hidden face.
    public static bool RecipientVisible(GameSnapshot state, PublicImpact impact) => impact.CardId is { Length: > 0 } id
        ? CardViews.Located(state).Any(item => (item.Card.VisualId == id || item.Card.FaceDown && item.Card.CombatId == id)
            && item.Zone is "Battlefield" or "Command" or "Stack" or "Graveyard" or "Exile")
        : impact.PlayerId is { } player && state.Players.Any(p => p.Id == player);
}
