using Mana.Contracts;

namespace Mana.Magic;

public enum DecisionDockPauseReason { None, Manual, Held, PhaseStop, Inspection, Interaction, Overlay, Chat, CommandFailure, Recovery, Presentation, Preview }
public enum DecisionDockStatus { Priority, AutoPassing, Target, Payment, AbilityChoice, Selection, Attack, Block, Waiting, Resolving, Preparing, Recovery, Finished, Watching }
public enum DecisionDockActorRole { None, Priority, Choosing, Paying, Attacking, Blocking }
public enum DecisionDockSurface { None, Portrait, Workspace }
public enum DecisionRequiredSurface { None, Source, Targets, Table, Gallery, Value, Allocation, Combat }
[Flags]
public enum DecisionReading { None = 0, SourceInspection = 1, OpeningHandInspection = 2, HandSelectionInspection = 4, TableInspection = 8, RevealInspection = 16 }

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
    public bool ResponsePending { get; init; }
}

public sealed record DecisionDockView(DecisionDockStatus Status, DecisionDockPauseReason PauseReason,
    string Headline, string Caption, string Instruction, DecisionDockActorRole ActorRole,
    int? ActorId, string ActorName, string SourceCardId, Card? SourceCard, int? SourceControllerId,
    string GameId, string DecisionId, string ActionLabel, PriorityStackEntry? Pending)
{
    // A reported response window need not ask for attention. Transport activity
    // cannot introduce a new surface or replace an authoritative decision.
    public DecisionDockSurface Surface { get; init; } = DecisionDockSurface.Workspace;
    public DecisionRequiredSurface RequiredSurface { get; init; }
    public DecisionReading Reading { get; init; }
    public ChoiceSelection? Selection { get; init; }
    public DecisionPayload? Payload { get; init; }
}

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
        var source = decision == null ? null : CardViews.Source(state, decision);
        string sourceId = source == null ? decision?.Context.Source?.CardId ?? "" : CardViews.Identity(source);
        var pause = Pause(state, controls);
        bool normalPass = controls.Automatic && !UndoFacts.CanUndo(state)
            && decision is { Payload: PriorityPayload { CanAutoPass: true } };
        bool quietPass = controls.QuietArmed && controls.QuietCanPass && decision is { Payload: PriorityPayload { QuietYieldToken.Length: > 0 } }
            && state.Stack.Length == 0;
        bool routinePriority = state.Status == "playing" && state.Turn > 0 && priority.CanPass && (normalPass || quietPass);
        bool ambientPause = pause is DecisionDockPauseReason.None or DecisionDockPauseReason.Inspection
            or DecisionDockPauseReason.Interaction or DecisionDockPauseReason.Overlay or DecisionDockPauseReason.Chat
            or DecisionDockPauseReason.Presentation or DecisionDockPauseReason.Preview;
        var payload = pause != DecisionDockPauseReason.Recovery && state.Viewer?.Eliminated != true
            && priority.Phase is not (PriorityPhase.Starting or PriorityPhase.Finished or PriorityPhase.Resolving) ? decision?.Payload : null;
        var selection = payload switch { OptionsPayload options => options.Selection, OrderingPayload order => order.Selection,
            AbilityChoicePayload ability => ability.Selection, _ => null };
        bool direct = payload is OptionsPayload or OrderingPayload && TableChoices.IsDirect(state);
        var requiredSurface = payload switch {
            null or PriorityPayload => DecisionRequiredSurface.None,
            OpeningHandPayload or ConfirmationPayload or AbilityChoicePayload or PaymentPayload => DecisionRequiredSurface.Source,
            TargetSelectionPayload or EntitySelectionPayload => DecisionRequiredSurface.Targets,
            OptionsPayload { CounterPayment: not null } => DecisionRequiredSurface.Source,
            OptionsPayload { ManaPaymentContext: not null } => DecisionRequiredSurface.Source,
            OptionsPayload or OrderingPayload when direct => DecisionRequiredSurface.Table,
            OptionsPayload or OrderingPayload or RevealPayload => DecisionRequiredSurface.Gallery,
            NumberPayload or TextPayload => DecisionRequiredSurface.Value,
            AllocationPayload => DecisionRequiredSurface.Allocation,
            AttackPayload or BlockPayload => DecisionRequiredSurface.Combat,
            _ => throw new InvalidOperationException("The payload has no prepared decision surface.")
        };
        var reading = (payload != null && source is { FaceDown: false } ? DecisionReading.SourceInspection : DecisionReading.None)
            | (payload is OpeningHandPayload ? DecisionReading.OpeningHandInspection : DecisionReading.None)
            | (payload is TargetSelectionPayload or EntitySelectionPayload || direct ? DecisionReading.HandSelectionInspection : DecisionReading.None)
            | (decision?.Payload is null or PriorityPayload or PaymentPayload or AbilityChoicePayload or OptionsPayload { CounterPayment: not null } or OptionsPayload { ManaPaymentContext: not null }
                ? DecisionReading.TableInspection : DecisionReading.None)
            | (decision?.Payload is RevealPayload ? DecisionReading.RevealInspection : DecisionReading.None);

        DecisionDockSurface Surface(DecisionDockStatus status, DecisionDockActorRole role) => status switch {
            DecisionDockStatus.Priority when controls.QuietArmed => DecisionDockSurface.Portrait,
            DecisionDockStatus.Priority when routinePriority && ambientPause => DecisionDockSurface.None,
            DecisionDockStatus.Priority => DecisionDockSurface.Portrait,
            DecisionDockStatus.AutoPassing or DecisionDockStatus.Resolving => controls.QuietArmed ? DecisionDockSurface.Portrait : DecisionDockSurface.None,
            DecisionDockStatus.Waiting when role == DecisionDockActorRole.Priority && state.Stack.Length > 0 => DecisionDockSurface.Workspace,
            DecisionDockStatus.Waiting when role == DecisionDockActorRole.Priority => controls.QuietArmed ? DecisionDockSurface.Portrait : DecisionDockSurface.None,
            _ => DecisionDockSurface.Workspace
        };

        DecisionDockView View(DecisionDockStatus status, string headline, string caption = "", string instruction = "",
            DecisionDockActorRole role = DecisionDockActorRole.None, int? actor = null, string action = "",
            bool includeSource = true, DecisionDockPauseReason? reason = null)
            => new(status, reason ?? pause, headline, caption, instruction, role, actor, Name(state, actor),
                includeSource ? sourceId : "", includeSource ? source : null,
                includeSource ? decision?.Context.Source?.ControllerId : null, state.Id, decision?.Id ?? "", action, priority.NextToResolve) {
                    Surface = Surface(status, role), RequiredSurface = requiredSurface, Reading = reading, Selection = selection, Payload = payload
                };

        if (priority.Phase == PriorityPhase.Finished)
            return View(DecisionDockStatus.Finished, state.Status == "finished" ? "Game over" : "Game stopped",
                instruction: state.Result ?? state.Error ?? "", includeSource: false, reason: DecisionDockPauseReason.None);
        if (pause == DecisionDockPauseReason.Recovery)
            return View(DecisionDockStatus.Recovery, "Reconnect to the table", includeSource: false);
        if (state.Viewer?.Eliminated == true)
            return View(DecisionDockStatus.Watching, "Watching the table", "You have been eliminated",
                includeSource: false, reason: DecisionDockPauseReason.None);
        if (priority.Phase == PriorityPhase.Starting)
            return View(DecisionDockStatus.Preparing, "Preparing the table", includeSource: false);
        if (priority.Phase == PriorityPhase.Resolving)
            return View(DecisionDockStatus.Resolving, "Resolving", includeSource: false);

        if (priority.Phase == PriorityPhase.Response) {
            if (!priority.IsLocalResponse)
                return View(DecisionDockStatus.Waiting, priority.ResponderId == null ? "Waiting for priority"
                    : priority.ResponderName + " has priority", ArmedCaption(controls, pause),
                    role: DecisionDockActorRole.Priority, actor: priority.ResponderId, includeSource: false);

            bool autoPassing = routinePriority && pause == DecisionDockPauseReason.None;
            string caption = pause != DecisionDockPauseReason.None ? PauseCaption(state, controls, pause)
                : controls.ResponsePending && !autoPassing ? state.Stack.Length > 0 ? "Auto paused for your response" : "Your chance to act"
                : controls.QuietArmed && !autoPassing ? "Waiting for action" : "";
            return View(autoPassing ? DecisionDockStatus.AutoPassing : DecisionDockStatus.Priority,
                autoPassing ? quietPass ? "Waiting for action" : "Auto passing" : state.Stack.Length > 0 ? "Your response" : "Your priority", caption,
                role: DecisionDockActorRole.Priority, actor: state.ViewerId,
                action: decision == null ? "" : TurnGuide.Confirm(state));
        }

        int? owner = priority.DecisionOwnerId;
        if (priority.Phase == PriorityPhase.Payment)
            return View(DecisionDockStatus.Payment, owner == state.ViewerId ? "Pay the cost"
                : owner == null ? "Paying a cost" : priority.DecisionOwnerName + " is paying",
                instruction: decision?.Context.Message ?? "", role: DecisionDockActorRole.Paying, actor: owner,
                action: decision?.Payload is PaymentPayload payment ? payment.Pay.Label : "");

        if (decision == null) {
            var role = state.ActionClass switch { "attack" => DecisionDockActorRole.Attacking,
                "block" => DecisionDockActorRole.Blocking, _ => DecisionDockActorRole.Choosing };
            string activity = role switch { DecisionDockActorRole.Attacking => " is choosing attackers",
                DecisionDockActorRole.Blocking => " is choosing blockers", _ => " is choosing" };
            return View(DecisionDockStatus.Waiting, owner == null ? "Waiting for a choice" : priority.DecisionOwnerName + activity,
                ArmedCaption(controls, pause), role: role, actor: owner, includeSource: false);
        }

        var status = decision.Payload switch {
            AttackPayload => DecisionDockStatus.Attack,
            BlockPayload => DecisionDockStatus.Block,
            TargetSelectionPayload => DecisionDockStatus.Target,
            AbilityChoicePayload => DecisionDockStatus.AbilityChoice,
            _ => DecisionDockStatus.Selection
        };
        string heading = status switch {
            DecisionDockStatus.Attack => "Choose attackers",
            DecisionDockStatus.Block => "Choose blockers",
            DecisionDockStatus.Target => TargetHeading(((TargetSelectionPayload)decision.Payload).Range),
            DecisionDockStatus.AbilityChoice => "Choose an ability",
            _ when decision.Payload is OrderingPayload => "Choose an order",
            _ when decision.Payload is NumberPayload => "Choose a number",
            _ => "Make a choice"
        };
        var actorRole = status switch { DecisionDockStatus.Attack => DecisionDockActorRole.Attacking,
            DecisionDockStatus.Block => DecisionDockActorRole.Blocking, _ => DecisionDockActorRole.Choosing };
        return View(status, heading, instruction: decision.Context.Message, role: actorRole, actor: state.ViewerId,
            action: TurnGuide.Confirm(state));
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
        : pause == DecisionDockPauseReason.None ? "Waiting for action" : pause switch {
            DecisionDockPauseReason.Inspection => "Waiting paused while reading",
            DecisionDockPauseReason.CommandFailure => "Waiting paused after an error",
            _ => "Waiting paused"
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

    private static string TargetHeading(SelectionRange range) => range.Max > 1
        ? range.Min == range.Max ? "Choose " + range.Max + " targets"
            : range.Min > 0 ? "Choose " + range.Min + "–" + range.Max + " targets" : "Choose up to " + range.Max + " targets"
        : range.Max == 1 && range.Min == 0 ? "Choose up to 1 target" : "Choose a target";

    private static string Name(GameSnapshot state, int? id) => id == null ? "" : id == state.ViewerId ? "You"
        : state.Players.FirstOrDefault(player => player.Id == id)?.Name ?? "Player " + id;
}
