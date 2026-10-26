using Mana.Contracts;

namespace Mana.Magic;

public enum PriorityPhase { Response, Decision, Payment, Resolving, Starting, Finished }

public sealed record PriorityStackEntry(int Position, string Id, string Name, bool Ability,
    int? ControllerId, string ControllerName, IReadOnlyList<StackTarget> Targets)
{
    public bool IsNext => Position == 1;
}

public sealed record PriorityView(PriorityPhase Phase, int? TurnOwnerId, string TurnOwnerName,
    int? ResponderId, string ResponderName, int? DecisionOwnerId, string DecisionOwnerName,
    bool IsLocalResponse, bool CanPass, string Heading, string Instruction, IReadOnlyList<PriorityStackEntry> Stack)
{
    public PriorityStackEntry? NextToResolve => Stack.FirstOrDefault();
}

// Current engine reports only. A response window is distinct from turn ownership,
// payment and resolution; no pass history or future responder is reconstructed.
public static class PriorityPresentation
{
    public static PriorityView From(GameSnapshot state)
    {
        var decision = state.Decision;
        bool localPriority = decision is { Payload: PriorityPayload };
        int? reportedResponder = state.PriorityPlayerId
            ?? (state.ActionClass == "priority" ? state.DecisionOwnerId : null);
        if (reportedResponder == null && state.ActionClass.Length == 0) {
            var legacy = state.Players.Where(player => player.Priority).Take(2).ToArray();
            if (legacy.Length == 1) reportedResponder = legacy[0].Id;
        }
        var phase = state.Status is "finished" or "error" || state.ActionClass is "finished" or "error" ? PriorityPhase.Finished
            : state.Status == "starting" || state.ActionClass == "starting" ? PriorityPhase.Starting
            : state.ActionClass == "resolving" ? PriorityPhase.Resolving
            : state.ActionClass == "mana" || decision?.Payload is PaymentPayload ? PriorityPhase.Payment
            : state.ActionClass.Length > 0 && state.ActionClass != "priority" ? PriorityPhase.Decision
            : decision != null && !localPriority ? PriorityPhase.Decision
            : state.ActionClass == "priority" || localPriority || state.ActionClass.Length == 0 && reportedResponder != null ? PriorityPhase.Response
            : state.DecisionOwnerId != null ? PriorityPhase.Decision : PriorityPhase.Resolving;
        int? responder = phase == PriorityPhase.Response ? reportedResponder ?? (localPriority ? state.ViewerId : null) : null;
        int? owner = state.DecisionOwnerId ?? (decision != null ? state.ViewerId : null);
        bool local = phase == PriorityPhase.Response && responder == state.ViewerId;
        bool canPass = decision?.Payload is PriorityPayload { Pass.Enabled: true } && phase == PriorityPhase.Response
            && (reportedResponder == null || reportedResponder == state.ViewerId)
            && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId);
        var stack = state.Stack.Select((item, index) => new PriorityStackEntry(index + 1, item.Id,
            string.IsNullOrWhiteSpace(item.Name) ? item.Ability ? "Ability" : "Spell" : item.Name,
            item.Ability, item.ControllerId, item.ControllerId == null ? "Controller not reported" : Name(state, item.ControllerId),
            Array.AsReadOnly(item.Targets.ToArray()))).ToArray();
        string actor = phase == PriorityPhase.Response ? Name(state, responder) : Name(state, owner);
        string heading = phase switch {
            PriorityPhase.Response => local ? "Your response" : responder == null ? "Response window" : actor + " is responding",
            PriorityPhase.Payment => owner == state.ViewerId ? "Pay the cost" : owner == null ? "Paying a cost" : actor + " is paying",
            PriorityPhase.Decision => owner == state.ViewerId ? "Make a choice" : owner == null ? "A choice is being made" : actor + " is choosing",
            PriorityPhase.Starting => "Preparing the table",
            PriorityPhase.Finished => "Game stopped",
            _ => "Resolving"
        };
        string instruction = phase switch {
            PriorityPhase.Response when stack.Length > 0 => "Next: " + stack[0].Name + ". "
                + (local ? "Respond or pass priority." : responder == null ? "Waiting for a response." : "Waiting for " + actor + "."),
            PriorityPhase.Response => local ? "Play a card or ability, or pass priority." : "Waiting for " + actor + ".",
            PriorityPhase.Payment when decision is { Context.Message.Length: > 0 } => decision.Context.Message,
            PriorityPhase.Payment => "The reported player is paying a cost.",
            PriorityPhase.Decision when decision is { Context.Message.Length: > 0 } => decision.Context.Message,
            PriorityPhase.Decision => "Waiting for the current choice.",
            PriorityPhase.Starting => "The game is preparing the table.",
            PriorityPhase.Finished => state.Result ?? state.Error ?? "The game has stopped.",
            _ => stack.Length > 0 ? "The game is processing " + stack[0].Name + "." : "The game is processing its current action."
        };
        return new(phase, state.ActivePlayerId, Name(state, state.ActivePlayerId), responder, Name(state, responder), owner,
            Name(state, owner), local, canPass, heading, instruction, Array.AsReadOnly(stack));
    }

    private static string Name(GameSnapshot state, int? id) => id == null ? "the table" : id == state.ViewerId ? "You"
        : state.Players.FirstOrDefault(player => player.Id == id)?.Name ?? "Player " + id;
}

