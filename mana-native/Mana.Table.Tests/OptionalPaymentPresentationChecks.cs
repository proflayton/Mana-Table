using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// These are authored disclosed inputs through the production session and pointer
// paths. Real Forge checks independently establish payment and affordability.
internal static class OptionalPaymentPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            using (var run = new Journey(profile, "decline-" + viewport.X, viewport)) {
                check(run.Has("optional-payment-ask") && run.Has("optional-payment-auto-pay") && run.Has("optional-payment-auto-decline")
                        && run.View.CurrentOptionalPayment?.Mode == OptionalPaymentMode.Ask && run.Port.Commands.IsEmpty,
                    "The actual optional source bill starts in Ask with separate current one-shot and saved ability answers at " + viewport);
                foreach (var (id, label) in new[] { ("optional-payment-ask", "Ask"), ("optional-payment-auto-pay", "Auto pay"),
                    ("optional-payment-auto-decline", "Auto decline"), ("mana-auto", "Pay once"), ("mana-cancel", "Decline once") })
                    CompleteActionLabelChecks.Run(run.Run.Frame, id, label, check);
                run.Capture("ask");
                run.Click("optional-payment-auto-decline");
                check(run.View.OptionalPaymentPolicies is [{ Mode: OptionalPaymentMode.AutoDecline, SourceCardId: "idol" }]
                        && run.View.FiniteResolution == null,
                    "Auto decline belongs to the exact physical source ability and creates no queue yield at " + viewport);
                run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.Port.Commands.Count == 1, "first optional decline");
                run.Capture("declined-priority");
                for (int tick = 0; tick < 90; tick++) run.Tick();
                check(run.Port.Commands.Single() is CancelPayment && run.View.Match?.Decision?.Payload is PriorityPayload
                        && run.View.OptionalPaymentPolicies.Length == 1 && run.Has("optional-payment-source:idol"),
                    "A saved negative choice answers once and stays visible on its source without passing the next priority at " + viewport);
                run.Click("optional-payment-source:idol");
                run.Until(() => run.Run.Frame.Hits.Any(hit => hit.Id.StartsWith("optional-payment-reset:", StringComparison.Ordinal)), "retained source policy reader");
                check(run.Text.Contains("Auto decline") && run.Text.Contains("Ask instead") && run.View.OptionalPaymentsPaused,
                    "Reading the same permanent after the offer ends shows its saved terms and pauses later automatic answers at " + viewport);
                run.Capture("source-policy-reader");
                run.Click(run.Run.Frame.Hits.Single(hit => hit.Id.StartsWith("optional-payment-reset:", StringComparison.Ordinal)).Id);
                check(run.View.OptionalPaymentPolicies.IsEmpty && run.Port.Commands.Count == 1,
                    "Ask instead clears the source preference without requiring another optional prompt or sending a game command at " + viewport);
            }

            using (var run = new Journey(profile, "pay-" + viewport.X, viewport)) {
                run.Click("optional-payment-auto-pay"); run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.Port.Commands.Count == 1, "current safe auto payment");
                check(run.Port.Commands.Single() is AutoPayOptionalPayment { OfferId: "idol-offer-0" }
                        && run.View.OptionalPaymentPolicies is [{ Mode: OptionalPaymentMode.AutoPay }]
                        && run.View.Match?.Decision?.Payload is PriorityPayload,
                    "Auto pay uses the engine-certified current safe-step command and never grants priority yield at " + viewport);
                run.Observe(Checkpoint(1, 10, amount: 2)); run.Move(new(20, 60));
                for (int tick = 0; tick < 180; tick++) run.Tick();
                check(run.View.CurrentOptionalPayment?.Mode == OptionalPaymentMode.Ask && run.View.OptionalPaymentPolicies.IsEmpty
                        && run.Port.Commands.Count == 1 && run.Has("optional-payment-auto-pay"),
                    "A changed original cost returns to readable Ask instead of spending under old source consent at " + viewport);
                run.Capture("changed-cost-ask");
            }

            using (var run = new Journey(profile, "hold-" + viewport.X, viewport, holdFirst: true)) {
                run.Click("optional-payment-auto-decline"); run.Move(new(20, 60));
                run.Until(() => run.Port.Started.Task.IsCompleted, "in-flight automatic answer");
                run.Click("hold");
                check(run.View.OptionalPaymentPolicies.IsEmpty && run.View.Held && run.Port.Commands.Count == 1,
                    "The permanent portrait hold control clears saved choices while one sent answer is still in flight at " + viewport);
                run.Port.Release.TrySetResult(); run.Until(() => !run.View.Busy, "already-sent answer reconciles");
                run.Observe(Checkpoint(1, 10)); run.Move(new(20, 60));
                for (int tick = 0; tick < 180; tick++) run.Tick();
                check(run.Port.Commands.Count == 1 && run.View.CurrentOptionalPayment?.Mode == OptionalPaymentMode.Ask,
                    "Taking control reconciles the one committed decline and leaves the next fresh offer unanswered at " + viewport);
            }

            using (var run = new Journey(profile, "unpayable-" + viewport.X, viewport, holdFirst: true,
                availability: OptionalManaPaymentAvailability.Unpayable)) {
                run.Capture("quiet-unpayable");
                check(run.View.Match?.Decision?.Payload is PaymentPayload && !run.Has("mana-auto") && !run.Has("mana-cancel") && !run.Has("confirm") && !run.Has("cancel")
                        && !run.Has("optional-payment-auto-pay") && run.View.ManaJourney == null
                        && !run.Run.Frame.Cards.Any(card => card.Id.StartsWith("payment:", StringComparison.Ordinal)),
                    "An engine-proven unpayable optional bill never opens a question, payment controls or duplicate source card at " + viewport);
                run.Port.Release.TrySetResult(); run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.Port.Commands.Count == 1, "quiet unpayable decline");
                check(run.Port.Commands.Single() is CancelPayment && run.View.OptionalPaymentPolicies.IsEmpty,
                    "The unpayable default declines only the current optional offer without creating a saved policy at " + viewport);
            }

            using (var run = new Journey(profile, "unknown-" + viewport.X, viewport, availability: OptionalManaPaymentAvailability.Unknown)) {
                run.Move(new(20, 60)); for (int tick = 0; tick < 180; tick++) run.Tick();
                check(run.Has("mana-cancel") && run.Has("optional-payment-auto-decline") && !run.Has("optional-payment-auto-pay")
                        && run.Port.Commands.IsEmpty && run.View.CurrentOptionalPayment?.Mode == OptionalPaymentMode.Ask,
                    "Unknown funding keeps the actual manual decision and never becomes an inferred unpayable or auto-pay permission at " + viewport);
                run.Capture("unknown-ask");
            }

            using (var run = new Journey(profile, "held-unpayable-" + viewport.X, viewport,
                availability: OptionalManaPaymentAvailability.Unknown)) {
                run.Click("hold");
                run.Observe(Checkpoint(revision: 2, availability: OptionalManaPaymentAvailability.Unpayable));
                run.Move(new(20, 60));
                for (int tick = 0; tick < 90; tick++) run.Tick();
                check(run.View.Held && run.Port.Commands.IsEmpty && run.Has("mana-cancel"),
                    "An unpayable optional offer arriving after Hold keeps its actual decline reachable instead of hiding an unanswered decision at " + viewport);
                run.Capture("held-unpayable-decline");
                run.Click("mana-cancel");
                run.Until(() => !run.View.Busy && run.Port.Commands.Count == 1, "held manual optional decline");
                check(run.Port.Commands.Single() is CancelPayment && run.View.Held,
                    "Decline once answers an unpayable offer while held without restarting automatic play at " + viewport);
            }

            using (var run = new Journey(profile, "reading-unpayable-" + viewport.X, viewport,
                availability: OptionalManaPaymentAvailability.Unknown)) {
                run.Click(run.Run.Frame.Cards.Last(card => card.Zone == "Battlefield" && card.Card.VisualId == "idol").Hit.Id);
                check(run.Run.Frame.Cards.Any(card => card.Card.VisualId == "idol" && card.Focused)
                        && run.View.OptionalPaymentPolicies.IsEmpty,
                    "Reading an ordinary source can pause a default optional answer without any saved policy at " + viewport);
                run.Observe(Checkpoint(revision: 2, availability: OptionalManaPaymentAvailability.Unpayable));
                for (int tick = 0; tick < 90; tick++) run.Tick();
                check(run.Port.Commands.IsEmpty && run.View.OptionalPaymentsPaused && run.Has("mana-cancel"),
                    "Reading keeps a newly unpayable current decision explicit and unanswered until reading ends at " + viewport);
                run.Capture("reading-unpayable-decline");
                run.CloseReader(); run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.Port.Commands.Count == 1, "default quiet decline after reading");
                check(run.Port.Commands.Single() is CancelPayment && run.View.OptionalPaymentPolicies.IsEmpty,
                    "Ending reading resumes only the current mechanical decline and never creates a future payment preference at " + viewport);
            }
        }
    }

    internal static GameSnapshot Checkpoint(int completed = 0, long revision = 1, int amount = 1,
        OptionalManaPaymentAvailability availability = OptionalManaPaymentAvailability.Payable, bool paying = true)
    {
        var state = FiniteResolutionPresentationChecks.Projection(3, completed, paying, revision);
        if (!paying || state.Decision?.Payload is not PaymentPayload payment) return state;
        ImmutableArray<ManaBillPart> bill = [new("{" + amount + "}", 1)];
        return state with { Decision = state.Decision with { Payload = payment with {
            UnpaidManaParts = bill, OptionalPaymentSupport = new("mindseye-optional-draw-" + amount, bill, availability,
                availability == OptionalManaPaymentAvailability.Payable)
        } } };
    }

    internal sealed class Journey : IDisposable
    {
        internal PresentationRun Run { get; }
        internal TableApplication.Probe View => Run.View;
        internal PaymentPort Port { get; }
        private Point pointer = new(20, 60);
        internal string Text => string.Join(" ", Run.Frame.Drawing.Commands.Select(item => item switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null }).Where(value => value != null));
        internal Journey(string profile, string name, Point viewport, bool holdFirst = false,
            OptionalManaPaymentAvailability availability = OptionalManaPaymentAvailability.Payable)
        {
            string path = Path.Combine(profile, "optional-payment-" + name); Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(path);
            Port = new(Checkpoint(availability: availability), holdFirst);
            var setup = new InteractionScenarioEngine();
            Run = new(new(setup, setup, Port, () => Task.CompletedTask), path, Port.State, viewport);
            Tick(); Tick();
        }
        internal bool Has(string id) => Run.Frame.Hits.Any(hit => hit.Id == id);
        internal void Move(Point point) { pointer = point; Tick(); }
        internal void Tick(ButtonState button = ButtonState.Released, KeyboardState keys = default)
        {
            var physical = RenderViewport.Fit(Run.Viewport, View.DesignHeight).ToPhysical(pointer.ToVector2());
            Run.Tick(TimeSpan.FromSeconds(1d / 60), new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0,
                button, default, default, default, default), keys);
        }
        internal void CloseReader() { Tick(keys: new(Keys.Escape)); Tick(); }
        internal void Click(string id)
        {
            pointer = Run.Frame.Hits.Single(hit => hit.Id == id).Bounds.Center;
            Tick(ButtonState.Pressed); Tick();
        }
        internal void Observe(GameSnapshot state) { Port.Present(state); Run.QueueObservation(state); Tick(); }
        internal void Until(Func<bool> ready, string step)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!ready() && DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(1); }
            if (!ready()) { Capture("timeout"); throw new InvalidOperationException("Optional-payment presentation stopped at " + step + "; " + View.Error); }
        }
        internal void Capture(string label) => File.WriteAllText(Path.Combine(View.Profile, label + ".json"),
            JsonSerializer.Serialize(new { frame = Run.Frame.Observe(), current = View.CurrentOptionalPayment,
                policies = View.OptionalPaymentPolicies, commands = Port.Commands.Select(command => command.GetType().Name) }, new JsonSerializerOptions { WriteIndented = true }));
        public void Dispose() { Port.Release.TrySetResult(); Run.Dispose(); }
    }

    internal sealed class PaymentPort(GameSnapshot initial, bool holdFirst) : IMatchConnection
    {
        private GameSnapshot state = initial;
        private readonly object gate = new();
        internal GameSnapshot State { get { lock (gate) return state; } }
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Present(GameSnapshot next) { lock (gate) state = next; }
        public async Task ExecuteAsync(GameCommand command)
        {
            lock (gate) {
                if (!GameCommandPermissions.Allows(state, command) || command is not (CancelPayment or PayMana or AutoPayOptionalPayment))
                    throw new InvalidOperationException("Optional-payment authored port received a stale or unexpected command.");
                Commands.Enqueue(command);
            }
            if (holdFirst && Commands.Count == 1) { Started.TrySetResult(); await Release.Task; }
            lock (gate) {
                int completed = state.Executions!.Occurrences.Count(item => item.Step == ExecutionStep.Complete) + 1;
                state = Checkpoint(completed, state.Revision + 1, paying: false);
            }
        }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
