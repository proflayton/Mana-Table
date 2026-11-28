namespace Mana.Contracts;

/// <summary>Public ownership of an actual unfinished controller input/dialog.
/// Published means its owner has a coherent decision. Generation is not a key.</summary>
public sealed record MatchWait([property: System.Text.Json.Serialization.JsonRequired] int PlayerId,
    [property: System.Text.Json.Serialization.JsonRequired] long Generation,
    [property: System.Text.Json.Serialization.JsonRequired] bool Published);

/// <summary>A read/control repair. It cannot answer an input or replay a command.</summary>
public interface IMatchProgressRecovery
{
    bool SupportsMatchProgressRecovery => false;
    Task ReconcileMatchProgressAsync(string gameId, CancellationToken cancellation = default);
}

/// <summary>Host-only counterpart; it grants no policy access to another seat.</summary>
public interface IAutomatedMatchProgressRecovery
{
    bool SupportsMatchProgressRecovery => false;
    Task ReconcileAutomatedMatchProgressAsync(string gameId, int seatId, CancellationToken cancellation = default);
}

public sealed class EngineMatchProgressException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Immutable liveness evidence. Publication/transport counters are not
/// rules progress. No card faces, source offers or command answers are retained.</summary>
public sealed record MatchProgressWitness(string GameId, int Turn, string Phase, int? ActivePlayerId,
    int? DecisionOwnerId, string DecisionId, string ActionClass, string Stack, long PublicAction, long Execution, long Restoration,
    string InputProgress)
{
    public static MatchProgressWitness From(GameSnapshot state) => From(state, state.ViewerId);
    public static MatchProgressWitness From(GameSnapshot state, int actor)
    {
        if (state.MatchWaits?.FirstOrDefault(wait => wait.PlayerId == actor && !wait.Published) is { } missing)
            return new(state.Id, 0, "unpublished-input", actor, actor, missing.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), "unpublished-input", "", 0, 0, 0, "");
        // A replacement publication scope is not a new rules input. Preserve
        // whether this audience has received its actual unfinished input, but
        // do not let repeated scope reissues renew a stuck automatic wait.
        string decision = state.MatchWaits?.FirstOrDefault(wait => wait.PlayerId == actor) is { } actual
            ? actual.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + (state.Decision == null ? ":undelivered" : ":delivered")
            : state.Decision?.Id ?? "";
        return new(state.Id, state.Turn, state.PhaseKey,
        state.ActivePlayerId, state.DecisionOwnerId, decision, state.ActionClass,
        string.Concat(state.Stack.Select(item => $"{item.Id.Length}:{item.Id}")),
        state.Activity.Where(item => item.Kind is "cast" or "ability" or "resolved" or "land" or "combat")
            .Select(item => item.Id).DefaultIfEmpty().Max(), state.Executions?.LastSequence ?? 0,
        state.Undo?.RestorationVersion ?? 0, StagedProgress(state));
    }

    private static string StagedProgress(GameSnapshot state)
    {
        string targets = state.Decision?.Payload is TargetSelectionPayload { Progress: { } selected }
            ? selected.SelectedCount + ":" + string.Join(',', selected.SelectedPlayers) : "";
        string selectedCards = string.Join(',', state.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
            .Where(card => card.Highlighted).Select(card => card.VisualId).Order(StringComparer.Ordinal));
        string combat = state.Combat == null ? "" : string.Join(';', state.Combat.Attackers
            .Select(attack => attack.CardId + ":" + attack.DefendingPlayerId + ":" + string.Join(',', attack.BlockerIds)));
        string bill = state.Decision?.Payload is PaymentPayload payment
            ? string.Join(',', payment.UnpaidMana) + ":" + string.Join(',', payment.UnpaidManaParts) : "";
        string mana = string.Join(';', state.Players.Select(player => player.Id + ":" + string.Join(',', player.Mana.OrderBy(item => item.Key))));
        return targets + "|" + selectedCards + "|" + combat + "|" + bill + "|" + mana;
    }

    public static bool RequiresClientProgress(GameSnapshot state)
    {
        if (state.Status is not ("playing" or "resolving") || state.Viewer?.Eliminated != false) return false;
        if (state.MatchWaits is { } waits) return waits.Any(wait => !wait.Published)
            || state.MatchReadingWaits is not { IsEmpty: false }
                && state.Decision == null && (waits.IsEmpty || waits.Any(wait => wait.PlayerId == state.ViewerId));
        return state.Decision == null && (state.DecisionOwnerId == state.ViewerId
            || state.DecisionOwnerId == null && state.Status == "resolving" && state.ActionClass != "simultaneous");
    }

    public static int ClientProgressOwner(GameSnapshot state) => state.MatchWaits?.FirstOrDefault(wait => !wait.Published)?.PlayerId ?? state.ViewerId;

    public static void Validate(GameSnapshot state)
    {
        if (state.MatchReadingWaits is { } readers && (readers.IsDefault || readers.Length > state.Players.Length
                || readers.Distinct().Count() != readers.Length
                || readers.Any(id => state.HumanPlayerIds?.Contains(id) != true
                    || !state.Players.Any(player => player.Id == id && !player.Eliminated))))
            throw new IOException("The engine published invalid human reading ownership.");
        if (state.MatchWaits is not { } waits) return;
        if (waits.IsDefault || waits.Length > state.Players.Length || waits.Any(wait => wait == null || wait.Generation <= 0
                || !state.Players.Any(player => player.Id == wait.PlayerId))
                || waits.Select(wait => wait.PlayerId).Distinct().Count() != waits.Length)
            throw new IOException("The engine published invalid match wait ownership.");
    }

    public static bool AwaitsHuman(GameSnapshot state) => state.DecisionOwnerId is { } owner
        && state.HumanPlayerIds?.Contains(owner) == true;
}

/// <summary>Pure monotonic-time reducer shared by the player session and AI host.
/// A repair does not renew the progress budget. Only new rules/decision evidence
/// or an explicitly human-owned/presentation wait establishes a new baseline.</summary>
public sealed record MatchProgressWatch(MatchProgressWitness? Witness, TimeSpan Started, bool RepairAttempted)
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(45);
    public static readonly MatchProgressWatch Empty = new(null, TimeSpan.Zero, false);
    public MatchProgressWatch Observe(MatchProgressWitness? witness, TimeSpan now) => witness == null ? Empty
        : Witness == witness ? this : new(witness, now, false);
    public bool Due(TimeSpan now) => Witness != null && now - Started >= Limit;
    public MatchProgressWatch Repair() => this with { RepairAttempted = true };
}
