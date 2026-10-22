using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Exercises the production application, composition and input paths. The
// controlled connection validates scoped commands; it does not simulate rules.
internal static class HeadlessPresentationSmoke
{
    internal static void Run(string profile)
    {
        Directory.CreateDirectory(profile);
        var checks = new List<string>();
        var trace = new List<object>();
        var performance = new List<object>();
        string? error = null;
        void Check(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        try {
            QuietPriorityPresentationChecks.Run(profile, Check);
            using (var run = new Runner(profile, "reading-and-scope", Example(), trace, performance)) {
                var first = run.Tick("initial");
                var second = run.Compose("same-time");
                Check(Fingerprint(first.Drawing).SequenceEqual(Fingerprint(second.Drawing))
                    && first.Cards.Select(card => (card.Id, card.Display)).SequenceEqual(second.Cards.Select(card => (card.Id, card.Display))),
                    "The actual application composes identical typed drawing and card geometry for unchanged state and time");
                Check(first.Drawing.Commands.Count > 0 && first.Hits.Count > 0 && first.Scene != null
                    && first.Drawing.Commands.OfType<DrawCommand.Surface>().All(command => command.Texture == null),
                    "Production table composition creates drawing, scene and input data without a graphics device or card textures");

                run.ClickCard("hand-a"); run.Settle("reader-a");
                var readerA = run.Frame.Cards.Single(card => card.Id == "hand-a");
                var readerAction = run.Hit("read-card-action");
                var actionBounds = readerAction.Bounds;
                Check(readerA.Focused && readerA.Display.Size.X == 410
                    && run.View.HitAt(actionBounds.Center) is { } actionHit && actionHit.Id == readerAction.Id && actionHit.Scope == readerAction.Scope
                    && run.Engine.Replies.Count == 0,
                    "Legacy engine card clicks retain an exposed current action hit without submitting a game action while reading");
                var pinnedA = run.Frame;
                var readerStart = pinnedA.Cards.Single(card => card.Id == "hand-a").Display.Center.ToPoint();
                var readerDrag = readerStart + new Point(20, 0);
                run.Input(readerStart, left: ButtonState.Pressed, label: "press-expanded-hand-face");
                run.Input(readerDrag, left: ButtonState.Pressed, label: "small-drag-inside-reader-origin");
                run.Input(readerDrag, label: "release-inside-reader-origin");
                run.Settle("uncommitted-reader-drag");
                Check(run.Engine.Replies.Count == 0 && !run.View.Busy
                    && run.View.Match!.Viewer!.Zone("Hand").Cards.Any(card => card.VisualId == "hand-a"),
                    "Twenty pixels of movement on an expanded hand card is insufficient deliberate displacement to submit a play");
                run.ClickCard("hand-a"); run.Settle("reader-a-restored");
                run.ClickCard("hand-b", originalHandSlot: true); run.Settle("reader-b");
                Check(run.Frame.Cards.Single(card => card.Id == "hand-b").Focused
                    && !run.Frame.Cards.Single(card => card.Id == "hand-a").Focused && run.Engine.Replies.Count == 0,
                    "A pinned card leaves the adjacent original hand slot available to real pointer input");
                run.ClickCard("hand-b"); run.Settle("reader-b-closed");
                Check(!run.Frame.Cards.Any(card => card.Focused)
                    && run.Frame.Cards.Single(card => card.Id == "hand-b").Display.Size.X == 137,
                    "A second click restores the selected hand card's compact production geometry");

                run.ClickCard("hand-a"); run.Settle("reader-before-play");
                string decision = run.View.Match!.Decision!.Id;
                string key = run.View.Match.Viewer!.Zone("Hand").Cards.Single(card => card.VisualId == "hand-a").Key;
                var playAction = run.Hit("read-card-action");
                run.Input(new(20, 60), label: "reader-action-idle");
                var idleFill = run.Frame.Drawing.Commands.OfType<DrawCommand.Panel>().Single(panel => panel.Bounds == playAction.Bounds).FillColor;
                run.Input(playAction.Bounds.Center, label: "reader-action-hover");
                var hoverFill = run.Frame.Drawing.Commands.OfType<DrawCommand.Panel>().Single(panel => panel.Bounds == playAction.Bounds).FillColor;
                run.Input(playAction.Bounds.Center, left: ButtonState.Pressed, label: "reader-action-pressed");
                var pressedFill = run.Frame.Drawing.Commands.OfType<DrawCommand.Panel>().Single(panel => panel.Bounds == playAction.Bounds).FillColor;
                Check(idleFill != hoverFill && hoverFill != pressedFill && run.Engine.Replies.Count == 0,
                    "The on-card action distinguishes pointer hover and press through production drawing without submitting before release");
                run.Input(playAction.Bounds.Center, label: "reader-action-release");
                run.Until(() => !run.View.Busy && run.Engine.Replies.Count == 1, "play-confirmed");
                var reply = run.Engine.Replies.Single();
                Check(reply is { Action: ReplyAction.SelectCard } && reply.DecisionId == decision && reply.Card == key
                    && run.View.Match!.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == "hand-a"),
                    "The explicit reader action sends exactly the current scoped card command through ClientSession");
                Check(pinnedA.Cards.Single(card => card.Id == "hand-a").Focused
                    && pinnedA.Cards.Single(card => card.Id == "hand-a").Display.Size.X == 410,
                    "Later input and compositions cannot change a previously published card frame");

                run.ClickCard("hand-b"); run.Settle("before-stale-press");
                var action = run.Hit("read-card-action"); var point = run.PointFor(action);
                run.Input(point, left: ButtonState.Pressed, label: "press-old-decision");
                run.Present(run.Engine.State with { Decision = run.Engine.State.Decision! with { Message = "A refreshed priority window." } });
                run.Input(point, label: "release-after-decision-change");
                Check(run.Engine.Replies.Count == 1 && !run.View.Busy,
                    "Changing the authoritative decision between press and release rejects the obsolete input scope");
                Check(Fingerprint(first.Drawing).SequenceEqual(Fingerprint(second.Drawing)) && first.Hits.Count == second.Hits.Count,
                    "Old drawing commands and interaction snapshots remain detached from subsequent frames");
            }

            using (var run = new Runner(profile, "reader-drag-to-play", Example(), trace, performance)) {
                run.Tick("initial");
                string decision = run.View.Match!.Decision!.Id;
                string key = run.View.Match.Viewer!.Zone("Hand").Cards.Single(card => card.VisualId == "hand-a").Key;
                var drag = run.DragReaderCard("hand-a");
                var held = run.View.DisplayedPose("hand-a");
                Check(drag.Reader.Contains(drag.Drop) && !drag.Anchor.Contains(drag.Drop) && TableWorld.CanPlayAt(drag.Drop)
                    && held is { } pose && pose.Size == new Vector2(132, 185)
                    && pose.Center == drag.Drop.ToVector2() + new Vector2(0, -72)
                    && !run.Frame.Cards.Any(card => card.Id == "hand-a" && card.Focused) && run.Engine.Replies.Count == 0,
                    "A deliberate reader-face drag becomes a compact held card above the pointer while remaining inside its former reader area");
                run.Input(drag.Drop, label: "release-reader-face-on-board");
                run.Until(() => !run.View.Busy && run.Engine.Replies.Count == 1, "reader-drag-play-confirmed");
                Check(run.Engine.Replies.Single() is { Action: ReplyAction.SelectCard } reply
                    && reply.DecisionId == decision && reply.Card == key
                    && run.View.Match!.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == "hand-a"),
                    "Releasing a deliberate reader-face drag on the board submits exactly the current scoped hand play without crossing the old reader boundary");
            }

            foreach (string condition in new[] { "escape", "escape-on-release", "right-click", "outside-board", "over-control", "hand-anchor", "new-prompt", "new-key", "permission-revoked", "visibility-revoked" }) {
                var state = Example();
                if (condition == "hand-anchor") state = state with { Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Count = 1, Cards = zone.Cards.Take(1).ToArray() }).ToArray()
                }).ToArray() };
                using var run = new Runner(profile, "reader-drag-" + condition, state, trace, performance);
                run.Tick("initial");
                var drag = run.DragReaderCard("hand-a");
                var release = drag.Drop;
                KeyboardState? releaseKeys = null;
                if (condition == "escape") run.Input(release, left: ButtonState.Pressed, label: "cancel-held-card-with-escape", keys: new(Keys.Escape));
                else if (condition == "escape-on-release") releaseKeys = new(Keys.Escape);
                else if (condition == "right-click") run.Input(release, left: ButtonState.Pressed, right: ButtonState.Pressed, label: "cancel-held-card-with-right-click");
                else if (condition == "outside-board") release = new(20, 60);
                else if (condition == "over-control") {
                    var control = run.Hit("instructions");
                    release = new(control.Bounds.Left + 8, control.Bounds.Top + 5);
                    Check(TableWorld.CanPlayAt(release) && run.View.HitAt(release)?.Id == control.Id,
                        "The invalid control destination lies inside the generic play area and has actual UI hit precedence");
                } else if (condition == "hand-anchor") {
                    release = drag.Anchor.Point(.5f, .01f).ToPoint();
                    Check(drag.Anchor.Contains(release) && TableWorld.CanPlayAt(release),
                        "The one-card hand anchor overlaps the generic play area, so its actual pose must prevent a drop");
                } else if (condition == "new-prompt") run.Present(run.Engine.State with {
                    Decision = run.Engine.State.Decision! with { Message = "A replacement priority window." }
                });
                else {
                    var current = run.View.Match!;
                    run.View.Accept(current with { Revision = current.Revision + 1, Players = current.Players.Select(player => player.Id != current.ViewerId ? player : player with {
                        Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                            Count = zone.Count - (condition == "visibility-revoked" ? 1 : 0),
                            Cards = zone.Cards.Where(card => condition != "visibility-revoked" || card.VisualId != "hand-a")
                                .Select(card => card.VisualId != "hand-a" ? card : condition == "new-key"
                                    ? card with { Key = card.Key + ":replacement" } : card with { Selectable = false }).ToArray()
                        }).ToArray()
                    }).ToArray() });
                }
                run.Input(release, label: "release-after-" + condition, keys: releaseKeys);
                run.Settle("reader-drag-rejected-" + condition);
                Check(run.Engine.Replies.Count == 0 && !run.View.Busy
                    && (condition != "visibility-revoked" || !run.Frame.Cards.Any(card => card.Id == "hand-a")),
                    $"A reader-origin hand drag cannot submit a play after {condition} and cannot retain a revoked card face");
            }

            using (var run = new Runner(profile, "generic-state-and-privacy", Example(), trace, performance)) {
                run.Tick("day-and-current-mana");
                Check(Text(run.Frame).Contains("Day") && Text(run.Frame).Contains("Momentum 6/6 MAX")
                    && run.View.ManaPoolBadge(0) is { Total: 2 },
                    "Actual drawing commands show table Day, an unfamiliar player maximum of six, and the current colorless pool");
                run.View.OpenBattlefield(0); run.Tick("focused-battlefield");
                var focusCard = run.Frame.Hits.First(hit => hit.Id.StartsWith("battlefield-focus-card:", StringComparison.Ordinal));
                run.ClickHit(focusCard.Id); run.Settle("reader-over-focused-battlefield");
                bool hadReader = run.Frame.Cards.Any(card => card.Focused);
                run.Input(new(20, 60), label: "escape-reader-only", keys: new(Microsoft.Xna.Framework.Input.Keys.Escape));
                run.Tick("reader-dismissed-parent-retained");
                Check(hadReader && !run.Frame.Cards.Any(card => card.Focused) && run.View.ZoneName == "Battlefield"
                    && run.Frame.Hits.Any(hit => hit.Id == "battlefield-focus-close") && run.Engine.Replies.Count == 0,
                    "Escape dismisses the top card reader while preserving its focused battlefield workspace and unanswered decision");
                run.ClickHit("battlefield-focus-close"); run.Tick("back-to-table");
                run.ClickCard("public-ward", right: true);
                Check(run.View.Overlay == "inspect"
                    && Text(run.Frame).Any(text => text.Contains("Ward {2} ×2") && text.Contains("Protection from blue"))
                    && run.Engine.Replies.Count == 0,
                    "The production inspector renders qualified Ward and protection descriptors without dispatching an action");
                run.ClickHit("close-overlay");
                var beforeSourceChoice = run.Engine.State;
                var choiceSource = beforeSourceChoice.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == "public-ward");
                run.Present(beforeSourceChoice with { Decision = new() {
                    Kind = "choice", Context = "playAbility", Min = 1, Max = 1, SourceCard = choiceSource,
                    SourceCardId = choiceSource.VisualId, SourceZone = "Battlefield", SourceControllerId = 0,
                    Choices = [new(17, "First reported ability", null), new(42, "Second reported ability", null)]
                } });
                run.Tick("text-choice-with-current-source");
                var sourceChoice = run.Engine.State;
                var sourcePrompt = sourceChoice.Decision!;
                Check(new[] { "choice:17", "choice:42" }.All(id => run.Frame.Hits.Any(hit => hit.Id == id))
                    && run.Frame.Cards.Any(card => card.Id == choiceSource.VisualId && card.Zone == "Battlefield")
                    && new[] { "hand-a", "hand-b" }.All(id => run.Frame.Cards.Any(card => card.Id == id && card.Zone == "Hand"))
                    && !run.Frame.Cards.Any(card => card.Zone == "Stack" && card.Card.VisualId == choiceSource.VisualId)
                    && !run.Frame.Hits.Any(hit => hit.Id == "stack")
                    && !run.Frame.Hits.Any(hit => hit.Id is "close-overlay" or "choicesfilter" or "choicesclear" or "choice-cancel" or "choicesdone")
                    && run.Engine.Replies.Count == 0,
                    "Required inline ability actions retain the actual battlefield and hand while exposing original indexes without a modal, confirmation or automatic reply");
                string sourceGame = run.View.Match!.Id, sourceDecision = run.View.Match.Decision!.Id;
                run.ClickCard(choiceSource.VisualId, right: true);
                Check(run.View.Overlay == "inspect" && run.View.Match!.Id == sourceGame
                    && run.View.Match.Decision!.Id == sourceDecision
                    && run.View.Selections.Count == 0 && run.Engine.Replies.Count == 0
                    && Text(run.Frame).Contains(choiceSource.Name)
                    && Text(run.Frame).Contains($"Power / toughness: {choiceSource.Power}/{choiceSource.Toughness}")
                    && Text(run.Frame).Any(text => text.Contains("Ward {2} ×2") && text.Contains("Protection from blue")),
                    "Clicking the ability source inspects its current public state while preserving the exact unanswered decision without selecting or activating an option");
                run.ClickHit("close-overlay"); run.Tick("source-inspection-returned-to-inline-actions");
                Check(run.View.Overlay.Length == 0 && run.View.Match!.Decision!.Id == sourceDecision
                    && run.View.Selections.Count == 0 && run.Engine.Replies.Count == 0
                    && run.Frame.Hits.Any(hit => hit.Id == "choice:17")
                    && run.Frame.Cards.Any(card => card.Id == choiceSource.VisualId && card.Zone == "Battlefield"),
                    "Closing inspection of the actual source restores the same unanswered inline actions without activating or replacing them");
                run.ClickCard("hand-b"); run.Settle("unrelated-hand-reading-during-ability");
                Check(run.Engine.Replies.Count == 0 && run.View.Match!.Decision!.Id == sourceDecision
                    && (run.View.Overlay == "inspect" || run.Frame.Cards.Any(card => card.Id == "hand-b" && card.Focused)),
                    "Clicking an unrelated selectable hand card during an ability choice only reads it and cannot submit a different card action");
                if (run.View.Overlay == "inspect") run.ClickHit("close-overlay");
                else {
                    run.Input(new(20, 60), label: "dismiss-unrelated-reader", keys: new(Keys.Escape));
                    run.Tick("unrelated-reader-dismissed");
                }
                var handTarget = run.Frame.Cards.Single(card => card.Id == "hand-b" && card.Zone == "Hand");
                var handPoint = run.PointFor(run.Hit(handTarget.Hit.Id));
                run.Input(handPoint, left: ButtonState.Pressed, label: "press-unrelated-hand-during-ability");
                run.Input(new(800, 360), left: ButtonState.Pressed, label: "drag-unrelated-hand-during-ability");
                run.Input(new(800, 360), label: "release-unrelated-hand-during-ability");
                Check(run.Engine.Replies.Count == 0 && !run.View.Busy && run.View.Match!.Decision!.Id == sourceDecision
                    && run.View.Match.Viewer!.Zone("Hand").Cards.Any(card => card.VisualId == "hand-b"),
                    "Dragging an unrelated hand card cannot bypass the pending ability choice or move that card out of hand");
                run.ClickHit("choice:17");
                run.Until(() => !run.View.Busy && run.Engine.Replies.Count == 1, "inline-ability-choice-accepted");
                Check(run.Engine.Replies.Single() is { Action: ReplyAction.Choose } compactReply
                    && compactReply.GameId == sourceGame && compactReply.DecisionId == sourceDecision && compactReply.Choices!.SequenceEqual([17]),
                    "One explicit inline ability action submits exactly its original index in the current scope without a separate confirmation");
                run.Present(sourceChoice with { Decision = sourcePrompt with { Ordered = true } });
                run.Tick("ordered-source-choice-gallery-fallback");
                run.ClickHit("choice:17");
                Check(run.Frame.Hits.Any(hit => hit.Id == "choicesfilter") && run.Frame.Hits.Any(hit => hit.Id == "choicesclear")
                    && run.Frame.Hits.Any(hit => hit.Id == "choicesdone") && run.View.Selections.SequenceEqual([17])
                    && run.Engine.Replies.Count == 1,
                    "An ordered source choice retains staged gallery selection and confirmation rather than activating a row directly");
                run.Present(sourceChoice with { Decision = sourcePrompt with {
                    Choices = sourcePrompt.Choices.Select(choice => choice with {
                        Detail = string.Join(" ", Enumerable.Repeat("This authoritative ability detail must remain available without being discarded to fit a compact panel.", 40))
                    }).ToArray()
                } });
                run.Tick("long-ability-detail-inline");
                Check(!run.Frame.Hits.Any(hit => hit.Id is "close-overlay" or "choicesfilter" or "choicesclear" or "choicesdone")
                    && run.Frame.Cards.Any(card => card.Id == choiceSource.VisualId && card.Zone == "Battlefield")
                    && run.Frame.Cards.Any(card => card.Id == "hand-a" && card.Zone == "Hand") && run.Engine.Replies.Count == 1,
                    "Long ability detail stays on the actual board without replacing its source and hand with a gallery or submitting an action");
                string longDecision = run.View.Match!.Decision!.Id;
                var pageDeadline = Stopwatch.StartNew();
                while (!run.Frame.Hits.Any(hit => hit.Id == "choice:42")) {
                    if (pageDeadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The second original inline ability action was not reachable through its pages");
                    run.ClickHit("choicesnext");
                }
                run.ClickHit("choice:42");
                run.Until(() => !run.View.Busy && run.Engine.Replies.Count == 2, "expanded-ability-choice-accepted");
                Check(run.Engine.Replies.Count == 2 && run.Engine.Replies[^1] is { Action: ReplyAction.Choose } expandedReply
                    && expandedReply.GameId == sourceGame && expandedReply.DecisionId == longDecision && expandedReply.Choices!.SequenceEqual([42]),
                    "A long-detail ability row keeps the same direct action intent and submits its exact original index once");
                int privacyReplies = run.Engine.Replies.Count;
                run.Present(beforeSourceChoice); run.Tick("source-choice-restored-priority");
                run.ClickCard("hand-a"); run.Settle("public-reader-before-revocation");
                var snapshot = run.Engine.State;
                run.Present(snapshot with {
                    States = [new("day-night", "Night", "status", null, null, "It is night for the whole table.", "day-night")],
                    Players = snapshot.Players.Select(player => player.Id != 0 ? player : player with {
                        Mana = new() { ["U"] = 3 }, Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                            Cards = zone.Cards.Where(card => card.VisualId != "hand-a").ToArray(), Count = zone.Count - 1
                        }).ToArray()
                    }).ToArray()
                });
                run.Settle("night-and-revoked-face");
                Check(Text(run.Frame).Contains("Night") && !Text(run.Frame).Contains("Day")
                    && run.View.ManaPoolBadge(0) is { Total: 3 } mana && mana.Parts.All(part => part.Key != "C")
                    && mana.Parts.Any(part => part.Key == "U" && part.Amount == "3"),
                    "Generic table transitions and authoritative mana replacement reach the next production frame");
                Check(!run.Frame.Cards.Any(card => card.Id == "hand-a")
                    && !run.Frame.Hits.Any(hit => hit.Card?.VisualId == "hand-a")
                    && !Text(run.Frame).Any(text => text.Contains("Public hand alpha"))
                    && !run.Frame.Hits.Any(hit => hit.Id == "read-card-action"),
                    "Visibility revocation removes the former face, reader, action and rendered text from the current frame");
                run.Present(run.Engine.State with { States = [] }); run.Tick("table-mechanic-ended");
                Check(!run.Frame.Hits.Any(hit => hit.Id.StartsWith("table-state:", StringComparison.Ordinal))
                    && !Text(run.Frame).Contains("Night") && run.Engine.Replies.Count == privacyReplies,
                    "Removing table state clears its old controls and labels without inventing a game command");
            }

            using (var run = new Runner(profile, "complete-dense-battlefield", DenseExample(), trace, performance)) {
                run.Tick("dense-all-seats");
                var cards = run.Frame.Cards.Where(card => card.Zone == "Battlefield").ToArray();
                Check(cards.Length == 80 && cards.Select(card => card.Id).Distinct().Count() == 80
                    && run.Frame.Scene!.Seats.All(seat => seat.Battlefield.Sum(cluster => cluster.Cards.Length) == 20),
                    "The actual production dense scene and prepared frame retain all eighty public battlefield occurrences");
                Check(cards.All(card => run.Frame.Hits.Count(hit => hit.Card != null && CardViews.Identity(hit.Card) == card.Id && hit.Zone == "Battlefield") == 1),
                    "Every dense occurrence publishes one canonical face-and-label hit instead of duplicate card targets");
                Check(cards.All(card => run.TryPointFor(run.Frame.Hits.Single(hit => hit.Card != null && CardViews.Identity(hit.Card) == card.Id && hit.Zone == "Battlefield"), out _)),
                    "Every dense occurrence has an exposed pointer target under actual production hit precedence");
                var hidden = cards.Single(card => card.Id == "dense-1-19");
                run.ClickCard(hidden.Id); run.Settle("anonymous-public-permanent");
                Check(!run.Frame.Cards.Any(card => card.Id == hidden.Id && card.Focused)
                    && hidden.Card.FaceDown && hidden.Card.VisualId.Length == 0 && hidden.Card.Text.Length == 0
                    && run.Engine.Replies.Count == 0,
                    "A dense anonymous face remains reachable without opening its hidden identity or issuing an action");
            }
        } catch (Exception exception) {
            error = exception.ToString(); throw;
        } finally {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(profile, "headless-presentation-trace.json"), JsonSerializer.Serialize(trace, options));
            File.WriteAllText(Path.Combine(profile, "headless-presentation-results.json"), JsonSerializer.Serialize(new {
                passed = error == null, checks, error, performance,
                scope = "Production application composition and pointer input; controlled projected-state connection, no rules simulation or GPU."
            }, options));
        }
    }

    private static string[] Text(PresentationFrame frame) => frame.Drawing.Commands.Select(command => command switch {
        DrawCommand.Text text => text.Value,
        DrawCommand.CenterText text => text.Value,
        _ => null
    }).Where(text => text != null).Cast<string>().ToArray();
    private static string[] Fingerprint(DrawingFrame frame) => frame.Commands.Select(command => command is DrawCommand.Polygon polygon
        ? polygon.Color.PackedValue + ":" + string.Join(";", polygon.Points.Select(point => $"{point.X:R},{point.Y:R}"))
        : command.ToString()).ToArray();

    private sealed class Runner : IDisposable
    {
        private readonly TableApplication application;
        private readonly string scenario;
        private readonly List<object> trace, performance;
        private readonly List<double> composeMilliseconds = [];
        private readonly Point viewport = new(1600, 900);
        private double seconds;
        private Point pointer = new(20, 60);
        internal InteractionScenarioEngine Engine { get; } = new();
        internal TableApplication.Probe View => application.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;

        internal Runner(string profile, string scenario, GameSnapshot state, List<object> trace, List<object> performance)
        {
            this.scenario = scenario; this.trace = trace; this.performance = performance;
            string directory = Path.Combine(profile, scenario); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            Engine.Present(state);
            application = new(Engine, directory, initialState: Engine.State);
            application.Automated = true; application.Initialize();
        }
        internal PresentationFrame Tick(string label) => Input(pointer, label: label);
        internal PresentationFrame Input(Point point, ButtonState left = ButtonState.Released,
            ButtonState right = ButtonState.Released, string label = "input", KeyboardState? keys = null)
        {
            pointer = point; seconds += 1.0 / 60;
            application.Update(Time(), new(point.X, point.Y, 0, left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released), keys ?? new(), viewport);
            return Compose(label);
        }
        internal PresentationFrame Compose(string label)
        {
            var timer = Stopwatch.StartNew(); Frame = application.ComposePresentation(Time()); timer.Stop();
            composeMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
            trace.Add(new { scenario, label, seconds, revision = View.Match?.Revision, decision = View.Match?.Decision?.Id,
                commands = Frame.Drawing.Commands.Count, cards = Frame.Cards.Count, hits = Frame.Hits.Count,
                replies = Engine.Replies.Count, text = Text(Frame) });
            if (View.Error.Length > 0) throw new InvalidOperationException(View.Error);
            return Frame;
        }
        private GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
        internal void Settle(string label) { Tick(label + "-restore"); Tick(label + "-settled"); }
        internal void Present(GameSnapshot state) { Engine.Present(state); View.Accept(Engine.State); }
        internal TableHit Hit(string id) => Frame.Hits.LastOrDefault(hit => hit.Id == id)
            ?? throw new InvalidOperationException("Missing production input target: " + id);
        internal void ClickHit(string id) { var point = PointFor(Hit(id)); Input(point, left: ButtonState.Pressed, label: "press-" + id); Input(point, label: "release-" + id); }
        internal void ClickCard(string id, bool originalHandSlot = false, bool right = false)
        {
            var card = Frame.Cards.Single(card => card.Id == id);
            var hit = Frame.Hits.Last(item => item.Id == card.Hit.Id && item.Scope == card.Hit.Scope);
            var point = originalHandSlot ? card.Rest.Point(.5f, .1f).ToPoint() : PointFor(hit);
            if (View.HitAt(point)?.Id != hit.Id) throw new InvalidOperationException("Card is covered at its original pointer target: " + id);
            Input(point, left: right ? ButtonState.Released : ButtonState.Pressed,
                right: right ? ButtonState.Pressed : ButtonState.Released, label: "press-card-" + id);
            Input(point, label: "release-card-" + id);
        }
        internal (Pose Reader, Pose Anchor, Point Drop) DragReaderCard(string id)
        {
            ClickCard(id); Settle("reader-before-drag");
            var card = Frame.Cards.Single(card => card.Id == id);
            var start = card.Display.Center.ToPoint();
            if (!card.Focused || View.HitAt(start)?.Card?.VisualId != id)
                throw new InvalidOperationException("The expanded card face is not exposed for a reader-origin drag: " + id);
            var drop = start + new Point(80, -20);
            Input(start, left: ButtonState.Pressed, label: "press-reader-face-" + id);
            Input(drop, left: ButtonState.Pressed, label: "deliberate-reader-drag-" + id);
            return (card.Display, card.Rest, drop);
        }
        internal Point PointFor(TableHit hit) => TryPointFor(hit, out var point) ? point
            : throw new InvalidOperationException("No exposed production pointer point for " + hit.Id);
        internal bool TryPointFor(TableHit hit, out Point point)
        {
            bool Matches(Point candidate) => candidate.X >= 0 && candidate.Y >= 0 && candidate.X < viewport.X && candidate.Y < viewport.Y
                && View.HitAt(candidate) is { } actual && actual.Id == hit.Id && actual.Scope == hit.Scope;
            point = new(Math.Clamp(hit.Bounds.Center.X, 0, viewport.X - 1), Math.Clamp(hit.Bounds.Center.Y, 0, viewport.Y - 1));
            if (Matches(point)) return true;
            for (int y = Math.Max(0, hit.Bounds.Top); y < Math.Min(viewport.Y, hit.Bounds.Bottom); y++)
                for (int x = Math.Max(0, hit.Bounds.Left); x < Math.Min(viewport.X, hit.Bounds.Right); x++) {
                    var candidate = new Point(x, y);
                    if (Matches(candidate)) { point = candidate; return true; }
                }
            return false;
        }
        internal void Until(Func<bool> ready, string label)
        {
            var deadline = Stopwatch.StartNew();
            while (!ready()) {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(label);
                Tick(label + "-pump"); Thread.Yield();
            }
            Tick(label);
        }
        public void Dispose()
        {
            performance.Add(new { scenario, frames = composeMilliseconds.Count,
                composeMilliseconds = composeMilliseconds.Sum(),
                meanComposeMilliseconds = composeMilliseconds.Average(),
                composedFramesPerSecond = 1000 * composeMilliseconds.Count / Math.Max(.001, composeMilliseconds.Sum()),
                note = "Observed composition throughput only; no GPU frame-rate threshold." });
            application.Dispose();
        }
    }

    private static GameSnapshot Example()
    {
        Card Card(string id, string name) => new() { VisualId = id, CombatId = id, Name = name,
            Type = "Creature", Power = 2, Toughness = 2, Selectable = true };
        var hand = new[] { Card("hand-a", "Public hand alpha"), Card("hand-b", "Public hand beta") };
        var ward = Card("public-ward", "Public ward creature") with { States = [
            new("ward-two", "Ward {2}", "ability", 2, null, "Each instance is an effective public ability.", "ward"),
            new("protection-blue", "Protection from blue", "ability", 1, null, "A public qualified protection ability.", "protection")
        ] };
        var players = Enumerable.Range(0, 4).Select(seat => new Player(seat, seat == 0 ? "You" : "Opponent " + seat,
            40, seat == 0, false, seat == 0 ? new() { ["C"] = 2 } : [], [
                new("Hand", seat == 0 ? 2 : 7, seat == 0 ? hand : [], null),
                new("Battlefield", seat == 0 ? 1 : 0, seat == 0 ? [ward] : [], null),
                new("Command", 0, [], null), new("Library", 80, [], null), new("Graveyard", 0, [], null), new("Exile", 0, [], null)
            ]) { States = seat == 0 ? [new("future-track", "Momentum", "progress", 6, 6, "An unfamiliar public track.")] : [] }).ToArray();
        return new("headless-production", "playing", null, null, 0, 4, "Main phase", "MAIN1", 0, players, [], null,
            new() { Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true, Message = "You have priority." }) {
                States = [new("day-night", "Day", "status", null, null, "It is day for the whole table.", "day-night")]
            };
    }
    private static GameSnapshot DenseExample()
    {
        var source = Example();
        return source with { Id = "headless-dense", Players = source.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Count = 20,
                Cards = Enumerable.Range(0, 20).Select(index => new Card { VisualId = player.Id == 1 && index == 19 ? "" : $"dense-{player.Id}-{index}", CombatId = $"dense-{player.Id}-{index}",
                    Name = player.Id == 1 && index == 19 ? "Face-down card" : $"Public creature {player.Id}/{index}",
                    FaceDown = player.Id == 1 && index == 19, Type = "Creature", Power = 2 + index, Toughness = 3 + index }).ToArray()
            }).ToArray()
        }).ToArray() };
    }
}
