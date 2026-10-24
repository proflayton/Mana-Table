using Mana.Contracts;

namespace Mana.Renderer;

public enum CueKind { Arrive, Damage, Tap, Play }
public sealed record CardCue(string VisualId, CueKind Kind, int Amount, double Started, int? PlayerId = null, bool Reveal = true);

// Cues carry identifiers and reported deltas, never hidden card objects or rules.
public sealed class SceneEffects
{
    private readonly List<CardCue> cards = [];
    public IReadOnlyList<CardCue> Cards => cards;
    public void Clear() => cards.Clear();
    public void Observe(PublicActivityBatch activity, double now)
    {
        var current = activity.After;
        var visible = Visible(current).ToDictionary(p => p.Card.VisualId, p => p);
        cards.RemoveAll(c => now - c.Started > (c.Kind == CueKind.Play ? 2 : 1.1) || !visible.ContainsKey(c.VisualId));
        if (activity.IsBaseline) { cards.Clear(); return; }
        var before = Visible(activity.Before!).ToDictionary(p => p.Card.VisualId, p => p);
        foreach (var entry in activity.Entries.Where(entry => (entry.Kind is "cast" or "land") && entry.CardId != null))
            if (visible.TryGetValue(entry.CardId!, out var played) && played.Zone is "Stack" or "Battlefield") {
                cards.RemoveAll(cue => cue.VisualId == entry.CardId && cue.Kind == CueKind.Play);
                Play(entry.CardId!, entry.PlayerId);
            }
        foreach (var spell in current.Stack.Where(spell => !spell.Ability && spell.Card is { FaceDown: false, VisualId.Length: > 0 }))
            if (!activity.Before!.Stack.Any(previous => !previous.Ability && previous.Card?.VisualId == spell.Card!.VisualId))
                Play(spell.Card!.VisualId, spell.ControllerId);
        void Play(string id, int? player, bool reveal = true)
        {
            // The activity cursor consumes events once; a stack observation may
            // describe the same cast. Keep that journey's original start.
            if (!cards.Any(cue => cue.VisualId == id && cue.Kind == CueKind.Play)) cards.Add(new(id, CueKind.Play, 0, now, player, reveal));
        }
        var reportedDamage = activity.Entries.Where(e => e.Impact is { Kind: "damage", CardId: not null })
            .Select(e => e.Impact!.CardId!).ToHashSet(StringComparer.Ordinal);
        cards.RemoveAll(c => c.Kind == CueKind.Damage && reportedDamage.Contains(c.VisualId));
        foreach (var item in visible.Values) {
            if (!before.TryGetValue(item.Card.VisualId, out var old)) {
                if (item.Zone == "Battlefield") Add(CueKind.Arrive, 0);
                continue;
            }
            if (old.Zone != item.Zone) Add(CueKind.Arrive, 0);
            if (old.Zone is "Hand" or "Command" && item.Zone == "Battlefield") Play(item.Card.VisualId, null);
            if (old.Zone == "Stack" && item.Zone == "Battlefield") Play(item.Card.VisualId, null, reveal: false);
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
