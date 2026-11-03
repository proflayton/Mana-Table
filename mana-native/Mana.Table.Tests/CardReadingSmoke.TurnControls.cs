using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class CardReadingSmoke
{
    private void QueueTurnControlChecks()
    {
        GameSnapshot? previous = null;
        PlayPreferences? preferences = null;
        Func<GameCommand, Task>? previousReply = null;
        bool automatic = false, held = false, study = false;
        int originalReplies = 0, quietReplies = 0;
        string game = "", decision = "";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaptureTurn(string name)
        {
            Capture(name);
            view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
            File.WriteAllText(Path.Combine(view.Profile, name + ".json"), System.Text.Json.JsonSerializer.Serialize(new {
                view.TurnProgress, view.DecisionDock, view.Automatic, view.Held, view.Busy,
                view.Match, Stops = PlayPreferences.Load(view.Profile).Stops,
                QuietReplies = Volatile.Read(ref quietReplies),
                Controls = view.Hits.Select(hit => new { hit.Id, hit.Scope, hit.Bounds }).ToArray()
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
        Step(0, () => {
            previous = scenario.State; previousReply = scenario.ReplyOverride;
            preferences = PlayPreferences.Load(view.Profile); automatic = view.Automatic; held = view.Held; study = view.TableStudyEnabled;
            originalReplies = scenario.Replies.Count;
            view.SetAutomatic(false); view.SetHeld(false); view.SetTableStudy(true); Move(new(10, 70));
            var state = PresentationSmoke.Example();
            Publish(state with { ActivePlayerId = state.ViewerId, PhaseKey = "MAIN1", Phase = "Main phase",
                PriorityPlayerId = state.ViewerId, DecisionOwnerId = state.ViewerId, ActionClass = "priority",
                Decision = state.Decision! with { Payload = ((PriorityPayload)(state.Decision!).Payload) with { CanAutoPass = false, QuietYieldToken = "" } },
                Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
                    Mana = ImmutableDictionary<string, int>.Empty.Add("W", 12).Add("U", 23).Add("B", 34)
                        .Add("R", 45).Add("G", 56).Add("C", 67)
                }).ToImmutableArray() });
            game = scenario.State.Id; decision = scenario.State.Decision!.Id;
        });
        Prepared(() => !view.ActivityOpen && view.TurnProgress is { IsOwnTurn: true } progress
            && progress.Markers.Single(marker => marker.Key == "MAIN1").Current
            && view.Hits.Any(hit => hit.Id == "phase-stop:END_OF_TURN"), 3, () => {
                Check(view.TextScale == 1.3f && view.Match!.Players.Length == 4
                    && view.TurnProgress!.Markers.Single(marker => marker.Key == "END_OF_TURN").Stop == false
                    && scenario.Replies.Count == originalReplies,
                    "The portrait journey opens the full four-seat own-main snapshot with six-color mana and an unsaved end stop at130%");
                Move(new(10, 70));
            }, "The full own-turn portrait scene must finish preparing before its first phase gesture");
        Step(1.6, () => { CaptureTurn("00-turn-own-main"); MoveHit("phase-stop:END_OF_TURN"); });
        Prepared(() => view.Frame!.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "Stop on my end step"), 2, () => {
            Check(scenario.Replies.Count == originalReplies, "The own-end hover hint sends no game command");
            CaptureTurn("00a-turn-end-hover");
        }, "Hovering the actual end marker must reveal its exact own-turn action"); Click();
        Prepared(() => view.TurnProgress!.Markers.Single(marker => marker.Key == "END_OF_TURN").Stop, 2, () => {
            Check(PlayPreferences.Load(view.Profile).Stops.SetEquals([.. preferences!.Stops, "END_OF_TURN"])
                && view.Match is { Decision: { } currentDecision } current && current.Id == game
                && currentDecision.Id == decision && scenario.Replies.Count == originalReplies,
                "The real end-marker gesture changes only the saved own-turn stop and leaves priority unanswered");
            Move(new(10, 70));
        }, "The accepted phase gesture must publish the saved end-step pause");
        Step(.08, () => {
            CaptureTurn("00b-turn-end-saved");
            Publish(scenario.State with { ActivePlayerId = 1, PhaseKey = "END_OF_TURN", Phase = "End step" });
        });
        Prepared(() => view.TurnProgress is { IsOwnTurn: false, PhaseLabel: "End step" } progress
            && progress.Markers.All(marker => !marker.Current), 2, () => {
                Check(view.TurnProgress!.Markers.Single(marker => marker.Key == "END_OF_TURN").Stop
                    && scenario.Replies.Count == originalReplies,
                    "The opponent's actual end step leaves the saved local stop visible without lighting a false local current phase");
                CaptureTurn("00c-turn-opponent-end");
                Publish(scenario.State with { Decision = scenario.State.Decision! with { Payload = ((PriorityPayload)(scenario.State.Decision!).Payload) with { QuietYieldToken = "portrait-quiet-window" } } });
            }, "An opponent's end step must retain local control ownership before quiet passing");
        Prepared(() => view.Hits.Any(hit => hit.Id == "quiet-priority-arm"), 2, () => {
            CompleteActionLabelChecks.Run(view.Frame!, "quiet-priority-arm", "Auto pass", Check);
            CompleteActionLabelChecks.Run(view.Frame!, "auto", "Auto off", Check);
            CaptureTurn("00d-turn-quiet-offered");
            scenario.ReplyOverride = async command => {
                if (command is not PassForNow { YieldToken: "portrait-quiet-window" })
                    throw new InvalidOperationException("Only the explicitly armed current quiet token belongs to this capture.");
                int number = Interlocked.Increment(ref quietReplies);
                if (number == 1) { started.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                else if (number == 2) { resumed.TrySetResult(); await resumeRelease.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                else throw new InvalidOperationException("The visible Auto journey repeated an unowned priority command.");
            };
            MoveHit("quiet-priority-arm");
        }, "The current opponent-turn token must offer the portrait-side bounded pass"); Click();
        Prepared(() => started.Task.IsCompleted && view.Busy && view.Hits.Any(hit => hit.Id == "quiet-priority-stop"), 3, () => {
            Check(Volatile.Read(ref quietReplies) == 1 && scenario.Replies.Count == originalReplies
                    && view.Automatic && !view.Held && PlayPreferences.Load(view.Profile).Automatic,
                "Physical Auto pass enables saved Auto and submits exactly one scoped pass while the controlled provider is pending");
            CompleteActionLabelChecks.Run(view.Frame!, "auto", "Auto on", Check);
            CaptureTurn("00e-turn-take-control"); MoveHit("quiet-priority-stop");
        }, "The armed quiet state must keep Take control reachable during its in-flight pass"); Click();
        Step(.08, () => {
            Check(view.Hits.All(hit => hit.Id != "quiet-priority-stop"),
                "Take control cancels the local continuation before the in-flight result returns");
            CaptureTurn("00f-turn-control-taken");
            // The completed first response returns a new actual prompt before
            // Resume can answer it; submitted-decision memory must not replay.
            Publish(scenario.State); release.TrySetResult();
        });
        Prepared(() => !view.Busy && view.Hits.Any(hit => hit.Id == "quiet-priority-arm"), 3, () => {
            Check(Volatile.Read(ref quietReplies) == 1 && scenario.Replies.Count == originalReplies && view.Error.Length == 0,
                "The submitted quiet pass reconciles once after Take control without choosing a card or submitting another pass");
            Check(view.Automatic && view.Held && PlayPreferences.Load(view.Profile).Automatic,
                "Take control leaves the saved Auto mode on and visibly held for this turn");
            CompleteActionLabelChecks.Run(view.Frame!, "quiet-priority-arm", "Resume Auto", Check);
            CompleteActionLabelChecks.Run(view.Frame!, "auto", "Auto paused", Check);
            CaptureTurn("00g-turn-resume-offered"); MoveHit("quiet-priority-arm");
        }, "The stopped current response must expose the named Resume Auto action"); Click();
        Prepared(() => resumed.Task.IsCompleted && view.Busy && view.Hits.Any(hit => hit.Id == "quiet-priority-stop"), 3, () => {
            Check(Volatile.Read(ref quietReplies) == 2 && scenario.Replies.Count == originalReplies
                    && view.Automatic && !view.Held && PlayPreferences.Load(view.Profile).Automatic,
                "Physical Resume Auto clears Hold without disabling Auto and submits only one fresh scoped quiet answer");
            CompleteActionLabelChecks.Run(view.Frame!, "auto", "Auto on", Check);
            CaptureTurn("00h-turn-resumed"); MoveHit("quiet-priority-stop");
        }, "Resume Auto must reach the held current-token RPC and keep Take control reachable"); Click();
        Step(.08, () => { resumeRelease.TrySetResult(); });
        Prepared(() => !view.Busy, 3, () => {
            Check(Volatile.Read(ref quietReplies) == 2 && view.Automatic && view.Held && view.Error.Length == 0,
                "Taking control after Resume reconciles the second answer without replaying either response");
            scenario.ReplyOverride = previousReply; MoveHit("phase-stop:END_OF_TURN");
        }, "The controlled quiet reply must reconcile before restoring the original card-state journey"); Click();
        Prepared(() => !view.TurnProgress!.Markers.Single(marker => marker.Key == "END_OF_TURN").Stop, 2, () => {
            Check(PlayPreferences.Load(view.Profile).Stops.SetEquals(preferences!.Stops),
                "The capture journey removes only its added stop before the remaining card-state checks");
            release.TrySetResult(); resumeRelease.TrySetResult(); scenario.ReplyOverride = previousReply;
            Publish(previous!); view.SetAutomatic(automatic); view.SetHeld(held); view.SetTableStudy(study);
            preferences!.Save(view.Profile); Move(new(10, 70));
        }, "Removing the authored end stop must restore its original preference set");
    }
}
