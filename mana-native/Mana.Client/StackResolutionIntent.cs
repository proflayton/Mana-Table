using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

/// <summary>A player's finite intent to pass priority for the currently observed stack.
/// Each pass still needs a fresh ordinary priority offer and the provider's unchanged interruption fence.</summary>
public sealed class StackResolutionIntent
{
    private readonly object gate = new();
    private readonly string game, phase;
    private readonly int viewer, turn;
    private readonly int? activePlayer;
    private readonly long interruptionVersion;
    private sealed record CapturedItem(int Position, StackActionKind Kind, int? ControllerId,
        ImmutableArray<(string Kind, string Id, int? PlayerId)> Targets);
    private readonly ImmutableDictionary<string, CapturedItem> captured;
    private ImmutableArray<string> remaining;
    private long revision;
    private bool active = true;
    private string? submittedScope;
    private int? submittedRemaining;

    private StackResolutionIntent(GameSnapshot state)
    {
        game = state.Id; viewer = state.ViewerId; turn = state.Turn; phase = state.PhaseKey; activePlayer = state.ActivePlayerId;
        interruptionVersion = state.StackInterruptionVersion!.Value;
        captured = state.Stack.Select((item, index) => (item, index)).ToImmutableDictionary(pair => pair.item.Id,
            pair => new CapturedItem(pair.index, pair.item.Kind, pair.item.ControllerId, pair.item.Targets.Select(Target).ToImmutableArray()), StringComparer.Ordinal);
        remaining = state.Stack.Select(item => item.Id).ToImmutableArray(); revision = state.Revision;
    }

    public static StackResolutionIntent? TryArm(GameSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status != "playing" || state.StackInterruptionVersion is not >= 0 || state.Viewer is not { Eliminated: false }
            || !OwnPriority(state) || state.Stack.IsDefaultOrEmpty
            || state.Stack.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id) || item.Targets.IsDefault || item.Targets.Any(target => target == null))
            || state.Stack.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != state.Stack.Length) return null;
        return new(state);
    }

    public int Remaining { get { lock (gate) return remaining.Length; } }
    public bool IsActive { get { lock (gate) return active; } }
    public void Stop() { lock (gate) active = false; }

    public bool CanContinue(GameSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (gate) return Continue(state);
    }

    private bool Continue(GameSnapshot state)
    {
        if (!active) return false;
        if (state.Id != game || state.ViewerId != viewer || state.Turn != turn || state.PhaseKey != phase
            || state.ActivePlayerId != activePlayer || state.StackInterruptionVersion != interruptionVersion
            || state.Status is not ("playing" or "resolving") || state.Viewer is not { Eliminated: false }
            || state.Stack.IsDefault || !WaitOrOwnPriority(state)) return End();
        var previous = state.Revision < revision ? captured.Keys.ToHashSet(StringComparer.Ordinal)
            : remaining.ToHashSet(StringComparer.Ordinal);
        int position = -1;
        foreach (var item in state.Stack) {
            if (item == null || !previous.Remove(item.Id) || !captured.TryGetValue(item.Id, out var original)
                || item.Kind != original.Kind || item.ControllerId != original.ControllerId || item.Targets.IsDefault
                || item.Targets.Any(target => target == null) || !item.Targets.Select(Target).SequenceEqual(original.Targets)) return End();
            int next = original.Position;
            if (next <= position) return End();
            position = next;
        }
        // The UI can still be showing an older accepted frame while a worker
        // observes progress. That frame cannot expand or retire the remaining queue.
        if (state.Revision >= revision) {
            revision = state.Revision;
            remaining = state.Stack.Select(item => item.Id).ToImmutableArray();
            if (remaining.IsEmpty) return End();
        }
        return true;
    }

    private static (string Kind, string Id, int? PlayerId) Target(StackTarget target) => (target.Kind, target.Id, target.PlayerId);
    private bool End() { active = false; return false; }

    private static bool OwnPriority(GameSnapshot state) => state.Decision is { Payload: PriorityPayload { Pass.Enabled: true } }
        && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
        && (state.PriorityPlayerId == null || state.PriorityPlayerId == state.ViewerId);

    private static bool WaitOrOwnPriority(GameSnapshot state) => state.Decision != null ? OwnPriority(state)
        : state.ActionClass is "priority" or "resolving" or ""
            && (state.DecisionOwnerId == null || state.DecisionOwnerId != state.ViewerId)
            && (state.PriorityPlayerId == null || state.PriorityPlayerId != state.ViewerId);

    internal PassPriority? NextPass(GameSnapshot state)
    {
        lock (gate) {
            if (!Continue(state) || !OwnPriority(state)) return null;
            string scope = state.Id + ":" + state.ViewerId + ":" + state.Decision!.Id;
            if (submittedScope == scope) return null;
            // Without an intervening action, this seat cannot receive another
            // priority round for the same queue. Never churn through fresh handles.
            if (submittedRemaining is { } count && state.Stack.Length >= count) { End(); return null; }
            var command = new PassPriority(state.Id, state.Decision.Id) { StackInterruptionVersion = interruptionVersion };
            if (GameCommandPermissions.Allows(state, command)) return command;
            End(); return null;
        }
    }

    internal void Submitted(GameSnapshot state)
    {
        lock (gate) {
            submittedScope = state.Id + ":" + state.ViewerId + ":" + state.Decision!.Id;
            submittedRemaining = state.Stack.Length;
        }
    }
}
