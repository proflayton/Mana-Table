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
    private ImmutableArray<(TableChoiceCue Cue, double Until)> choices = [];
    public bool Supported => !closed && !unavailable && provider is { SupportsTablePresence: true };
    public bool InFlight => inFlight;

    public void Observe(GameSnapshot? value)
    {
        if (closed) return;
        if (state?.Id != value?.Id || state?.ViewerId != value?.ViewerId) unavailable = false;
        if (state?.Id != value?.Id || state?.ViewerId != value?.ViewerId
            || state?.BoardRevision != value?.BoardRevision || state?.PublicInspection != value?.PublicInspection
            || state?.PublicChoice != value?.PublicChoice || state?.Status != value?.Status
            || state?.DecisionOwnerId != value?.DecisionOwnerId || state?.ActionClass != value?.ActionClass) {
            generation++; cues = []; choices = []; acceptedRevision = -1;
        }
        state = value;
    }

    // The caller gives gameplay work precedence and supplies only a current
    // rendered public occurrence. No pointer coordinates cross this boundary.
    public void Update(double now, TableInspectionSubject? desired, bool foreground, bool transportAvailable, TableChoicePreview? choice = null)
    {
        if (closed || !double.IsFinite(now)) return;
        if (now < latestTime) { generation++; cues = []; choices = []; nextExchange = now; }
        latestTime = now;
        if (active != foreground) { active = foreground; generation++; cues = []; choices = []; }
        if (!active || !Supported || inFlight || !transportAvailable || now < nextExchange
            || state is not { Status: "playing", Viewer: { Eliminated: false } } current) return;
        var context = current.PublicInspection;
        bool ownsContext = context != null && context.PlayerId == current.ViewerId && PublicInspectionFacts.IsCurrent(current);
        if (!ownsContext || !PublicSubject(current, desired)) desired = null;
        var choiceContext = current.PublicChoice;
        bool ownsChoice = provider is { SupportsTableChoices: true } && choiceContext != null
            && choiceContext.PlayerId == current.ViewerId && PublicChoiceFacts.IsCurrent(current);
        if (!ownsChoice || !PublicChoiceFacts.AllowsPreview(current, choice)) choice = null;
        var request = new TablePresenceUpdate(current.Id, current.BoardRevision, ++sequence,
            ownsContext ? context!.Id : null, desired, ownsChoice ? choiceContext!.Id : null, choice);
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
                if (failed) { cues = []; choices = []; nextExchange = Math.Max(nextExchange, latestTime + 1); return; }
                if (result is TablePresenceResult.Unsupported) { unavailable = true; cues = []; choices = []; return; }
                if (result is TablePresenceResult.Superseded) { cues = []; choices = []; acceptedRevision = -1; generation++; return; }
                if (result is not TablePresenceResult.Current { Frame: { } frame }) { cues = []; choices = []; return; }
                if (frame.GameId != state.Id || frame.BoardRevision != state.BoardRevision
                    || frame.Revision < acceptedRevision || frame.Cues.IsDefault || frame.Cues.Length > 6
                    || frame.Choices.IsDefault || frame.Choices.Length > 6
                    || frame.Choices.Any(cue => cue == null)
                    || frame.Choices.Select(cue => cue.PlayerId).Distinct().Count() != frame.Choices.Length) {
                    cues = []; choices = []; return;
                }
                acceptedRevision = frame.Revision;
                // Anchor remaining leases at dispatch: delayed responses must
                // shorten a cue's life rather than extend stale attention.
                cues = frame.Cues.Where(cue => CurrentCue(state, cue))
                    .Select(cue => (cue, now + cue.RemainingMilliseconds / 1000d)).ToImmutableArray();
                choices = provider is { SupportsTableChoices: true } ? frame.Choices.Where(cue => CurrentChoice(state, cue))
                    .Select(cue => (cue, now + cue.RemainingMilliseconds / 1000d)).ToImmutableArray() : [];
            });
        });
    }

    public ImmutableArray<TableInspectionCue> Visible(double now)
        => !closed && active && state != null && double.IsFinite(now)
            ? cues.Where(cue => now < cue.Until && CurrentCue(state, cue.Cue))
                .Select(cue => cue.Cue).ToImmutableArray() : [];
    public ImmutableArray<TableChoiceCue> VisibleChoices(double now)
        => !closed && active && state != null && double.IsFinite(now)
            ? choices.Where(cue => now < cue.Until && CurrentChoice(state, cue.Cue))
                .Select(cue => cue.Cue).ToImmutableArray() : [];

    public void Suspend()
    {
        if (closed) return;
        active = false; generation++; cues = []; choices = [];
    }

    public void Close()
    {
        if (closed) return;
        closed = true; generation++; cues = []; choices = []; state = null;
        // Already dispatched presence cannot cancel a shared rules transport.
    }

    private static bool CurrentCue(GameSnapshot current, TableInspectionCue cue)
        => PublicInspectionFacts.IsCurrent(current) && current.PublicInspection is { } context
            && context.BoardRevision == current.BoardRevision && context.PlayerId == cue.PlayerId
            && context.Id == cue.ContextId && cue.PlayerId != current.ViewerId
            && current.Players.Any(player => player.Id == cue.PlayerId && !player.Eliminated)
            && cue.RemainingMilliseconds is > 0 and <= 1500 && PublicSubject(current, cue.Subject);

    private static bool CurrentChoice(GameSnapshot current, TableChoiceCue cue)
        => cue != null && current.PublicChoice is { } context && context.PlayerId == cue.PlayerId
            && context.Id == cue.ContextId && cue.PlayerId != current.ViewerId
            && cue.RemainingMilliseconds is > 0 and <= 1500 && PublicChoiceFacts.AllowsPreview(current, cue.Preview);

    public static bool PublicSubject(GameSnapshot current, TableInspectionSubject? subject)
        => PublicChoiceFacts.PublicSubject(current, subject);
}
