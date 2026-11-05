using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>Optional, lossy table attention. Owner-thread methods and posted
/// completions never submit game commands or change the match replica.</summary>
public sealed class TablePresenceSession(ITablePresence? provider, Action<Action> post)
{
    private GameSnapshot? state;
    private long generation, sequence, acceptedRevision = -1;
    private bool inFlight, closed, unavailable, active;
    private double nextExchange, latestTime;
    private ImmutableArray<(TableInspectionCue Cue, double Until)> cues = [];
    public bool Supported => !closed && !unavailable && provider is { SupportsTablePresence: true };
    public bool InFlight => inFlight;

    public void Observe(GameSnapshot? value)
    {
        if (closed) return;
        if (state?.Id != value?.Id || state?.ViewerId != value?.ViewerId) unavailable = false;
        if (state?.Id != value?.Id || state?.ViewerId != value?.ViewerId
            || state?.BoardRevision != value?.BoardRevision || state?.PublicInspection != value?.PublicInspection) {
            generation++; cues = []; acceptedRevision = -1;
        }
        state = value;
    }

    // The caller gives gameplay work precedence and supplies only a current
    // rendered public occurrence. No pointer coordinates cross this boundary.
    public void Update(double now, TableInspectionSubject? desired, bool foreground, bool transportAvailable)
    {
        if (closed || !double.IsFinite(now)) return;
        if (now < latestTime) { generation++; cues = []; nextExchange = now; }
        latestTime = now;
        if (active != foreground) { active = foreground; generation++; cues = []; }
        if (!active || !Supported || inFlight || !transportAvailable || now < nextExchange
            || state is not { Status: "playing", Viewer: { Eliminated: false } } current) return;
        var context = current.PublicInspection;
        bool ownsContext = context != null && context.PlayerId == current.ViewerId && PublicInspectionFacts.IsCurrent(current);
        if (!ownsContext || !PublicSubject(current, desired)) desired = null;
        var request = new TablePresenceUpdate(current.Id, current.BoardRevision, ++sequence,
            ownsContext ? context!.Id : null, desired);
        long capturedGeneration = generation;
        int viewer = current.ViewerId;
        inFlight = true; nextExchange = now + .25;
        _ = Task.Run(async () => {
            TablePresenceResult? result = null;
            bool failed = false;
            try { result = await provider!.ExchangePresenceAsync(request); }
            catch { failed = true; } // Presence failure is never a game failure.
            post(() => {
                inFlight = false;
                if (closed || capturedGeneration != generation || !active || state?.ViewerId != viewer
                    || state.Id != request.GameId || state.BoardRevision != request.BoardRevision) return;
                if (failed) { cues = []; nextExchange = Math.Max(nextExchange, latestTime + 1); return; }
                if (result is TablePresenceResult.Unsupported) { unavailable = true; cues = []; return; }
                if (result is TablePresenceResult.Superseded) { cues = []; acceptedRevision = -1; generation++; return; }
                if (result is not TablePresenceResult.Current { Frame: { } frame }) { cues = []; return; }
                if (frame.GameId != state.Id || frame.BoardRevision != state.BoardRevision
                    || frame.Revision < acceptedRevision || frame.Cues.IsDefault || frame.Cues.Length > 6) {
                    cues = []; return;
                }
                acceptedRevision = frame.Revision;
                // Anchor remaining leases at dispatch: delayed responses must
                // shorten a cue's life rather than extend stale attention.
                cues = frame.Cues.Where(cue => CurrentCue(state, cue))
                    .Select(cue => (cue, now + cue.RemainingMilliseconds / 1000d)).ToImmutableArray();
            });
        });
    }

    public ImmutableArray<TableInspectionCue> Visible(double now)
        => !closed && active && state != null && double.IsFinite(now)
            ? cues.Where(cue => now < cue.Until && CurrentCue(state, cue.Cue))
                .Select(cue => cue.Cue).ToImmutableArray() : [];

    public void Suspend()
    {
        if (closed) return;
        active = false; generation++; cues = [];
    }

    public void Close()
    {
        if (closed) return;
        closed = true; generation++; cues = []; state = null;
        // Already dispatched presence cannot cancel a shared rules transport.
    }

    private static bool CurrentCue(GameSnapshot current, TableInspectionCue cue)
        => PublicInspectionFacts.IsCurrent(current) && current.PublicInspection is { } context
            && context.BoardRevision == current.BoardRevision && context.PlayerId == cue.PlayerId
            && context.Id == cue.ContextId && cue.PlayerId != current.ViewerId
            && current.Players.Any(player => player.Id == cue.PlayerId && !player.Eliminated)
            && cue.RemainingMilliseconds is > 0 and <= 1500 && PublicSubject(current, cue.Subject);

    public static bool PublicSubject(GameSnapshot current, TableInspectionSubject? subject)
        => subject is { Id.Length: > 0 } && (subject.Kind switch {
            TableInspectionKind.BattlefieldCard => current.Players.SelectMany(player => player.Zone("Battlefield").Cards)
                .Count(card => card.VisualId == subject.Id && !card.FaceDown && !card.PhasedOut) == 1,
            TableInspectionKind.StackItem => current.Stack.Count(item => item.Id == subject.Id
                && item.Card is { FaceDown: false } && !string.IsNullOrWhiteSpace(item.Name)) == 1,
            _ => false
        });
}
