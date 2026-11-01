using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class MatchHeaderInteractionChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        CheckSeatHudReadouts(profile, check);
        CheckManaReadingLateLayers(profile, check);
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (float scale in new[] { 1f, 1.3f }) {
            string path = Path.Combine(profile, $"match-header-{size.X}-{scale}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = scale }.Save(path);
            var stages = FourPlayerTableScenario.Create();
            var engine = new InteractionScenarioEngine(); engine.Present(stages[0].Snapshot);
            var services = TestTableServices.For(engine) with { Social = new HeaderConversation() };
            using var app = new TableApplication(services, path, initialState: engine.State) { Automated = true };
            app.Initialize(); app.EnableLiveTableStudy();
            var view = app.TestPort;
            var lobby = new Lobby("joined", "Playing", null, false, null, true, "", null, [], [], "header-table");
            view.SetConnectedTable(lobby); view.PresentLobby(lobby, true);
            double time = 0;
            PresentationFrame Tick(Point? point = null, ButtonState button = ButtonState.Released)
            {
                time += .016;
                var at = point ?? new(-100, -100);
                app.Update(new(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(.016)),
                    new(at.X, at.Y, 0, button, default, default, default, default), new(), size);
                return app.ReadPresentation();
            }
            var frame = Tick();
            var chat = frame.Hits.Single(hit => hit.Id == "chat-toggle").Bounds;
            engine.Present(stages[2].Snapshot with { States = [new("day-night", "Day", "status", null, null, "It is day.", "day-night")] });
            view.Accept(engine.State); frame = Tick();
            var controls = frame.Hits.Where(hit => hit.Bounds.Top >= 0 && hit.Bounds.Bottom <= 54).ToArray();
            check(controls.Any(hit => hit.Id == "recent-changes") && controls.Any(hit => hit.Id == "table-states")
                    && controls.Single(hit => hit.Id == "chat-toggle").Bounds == chat
                    && controls.All(first => controls.All(second => first.Id == second.Id || !first.Bounds.Intersects(second.Bounds))),
                "The real connected header keeps chat, recent changes, table states and navigation in separate stable slots at both window sizes and text scales");
            Point Physical(string id)
            {
                var hit = frame.Hits.Single(hit => hit.Id == id);
                var viewport = RenderViewport.Fit(size, frame.DesignHeight);
                var pixel = viewport.ToPhysical(hit.Bounds.Center.ToVector2());
                var physical = new Point((int)MathF.Round(pixel.X), (int)MathF.Round(pixel.Y));
                check(view.HitAt(viewport.ToLogical(physical))?.Id == id,
                    "The visible header control owns its actual physical pointer target: " + id);
                return physical;
            }
            foreach (var control in controls) Physical(control.Id);
            void Click(string id) { var at = Physical(id); Tick(at, ButtonState.Pressed); frame = Tick(at); }
            Click("chat-toggle");
            check(view.ChatOpen, "Table chat opens from its own visible header button");
            Click("chat-toggle"); Click("recent-changes");
            check(!view.ChatOpen && view.Overlay == "changes" && engine.Replies.Count == 0,
                "Recent changes opens independently of chat without submitting a game action");
        }
    }

    private static void CheckSeatHudReadouts(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
        foreach (float scale in new[] { 1f, 1.3f }) foreach (int count in new[] { 2, 3, 4 }) {
            string path = Path.Combine(profile, $"seat-hud-{count}-{size.X}-{scale}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = scale }.Save(path);
            var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
            var state = baseline with { Players = baseline.Players.Take(count).Select(player => player.Id == baseline.ViewerId ? player : player with {
                Mana = new[] { "W", "U", "B", "R", "G", "C" }.ToImmutableDictionary(symbol => symbol, _ => 1),
                States = [new("counter:energy", "Energy", "counter", 2, null, "Two public energy counters."),
                    new("counter:experience", "Experience", "counter", 3, null, "Three public experience counters.")],
                Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = 7 } : zone.Name != "Command" ? zone : zone with {
                    Count = 2, Cards = [zone.Cards[0], zone.Cards[0] with { VisualId = "hud-partner-" + player.Id }]
                }).ToImmutableArray()
            }).ToImmutableArray() };
            int topId = TableLayout.Opponents(state)[count == 2 ? 0 : 1].Id;
            state = state with { Combat = new([new(state.Viewer!.Zone("Battlefield").Cards[0].VisualId, topId, [], [])], [], null) };
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
            run.EnableLiveTableStudy();
            var view = run.View; PresentationFrame frame = null!; int wheel = 0;
            void Tick(double seconds = .02, Point? point = null, ButtonState left = ButtonState.Released, Keys? key = null) {
                var physical = RenderViewport.Fit(size, view.DesignHeight).ToPhysical((point ?? new Point(-100, -100)).ToVector2() + new Vector2(.5f));
                frame = run.Tick(TimeSpan.FromSeconds(seconds), new((int)physical.X, (int)physical.Y, wheel, left, default, default, default, default),
                    key is { } pressed ? new(pressed) : new());
            }
            Tick(); Tick();
            var snapshot = view.Match;
            var canonical = frame.Scene!.BattlefieldDrawOrder.ToDictionary(card => card.Id, card => card.Pose);
            var plans = view.ReadoutPlans.ToDictionary(pair => pair.Key, pair => pair.Value);
            var viewport = new Rectangle(0, 54, 1600, 724);
            check(plans.Count == count - 1 && plans.Values.All(plan => plan.HiddenStates == 0 && plan.States.Count == 2
                    && plan.Pieces.All(viewport.Contains)) && frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "1 INCOMING"),
                $"{count} seats, {size.X}px {scale:P0}: every opponent owns a complete measured upright HUD with two full counter types, land totals and incoming combat");
            check(plans.All(pair => pair.Value.Text.Single(item => item.Role == SeatReadoutTextRole.LandTotal).Text
                        .StartsWith(state.Players.Single(player => player.Id == pair.Key).Name + " · ", StringComparison.Ordinal))
                    && plans.Values.Select(plan => plan.Text.Single(item => item.Role == SeatReadoutTextRole.LandTotal).Text).Distinct().Count() == plans.Count,
                "Equal land totals at different seats retain distinct complete visible owner names in their measured first line");
            void CheckHud(string journey, bool overview) {
                check(ReferenceEquals(view.Match, snapshot) && engine.Replies.Count == 0
                        && frame.Scene!.BattlefieldDrawOrder.All(card => canonical[card.Id] == card.Pose),
                    journey + ": camera movement preserves the authoritative snapshot, physical occurrences and zero game commands");
                foreach (var (owner, plan) in plans) {
                    var current = view.ReadoutPlans[owner];
                    var badge = view.CounterBadge(owner);
                    check(current.Pieces.SequenceEqual(plan.Pieces) && current.States.Select(item => item.Text).SequenceEqual(plan.States.Select(item => item.Text))
                            && badge is { Chips.Length: 2 } && badge.Chips.All(chip => chip.MeasuredWidth <= chip.AvailableWidth)
                            && current.Text.All(item => frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == item.Text
                                && text.X == item.Bounds.X && text.Y == item.Bounds.Y && text.Size == item.Size))
                            && current.States.All(item => frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == item.Text
                                && text.X == item.Bounds.X + 6 && text.Y == item.Bounds.Y + 2 && text.Size == .6f)),
                        journey + $": opponent {owner}'s complete public data remains at the same full font and fixed measured coordinates");
                    foreach (var item in current.States) {
                        string id = "player-state:" + owner + ":" + item.Id;
                        var hit = frame.Hits.Single(candidate => candidate.Id == id);
                        check(hit.Bounds == item.Bounds && hit.Scope == frame.Stamp.Scope && view.HitAt(item.Bounds.Center)?.Id == id
                                && hit.Intent is MatchIntent.OpenPlayerState intent && intent.PlayerId == owner,
                            journey + ": the visible state control owns the same actual fixed target and current player identity: " + id);
                    }
                    bool TableZoneControl(TableHit hit)
                    {
                        if (hit.Intent is not MatchIntent.OpenZone open) return false;
                        var geometry = frame.Scene!.Seats.FirstOrDefault(seat => seat.PlayerId == open.PlayerId)?.Geometry;
                        return open.Zone == "Command" ? geometry?.ZoneStation != null
                            : geometry?.Piles.Any(pile => pile.Zone == open.Zone && pile.TableAnchored) == true;
                    }
                    var seatHits = frame.Hits.Where(hit => hit.Id.StartsWith("focus:", StringComparison.Ordinal)
                        || hit.Id.StartsWith("player:", StringComparison.Ordinal) || hit.Id.StartsWith("hand-zone:", StringComparison.Ordinal)
                        || hit.Id.StartsWith("zone:", StringComparison.Ordinal) || hit.Id.StartsWith("zone-actions:", StringComparison.Ordinal)
                        || hit.Id is "study-focus-return").ToArray();
                    // A current physical pile/commander follows the table, while
                    // a held card, portrait, reading surface or legacy zone rail
                    // remains fixed. Manual navigation may carry table objects
                    // behind the HUD; that must not make the HUD chase the camera.
                    var fixedHits = seatHits.Where(hit => overview || !TableZoneControl(hit)).ToArray();
                    var fixedCards = frame.Cards.Where(card => overview || !card.Request.UsesTableCamera
                        || card.Focused || card.Layer == CardPresentationLayer.Reader).ToArray();
                    bool clear = current.Pieces.All(piece => fixedHits.All(hit => !hit.Bounds.Intersects(piece)))
                            && frame.Scene.Seats.All(seat => view.ManaPoolBadge(seat.PlayerId)!.Parts.All(part => current.Pieces.All(piece => !part.Bounds.Intersects(piece))))
                            && current.Pieces.All(piece => fixedCards.All(card => !Overlap(card.Display, piece)))
                            && (!overview || current.Pieces.All(piece => frame.Scene.Seats.All(seat => !seat.Geometry.PlaymatBounds!.Value.Intersects(piece))));
                    if (!clear) {
                        string failure = Path.Combine(path, "hud-clearance-first-failure.json");
                        if (!File.Exists(failure)) using (var stream = new FileStream(failure, FileMode.CreateNew, FileAccess.Write))
                            System.Text.Json.JsonSerializer.Serialize(stream, new {
                                journey, overview, owner, size, scale, camera = frame.BoardView, frame.Stamp.Scope,
                                hud = current.Pieces,
                                fixedControls = fixedHits.Select(hit => new { hit.Id, hit.Bounds, tableZone = TableZoneControl(hit),
                                    hit.Scope, intent = hit.Intent.ToString(), collides = current.Pieces.Any(hit.Bounds.Intersects) }).ToArray(),
                                movingZoneControls = seatHits.Where(TableZoneControl).Select(hit => new { hit.Id, hit.Bounds,
                                    collides = current.Pieces.Any(hit.Bounds.Intersects) }).ToArray(),
                                cards = frame.Cards.Select(card => new { card.Id, card.Zone, card.Request.TableAnchored,
                                    card.Request.UsesTableCamera, card.Focused, card.Layer, bounds = card.Display.Bounds,
                                    protectedInThisView = fixedCards.Contains(card), collides = current.Pieces.Any(piece => Overlap(card.Display, piece)) }).ToArray(),
                                mana = frame.Scene.Seats.Select(seat => new { seat.PlayerId, badge = view.ManaPoolBadge(seat.PlayerId) }).ToArray(),
                                scope = "Actual prepared occurrence camera policy and owner/zone station policy; fixed HUD, portrait, mana, held-card and reader protections remain active."
                            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
                    }
                    check(clear,
                        journey + ": the measured HUD clears fixed names, portraits, held cards, readers and mana; overview also clears physical zone controls, command faces and cloth");
                }
            }
            CheckHud("Overview", true);
            if (count > 2) foreach (int owner in plans.Keys) {
                view.OpenBattlefield(owner); Tick(.7);
                check(view.BoardView.Scale > 1 && plans.Values.SelectMany(plan => plan.Pieces).All(piece =>
                        frame.Cards.Where(card => card.Zone == "Battlefield" && card.Request.Owner == owner).All(card => !Overlap(card.Display, piece))),
                    "Focus frames the same complete mat clear of the fixed readable state HUD");
                CheckHud("Focus " + owner, false);
                Tick(key: Keys.Home); Tick(.7); CheckHud("Home", true);
            }
            Tick(.05, key: Keys.W); Tick(.05, key: Keys.W); Tick(); wheel = 480; Tick(point: new(800, 420)); Tick(.7);
            check(view.BoardView.Scale > 1, "Wheel zoom enlarges the physical table independently of the readable fixed HUD");
            CheckHud("Manual pan and zoom", false);
            var first = plans.Values.First(); var chip = first.States[0];
            Tick(point: chip.Bounds.Center); Tick(point: chip.Bounds.Center, left: ButtonState.Pressed); Tick(point: chip.Bounds.Center);
            check(view.Overlay == "player" && engine.Replies.Count == 0,
                "The actual readable public-state chip opens its full details after camera movement without a game command");
        }
        var metrics = new TextMetrics();
        Rectangle[] diagonalPieces = [new(0, 0, 20, 20)];
        Rectangle[] diagonalObstacles = [new(0, 0, 100, 30), new(0, 0, 30, 100)];
        check(DecisionWorkspaceLayout.Translate(diagonalPieces, new(0, 0, 100, 100), diagonalObstacles) == null
                && DecisionWorkspaceLayout.TranslateComplete(diagonalPieces, new(0, 0, 100, 100), diagonalObstacles) == new Rectangle(30, 30, 20, 20)
                && DecisionWorkspaceLayout.TranslateComplete(diagonalPieces, new(0, 0, 100, 100), [new(0, 0, 100, 100)]) == null,
            "Full measured HUD placement finds a clear diagonal slot that one-axis movement misses and reports genuinely full space without intersecting paint");
        var playerLong = FourPlayerTableScenario.Create()[0].Snapshot.Players[1] with {
            States = [new("counter:long", string.Join(" ", Enumerable.Repeat("Full public label", 80)), "counter", 3, null, "Full detail"),
                new("counter:energy", "Energy", "counter", 2, null, "Energy detail")]
        };
        var overflow = Mana.Table.Presentation.SeatReadoutPlan.Build(playerLong, 0, 2, "", false, (text, size, bold) => metrics.TextWidth(text, size, bold, 1.3f),
            (text, size, width, bold) => metrics.TextSize(text, size, width, 80, bold, 1.3f));
        check(overflow.HiddenStates == 2 && overflow.States.Single() is { More: true, Text: "+2 states" }
                && overflow.Bounds.Height < 160 && overflow.States[0].Bounds.Height < 40,
            "An omitted long public state retains a counted detail target without inflating visible chips to its hidden wrapped-text height");
        var constrainedPlayer = playerLong with { States = Enumerable.Range(0, 5).Select(index =>
            new PlayerState("counter:resource-" + index, "Public resource " + index, "counter", index + 1, null, "Full state detail " + index)).ToImmutableArray() };
        SeatReadoutPlan Plan(int width = 224, bool? horizontal = null, bool counted = false) => SeatReadoutPlan.Build(constrainedPlayer, 0, 2,
            "1 INCOMING", true, (text, size, bold) => metrics.TextWidth(text, size, bold, 1.3f),
            (text, size, measureWidth, bold) => metrics.TextSize(text, size, measureWidth, 80, bold, 1.3f), width, horizontal, counted);
        var pinned = Plan(counted: true);
        var admitted = (SeatReadoutPlan.Place([Plan(), Plan(264), Plan(horizontal: false)], pinned, pinned.Bounds, []) as SeatReadoutPlacement.Placed)?.Plan;
        check(ReferenceEquals(admitted, pinned) && admitted!.HiddenStates == 5 && admitted.States.Single() is { More: true, Text: "+5 states" }
                && admitted.Text.Any(item => item.Role == SeatReadoutTextRole.LandTotal)
                && admitted.Text.Any(item => item.Role == SeatReadoutTextRole.LandState)
                && admitted.Text.Any(item => item.Role == SeatReadoutTextRole.Incoming && item.Text == "1 INCOMING")
                && admitted.Pieces.All(pinned.Bounds.Contains),
            "A deliberately constrained HUD tries every full-font arrangement, then retains land/combat facts and the complete counted-state target in its reserved slot");
        check(SeatReadoutPlan.Place([Plan(), Plan(264), Plan(horizontal: false)], pinned, pinned.Bounds, [pinned.Bounds])
                is SeatReadoutPlacement.NoFit { PlayerId: 1, SeatOrdinal: 2 },
            "A truly impossible measured placement returns a typed read-only workspace requirement without throwing or claiming an intersecting plan succeeded");
        CheckOverflowReader(profile, constrainedPlayer.States, check);
        CheckOverflowReader(profile, constrainedPlayer.States, check, impossible: true);
        CheckLongOwnerNames(profile, check);
    }

    private static void CheckLongOwnerNames(string profile, Action<bool, string> check)
    {
        string[] names = ["You", "Witherbloom Pestilence - Secrets of Strixhaven · AI 1",
            "Explorers of the Deep - The Lost Caverns of Ixalan · AI 2", "Veloci-Ramp-Tor - The Lost Caverns of Ixalan · AI 3"];
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) foreach (int count in new[] { 2, 3, 4 })
        foreach (int viewer in new[] { 0, count - 1 }) {
            string path = Path.Combine(profile, $"long-owner-hud-{count}-{size.X}-viewer-{viewer}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(path);
            var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
            var state = baseline with { ViewerId = viewer, Decision = null, DecisionOwnerId = null, PriorityPlayerId = null,
                Players = baseline.Players.Take(count).Select((player, ordinal) => player with {
                    Name = names[ordinal], Mana = new[] { "W", "U", "B", "R", "G", "C" }.ToImmutableDictionary(symbol => symbol, _ => 1),
                    States = [new("counter:energy", "Energy", "counter", 2, null, "Two public energy counters."),
                        new("counter:experience", "Experience", "counter", 3, null, "Three public experience counters.")],
                    Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Cards = [] } : zone.Name != "Command" ? zone : zone with {
                        Count = 2, Cards = [zone.Cards[0], zone.Cards[0] with { VisualId = "long-owner-partner-" + player.Id }]
                    }).ToImmutableArray()
                }).ToImmutableArray() };
            int incomingOwner = state.Players.First(player => player.Id != viewer).Id;
            state = state with { Combat = new([new(state.Viewer!.Zone("Battlefield").Cards[0].VisualId, incomingOwner, [], [])], [], null) };
            var engine = new InteractionScenarioEngine(); engine.Present(state);
            using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
            run.EnableLiveTableStudy();
            var view = run.View; PresentationFrame frame = null!;
            void Tick(Point? point = null, ButtonState left = ButtonState.Released) {
                var physical = RenderViewport.Fit(size, view.DesignHeight).ToPhysical((point ?? new Point(-100, -100)).ToVector2() + new Vector2(.5f));
                frame = run.Tick(TimeSpan.FromSeconds(.02), new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default));
            }
            Tick(); Tick();
            var viewport = new Rectangle(0, 54, 1600, 724);
            foreach (var (owner, plan) in view.ReadoutPlans) {
                var player = state.Players.Single(player => player.Id == owner);
                int ordinal = state.Players.IndexOf(player) + 1;
                var nameplate = frame.Hits.Single(hit => hit.Id == "focus:" + owner);
                var title = plan.Text.Single(item => item.Role == SeatReadoutTextRole.LandTotal);
                check(plan.SeatOrdinal == ordinal && plan.Pieces.All(viewport.Contains)
                        && (title.Text.StartsWith(player.Name + " · ", StringComparison.Ordinal)
                            || title.Text.StartsWith("Seat " + ordinal + " · ", StringComparison.Ordinal))
                        && frame.Drawing.Commands.OfType<DrawCommand.CenterText>().Any(text => text.Value == ordinal.ToString()
                            && nameplate.Bounds.Contains(text.Bounds))
                        && plan.Pieces.All(piece => frame.Hits.Where(hit => hit.Id.StartsWith("focus:", StringComparison.Ordinal)
                            || hit.Id.StartsWith("zone:", StringComparison.Ordinal)).All(hit => !piece.Intersects(hit.Bounds)))
                        && view.ReadoutPlans.Where(other => other.Key != owner).All(other => plan.Pieces.All(piece => other.Value.Pieces.All(area => !piece.Intersects(area)))),
                    $"Long preset-style owner {owner}, {count} seats, {size.X}px, viewer {viewer}: full names or their explicit stable public ordinal remain readable without throwing, overlapping peers or relabelling with the viewing seat");
            }
            var selected = view.ReadoutPlans.Values.First(plan => plan.PlayerId == incomingOwner);
            check(selected.Text.Any(item => item.Role == SeatReadoutTextRole.Incoming && item.Text.Contains("1 INCOMING", StringComparison.Ordinal))
                    && selected.Text.Any(item => item.Role == SeatReadoutTextRole.LandState),
                "Long names preserve actual incoming combat and untapped/tapped land facts in the visible admitted plan");
            var entry = selected.States.First();
            string target = entry.More ? "player-state-more:" + selected.PlayerId : "player-state:" + selected.PlayerId + ":" + entry.Id;
            var hit = frame.Hits.Single(item => item.Id == target);
            check(view.HitAt(hit.Bounds.Center)?.Id == hit.Id && hit.Scope == frame.Stamp.Scope,
                "A long-name owner's visible public-state affordance retains its actual current player and scoped physical target");
            Tick(hit.Bounds.Center); Tick(hit.Bounds.Center, ButtonState.Pressed); Tick(hit.Bounds.Center);
            check(view.Overlay == "player" && frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == state.Players.Single(player => player.Id == incomingOwner).Name)
                    && engine.Replies.Count == 0,
                "Long-name state reading preserves the full unparsed source owner name and public details without a rules command");
        }
    }

    private static void CheckOverflowReader(string profile, ImmutableArray<PlayerState> states, Action<bool, string> check, bool impossible = false)
    {
        string path = Path.Combine(profile, impossible ? "hud-no-fit-workspace-1000-130" : "hud-overflow-reader-1000-130");
        Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(path);
        var baseline = FourPlayerTableScenario.Create()[0].Snapshot;
        var state = baseline with { Players = baseline.Players.Select(player => player.Id != 1 ? player : player with { States = states }).ToImmutableArray(),
            Combat = new([new(baseline.Viewer!.Zone("Battlefield").Cards[0].VisualId, 1, [], [])], [], null) };
        var engine = new InteractionScenarioEngine(); engine.Present(state);
        var size = new Point(1000, 740);
        using var run = new PresentationRun(TestTableServices.For(engine), path, engine.State, size);
        run.EnableLiveTableStudy();
        var view = run.View; PresentationFrame frame = null!;
        if (impossible) view.SetReadoutPlacer((arrangements, reserved, viewport, occupied) => SeatReadoutPlan.Place(arrangements, reserved, viewport,
            reserved.PlayerId == 1 ? [viewport] : occupied));
        void Tick(Point? point = null, ButtonState left = ButtonState.Released) {
            var physical = RenderViewport.Fit(size, view.DesignHeight).ToPhysical((point ?? new Point(-100, -100)).ToVector2() + new Vector2(.5f));
            frame = run.Tick(TimeSpan.FromSeconds(.02), new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default));
        }
        void Click(string id) {
            var hit = frame.Hits.Single(item => item.Id == id);
            check(view.HitAt(hit.Bounds.Center)?.Id == id && hit.Scope == frame.Stamp.Scope,
                "The full public-state journey uses its actual current scoped target: " + id);
            Tick(hit.Bounds.Center); Tick(hit.Bounds.Center, ButtonState.Pressed); Tick(hit.Bounds.Center);
        }
        Tick(); Tick();
        var plan = view.ReadoutPlans.GetValueOrDefault(1);
        if (impossible) {
            check(plan == null && frame.Drawing.Commands.OfType<DrawCommand.CenterText>().Any(text => text.Value == "Read resources")
                    && frame.Hits.Single(hit => hit.Id == "focus:1").Intent is MatchIntent.OpenPlayerState { PlayerId: 1 }
                    && frame.Hits.Single(hit => hit.Id == "player:1").Intent is MatchIntent.UsePlayer { PlayerId: 1 },
                "True no-fit publishes no overlapping HUD and explicitly labels the existing nameplate Read resources while retaining portrait targeting authority");
        } else check(plan is { HiddenStates: > 0 } && plan.States.Any(item => item.More), "Several player-state types expose a counted complete detail reader instead of disappearing");
        string game = view.Match!.Id; string? decision = view.Match.Decision?.Id;
        Click(impossible ? "focus:1" : "player-state-more:1");
        var lands = Mana.Magic.PublicResources.Lands(state.Players[1]);
        check(frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == lands.Total + " lands · " + lands.Untapped + " untapped · " + lands.Tapped + " tapped")
                && frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "1 INCOMING"),
            "The complete read-only player workspace includes actual land totals, tapped/untapped counts and incoming combat");
        foreach (var (item, index) in states.Select((item, index) => (item, index))) {
            var text = frame.Drawing.Commands.OfType<DrawCommand.Text>().Select(value => value.Value).ToArray();
            check(view.Overlay == "player" && text.Contains(Mana.Magic.StateClarity.StateCaption(item)) && text.Contains(item.Detail)
                    && view.Match!.Id == game && view.Match.Decision?.Id == decision && engine.Replies.Count == 0,
                "The counted public-state reader retains every omitted type, exact total and full detail for its current player without a game command: " + item.Id);
            if (index < states.Length - 1) Click("counter-details-next");
        }
        Click("close-overlay");
        check(view.Overlay.Length == 0 && (impossible ? !view.ReadoutPlans.ContainsKey(1) : view.ReadoutPlans[1].HiddenStates == plan!.HiddenStates) && engine.Replies.Count == 0,
            "Closing complete public-state reading restores the same fixed counted facts without changing the rules state");
        if (impossible) {
            view.OpenBattlefield(1); Tick();
            check(view.StudyFocusOwner == 1 && engine.Replies.Count == 0,
                "The Battlefield navigation still focuses the same player when their existing nameplate explicitly offers complete resource reading");
        }
    }

    private static bool Overlap(Pose pose, Rectangle bounds) => TableSurfaceProjectionChecks.Overlap(pose,
        new(new(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f), bounds.Size.ToVector2()));

    private static void CheckManaReadingLateLayers(string profile, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "mana-reading-connected-late-layers"); Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(path);
        var engine = new InteractionScenarioEngine();
        var initial = ManaPaymentScenario.Snapshot(0) with { DecisionOwnerId = 0, PriorityPlayerId = 0 };
        initial = initial with { Players = initial.Players.Select(player => player.Id is 0 or 1 ? player with {
            Mana = ImmutableDictionary<string, int>.Empty.Add("C", player.Id == 0 ? 4 : 9)
        } : player).ToImmutableArray() };
        engine.Present(initial);
        engine.SourceActionRead = (game, decision, key) => new(game, decision, key, ManaPaymentScenario.Spell,
            [new("current-cast", "cast", "Cast Faerie Vandal")]) { Revision = engine.State.Revision };
        var services = TestTableServices.For(engine) with { Social = new HeaderConversation() };
        var size = new Point(1000, 740);
        using var run = new PresentationRun(services, path, engine.State, size);
        var view = run.View;
        var lobby = new Lobby("joined", "Playing", null, false, null, true, "", null, [], [], "header-table");
        view.SetConnectedTable(lobby); view.PresentLobby(lobby, true);
        PresentationFrame Tick(Point? point = null, ButtonState button = ButtonState.Released,
            Microsoft.Xna.Framework.Input.KeyboardState? keys = null) => run.Tick(TimeSpan.FromSeconds(1d / 60),
                new((point ?? new(-100, -100)).X, (point ?? new(-100, -100)).Y, 0, button, default, default, default, default), keys ?? new());
        var frame = Tick(); Tick(); frame = Tick();
        void Click(string id) {
            var hit = frame.Hits.Single(hit => hit.Id == id);
            var pixel = RenderViewport.Fit(size, frame.DesignHeight).ToPhysical(hit.Bounds.Center.ToVector2());
            var point = new Point((int)MathF.Round(pixel.X), (int)MathF.Round(pixel.Y));
            Tick(point, ButtonState.Pressed); frame = Tick(point);
        }
        var chat = frame.Hits.Single(hit => hit.Id == "chat-toggle");
        Click(frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell).Hit.Id);
        for (int index = 0; index < 120 && !frame.Hits.Any(hit => hit.Id == "read-card-action"); index++) { frame = Tick(); Thread.Yield(); }
        check(frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused) && view.ManaJourney != null,
            "The real connected-social variant starts with a current pinned hand reader and bill");
        var bill = view.ManaJourney!;
        string pinnedKey = frame.Cards.Single(card => card.Id == ManaPaymentScenario.Spell && card.Focused).Card.Key;
        Click("chat-toggle"); view.TypeText("Unsent table draft"); frame = Tick();
        check(view.ChatOpen && view.ChatDraft == "Unsent table draft" && frame.Hits.Any(hit => hit.Id == "chat-background"),
            "The connected continuation opens the real chat drawer and enters an unsent draft before mana inspection");
        // The left seat's public pip remains exposed beside the open drawer.
        Click("mana-pool:1:C"); frame = Tick();
        var reading = frame.Observe();
        bool lateTogglePainted = frame.Drawing.Commands.OfType<DrawCommand.CenterText>()
            .Any(text => text.Value == "Table chat" && chat.Bounds.Contains(text.Bounds));
        bool drawerRetired = !view.ChatOpen && view.ChatDraft == "Unsent table draft"
            && !frame.Hits.Any(hit => hit.Id == "chat-background");
        bool readerAndBillRetained = view.ManaPoolDetail is { Total: 9 }
            && frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Card.Key == pinnedKey && card.Focused)
            && view.ManaJourney is { } current && current.SourceId == bill.SourceId && current.Symbols.SequenceEqual(bill.Symbols);
        bool isolated = ManaPaymentSmoke.PoolReadingIsolated(view);
        File.WriteAllText(Path.Combine(path, "mana-reading-chat-checkpoint.json"), System.Text.Json.JsonSerializer.Serialize(new {
            stage = "after-real-remote-pip-before-chat-activation-probe", reading, state = view.Match, view.Overlay,
            view.ChatOpen, view.ChatDraft, view.SocialSending, pinnedKey, beforeReading = bill, currentJourney = view.ManaJourney,
            detail = view.ManaPoolDetail is { } detail ? detail with { Parts = detail.Parts.ToArray() } : null,
            sources = view.ManaSources.ToArray(), colors = view.ManaColors.ToArray(),
            cards = frame.Cards.Select(card => new { card.Id, card.Zone, card.Card.Key, card.Focused,
                bounds = card.Display.Bounds, card.ReaderTarget }).ToArray(),
            hits = frame.Hits.Select(hit => new { hit.Id, hit.Bounds, intent = hit.Intent.GetType().Name }).ToArray(),
            chat = new { chat.Id, chat.Bounds, intent = chat.Intent.GetType().Name,
                resolvedIntent = view.HitAt(chat.Bounds.Center)?.Intent.GetType().Name },
            lateTogglePainted, drawerRetired, readerAndBillRetained, isolated,
            poolReadingClearsContext = ManaPaymentSmoke.PoolReadingClearsContext(view, 1), replies = engine.Replies.Count,
            focusEvidence = "Private focus is not exposed; subsequent real typing and Enter check retired focus without inspecting implementation state."
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        check(lateTogglePainted, "The real late chat toggle remains painted with its complete centered label inside its original button bounds");
        check(drawerRetired, "Opening the exposed mana pip retires the chat drawer while preserving its unsent draft");
        check(readerAndBillRetained, "The remote C9 inspection preserves the exact current pinned hand occurrence and bill");
        check(isolated, "The completed connected mana-reading frame isolates the late chat toggle after Social paints it");
        check(!view.Activate(chat) && view.HitAt(chat.Bounds.Center)?.Intent is Mana.Table.Interaction.UiIntent.NoInteraction
            && engine.Replies.Count == 0,
            "An old chat hit and its current coordinate cannot reopen the drawer or submit commands through mana reading");
        view.TypeText(" must not append"); frame = Tick(keys: new(Microsoft.Xna.Framework.Input.Keys.Enter)); frame = Tick();
        check(view.ChatDraft == "Unsent table draft" && !view.ChatOpen && !view.SocialSending && engine.Replies.Count == 0,
            "Passive mana reading cannot capture typing or send the retired chat draft through Enter");
        Click("close-overlay"); frame = Tick();
        view.TypeText(" still must not append");
        check(frame.Hits.Any(hit => hit.Id == "chat-toggle") && frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused)
            && !view.ChatOpen && view.ChatDraft == "Unsent table draft",
            "Closing mana reading returns the actual chat toggle and valid pin without restoring the drawer or its text focus");
        Click("chat-toggle"); check(view.ChatOpen && view.ChatDraft == "Unsent table draft",
            "A new deliberate chat click opens the preserved unsent draft after mana reading");
        Click("chat-toggle"); Click("mana-pool:0:C");
        frame = Tick(keys: new(Microsoft.Xna.Framework.Input.Keys.F1)); frame = Tick();
        var guide = frame.Observe();
        check(view.TableGuideOpen && frame.Hits.Any(hit => hit.Id == "experience-guide-close")
            && frame.Hits.Any(hit => hit.Id == "experience-guide-done") && !frame.Hits.Any(hit => hit.Id == "mana-reading-surface"),
            "A real F1 guide takeover retains its own late close/back controls instead of being erased by mana input isolation");
        Click("experience-guide-done"); frame = Tick();
        check(!view.TableGuideOpen && view.Overlay.Length == 0 && view.ManaPoolDetail == null
            && !frame.Cards.Any(card => card.Id == ManaPaymentScenario.Spell && card.Focused) && engine.Replies.Count == 0,
            "Back to it closes the higher guide without restoring the cleared mana reader, pinned card or old action");
        File.WriteAllText(Path.Combine(path, "mana-reading-late-layers.json"), System.Text.Json.JsonSerializer.Serialize(new {
            reading, guide, afterGuide = frame.Observe(), replies = engine.Replies.Count,
            scope = "Candidate-only actual connected Social and guide composition; authored current facts, not a rules or human UX claim."
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    private sealed class HeaderConversation : ITableSocial
    {
        public Task<TableConversation> ConversationAsync(string tableId) => Task.FromResult(new TableConversation(tableId, 0, 0, [], []));
        public Task<TableConversation> SendMessageAsync(string tableId, string kind, string text) => ConversationAsync(tableId);
        public Task<TableConversation> SetPlayerNameAsync(string tableId, string name) => ConversationAsync(tableId);
    }
}
