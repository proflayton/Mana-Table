using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class PriorityNotificationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        Policy(check);
        Arrival(profile, check);
        Occupied(profile, check);
        Mute(profile, check);
        Eligibility(profile, check);
    }

    private static GameSnapshot Response(long version = 1) => FourPlayerTableScenario.Create()[0].Snapshot with {
        Id = "priority-notification", PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
        StackInterruptionVersion = version,
        Decision = new("notification-prompt", new(), new PriorityPayload(new("Pass priority", true)))
    };

    private static void Policy(Action<bool, string> check)
    {
        var state = Response();
        var notice = new PriorityNotification();
        check(!notice.Update(state, true, true, 0) && !notice.Update(state, true, true, 1.999)
                && notice.Update(state, true, true, 2),
            "A stable human priority window receives one reminder only after two seconds of attention grace");
        var refreshed = state with { Revision = 900, Decision = state.Decision! with { Id = "replacement-capability" } };
        bool repeats = notice.Update(refreshed, true, true, 25);
        repeats |= notice.Update(refreshed with { Decision = new("payment", new(), new PaymentPayload(new("Pay", false), new("Cancel", true))) }, false, true, 26);
        repeats |= notice.Update(refreshed, true, true, 30);
        repeats |= notice.Update(refreshed with { Decision = null, ActionClass = "resolving" }, false, true, 31);
        repeats |= notice.Update(refreshed, true, true, 35);
        check(!repeats, "Fresh decision capabilities, payment and stack-drain re-entry do not repeat the same public response cohort");

        notice = new();
        notice.Update(state, true, true, 0); notice.Update(state, false, true, 1);
        check(!notice.Update(state, true, true, 3) && !notice.Update(state, true, true, 4.999)
                && notice.Update(state, true, true, 5),
            "A response window that closes before grace must wait a full new grace after returning");

        var rapid = state with { StackInterruptionVersion = 2 };
        check(!notice.Update(rapid, true, true, 6) && !notice.Update(rapid, true, true, 30)
                && !notice.Update(state with { StackInterruptionVersion = 3 }, true, true, 31)
                && notice.Update(state with { StackInterruptionVersion = 3 }, true, true, 33),
            "Cooldown consumes a rapid new cohort without queuing a late chime; a later distinct window can remind again");

        notice = new();
        check(!notice.Update(state, true, false, 0) && !notice.Update(state, true, true, 10),
            "Turning global sound back on does not replay a response first observed with sound off");
        notice.Update(rapid, false, true, 11); notice.ToggleMute(11);
        check(notice.IsMuted(310.999) && !notice.IsMuted(311)
                && !notice.Update(rapid, true, true, 12) && !notice.Update(rapid, true, true, 312),
            "Temporary mute lasts five minutes and consumes suppressed windows without an expiry backlog");
        notice.ToggleMute(313); notice.ToggleMute(314);
        check(!notice.IsMuted(314), "Toggling the muted bell restores alerts immediately");
        notice.ToggleMute(315);
        var anotherGame = state with { Id = "next-game" };
        check(!notice.Update(anotherGame, true, true, 316) && !notice.IsMuted(316)
                && notice.Update(anotherGame, true, true, 318),
            "A new game clears temporary mute, prior cohorts and cooldown while keeping the attention grace");
        var anotherViewer = anotherGame with { ViewerId = 1 };
        check(!notice.Update(anotherViewer, true, true, 319) && notice.Update(anotherViewer, true, true, 321),
            "Changing the viewer establishes an independent reminder scope");
    }

    private static void Arrival(string profile, Action<bool, string> check)
    {
        using var run = new NotificationRun(profile, "priority-chime-arrival");
        var before = run.View.Match;
        run.Tick(1.9);
        check(run.Chimes.Count == 0 && run.View.DecisionDock is { Status: DecisionDockStatus.Priority, Surface: DecisionDockSurface.Portrait },
            "The actual portrait response remains quiet during its initial attention grace");
        run.Tick(.11);
        check(run.Chimes is [{ Key: "your-priority", Volume: .24f }] && ReferenceEquals(before, run.View.Match)
                && run.Engine.Replies.Count == 0,
            "Actual waiting priority plays the quiet default chime without changing match state or submitting a command");
        run.Present(run.Engine.State);
        run.Tick(20);
        check(run.Chimes.Count == 1 && run.Engine.Replies.Count == 0,
            "Repolling the same waiting response under a new scoped decision does not replay its chime");
    }

    private static void Occupied(string profile, Action<bool, string> check)
    {
        foreach (string occupation in new[] { "settings", "reader", "pointer", "inactive" }) {
            using var run = new NotificationRun(profile, "priority-chime-" + occupation);
            Point? at = null;
            if (occupation == "settings") run.Click("settings");
            if (occupation is "reader" or "pointer") {
                string card = run.Frame.Cards.First(card => card.Zone == "Hand").Hit.Id;
                if (occupation == "reader") run.Click(card);
                else { at = run.At(card); run.Tick(.05, at, ButtonState.Pressed); }
            }
            run.Tick(2.1, at, occupation == "pointer" ? ButtonState.Pressed : ButtonState.Released, active: occupation != "inactive");
            bool preserved = occupation switch {
                "settings" => run.View.Overlay == "settings",
                "reader" => run.Frame.Cards.Any(card => card.Focused),
                "pointer" => run.AcceptedHit == null && !run.Frame.Cards.Any(card => card.Focused),
                _ => run.AcceptedHit == null
            };
            check(run.Chimes.Count == 1 && preserved && run.Engine.Replies.Count == 0,
                "A waiting response still reminds during " + occupation + " without interrupting that context or sending an action");
        }
    }

    private static void Mute(string profile, Action<bool, string> check)
    {
        using var run = new NotificationRun(profile, "priority-chime-mute");
        var alert = run.Frame.Hits.Single(hit => hit.Id == "priority-alerts");
        check(run.Frame.Hits.Where(hit => hit.Id is "auto" or "hold" || hit.Id.StartsWith("phase-stop:", StringComparison.Ordinal))
                .All(hit => Vector2.Distance(alert.Bounds.Center.ToVector2(), hit.Bounds.Center.ToVector2()) >= 30)
                && run.At("priority-alerts") == alert.Bounds.Center,
            "The portrait bell has its own reachable circular target clear of the existing phase and passing controls");
        run.Click("priority-alerts"); run.Tick(2.1, alert.Bounds.Center);
        check(run.Chimes.Count == 0 && run.PaintedText.Any(text => text.StartsWith("Unmute priority alerts", StringComparison.Ordinal))
                && PlayPreferences.Load(run.Profile).Sounds && run.Engine.Replies.Count == 0,
            "Clicking the actual bell mutes priority reminders for five minutes without changing global sound or engine state");
        run.Click("priority-alerts"); run.Tick(3);
        check(run.Chimes.Count == 0, "Unmuting the actual bell does not replay the response suppressed during mute");
        run.Present(run.Engine.State with { StackInterruptionVersion = 2 }); run.Tick(2.1);
        check(run.Chimes.Count == 1, "A fresh response after unmuting can chime after its own attention grace");
        run.Click("priority-alerts");
        run.Present(run.Engine.State with { StackInterruptionVersion = 3 }); run.Tick(301);
        check(run.Chimes.Count == 1, "Automatic mute expiry does not announce an old suppressed response");
        run.Present(run.Engine.State with { StackInterruptionVersion = 4 }); run.Tick(2.1);
        check(run.Chimes.Count == 2, "The bell automatically restores reminders for fresh windows after five minutes");
        run.Click("priority-alerts");
        run.Present(run.Engine.State with { Id = "priority-chime-next-game", StackInterruptionVersion = 1 }); run.Tick(2.1);
        check(run.Chimes.Count == 3 && PlayPreferences.Load(run.Profile).Sounds && run.Engine.Replies.Count == 0,
            "Starting another actual match clears temporary mute and cooldown without persisting either setting");
    }

    private static void Eligibility(string profile, Action<bool, string> check)
    {
        var state = Response();
        foreach (var invalid in new[] {
            state with { PriorityPlayerId = 1, DecisionOwnerId = 1, Decision = null },
            state with { DecisionOwnerId = 1 },
            state with { Decision = state.Decision! with { Payload = new PriorityPayload(new("Pass priority", false)) } },
            state with { ActionClass = "payment", Decision = new("payment", new(), new PaymentPayload(new("Pay", false), new("Cancel", true))) }
        }) {
            using var run = new NotificationRun(profile, "priority-chime-ineligible-" + invalid.ActionClass + "-" + invalid.DecisionOwnerId + "-" + invalid.Decision?.Payload.GetType().Name, invalid);
            run.Tick(2.1);
            check(run.Chimes.Count == 0 && run.Engine.Replies.Count == 0,
                "No priority chime is invented for nonlocal, disabled or payment-only current authority: " + invalid.DecisionOwnerId + "/" + invalid.Decision?.Payload.GetType().Name);
        }
        using (var silent = new NotificationRun(profile, "priority-chime-global-off", sounds: false)) {
            silent.Tick(3);
            check(silent.Chimes.Count == 0 && !silent.Frame.Hits.Any(hit => hit.Id == "priority-alerts"),
                "Global sound off suppresses actual priority playback and cannot be overridden by the temporary bell");
            silent.Click("settings"); silent.Click("sound"); silent.Click("close-overlay"); silent.Tick(3);
            check(silent.Chimes.Count == 0 && PlayPreferences.Load(silent.Profile).Sounds,
                "Enabling actual global sound does not replay an already-suppressed response");
            silent.Present(silent.Engine.State with { StackInterruptionVersion = 2 }); silent.Tick(2.1);
            check(silent.Chimes.Count == 1, "A new response can remind after actual global sound is enabled");
        }
        using var auto = new NotificationRun(profile, "priority-chime-auto", state with {
            Decision = state.Decision! with { Payload = new PriorityPayload(new("Pass priority", true), CanAutoPass: true) }
        });
        auto.View.SetAutomatic(true); auto.Tick();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (auto.View.Busy && deadline.Elapsed < TimeSpan.FromSeconds(5)) { auto.Tick(); Thread.Yield(); }
        auto.Tick(3);
        check(auto.Engine.Replies.SingleOrDefault() is AutoPassPriority && auto.Chimes.Count == 0,
            "The notification runs after actual automatic dispatch and stays quiet for a response the client already passes");
    }

    private sealed record Sound(string Key, float Volume);

    private sealed class NotificationRun : IDisposable
    {
        private readonly TableApplication app;
        private double seconds;
        internal string Profile { get; }
        internal InteractionScenarioEngine Engine { get; } = new();
        internal List<Sound> Chimes { get; } = [];
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;
        internal string? AcceptedHit => app.AcceptedHit;
        internal IEnumerable<string> PaintedText => Frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null }).OfType<string>();
        internal NotificationRun(string profile, string name, GameSnapshot? state = null, bool sounds = true)
        {
            Profile = Path.Combine(profile, name); Directory.CreateDirectory(Profile);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = sounds }.Save(Profile);
            Engine.Present(state ?? Response());
            app = new(TestTableServices.For(Engine), Profile, initialState: Engine.State, resources: new() {
                AudioOutput = new RecordingAudioOutput((key, volume) => { if (key == "your-priority") Chimes.Add(new(key, volume)); })
            }) { Automated = true };
            app.Initialize(); app.EnableLiveTableStudy();
            View.PresentLobby(new("local", "Priority notification fixture", null, false, null, true, "", null, [], []), true);
            Tick();
        }
        internal void Present(GameSnapshot state) { Engine.Present(state); View.Accept(Engine.State); Tick(); }
        internal void Tick(double elapsed = .05, Point? point = null, ButtonState left = ButtonState.Released, bool active = true)
        {
            seconds += elapsed; var at = point ?? new Point(20, 30);
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(elapsed)),
                new(at.X, at.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released), new(), new(1600, 900), inputActive: active);
            Frame = app.ReadPresentation();
            if (View.Error.Length > 0) throw new InvalidOperationException(View.Error);
        }
        internal Point At(string id)
        {
            var hit = Frame.Hits.Last(hit => hit.Id == id);
            if (View.HitAt(hit.Bounds.Center)?.Id == id) return hit.Bounds.Center;
            for (int y = Math.Max(0, hit.Bounds.Top + 2); y < Math.Min(900, hit.Bounds.Bottom - 2); y += 3)
                for (int x = Math.Max(0, hit.Bounds.Left + 2); x < Math.Min(1600, hit.Bounds.Right - 2); x += 3)
                    if (View.HitAt(new(x, y))?.Id == id) return new(x, y);
            throw new InvalidOperationException("Covered priority-notification control: " + id);
        }
        internal void Click(string id)
        {
            var at = At(id); Tick(point: at, left: ButtonState.Pressed); Tick(point: at);
            if (AcceptedHit != id) throw new InvalidOperationException("Priority-notification click was not accepted: " + id);
        }
        public void Dispose() => app.Dispose();
    }
}
