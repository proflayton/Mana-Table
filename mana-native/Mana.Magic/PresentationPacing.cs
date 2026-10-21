using Mana.Contracts;

namespace Mana.Magic;

/// <summary>Gives visible actions time to read before Auto advances priority.
/// Snapshots and required decisions remain live; repeated polling never extends a beat.</summary>
public sealed class PresentationPacing
{
    private double until;
    public bool Ready(double now) => now >= until;
    public void Reset() => until = 0;
    public void Observe(GameSnapshot? before, GameSnapshot after, double now)
    {
        double beat = Beat(before, after);
        if (beat > 0) until = Math.Max(until, now + beat);
    }
    public static bool ShouldPresent(GameSnapshot before, GameSnapshot after) => Beat(before, after) > 0
        || before.Status != after.Status && after.Status is "finished" or "error"
        || !before.Notices.SequenceEqual(after.Notices);
    private static double Beat(GameSnapshot? before, GameSnapshot after)
    {
        if (before == null || before.Id != after.Id) return 1.2;
        double beat = 0;
        // Empty phases have no animation to read. Decisions and saved stops govern those pauses.
        if (before.Turn != after.Turn || before.ActivePlayerId != after.ActivePlayerId) beat = 1.2;
        if (!before.Stack.Select(Stack).SequenceEqual(after.Stack.Select(Stack))) beat = Math.Max(beat, after.Stack.FirstOrDefault()?.Targets.Length > 0 ? 2.5 : 1.25);
        if (!Combat(before).SequenceEqual(Combat(after))) beat = Math.Max(beat, 1.6);
        if (!before.States.OrderBy(state => state.Id, StringComparer.Ordinal).SequenceEqual(after.States.OrderBy(state => state.Id, StringComparer.Ordinal)))
            beat = Math.Max(beat, 1.1);
        if (!Seats(before).SequenceEqual(Seats(after)) || !Cards(before).SequenceEqual(Cards(after))) beat = Math.Max(beat, 1.1);
        var last = before.Activity.LastOrDefault()?.Id ?? 0;
        if (after.Activity.Any(a => a.Id > last && a.Kind == "resolved")) beat = Math.Max(beat, 2.6);
        if (after.Activity.Any(a => a.Id > last && a.Kind is "damage" or "life" or "combat")) beat = Math.Max(beat, 1.6);
        else if (after.Activity.Any(a => a.Id > last && a.Kind is "land" or "cast" or "ability" or "resolved" or "draw" or "arrived" or "moved" or "counters")) beat = Math.Max(beat, 1.25);
        return beat;
    }
    private static IEnumerable<string> Combat(GameSnapshot s) => (s.Combat?.Attackers ?? []).OrderBy(a => a.CardId)
        .Select(a => $"{a.CardId}:{a.Defender?.Kind}:{a.Defender?.Id}:{a.DefendingPlayerId}:{a.Blocked}:{string.Join(',', a.BlockerIds.Order())}");
    private static string Stack(StackItem item) => item.Id + ":" + string.Join(',', item.Targets.Select(t => t.Kind + ':' + t.Id));
    private static IEnumerable<string> Seats(GameSnapshot s) => s.Players.OrderBy(p => p.Id)
        .Select(p => $"{p.Id}:{p.Life}:{p.Eliminated}:{string.Join(',', p.Mana.OrderBy(m => m.Key))}:{string.Join(',', p.Counters.OrderBy(m => m.Key))}:{string.Join(',', p.States.OrderBy(state => state.Id))}:{string.Join(',', p.Zones.OrderBy(z => z.Name).Select(z => z.Name + ':' + z.Count + ':' + string.Join(',', z.Cards.Select(c => c.VisualId).Order())))}");
    private static IEnumerable<string> Cards(GameSnapshot s) => CardViews.Located(s).Where(c => CardViews.Identity(c.Card).Length > 0)
        .OrderBy(c => CardViews.Identity(c.Card)).ThenBy(c => c.Zone)
        .Select(c => $"{CardViews.Identity(c.Card)}:{c.Zone}:{c.Card.Name}:{c.Card.Type}:{c.Card.GamePieceType}:{c.Card.FaceDown}:{c.Card.PhasedOut}:{c.Card.Sick}:{c.Card.Attacking}:{c.Card.Blocking}:{c.Card.Tapped}:{c.Card.Power}:{c.Card.Toughness}:{c.Card.Damage}:{c.Card.AttachedTo}:{string.Join(',', c.Card.Attachments.OrderBy(attachment => attachment.Id))}:{string.Join(',', c.Card.CombatKeywords.Order())}:{(c.Card.States == null ? "legacy" : string.Join(',', c.Card.States.OrderBy(state => state.Id)))}:{string.Join(',', c.Card.Counters.OrderBy(k => k.Key))}");
}
