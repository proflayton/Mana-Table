using Mana.Contracts;
using Mana.Client;
using Mana.Magic;
using Mana.Renderer;

namespace Mana.Table.Presentation;

/// <summary>
/// Applies accepted, seat-filtered observations to the table's transient feedback.
/// A scope boundary revokes the preceding viewer's readers, motion and cues before
/// any consumer sees the next observation. This owner never advances the engine.
/// </summary>
internal sealed class MatchPresentationLifecycle
{
    internal sealed record Departure(Card Card, Pose Pose, string Destination, double Started);
    internal readonly record struct Transition(bool ScopeChanged, bool Finished, int LifeLosses,
        bool CombatStarted, bool CardPlayed, string? Notice);

    private readonly PublicActivityCursor activity = new();
    private readonly SceneEffects effects = new();
    private readonly TablePresence presence = new();
    private readonly PresentationPacing pacing = new();
    private readonly ActionFeedback actions = new();
    private readonly PublicChangeFeedback changes = new();
    private readonly ImpactFeedback impacts = new();
    private readonly ManaPoolPresence mana = new();
    private readonly PriorityTransitionFeedback priority = new();
    private readonly StackArrivalPresentation stackArrival = new();
    private readonly List<Departure> departures = [];
    private (string Game, int Viewer)? scope;
    private double finiteBeatUntil;

    internal CardPresentationController Cards { get; } = new();
    internal IReadOnlyList<CardCue> CardCues => effects.Cards;
    internal IReadOnlyList<SeatCue> SeatCues => presence.Cues;
    internal IReadOnlyList<EffectChangeGroup> Changes => changes.Groups;
    internal EffectChangeGroup? LatestChange => changes.Latest;
    internal ResolutionCue? LastResolution => actions.Last;
    internal IReadOnlyList<ImpactCue> Impacts => impacts.Cues;
    internal PriorityTransition? PriorityTransition => priority.Latest;
    internal IReadOnlyList<Departure> Departures => departures;
    internal bool Ready(double now) => pacing.Ready(now) && stackArrival.Ready(now);
    internal bool ReadyForStack(double now) => pacing.Ready(now) && stackArrival.ProgressReady(now);
    internal bool ReadyForFinite(double now) => now >= finiteBeatUntil && stackArrival.ProgressReady(now);
    internal void BeginFinite(IEnumerable<(string Execution, string Group)> captured, double now)
    {
        stackArrival.BeginFinite(captured, now);
        finiteBeatUntil = now + 1.2;
    }
    internal void EndFinite() => stackArrival.EndFinite();
    internal float StackArrivalStrength(string id, double now) => stackArrival.Strength(id, now);
    internal StackArrivalPresentation.Announcement? Announcement(double now) => stackArrival.Current(now);
    internal int AnnouncementCount => stackArrival.PendingCount;
    internal int IncompleteAnnouncements => stackArrival.IncompleteCount;
    internal void HoldAnnouncements(double now, bool holding) => stackArrival.Hold(now, holding);
    internal int PendingDraws(int player, double now) => presence.PendingDraws(player, now);
    internal ManaPoolCue? ManaCue(int player, string symbol, double now) => mana.For(player, symbol, now);
    internal IReadOnlyList<ManaPoolCue> ManaCues(int player, double now) => mana.ForPlayer(player, now);

    internal Transition Observe(GameSnapshot? previous, GameSnapshot? current, double now, BoardView view, ExecutionBatch? execution = null,
        bool finiteProgress = false)
    {
        var nextScope = current == null ? ((string Game, int Viewer)?)null : (current.Id, current.ViewerId);
        bool changed = scope != nextScope;
        if (changed || current == null) Reset();
        scope = nextScope;
        if (current == null) return new(changed, false, 0, false, false, null);

        // No consumer may compare against another viewer's historical snapshot,
        // even when the engine's game identity has not changed.
        var before = changed ? null : previous;
        var batch = activity.Observe(before, current, execution?.Transitions ?? [], execution?.Baseline ?? false, execution?.Gap ?? false);
        effects.Observe(batch, now); presence.Observe(batch, now);
        actions.Observe(batch, now); changes.Observe(batch, now); impacts.Observe(batch, now);
        pacing.Observe(before, current, now); mana.Observe(before, current, now); priority.Observe(before, current, now);
        if (finiteProgress) finiteBeatUntil = Math.Max(finiteBeatUntil, now + .16);
        else if (before != null && PresentationPacing.ShouldPresent(before, current))
            finiteBeatUntil = Math.Max(finiteBeatUntil, now + 2.6);
        stackArrival.Observe(before, current, now, batch.Entries, batch.HasHistoryGap,
            batch.Executions, batch.ExecutionBaseline, batch.ExecutionGap);
        ObserveDepartures(batch, now, view);
        bool comparable = before != null && before.Id == current.Id && before.ViewerId == current.ViewerId;
        return new(changed,
            comparable && current.Status == "finished" && before!.Status != "finished",
            comparable ? current.Players.Count(player => before!.Players.Any(old => old.Id == player.Id && old.Life > player.Life)) : 0,
            comparable && current.Combat?.Attackers.Length > 0 && before!.Combat?.Attackers.Length is null or 0,
            comparable && current.Activity.LastOrDefault()?.Id != before!.Activity.LastOrDefault()?.Id
                && current.Activity.LastOrDefault()?.Kind is "land" or "cast",
            comparable && current.Notices.LastOrDefault() is { } latest && latest != before!.Notices.LastOrDefault() ? latest : null);
    }

    internal void Reset()
    {
        scope = null; activity.Clear(); effects.Clear(); presence.Clear(); pacing.Reset();
        actions.Clear(); changes.Clear(); impacts.Clear(); mana.Clear(); priority.Clear();
        stackArrival.Clear();
        finiteBeatUntil = 0;
        departures.Clear(); Cards.Reset();
    }

    private void ObserveDepartures(PublicActivityBatch batch, double now, BoardView view)
    {
        var visible = CardViews.Located(batch.After).Where(item => !item.Card.FaceDown)
            .ToLookup(item => item.Card.VisualId, StringComparer.Ordinal);
        departures.RemoveAll(departure => now - departure.Started > 1.5
            || !visible[departure.Card.VisualId].Any(item => item.Zone is "Graveyard" or "Exile"));
        if (batch.IsBaseline) { departures.Clear(); return; }
        foreach (var card in batch.Before!.Players.SelectMany(player => player.Zone("Battlefield").Cards)
                     .Where(card => !card.FaceDown && card.VisualId.Length > 0)) {
            var destination = visible[card.VisualId].FirstOrDefault();
            if (destination.Card == null || destination.Zone is not ("Graveyard" or "Exile")
                || !Cards.TryGetPose(card.VisualId, out var pose)) continue;
            departures.RemoveAll(departure => departure.Card.VisualId == card.VisualId);
            var entry = Cards.Cards.LastOrDefault(entry => entry.Id == card.VisualId);
            var origin = entry?.Focused == true ? entry.Rest : pose;
            departures.Add(new(card, (entry?.View ?? view).Inverse(origin), destination.Zone, now));
        }
    }
}
