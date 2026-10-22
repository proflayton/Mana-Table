using Mana.Contracts;

namespace Mana.Magic;

public enum DecisionDockPauseReason { None, Manual, Held, PhaseStop, Inspection, Interaction, Overlay, Chat, CommandFailure, Recovery, Presentation, Preview }
public enum DecisionDockStatus { Priority, AutoPassing, Target, Payment, AbilityChoice, Selection, Attack, Block, Waiting, Resolving, Preparing, Submitted, Recovery, Finished, Watching }
public enum DecisionDockActorRole { None, Priority, Choosing, Paying, Attacking, Blocking }

// Local control facts must come from the same gate used by the update loop.
// QuietCanPass is its current engine-token check, not a catalog of legal actions.
public sealed record DecisionDockControlFacts
{
    public bool Automatic { get; init; } = true;
    public bool Held { get; init; }
    public string PhaseStopKey { get; init; } = "";
    public DecisionDockPauseReason PauseReason { get; init; }
    public bool QuietArmed { get; init; }
    public bool QuietCanPass { get; init; }
    public bool AutomaticAdvanceInFlight { get; init; }
    public bool CommandBusy { get; init; }
    // A background observation alone must not relabel or move a current action.
    public bool ObservationPending { get; init; }
}

public sealed record DecisionDockView(DecisionDockStatus Status, DecisionDockPauseReason PauseReason,
    string Headline, string Caption, string Instruction, DecisionDockActorRole ActorRole,
    int? ActorId, string ActorName, string SourceCardId, Card? SourceCard, int? SourceControllerId,
    string GameId, string DecisionId, string ActionLabel, PriorityStackEntry? Pending);

// Describes the current authorized snapshot, never a future resolution or a
// reason inferred from prompt prose/activity. Labels confer no command permission;
// controls still validate their original decision scope and legal-action gates.
public static class DecisionDockPresentation
{
    public static DecisionDockView From(GameSnapshot state, DecisionDockControlFacts controls)
    {
        var priority = PriorityPresentation.From(state);
        var reported = state.Decision;
        bool localOwner = priority.DecisionOwnerId == null || priority.DecisionOwnerId == state.ViewerId;
        var decision = localOwner ? reported : null;
        var source = decision == null ? null : InteractionCards.Source(state, decision);
        string sourceId = source == null ? decision?.SourceCardId ?? "" : CardViews.Identity(source);
        var pause = Pause(state, controls);

        DecisionDockView View(DecisionDockStatus status, string headline, string caption = "", string instruction = "",
            DecisionDockActorRole role = DecisionDockActorRole.None, int? actor = null, string action = "",
            bool includeSource = true, DecisionDockPauseReason? reason = null)
            => new(status, reason ?? pause, headline, caption, instruction, role, actor, Name(state, actor),
                includeSource ? sourceId : "", includeSource ? source : null,
                includeSource ? decision?.SourceControllerId : null, state.Id, decision?.Id ?? "", action, priority.NextToResolve);

        if (priority.Phase == PriorityPhase.Finished)
            return View(DecisionDockStatus.Finished, state.Status == "finished" ? "Game over" : "Game stopped",
                instruction: state.Result ?? state.Error ?? "", includeSource: false, reason: DecisionDockPauseReason.None);
        if (pause == DecisionDockPauseReason.Recovery)
            return View(DecisionDockStatus.Recovery, "Reconnect to the table", includeSource: false);
        if (state.Viewer?.Eliminated == true)
            return View(DecisionDockStatus.Watching, "Watching the table", "You have been eliminated",
                includeSource: false, reason: DecisionDockPauseReason.None);
        if (controls.CommandBusy && !controls.AutomaticAdvanceInFlight)
            return View(DecisionDockStatus.Submitted, "Action submitted", "Waiting for the table");
        if (priority.Phase == PriorityPhase.Starting)
            return View(DecisionDockStatus.Preparing, "Preparing the table", includeSource: false);
        if (priority.Phase == PriorityPhase.Resolving)
            return View(DecisionDockStatus.Resolving, "Resolving", includeSource: false);

        if (priority.Phase == PriorityPhase.Response) {
            if (!priority.IsLocalResponse)
                return View(DecisionDockStatus.Waiting, priority.ResponderId == null ? "Waiting for priority"
                    : priority.ResponderName + " has priority", ArmedCaption(controls, pause),
                    role: DecisionDockActorRole.Priority, actor: priority.ResponderId, includeSource: false);

            bool normalPass = controls.Automatic && decision is { Kind: "input", Intent: DecisionIntent.Priority, CanAutoPass: true };
            bool quietPass = controls.QuietArmed && controls.QuietCanPass && decision is { QuietYieldToken.Length: > 0 }
                && state.Stack.Length == 0 && state.ActivePlayerId != state.ViewerId;
            bool autoPassing = state.Status == "playing" && state.Turn > 0 && priority.CanPass
                && pause == DecisionDockPauseReason.None && (normalPass || quietPass);
            string caption = pause != DecisionDockPauseReason.None ? PauseCaption(state, controls, pause)
                : controls.QuietArmed && !autoPassing ? "Quiet pass armed" : "";
            return View(autoPassing ? DecisionDockStatus.AutoPassing : DecisionDockStatus.Priority,
                autoPassing ? quietPass ? "Passing for now" : "Auto passing" : "Your priority", caption,
                role: DecisionDockActorRole.Priority, actor: state.ViewerId,
                action: decision == null ? "" : TurnGuide.Confirm(state));
        }

        int? owner = priority.DecisionOwnerId;
        if (priority.Phase == PriorityPhase.Payment)
            return View(DecisionDockStatus.Payment, owner == state.ViewerId ? "Pay the cost"
                : owner == null ? "Paying a cost" : priority.DecisionOwnerName + " is paying",
                instruction: decision?.Message ?? "", role: DecisionDockActorRole.Paying, actor: owner,
                action: decision?.Ok ?? "");

        if (decision == null) {
            var role = state.ActionClass switch { "attack" => DecisionDockActorRole.Attacking,
                "block" => DecisionDockActorRole.Blocking, _ => DecisionDockActorRole.Choosing };
            string activity = role switch { DecisionDockActorRole.Attacking => " is choosing attackers",
                DecisionDockActorRole.Blocking => " is choosing blockers", _ => " is choosing" };
            return View(DecisionDockStatus.Waiting, owner == null ? "Waiting for a choice" : priority.DecisionOwnerName + activity,
                ArmedCaption(controls, pause), role: role, actor: owner, includeSource: false);
        }

        var status = decision.Intent switch {
            DecisionIntent.Attack => DecisionDockStatus.Attack,
            DecisionIntent.Block => DecisionDockStatus.Block,
            DecisionIntent.Selection when decision.InputType == "InputSelectTargets" => DecisionDockStatus.Target,
            _ when IsAbilityChoice(decision) => DecisionDockStatus.AbilityChoice,
            _ => DecisionDockStatus.Selection
        };
        string heading = status switch {
            DecisionDockStatus.Attack => "Choose attackers",
            DecisionDockStatus.Block => "Choose blockers",
            DecisionDockStatus.Target => TargetHeading(decision),
            DecisionDockStatus.AbilityChoice => "Choose an ability",
            _ when decision.Ordered => "Choose an order",
            _ when decision.Numeric => "Choose a number",
            _ => "Make a choice"
        };
        var actorRole = status switch { DecisionDockStatus.Attack => DecisionDockActorRole.Attacking,
            DecisionDockStatus.Block => DecisionDockActorRole.Blocking, _ => DecisionDockActorRole.Choosing };
        return View(status, heading, instruction: decision.Message, role: actorRole, actor: state.ViewerId,
            action: decision.Kind == "input" ? TurnGuide.Confirm(state) : decision.Ok);
    }

