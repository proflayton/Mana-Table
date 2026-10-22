using Mana.Contracts;

namespace Mana.Renderer;

public enum CueKind { Arrive, Damage, Tap }
public sealed record CardCue(string VisualId, CueKind Kind, int Amount, double Started);

// Cues carry identifiers and reported deltas, never hidden card objects or rules.
public sealed class SceneEffects
{
    private readonly List<CardCue> cards = [];
    public IReadOnlyList<CardCue> Cards => cards;
    public void Clear() => cards.Clear();
    public void Observe(GameSnapshot? previous, GameSnapshot current, double now)
    {
        var visible = Visible(current).ToDictionary(p => p.Card.VisualId, p => p);
        cards.RemoveAll(c => now - c.Started > 1.1 || !visible.ContainsKey(c.VisualId));
        if (previous == null || previous.Id != current.Id) { cards.Clear(); return; }
        var before = Visible(previous).ToDictionary(p => p.Card.VisualId, p => p);
        long priorActivity = previous.Activity.Select(a => a.Id).DefaultIfEmpty().Max();
        var reportedDamage = current.Activity.Where(e => e.Id > priorActivity && e.Impact is { Kind: "damage", CardId: not null })
            .Select(e => e.Impact!.CardId!).ToHashSet(StringComparer.Ordinal);
        cards.RemoveAll(c => c.Kind == CueKind.Damage && reportedDamage.Contains(c.VisualId));
        foreach (var item in visible.Values) {
            if (!before.TryGetValue(item.Card.VisualId, out var old)) {
                if (item.Zone == "Battlefield") Add(CueKind.Arrive, 0);
                continue;
            }
            if (old.Zone != item.Zone) Add(CueKind.Arrive, 0);
            // Damage is an event-time result. A marked-damage delta cannot
            // represent infect, prevention or a recipient dying between polls.
            // Hosts without typed impacts retain the quieter legacy cue only.
            if (item.Card.Damage > old.Card.Damage && !reportedDamage.Contains(item.Card.VisualId))
                Add(CueKind.Damage, item.Card.Damage - old.Card.Damage);
            if (item.Card.Tapped != old.Card.Tapped) Add(CueKind.Tap, 0);
            void Add(CueKind kind, int amount) { cards.RemoveAll(c => c.VisualId == item.Card.VisualId && c.Kind == kind); cards.Add(new(item.Card.VisualId, kind, amount, now)); }
        }
    }
    private static IEnumerable<(Card Card, string Zone)> Visible(GameSnapshot state) => CardViews.Located(state)
        .Where(p => !p.Card.FaceDown && !string.IsNullOrEmpty(p.Card.VisualId)).DistinctBy(p => p.Card.VisualId);
}
