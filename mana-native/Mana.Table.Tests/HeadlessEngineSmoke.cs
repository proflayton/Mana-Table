using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Magic;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

/// <summary>A short real-Forge game observed and operated through production composition, without a window.</summary>
internal static class HeadlessEngineSmoke
{
    internal static async Task RunAsync(ForgePaths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        var checks = new List<string>();
        var frames = new List<object>();
        var actions = new List<object>();
        var elapsed = Stopwatch.StartNew();
        var json = new JsonSerializerOptions { WriteIndented = true };
        string? error = null;
        int compositions = 0;
        double compositionMilliseconds = 0;
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException(description);
            checks.Add(description);
            File.AppendAllText(Path.Combine(paths.Profile, "headless-engine-progress.log"), description + "\n");
        }
        await using var engine = new ForgeEngine(paths);
        try {
            await engine.InitializeAsync();
            var deck = await engine.ImportAsync("Headless production composition", "Deck\n99 Mountain\nCommander\n1 Rograkh, Son of Rohgahh");
            var setup = await engine.PrepareSoloAsync(deck.Id);
            Check(setup.Opponents.Any(opponent => opponent.Id == "green") && setup.Opponents.Any(opponent => opponent.Id == "red"),
                "The real adapter imports the Commander deck and exposes bundled starter AI opponents");
            await engine.StartSoloAsync(deck.Id, ["green", "red", "green"]);
            GameSnapshot? initial = null;
            while (initial?.Players.Length != 4 || initial.Decision == null) {
                Guard();
                initial = await engine.ObserveAsync();
                if (initial?.Status is "error" or "finished") throw new InvalidOperationException(initial.Error ?? "Game ended before its opening decision.");
                if (initial?.Decision == null) await Task.Delay(10);
            }
            Check(initial.Players.All(player => player.Life == 40), "A real four-player Commander game reaches its opening decision");
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(paths.Profile);
            using var app = new TableApplication(engine, paths.Profile, initialState: initial);
            app.Automated = true; app.Initialize();
            var view = app.TestPort;
            var viewport = new Point(1600, 900);
            Point pointer = new(20, 60);
            double seconds = 0;
            PresentationFrame frame = Tick("opening");
            Save("opening");
            string? landId = null, commanderId = null;
            bool landSaved = false, commanderSaved = false;
            int decisions = 0;

            while (!landSaved || !commanderSaved) {
                Guard();
                await Refresh("observe");
                var state = view.Match ?? throw new InvalidOperationException("The active Forge game disappeared.");
                if (state.Status is "error" or "finished") throw new InvalidOperationException(state.Error ?? "Game ended before the focused scenario completed.");
                var land = state.Viewer!.Zone("Battlefield").Cards.FirstOrDefault(card => card.Name == "Mountain");
                var commander = state.Viewer.Zone("Battlefield").Cards.FirstOrDefault(card => card.Name == "Rograkh, Son of Rohgahh");
                if (land != null && !landSaved) {
                    landId = CardViews.Identity(land); landSaved = true; Save("land-on-battlefield");
                    Check(frame.Cards.Any(card => card.Id == landId && card.Zone == "Battlefield")
                        && state.Viewer.LandPlaysUsed >= 1,
                        "Forge's resolved land play reaches the production scene and authoritative land-play state");
                }
                if (commander != null && !commanderSaved) {
                    commanderId = CardViews.Identity(commander); commanderSaved = true; Save("commander-on-battlefield");
                    var displayed = frame.Cards.Single(card => card.Id == commanderId && card.Zone == "Battlefield");
                    Check(displayed.Card.Power == commander.Power && displayed.Card.Toughness == commander.Toughness
                        && commander.Power == 0 && commander.Toughness == 1,
                        "The actual resolved commander displays the engine's current zero power and one toughness");
                    Check(commander.States != null && new[] { "first strike", "menace", "trample" }
                        .All(key => commander.States.Any(state => state.Kind == "ability" && state.Key == key))
                        && CardPresentation.StateLabels(displayed.Card).Contains("Menace"),
                        "Effective commander keywords travel from Forge's typed public descriptors into the actual prepared frame");
                }
                if (landSaved && commanderSaved) break;
                if (view.Busy || view.Match?.Decision == null) { await Task.Delay(5); continue; }
                if (++decisions > 80) throw new InvalidOperationException("The focused opening scenario required unexpectedly many decisions.");
                var decision = view.Match.Decision;
                if (decision is { Kind: "input", Intent: DecisionIntent.Priority }
                    && view.Match.ActivePlayerId == view.Match.ViewerId && view.Match.PhaseKey is "MAIN1" or "MAIN2") {
                    Card? playable = !landSaved ? view.Match.Viewer!.Zone("Hand").Cards.FirstOrDefault(card => card.Selectable && card.Type.Contains("Land", StringComparison.Ordinal))
                        : !commanderSaved ? view.Match.Viewer!.Zone("Command").Cards.FirstOrDefault(card => card.Selectable) : null;
                    if (playable != null) {
                        await PlayCard(playable); continue;
                    }
                }
                if (decision.Kind is "choice" or "reveal") {
                    int required = decision.Kind == "reveal" ? 0 : Math.Max(decision.Min, Math.Min(1, decision.Max));
                    foreach (var choice in decision.Choices.Take(required)) Click("choice:" + choice.Index);
                    if (frame.Hits.Any(hit => hit.Id == "choicesdone")) Click("choicesdone");
                    else if (frame.Hits.Any(hit => hit.Id == "confirm")) Click("confirm");
                    else throw new InvalidOperationException("No real confirmation control for " + decision.Kind);
                    actions.Add(new { mode = "application-pointer", decision = decision.Id, action = "confirm projected choice", required });
                } else if (decision.Kind is "number" or "text" && frame.Hits.Any(hit => hit.Id == "value")) {
                    Click("value");
                    actions.Add(new { mode = "application-pointer", decision = decision.Id, action = "confirm initial projected value" });
                } else if (decision.Kind == "input" && decision.OkEnabled && frame.Hits.Any(hit => hit.Id == "confirm")) {
                    Click("confirm");
                    actions.Add(new { mode = "application-pointer", decision = decision.Id, action = "confirm current opening/priority decision" });
                } else if (decision.PlayerChoices.Length > 0) {
                    int recipient = decision.PlayerChoices.Contains(view.Match.ViewerId) ? view.Match.ViewerId : decision.PlayerChoices[0];
                    var target = frame.Hits.FirstOrDefault(hit => hit.Id == "recipient:" + recipient)
                        ?? frame.Hits.FirstOrDefault(hit => hit.Id == "player:" + recipient)
                        ?? throw new InvalidOperationException("No production control for the projected player choice.");
                    Click(target.Id);
                    actions.Add(new { mode = "application-pointer", decision = decision.Id, action = "choose projected player", recipient });
                } else throw new InvalidOperationException("Unhandled real opening decision: " + JsonSerializer.Serialize(decision));
                await Idle();
            }

            Check(actions.Any(action => JsonSerializer.Serialize(action).Contains("read-card-action"))
                && landId != null && commanderId != null,
                "Real land and commander plays both use the production card reader's explicit action control");
            Check(frame.Stamp.GameId == view.Match!.Id && frame.Stamp.Revision == view.Match.Revision
                && frame.Stamp.DecisionId == view.Match.Decision?.Id,
                "The published frame is stamped with the exact authoritative game, revision and decision being displayed");
            Check(view.Match.Players.Where(player => player.Id != view.Match.ViewerId).All(player => player.Zone("Hand").Cards.Length == 0)
                && frame.Cards.Where(card => card.Zone == "Hand").All(card => card.Request.Owner == view.Match.ViewerId),
                "Private AI hands remain absent from both the real adapter snapshot and production prepared frame");
            Click("concede"); Click("confirm-concede"); await Idle(); await Refresh("finished");
            Check(view.Match is { Status: "finished", Result: "Defeat" }, "The real game concedes through production confirmation controls and publishes its result");
            Save("finished");

            GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
            PresentationFrame Tick(string stage, ButtonState left = ButtonState.Released)
            {
                seconds += 1.0 / 60;
                app.Update(Time(), new(pointer.X, pointer.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released), new(), viewport);
                var timer = Stopwatch.StartNew();
                var composed = app.ComposePresentation(Time()); timer.Stop();
                compositions++; compositionMilliseconds += timer.Elapsed.TotalMilliseconds;
                if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
                if (composed.Stamp.Revision != (view.Match?.Revision ?? 0)) throw new InvalidOperationException("Composition stamp does not match the actual snapshot.");
                if (view.Match is { } state && state.Players.Any(player => player.Id != state.ViewerId && player.Zone("Hand").Cards.Length > 0))
                    throw new InvalidOperationException("The adapter exposed a private AI hand.");
                frames.Add(new { stage, stamp = composed.Stamp, commandCount = composed.Drawing.Commands.Count,
                    cardCount = composed.Cards.Count, hitCount = composed.Hits.Count });
                return composed;
            }
            async Task Refresh(string stage)
            {
                if (await engine.ObserveAsync() is { } next) view.Accept(next);
                frame = Tick(stage);
            }
            async Task Idle()
            {
                while (view.Busy) { Guard(); await Task.Delay(5); frame = Tick("await-command"); }
                await Refresh("after-command");
            }
            void Save(string stage) => File.WriteAllText(Path.Combine(paths.Profile, "headless-engine-" + stage + ".json"),
                JsonSerializer.Serialize(new { snapshot = view.Match, frame = frame.Observe() }, json));
            void Click(string id)
            {
                var hit = frame.Hits.LastOrDefault(hit => hit.Id == id) ?? throw new InvalidOperationException("Missing real application control: " + id);
                pointer = Exposed(hit);
                frame = Tick("press-" + id, ButtonState.Pressed);
                frame = Tick("release-" + id);
            }
            Point Exposed(TableHit hit)
            {
                bool Matches(Point point) => point.X >= 0 && point.Y >= 0 && point.X < viewport.X && point.Y < viewport.Y
                    && view.HitAt(point) is { } current && current.Id == hit.Id && current.Scope == hit.Scope;
                var center = new Point(Math.Clamp(hit.Bounds.Center.X, 0, viewport.X - 1), Math.Clamp(hit.Bounds.Center.Y, 0, viewport.Y - 1));
                if (Matches(center)) return center;
                for (int y = Math.Max(0, hit.Bounds.Top); y < Math.Min(viewport.Y, hit.Bounds.Bottom); y++)
                    for (int x = Math.Max(0, hit.Bounds.Left); x < Math.Min(viewport.X, hit.Bounds.Right); x++) {
                        var point = new Point(x, y); if (Matches(point)) return point;
                    }
                throw new InvalidOperationException("No exposed real pointer target for " + hit.Id);
            }
            async Task PlayCard(Card card)
            {
                string occurrence = CardViews.Identity(card);
                var entry = frame.Cards.Single(item => item.Id == occurrence);
                var hit = frame.Hits.Last(item => item.Id == entry.Hit.Id && item.Scope == entry.Hit.Scope);
                Click(hit.Id);
                frame = Tick("restore-reader-footprint"); frame = Tick("reader-settled");
                if (!frame.Cards.Any(item => item.Id == occurrence && item.Focused)) throw new InvalidOperationException("The real card reader did not open.");
                string decisionId = view.Match!.Decision!.Id;
                Click("read-card-action");
                actions.Add(new { mode = "application-pointer", decision = decisionId, action = "read-card-action", card.Name, occurrence });
                await Idle();
            }
        } catch (Exception exception) {
            error = exception.ToString(); throw;
        } finally {
            File.WriteAllText(Path.Combine(paths.Profile, "headless-engine-trace.json"), JsonSerializer.Serialize(new { frames, actions }, json));
            File.WriteAllText(Path.Combine(paths.Profile, "headless-engine-results.json"), JsonSerializer.Serialize(new {
                passed = error == null, checks, error, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                compositions, compositionMilliseconds,
                meanComposeMilliseconds = compositions > 0 ? compositionMilliseconds / compositions : 0,
                scope = "Real Forge game and production application pointer/composition paths; no window or GPU, no simulated Magic rules."
            }, json));
        }
        void Guard()
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Real Forge opening-to-land/commander presentation exceeded its bounded smoke watchdog.");
        }
    }
}