    private static DecisionDockPauseReason Pause(GameSnapshot state, DecisionDockControlFacts controls)
    {
        if (controls.PauseReason is DecisionDockPauseReason.Recovery or DecisionDockPauseReason.CommandFailure) return controls.PauseReason;
        if (controls.Held) return DecisionDockPauseReason.Held;
        if (state.ActivePlayerId == state.ViewerId && controls.PhaseStopKey.Length > 0 && controls.PhaseStopKey == state.PhaseKey)
            return DecisionDockPauseReason.PhaseStop;
        if (!controls.Automatic && !controls.QuietArmed) return DecisionDockPauseReason.Manual;
        // A stale saved-stop fact cannot pause a different phase or another seat.
        return controls.PauseReason == DecisionDockPauseReason.PhaseStop ? DecisionDockPauseReason.None : controls.PauseReason;
    }

    private static string ArmedCaption(DecisionDockControlFacts controls, DecisionDockPauseReason pause) => !controls.QuietArmed ? ""
        : pause == DecisionDockPauseReason.None ? "Quiet pass armed" : pause switch {
            DecisionDockPauseReason.Inspection => "Quiet pass paused while reading",
            DecisionDockPauseReason.CommandFailure => "Quiet pass paused after an error",
            _ => "Quiet pass paused"
        };

    private static string PauseCaption(GameSnapshot state, DecisionDockControlFacts controls, DecisionDockPauseReason reason) => reason switch {
        DecisionDockPauseReason.Manual => "Manual control",
        DecisionDockPauseReason.Held => "Holding this turn",
        DecisionDockPauseReason.PhaseStop => (TurnGuide.Stops.FirstOrDefault(stop => stop.Key == controls.PhaseStopKey).Label
            ?? state.Phase) + " stop",
        DecisionDockPauseReason.Inspection => "Paused while reading",
        DecisionDockPauseReason.Interaction => "Paused while interacting",
        DecisionDockPauseReason.Overlay => "Paused while viewing the table",
        DecisionDockPauseReason.Chat => "Paused while chatting",
        DecisionDockPauseReason.CommandFailure => "Paused after an error",
        DecisionDockPauseReason.Recovery => "Connection needs attention",
        DecisionDockPauseReason.Presentation => "Showing the last action",
        DecisionDockPauseReason.Preview => "Scripted preview",
        _ => "Paused"
    };

    private static bool IsAbilityChoice(Decision decision) => decision.Kind == "choice" && decision.Context == "playAbility"
        && !decision.Ordered && decision.Max == 1 && decision.Min is 0 or 1 && decision.Choices.Length > 0
        && decision.LibraryCards.Length == 0 && decision.PlayerChoices.Length == 0
        && decision.Choices.All(choice => choice.Card == null && choice.CardId.Length == 0 && choice.PlayerId == null);

    private static string TargetHeading(Decision decision) => decision.Max > 1
        ? decision.Min == decision.Max ? "Choose " + decision.Max + " targets"
            : decision.Min > 0 ? "Choose " + decision.Min + "–" + decision.Max + " targets" : "Choose up to " + decision.Max + " targets"
        : decision.Max == 1 && decision.Min == 0 ? "Choose up to 1 target" : "Choose a target";

    private static string Name(GameSnapshot state, int? id) => id == null ? "" : id == state.ViewerId ? "You"
        : state.Players.FirstOrDefault(player => player.Id == id)?.Name ?? "Player " + id;
}