public sealed record PriorityTransition(int? PreviousResponderId, int? ResponderId, PriorityPhase Phase,
    bool ResponderChanged, bool PhaseChanged, bool StackChanged, IReadOnlyList<string> AddedStackIds,
    IReadOnlyList<string> RemovedStackIds, string? NextStackId, double Started);

// Retains public opaque identities and presentation timing only. A disappearing
// item is not called resolved: that requires the engine's outcome event.
public sealed class PriorityTransitionFeedback
{
    private string? gameId;
    private long revision;
    private PriorityPhase phase;
    private int? responder;
    private string[] stack = [];
    private readonly HashSet<string> retiredGames = new(StringComparer.Ordinal);
    public PriorityTransition? Latest { get; private set; }

    public void Observe(GameSnapshot? before, GameSnapshot after, double now)
    {
        if (retiredGames.Contains(after.Id)) return;
        if (gameId == null) {
            Baseline(before?.Id == after.Id ? before! : after);
            if (before?.Id != after.Id) return;
        }
        if (gameId != after.Id) { retiredGames.Add(gameId!); Baseline(after); Latest = null; return; }
        if (revision > 0 && after.Revision <= revision || after.Revision < revision) return;
        var current = PriorityPresentation.From(after);
        var ids = current.Stack.Select(item => item.Id).ToArray();
        bool responseChanged = responder != current.ResponderId, phaseChanged = phase != current.Phase;
        bool stackChanged = !stack.SequenceEqual(ids, StringComparer.Ordinal);
        if (responseChanged || phaseChanged || stackChanged) {
            var previous = stack.ToHashSet(StringComparer.Ordinal);
            var next = ids.ToHashSet(StringComparer.Ordinal);
            Latest = new(responder, current.ResponderId, current.Phase, responseChanged, phaseChanged, stackChanged,
                Array.AsReadOnly(ids.Where(id => id.Length > 0 && !previous.Contains(id)).ToArray()),
                Array.AsReadOnly(stack.Where(id => id.Length > 0 && !next.Contains(id)).ToArray()),
                ids.FirstOrDefault() is { Length: > 0 } first ? first : null, now);
        }
        revision = after.Revision; responder = current.ResponderId; phase = current.Phase; stack = ids;
    }

    private void Baseline(GameSnapshot state)
    {
        var current = PriorityPresentation.From(state);
        gameId = state.Id; revision = state.Revision; phase = current.Phase; responder = current.ResponderId;
        stack = current.Stack.Select(item => item.Id).ToArray(); Latest = null;
    }
    public void Clear() { gameId = null; revision = 0; responder = null; stack = []; Latest = null; retiredGames.Clear(); }
}
