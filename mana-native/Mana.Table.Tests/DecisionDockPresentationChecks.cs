using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal static class DecisionDockPresentationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var example = PresentationSmoke.Example();
        var priority = example with {
            Id = "decision-dock", ActivePlayerId = 2, PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            Decision = new Decision("priority-a", new DecisionContext(), new PriorityPayload(new DecisionReply("OK", true), null, CanAutoPass: true, QuietYieldToken: "", Abilities: []))
        };
        var auto = new DecisionDockControlFacts();
        var ready = DecisionDockPresentation.From(priority, auto);
        var inspecting = DecisionDockPresentation.From(priority, auto with { PauseReason = DecisionDockPauseReason.Inspection });
        var resumed = DecisionDockPresentation.From(priority, auto);
        check(ready is { Status: DecisionDockStatus.AutoPassing, Surface: DecisionDockSurface.None } && inspecting is {
                Status: DecisionDockStatus.Priority, Headline: "Your priority", Caption: "Paused while reading",
                PauseReason: DecisionDockPauseReason.Inspection, Surface: DecisionDockSurface.None } && resumed == ready,
            "Reading pauses the same authorized auto window without opening an attention surface, and closing the reader restores its permission");

        var blockers = new[] { DecisionDockPauseReason.Interaction, DecisionDockPauseReason.Overlay, DecisionDockPauseReason.Chat,
            DecisionDockPauseReason.CommandFailure, DecisionDockPauseReason.Presentation };
        check(blockers.All(reason => DecisionDockPresentation.From(priority, auto with {
                PauseReason = reason }) is {
                    Status: DecisionDockStatus.Priority } paused && paused.PauseReason == reason && paused.Caption.Length > 0),
            "Every explicit update blocker suppresses automatic permission while retaining its actual pause reason");
        check(DecisionDockPresentation.From(priority, auto with { PauseReason = DecisionDockPauseReason.Preview }) is {
                Status: DecisionDockStatus.Priority, PauseReason: DecisionDockPauseReason.Preview, Caption: "Scripted preview" },
            "An authored read-only preview names its local pause instead of claiming the game is automatically passing");

        check(DecisionDockPresentation.From(priority with { Revision = priority.Revision + 1 }, auto) == ready
            && DecisionDockPresentation.From(priority, auto with { PauseReason = DecisionDockPauseReason.Presentation }).Surface == DecisionDockSurface.None,
            "A fresh revision with unchanged decision facts and a presentation beat never introduce another attention surface");

        var ownMain = priority with { ActivePlayerId = 0, PhaseKey = "MAIN1" };
        var stop = auto with { PhaseStopKey = "MAIN1" };
        check(DecisionDockPresentation.From(ownMain, stop) is { Headline: "Your priority", Caption: "Main 1 stop", PauseReason: DecisionDockPauseReason.PhaseStop, Surface: DecisionDockSurface.Portrait }
            && DecisionDockPresentation.From(priority, stop).Status == DecisionDockStatus.AutoPassing
            && DecisionDockPresentation.From(ownMain with { PhaseKey = "MAIN2" }, stop).Status == DecisionDockStatus.AutoPassing
            && DecisionDockPresentation.From(ownMain with { PhaseKey = "COMBAT_DAMAGE" }, auto with { PhaseStopKey = "COMBAT_BEGIN" }).Status == DecisionDockStatus.AutoPassing,
            "A named phase stop applies only to its exact phase on the viewer's turn, not another seat or all combat steps");
        check(DecisionDockPresentation.From(priority, auto with { Automatic = false }) is { Caption: "Manual control", PauseReason: DecisionDockPauseReason.Manual, Surface: DecisionDockSurface.Portrait }
            && DecisionDockPresentation.From(priority, auto with { Held = true }) is { Caption: "Holding this turn", PauseReason: DecisionDockPauseReason.Held, Surface: DecisionDockSurface.Portrait },
            "Manual control and a held turn identify the actual local reason for retaining priority");

        CheckTurnProgress(ownMain, check);

        var optional = priority with { Decision = priority.Decision! with { Context = priority.Decision!.Context with { Message = "Arid Mesa could be activated. This text does not explain the engine's conservative permission." }, Payload = ((PriorityPayload)(priority.Decision!).Payload) with { CanAutoPass = false, Abilities = [new("mesa", "mesa-key", "Arid Mesa", "Search your library.", false)] } } };
        var attention = DecisionDockPresentation.From(optional, auto);
        var withoutAbilities = DecisionDockPresentation.From(optional with { Decision = optional.Decision! with {
            Payload = ((PriorityPayload)optional.Decision.Payload) with { Abilities = [] }
        } }, auto);
        check(attention is { Status: DecisionDockStatus.Priority, Headline: "Your priority", Caption: "", Instruction: "", Surface: DecisionDockSurface.Portrait }
            && withoutAbilities with { Payload = attention.Payload } == attention
            && attention.Payload is PriorityPayload { Abilities.Length: 1 }
            && withoutAbilities.Payload is PriorityPayload { Abilities.IsEmpty: true }
            && DecisionDockPresentation.From(priority with { Decision = priority.Decision! with { Payload = ((PriorityPayload)priority.Decision.Payload) with { Pass = new("OK", false) } } }, auto).Status == DecisionDockStatus.Priority
            && DecisionDockPresentation.From(priority with { PriorityPlayerId = 1 }, auto).Status == DecisionDockStatus.Waiting,
            "Conservative engine permission, disabled input and a different reported responder never invent a card-specific pause reason or an auto action");

        var quiet = auto with { Automatic = false, QuietArmed = true, QuietCanPass = true };
        var quietWindow = optional with { Decision = optional.Decision! with { Payload = ((PriorityPayload)(optional.Decision!).Payload) with { QuietYieldToken = "engine-owned-permission" } } };
        check(DecisionDockPresentation.From(quietWindow, quiet) is { Status: DecisionDockStatus.AutoPassing, Surface: DecisionDockSurface.Portrait }
            && DecisionDockPresentation.From(quietWindow, quiet with { PauseReason = DecisionDockPauseReason.Inspection }) is {
                Status: DecisionDockStatus.Priority, Caption: "Paused while reading", Surface: DecisionDockSurface.Portrait }
            && DecisionDockPresentation.From(quietWindow, quiet with { QuietCanPass = false }).Status == DecisionDockStatus.Priority
            && DecisionDockPresentation.From(optional, quiet).Status == DecisionDockStatus.Priority,
            "An explicit quiet pass needs its current engine-token permission and pauses for reading without turning ordinary legal responses into auto permission");

        var namedPlayers = priority.Players.Select(player => player.Id == 1 ? player with { Name = "Mira, Weaver of Very Long Names" } : player).ToArray();
        var other = priority with { Players = namedPlayers.ToImmutableArray(), Decision = null, PriorityPlayerId = 2, DecisionOwnerId = 1, ActionClass = "mana" };
        var payment = DecisionDockPresentation.From(other, auto);
        var choosing = DecisionDockPresentation.From(other with { ActionClass = "choice" }, auto);
        check(payment is { Status: DecisionDockStatus.Payment, ActorRole: DecisionDockActorRole.Paying, ActorId: 1, Surface: DecisionDockSurface.Workspace,
                Headline: "Mira, Weaver of Very Long Names is paying", Instruction: "", SourceCard: null, DecisionId: "", ActionLabel: "" }
            && choosing is { Status: DecisionDockStatus.Waiting, ActorRole: DecisionDockActorRole.Choosing, ActorId: 1, Surface: DecisionDockSurface.Workspace,
                Headline: "Mira, Weaver of Very Long Names is choosing", Instruction: "" },
            "Another player's public paying or choosing role stays distinct from resolving and exposes neither a private prompt nor a local action");
        check(DecisionDockPresentation.From(other with { ActionClass = "attack" }, auto).ActorRole == DecisionDockActorRole.Attacking
            && DecisionDockPresentation.From(other with { ActionClass = "block" }, auto).ActorRole == DecisionDockActorRole.Blocking
            && DecisionDockPresentation.From(other with { ActionClass = "priority", PriorityPlayerId = 3, DecisionOwnerId = 3 }, quiet) is {
                Status: DecisionDockStatus.Waiting, ActorId: 3, ActorRole: DecisionDockActorRole.Priority, Caption: "Quiet pass armed", Surface: DecisionDockSurface.Portrait }
            && DecisionDockPresentation.From(other with { ActionClass = "priority", PriorityPlayerId = 3, DecisionOwnerId = 3 }, auto).Surface == DecisionDockSurface.None,
            "Public combat choices and the reported responder keep their own roles while an armed quiet pass waits for another player");

        var currentSource = new Card { VisualId = "source-occurrence", Name = "Nicanzil, Current Conductor", Power = 7, Toughness = 8 };
        var sourcePlayers = priority.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Count = 1, Cards = [currentSource] }).ToImmutableArray()
        }).ToArray();
        var target = priority with { Players = sourcePlayers.ToImmutableArray(), ActionClass = "selection", Decision = new Decision("target-after-priority", new DecisionContext(Message: "Select a legal target reported by the engine.", Source: new DecisionSource(CardId: currentSource.VisualId, Card: currentSource with { Power = 2, Toughness = 3 }, ControllerId: 0)), new TargetSelectionPayload(new DecisionReply("Confirm target", false), new DecisionReply("Cancel", false), new SelectionRange(1, 1), [], null, null)) };
        var targeting = DecisionDockPresentation.From(target, quiet);
        check(targeting is { Status: DecisionDockStatus.Target, Headline: "Choose a target", ActorId: 0, Surface: DecisionDockSurface.Workspace,
                GameId: "decision-dock", DecisionId: "target-after-priority", SourceCardId: "source-occurrence", SourceControllerId: 0,
                ActionLabel: "Confirm target", SourceCard: { Name: "Nicanzil, Current Conductor", Power: 7 } }
            && ReferenceEquals(targeting.SourceCard, currentSource) && !((TargetSelectionPayload)target.Decision!.Payload).Complete.Enabled,
            "The priority-to-target transition binds the new exact decision and current disclosed source without changing disabled confirmation or current stats");
        check(DecisionDockPresentation.From(target with { DecisionOwnerId = 1 }, auto) is {
                Status: DecisionDockStatus.Waiting, SourceCard: null, Instruction: "", DecisionId: "", ActionLabel: "" },
            "A different reported decision owner prevents a stale local source or private instruction from becoming that player's choice");

        var unknown = target with { Decision = new Decision("unfamiliar-required", new DecisionContext(Message: "Choose a target-looking option, then place the remaining objects in a special order.\nThe engine owns these unfamiliar constraints."), new ConfirmationPayload(new DecisionReply("Confirm", false), null, null)) };
        var fallback = DecisionDockPresentation.From(unknown, auto);
        check(fallback is { Status: DecisionDockStatus.Selection, Headline: "Make a choice", Caption: "", SourceCard: null }
            && fallback.Instruction == unknown.Decision!.Context.Message && fallback.DecisionId == "unfamiliar-required"
            && !fallback.Headline.Contains("target", StringComparison.OrdinalIgnoreCase),
            "An unfamiliar required input retains its exact engine instruction separately from the concise heading and never infers a target flow from prose");

        GameSnapshot Range(int min, int max) => target with { Decision = target.Decision! with { Payload = ((TargetSelectionPayload)target.Decision.Payload) with { Range = new(min, max) } } };
        check(DecisionDockPresentation.From(Range(2, 2), auto).Headline == "Choose 2 targets"
            && DecisionDockPresentation.From(Range(1, 3), auto).Headline == "Choose 1–3 targets"
            && DecisionDockPresentation.From(Range(0, 1), auto).Headline == "Choose up to 1 target",
            "The task preserves exact and optional target counts from the engine without repeating or parsing its effect prose");

        var ability = target with { ActionClass = "choice", Decision = target.Decision! with {
            Id = "choose-source-ability", Payload = new AbilityChoicePayload(new([new(4, "Pay the reported cost: do the first action.", null), new(9, "Do the second action.", null)], new(1, 1))) } };
        check(DecisionDockPresentation.From(ability, quiet) is { Status: DecisionDockStatus.AbilityChoice, Headline: "Choose an ability", DecisionId: "choose-source-ability", Surface: DecisionDockSurface.Workspace }
            && DecisionDockPresentation.From(target with { Decision = target.Decision! with { Payload = new PaymentPayload(new("Pay", true), new("Cancel", false)) }, ActionClass = "mana" }, auto) is {
                Status: DecisionDockStatus.Payment, Headline: "Pay the cost", ActorRole: DecisionDockActorRole.Paying, ActorId: 0, Surface: DecisionDockSurface.Workspace },
            "Source ability choice and subsequent payment remain required typed steps even when quiet pass intent was armed");

        var pendingCard = new Card { VisualId = "public-pending-source", Name = "Toothy, Imaginary Friend" };
        var pending = new StackItem("Toothy, Imaginary Friend — draw ability", "Public effect", true, pendingCard) {
            Id = "public-ability", ControllerId = 2 };
        var otherPending = priority with { Players = namedPlayers.ToImmutableArray(), Decision = null,
            DecisionOwnerId = 1, PriorityPlayerId = 1, Stack = [pending] };
        check(new[] { auto, quiet }.All(controls => DecisionDockPresentation.From(otherPending, controls) is {
                Status: DecisionDockStatus.Waiting, ActorRole: DecisionDockActorRole.Priority, ActorId: 1,
                ActorName: "Mira, Weaver of Very Long Names", Headline: "Mira, Weaver of Very Long Names has priority",
                Surface: DecisionDockSurface.Workspace, Pending.Id: "public-ability", Instruction: "",
                DecisionId: "", ActionLabel: "", SourceCard: null, SourceCardId: "" }),
            "Another responder beside a pending effect keeps their complete name and public stack context visible without exposing a private prompt or local action, even if quiet intent was armed");
        var processing = target with { Decision = null, DecisionOwnerId = null, PriorityPlayerId = null, ActionClass = "resolving", Stack = [pending],
            Activity = [new(1, 1, "MAIN1", "changed", 1, "Unrelated nearby activity", "unrelated", "Unrelated card")] };
        var processingView = DecisionDockPresentation.From(processing, auto);
        check(processingView is { Status: DecisionDockStatus.Resolving, SourceCard: null, SourceCardId: "", DecisionId: "", ActorId: null, Surface: DecisionDockSurface.None }
            && processingView.Pending is { Id: "public-ability", Name: "Toothy, Imaginary Friend — draw ability", ControllerId: 2 }
            && DecisionDockPresentation.From(processing with { Id = "replacement-game", Stack = [], Activity = [] }, auto) is {
                GameId: "replacement-game", SourceCard: null, Pending: null },
            "Leaving a decision drops its source and scope immediately; only the current public stack supplies pending context, never adjacent activity or a previous game");

        check(DecisionDockPresentation.From(priority with { ActionClass = "choice" }, auto).Status == DecisionDockStatus.Selection
            && DecisionDockPresentation.From(priority, auto with { PauseReason = DecisionDockPauseReason.Recovery }) is { Status: DecisionDockStatus.Recovery, Surface: DecisionDockSurface.Workspace }
            && DecisionDockPresentation.From(priority with { Status = "finished", Result = "Game finished" }, auto) is { Status: DecisionDockStatus.Finished, Surface: DecisionDockSurface.Workspace },
            "A reported required choice, recovery or terminal snapshot takes precedence over stale automatic permission");
    }

    private static void CheckTurnProgress(GameSnapshot state, Action<bool, string> check)
    {
        var stops = new HashSet<string> { "COMBAT_BEGIN", "END_OF_TURN" };
        var end = TurnProgressPresentation.From(state with { PhaseKey = "END_OF_TURN" }, stops);
        check(end.IsOwnTurn && end.ActivePlayerId == state.ViewerId && end.PhaseLabel == "End step"
                && end.Markers.Select(marker => marker.Key).SequenceEqual(TurnGuide.Stops.Select(stop => stop.Key))
                && end.Markers.Single(marker => marker.Current) is {
                    Key: "END_OF_TURN", Label: "End step", Stop: true, Hint: "Remove my end-step stop" }
                && TurnProgressPresentation.From(state, new HashSet<string>()).Markers.Single(marker => marker.Key == "END_OF_TURN")
                    is { Stop: false, Hint: "Stop on my end step" },
            "Own-turn progress retains the exact six saved stop keys and explains adding or removing the end-step stop in full");

        var opponent = TurnProgressPresentation.From(state with { ActivePlayerId = 2, PhaseKey = "COMBAT_DECLARE_BLOCKERS" }, stops);
        check(!opponent.IsOwnTurn && opponent.ActivePlayerId == 2 && opponent.PhaseLabel == "Declare blockers"
                && opponent.Markers.All(marker => !marker.Current)
                && opponent.Markers.Where(marker => marker.Stop).Select(marker => marker.Key).ToHashSet().SetEquals(stops),
            "Another player's exact combat step never highlights the viewer's planned stop markers or changes their saved choices");

        (string Key, string Label)[] combat = [("COMBAT_BEGIN", "Beginning of combat"), ("COMBAT_DECLARE_ATTACKERS", "Declare attackers"),
            ("COMBAT_DECLARE_BLOCKERS", "Declare blockers"), ("COMBAT_FIRST_STRIKE_DAMAGE", "First-strike damage"),
            ("COMBAT_DAMAGE", "Combat damage"), ("COMBAT_END", "End of combat")];
        check(combat.All(step => TurnProgressPresentation.From(state with { PhaseKey = step.Key }, stops) is { } view
                && view.PhaseLabel == step.Label && view.Markers.Single(marker => marker.Current) is {
                    Key: "COMBAT_BEGIN", Hint: "Remove my beginning-of-combat stop" })
                && TurnGuide.CanAutoPass(state with { PhaseKey = "COMBAT_DAMAGE" }, true, false, stops, false),
            "Known combat substeps share progress while retaining their exact step labels and the existing beginning-only stop policy");

        var untap = TurnProgressPresentation.From(state with { PhaseKey = "UNTAP" }, stops);
        var cleanup = TurnProgressPresentation.From(state with { PhaseKey = "CLEANUP" }, stops);
        check(untap.PhaseLabel == "Untap step" && untap.Markers.Single(marker => marker.Current).Key == "UPKEEP"
                && cleanup.PhaseLabel == "Cleanup" && cleanup.Markers.Single(marker => marker.Current).Key == "END_OF_TURN",
            "Untap and cleanup occupy their progress groups without relabeling the authoritative current step");

        var unknown = TurnProgressPresentation.From(state with { PhaseKey = "COMBAT_FUTURE_STEP", Phase = "A newly supported combat window" }, stops);
        var noActor = TurnProgressPresentation.From(state with { ActivePlayerId = null, PhaseKey = "MAIN1" }, stops);
        check(unknown.PhaseLabel == "A newly supported combat window" && unknown.Markers.All(marker => !marker.Current)
                && !noActor.IsOwnTurn && noActor.ActivePlayerId == null && noActor.Markers.All(marker => !marker.Current),
            "An unfamiliar phase preserves its reported label and a missing active player never inherits a fabricated current marker");
    }
}
