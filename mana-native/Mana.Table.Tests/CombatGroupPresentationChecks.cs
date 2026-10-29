using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// The port publishes authored declaration changes and validates each current
// command. This tests the real pointer/session journey, not Magic combat rules.
internal static class CombatGroupPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        CheckMixedCohorts(check);
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            using (var run = new Journey(profile, "cohorts-" + viewport.X, viewport)) {
                var original = run.Frame.BoardView;
                var originalIds = run.View.TableScene!.Seats.SelectMany(seat => seat.Cohorts).SelectMany(group => group.MemberIds).ToHashSet(StringComparer.Ordinal);
                var oldConfirm = run.Frame.Hits.Single(hit => hit.Id == "confirm");
                run.Click("group-block-begin");
                var own = run.View.TableScene!.Seat(0).Cohorts.Single();
                var incoming = run.View.TableScene.Seat(1).Cohorts.Single();
                check(own.MemberIds.Count == 100 && incoming.MemberIds.Count == 100
                        && run.Frame.BoardView == original && originalIds.SetEquals(run.View.TableScene.Seats.SelectMany(seat => seat.Cohorts).SelectMany(group => group.MemberIds)),
                    "The 100-blocker and 100-attacker armies remain physical source/target card cohorts with a group gesture at " + viewport);
                run.ClickCard(own.Front.Id);
                check(run.Text.Contains("100 blockers") && run.Port.Commands.IsEmpty && !run.Has("confirm"),
                    "Selecting the own physical token group captures its exact 100 members without choosing a single blocker or committing combat at " + viewport);
                run.ClickCard(incoming.Front.Id);
                run.Capture("reviewed-100-pairs");
                check(run.Text.Contains("100 blocks") && run.Text.Contains("0 unpaired attackers")
                        && run.Text.Contains("100 attackers") && run.Port.Commands.IsEmpty,
                    "Selecting the incoming physical cohort previews 100 distinct current pairings and exposes the uncovered count before any engine toggle at " + viewport);
                CompleteActionLabelChecks.Run(run.Frame, "group-block-assign", "Assign 100 blocks", check);
                CheckCombatClearance(run.Frame, check);
                run.View.Activate(oldConfirm);
                check(run.Port.Commands.IsEmpty,
                    "A retained ordinary confirmation cannot bypass group review before the arrangement is staged at " + viewport);
                run.Click("group-block-assign"); run.Move(new(20, 60));
                run.Until(() => !run.View.Busy && run.Has("confirm") && !run.Has("group-block-stop"), "100 staged pairs");
                run.Capture("100-pairs-staged");
                check(run.Port.Commands.Count == 100 && run.Port.Commands.All(command => command is ToggleBlock)
                        && run.View.Match?.Combat?.Attackers.All(attack => attack.BlockerIds.Length == 1) == true
                        && run.View.Match.Decision?.Payload is BlockPayload
                        && run.View.CombatRelations.Any(link => link.Kind == "attack" && link.Count == 100),
                    "One reviewed arrangement stages every exact block while the ordinary Confirm blocks remains a separate final decision at " + viewport);
                check(run.View.TableScene!.Seat(0).Cohorts.Single().MemberIds.Count == 100
                        && run.View.TableScene.Seat(1).Cohorts.Single().MemberIds.Count == 100
                        && originalIds.SetEquals(run.View.TableScene.Seats.SelectMany(seat => seat.Cohorts).SelectMany(group => group.MemberIds))
                        && run.Frame.Drawing.Commands.OfType<DrawCommand.Arrow>().Count() == 2
                        && run.View.CombatRelations.Any(link => link.Kind == "block" && link.Count == 100)
                        && run.Text.Contains("100 blocks"),
                    "The staged army keeps two full physical cohorts and one actual100-edge block relation instead of200tinyfaces and crossing per-card arrows at " + viewport);
                check(!run.Text.Contains("100 attacking \u00b7 100 blocked"),
                    "The actual cohort links carry combat counts without a duplicate summary covering another seat's public piles at " + viewport);
                CheckCombatClearance(run.Frame, check);
                CompleteActionLabelChecks.Run(run.Frame, "combat-review", "Combat", check);
                run.Click("group-block-begin"); run.Click("group-block-cancel");
                check(run.View.TableScene.Seat(0).Cohorts.Single().MemberIds.Count == 100 && run.Has("confirm"),
                    "Canceling a later preview retains the already-staged grouped declaration review at " + viewport);
                run.Click("confirm"); run.Move(new(20, 60)); run.Until(() => !run.View.Busy, "explicit combat confirmation");
                check(run.Port.Commands.Count == 101 && run.Port.Commands.Last() is CompleteBlockers,
                    "Only a separate physical Confirm blocks submits the final declaration after the player can review the arrangement at " + viewport);
                check(run.View.GroupedCombatReview && run.View.TableScene.Seat(0).Cohorts.Single().MemberIds.Count == 100,
                    "The same actual combat retains its grouped review after the owned declaration prompt and DeclarationId disappear at " + viewport);
                run.Observe(run.View.Match! with { Revision = run.View.Match!.Revision + 1, Status = "finished", ActivePlayerId = null });
                run.CloseReader();
                check(run.View.GroupedCombatReview && run.View.TableScene.Seat(0).Cohorts.Single().MemberIds.Count == 100
                        && !run.Has("combat-review"),
                    "A retained terminal combat remains readable while its result dock owns the action area at " + viewport);
            }

            foreach (string ending in new[] { "error", "eliminated" })
            using (var run = new Journey(profile, "result-" + ending + "-" + viewport.X, viewport, initial: Enumerable.Range(0, 100)
                    .Select(index => new BlockAssignment("attacker-" + index.ToString("D3"), "blocker-" + index.ToString("D3"))).ToImmutableArray(),
                    allowErrors: true)) {
                run.Click("group-block-begin"); run.Click("group-block-cancel");
                var state = run.View.Match!;
                run.Observe(state with { Revision = state.Revision + 1, Status = ending == "error" ? "error" : "playing",
                    Error = ending == "error" ? "Authored retained combat failure" : null, Decision = null, DecisionOwnerId = null,
                    Players = state.Players.Select(player => player.Id == 0 && ending == "eliminated"
                        ? player with { Eliminated = true } : player).ToImmutableArray() });
                run.CloseReader();
                check(run.View.GroupedCombatReview && run.View.TableScene!.Seat(0).Cohorts.Single().MemberIds.Count == 100
                        && !run.Has("combat-review") && (ending == "error" ? run.Has("error-leave") || run.Has("return") : run.Has("eliminated-leave")),
                    "The retained " + ending + " combat keeps its physical formation while the return/leave dock owns its controls at " + viewport);
                run.PressCombat();
                check(run.View.Overlay == "combat" && run.Port.Commands.IsEmpty,
                    "Keyboard combat inspection remains available without restoring committing controls after " + ending + " at " + viewport);
            }

            using (var run = new Journey(profile, "inspect-edit-" + viewport.X, viewport, initial: Enumerable.Range(0, 100)
                    .Select(index => new BlockAssignment("attacker-" + index.ToString("D3"), "blocker-" + index.ToString("D3"))).ToImmutableArray())) {
                run.Click("group-block-begin"); run.Click("group-block-cancel");
                string front = run.View.TableScene!.Seat(0).Cohorts.Single().Front.Id;
                run.ReadCard(front);
                check((run.Frame.Cards.Any(card => card.Id == front && card.Focused)
                        || run.View.Overlay == "inspect" && run.Text.Contains("Power / toughness: 1/1")) && run.Port.Commands.IsEmpty,
                    "The physical cohort's actual front opens its full reader without changing any of the100assignments at " + viewport);
                run.CloseReader(); run.ClickCard(front);
                CheckCombatClearance(run.Frame, check);
                foreach (var (id, label) in new[] { ("combat-clear", "Cancel"), ("combat-remove", "Remove"), ("combat-review", "Combat") })
                    CompleteActionLabelChecks.Run(run.Frame, id, label, check);
                run.Click("combat-remove"); run.Move(new(20, 60));
                run.Until(() => !run.View.Busy, "one exact assignment edited");
                check(run.Port.Commands.Count == 1 && run.Port.Commands.Single() is ToggleBlock
                        && run.View.Match?.Combat?.Attackers.Sum(attack => attack.BlockerIds.Length) == 99
                        && run.View.TableScene.Seat(0).Cohorts.Any(group => group.MemberIds.Count == 99),
                    "Editing the current front removes exactly one authoritative block and splits the changed member from its99-equivalent neighbors at " + viewport);
                run.Observe(run.View.Match! with { Revision = run.View.Match!.Revision + 1, PhaseKey = "COMBAT_BEGIN",
                    Combat = run.View.Match.Combat! with { DeclarationId = "" } });
                check(!run.View.GroupedCombatReview,
                    "A new beginning-of-combat phase retires the old formation even if the last combat objects remain published at " + viewport);
            }

            using (var run = new Journey(profile, "actual-edge-count-" + viewport.X, viewport,
                    initial: [new("attacker-000", "blocker-000"), new("attacker-001", "blocker-000")])) {
                run.Click("group-block-begin"); run.Click("group-block-cancel");
                check(run.View.CombatRelations.Any(link => link.Kind == "block" && link.Count == 2)
                        && run.View.TableScene!.Cohort("blocker-000")?.MemberIds.Count == 1
                        && run.View.TableScene.Cohort("attacker-000")?.MemberIds.Count == 2,
                    "A counted combat relation reports two actual assignments for one blocker, rather than guessing edge counts from pile size at " + viewport);
                run.Observe(run.View.Match! with { Revision = run.View.Match!.Revision + 1, PhaseKey = "MAIN2",
                    Combat = run.View.Match.Combat! with { DeclarationId = "" } });
                check(!run.View.GroupedCombatReview,
                    "Second main retires the combat formation even when a provider retains its last public combat record at " + viewport);
            }

            using (var run = new Journey(profile, "all-stop-" + viewport.X, viewport, holdFirst: true)) {
                var original = run.Frame.BoardView;
                run.Click("group-block-begin"); run.Click("group-block-all-own"); run.Click("group-block-all-incoming");
                check(run.Text.Contains("100 blocks") && run.Port.Commands.IsEmpty && run.Has("group-block-cancel"),
                    "All blockers and All incoming populate the same finite review without paging through 100 creatures or sending commands at " + viewport);
                run.Click("group-block-cancel");
                check(run.View.TableScene!.Seat(0).Cohorts.Count == 100 && run.Frame.BoardView == original
                        && run.Has("confirm") && run.Port.Commands.IsEmpty,
                    "Canceling group review restores the ordinary individual declaration with the same camera and no game command at " + viewport);
                run.Click("group-block-begin"); run.Click("group-block-all-own"); run.Click("group-block-all-incoming");
                run.Click("group-block-assign"); run.Move(new(20, 60));
                run.Until(() => run.Port.Started.Task.IsCompleted, "in-flight first pair");
                check(run.Has("group-block-stop") && !run.Has("confirm"),
                    "Take control stays reachable during an in-flight group assignment while final combat confirmation is unavailable at " + viewport);
                run.Click("group-block-stop"); run.Port.Release.TrySetResult();
                run.Move(new(20, 60)); run.Until(() => !run.View.Busy, "stopped first pair observed");
                check(run.Port.Commands.Count == 1 && run.View.Match?.Combat?.Attackers.Sum(attack => attack.BlockerIds.Length) == 1
                        && run.Has("confirm") && !run.Has("group-block-stop"),
                    "Stopping a group reconciles its one committed pair, stops subsequent toggles and restores ordinary review without undo at " + viewport);
                check(run.View.GroupedCombatReview && run.View.TableScene!.Seat(0).Cohorts.Count == 2,
                    "Take control retains the actual one-assigned/99-unassigned physical formation at " + viewport);
                run.Observe(run.View.Match! with { Revision = run.View.Match!.Revision + 1,
                    Combat = run.View.Match.Combat! with { DeclarationId = "new-actual-declaration" } });
                check(!run.View.GroupedCombatReview,
                    "An actual new declaration retires the old explicit grouping context at " + viewport);
            }
        }
    }

    private static void CheckCombatClearance(PresentationFrame frame, Action<bool, string> check)
    {
        var metrics = new TextMetrics();
        var portraitAndControls = frame.Hits.Where(hit => hit.Id is "player:0" or "auto" or "hold"
            || hit.Id.StartsWith("phase-stop:", StringComparison.Ordinal)).Select(hit => hit.Bounds).ToArray();
        var attackLabels = frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.Value == "100 attacking").ToArray();
        check(attackLabels.Length > 0 && attackLabels.All(text => {
            var size = metrics.TextSize(text.Value!, text.Size, text.Width, text.MaxLines, text.Bold, frame.Drawing.TextScale);
            return portraitAndControls.All(bounds => !bounds.Intersects(new(text.X, text.Y, size.X, size.Y)));
        }), "The actual counted attack text clears the local portrait and every current phase/Auto/Hold control");
        var rightSeat = frame.Scene!.Seats.Single(seat => seat.Index == 2);
        // The live pile hit includes its top face and its painted count/zone label.
        var publicPiles = frame.Hits.Where(hit => hit.Id.StartsWith("zone:" + rightSeat.PlayerId + ":", StringComparison.Ordinal))
            .Select(hit => hit.Bounds).ToArray();
        var combatControls = frame.Hits.Where(hit => hit.Id.StartsWith("group-block-", StringComparison.Ordinal)
            || hit.Id is "combat-clear" or "combat-remove" or "combat-review").ToArray();
        check(publicPiles.Length >= 3 && combatControls.Length > 0
                && combatControls.All(control => publicPiles.All(pile => !control.Bounds.Intersects(pile)))
                && !frame.Drawing.Commands.OfType<DrawCommand.Gradient>().Any(gradient => gradient.Bounds == new Rectangle(1250, 623, 328, 88)),
            "Combat review controls and their backdrop leave the right seat's actual public zone faces and labels unobscured");
    }

    private static void CheckMixedCohorts(Action<bool, string> check)
    {
        var state = Projection([new("attacker-000", "blocker-000"), new("attacker-001", "blocker-001")]);
        state = state with { Combat = state.Combat! with { Attackers = state.Combat.Attackers.SetItem(2,
            state.Combat.Attackers[2] with { DefendingPlayerId = 2, Defender = new("player", "2", 2, "Seat 2") }) },
            Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card.CombatId == "blocker-001" ? card with { Power = 2 }
                        : card.CombatId == "blocker-003" ? card with { Tapped = true }
                        : card.CombatId == "blocker-004" ? card with { Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1) }
                        : card).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        var scene = TableScene.Build(state, new(20, 60), null, null, false, _ => false, sharedTableStudy: true, groupCombatCandidates: true);
        check(new[] { "blocker-000", "blocker-001", "attacker-000", "attacker-001", "attacker-002", "blocker-003", "blocker-004" }
                .All(id => scene.Cohort(id)?.MemberIds.Count == 1)
                && scene.Cohort("blocker-005")?.MemberIds.Count == 96 && scene.Cohort("attacker-003")?.MemberIds.Count == 97,
            "Group combat keeps distinct assignments, a different defender, tapped state and counters separate while equivalent unassigned candidates remain physical piles");
        var ordinary = TableScene.Build(state, new(20, 60), null, null, false, _ => false, sharedTableStudy: true);
        check(ordinary.Seat(0).Cohorts.Count == 100 && ordinary.Seat(1).Cohorts.Count == 100,
            "Only a deliberate group-review gesture changes candidate grouping; ordinary individual combat never inherits that presentation mode");
        var capacity = Projection() with { Players = Projection().Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = zone.Cards.Select((card, index) => index switch {
                0 => card with { BlockingCapacity = new(1) }, 1 => card with { BlockingCapacity = new(2) },
                2 => card with { BlockingCapacity = new(null, true) }, 3 => card with { GamePieceType = "card" }, _ => card
            }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        var capacityScene = TableScene.Build(capacity, new(20, 60), null, null, false, _ => false, sharedTableStudy: true, groupCombatCandidates: true);
        check(Enumerable.Range(0, 4).All(index => capacityScene.Cohort("blocker-" + index.ToString("D3"))?.MemberIds.Count == 1)
                && capacityScene.Cohort("blocker-004")?.MemberIds.Count == 96,
            "Intrinsic combat cohorts preserve known one/two/unlimited blocking capacity, unknown capacity and real-card versus token identity even before assignment");
        var unknown = Projection();
        unknown = unknown with { Combat = unknown.Combat! with { Attackers = unknown.Combat.Attackers.Select((attack, index) => index < 2
            ? attack with { HasDefender = false } : attack).ToImmutableArray() } };
        var unknownScene = TableScene.Build(unknown, new(20, 60), null, null, false, _ => false, sharedTableStudy: true, groupCombatCandidates: true);
        check(unknownScene.Cohort("attacker-000")?.MemberIds.Count == 1 && unknownScene.Cohort("attacker-001")?.MemberIds.Count == 1,
            "Absent defender evidence cannot merge combatants through a stale defender field");
        var anonymous = Projection();
        anonymous = anonymous with { Players = anonymous.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = zone.Cards.Select((card, index) => index < 2
                ? card with { VisualId = "", CombatId = "", Key = "" } : card).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        var anonymousScene = TableScene.Build(anonymous, new(20, 60), null, null, false, _ => false, sharedTableStudy: true, groupCombatCandidates: true);
        check(anonymousScene.Seat(0).Battlefield.SelectMany(cluster => cluster.Cards).Where(node => node.Card.VisualId.Length == 0)
                .Count(node => node.GroupCount == 1) == 2,
            "Anonymous public objects missing graph membership keep occurrence-only geometry and cannot acquire inferred combat equivalence");
    }

    private sealed class Journey : IDisposable
    {
        private readonly PresentationRun run;
        internal TableApplication.Probe View => run.View;
        internal PresentationFrame Frame => run.Frame;
        internal BlocksPort Port { get; }
        private Point pointer = new(20, 60);
        internal string Text => string.Join(" ", run.Frame.Drawing.Commands.Select(item => item switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null
        }).Where(value => value != null));
        internal Journey(string profile, string name, Point viewport, bool holdFirst = false, ImmutableArray<BlockAssignment> initial = default,
            bool allowErrors = false)
        {
            string path = Path.Combine(profile, "combat-groups-" + name); Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(path);
            Port = new(holdFirst, initial); var setup = new InteractionScenarioEngine();
            run = new(new(setup, setup, Port, () => Task.CompletedTask), path, Port.State, viewport, allowErrors: allowErrors);
            run.EnableLiveTableStudy(); Tick(); Tick();
        }
        internal bool Has(string id) => run.Frame.Hits.Any(hit => hit.Id == id);
        private TableHit Hit(string id) => run.Frame.Hits.Last(hit => hit.Id == id);
        internal void ClickCard(string id)
        {
            var hit = run.Frame.Hits.Last(hit => hit.Card?.VisualId == id && hit.Zone == "Battlefield");
            pointer = hit.Bounds.Center; Tick(ButtonState.Pressed); Tick();
        }
        internal void Click(string id) { pointer = Hit(id).Bounds.Center; Tick(ButtonState.Pressed); Tick(); }
        internal void Move(Point logical) { pointer = logical; Tick(); }
        internal void ReadCard(string id) {
            pointer = run.Frame.Hits.Last(hit => hit.Card?.VisualId == id && hit.Zone == "Battlefield").Bounds.Center;
            Tick(right: ButtonState.Pressed); Tick();
            for (int i = 0; i < 60; i++) Tick();
        }
        internal void CloseReader() { Tick(keys: new KeyboardState(Microsoft.Xna.Framework.Input.Keys.Escape)); Tick(); }
        internal void PressCombat() { Tick(keys: new KeyboardState(Microsoft.Xna.Framework.Input.Keys.C)); Tick(); }
        internal void Observe(GameSnapshot state) { run.QueueObservation(state); Tick(); }
        internal void Capture(string name) => File.WriteAllText(Path.Combine(View.Profile, name + ".json"),
            JsonSerializer.Serialize(new { frame = run.Frame.Observe(),
                commands = Port.Commands.Select(command => command.GetType().Name) }, new JsonSerializerOptions { WriteIndented = true }));
        private void Tick(ButtonState button = ButtonState.Released, ButtonState right = ButtonState.Released, KeyboardState keys = default)
        {
            var point = RenderViewport.Fit(run.Viewport, View.DesignHeight).ToPhysical(pointer.ToVector2());
            run.Tick(TimeSpan.FromSeconds(1d / 60), new((int)MathF.Ceiling(point.X), (int)MathF.Ceiling(point.Y), 0,
                button, default, right, default, default), keys);
        }
        internal void Until(Func<bool> ready, string step)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!ready() && DateTime.UtcNow < deadline) { Tick(); Thread.Sleep(1); }
            if (!ready()) throw new InvalidOperationException("Group combat presentation stopped at " + step + "; " + View.Error
                + "; commands=" + Port.Commands.Count + "; busy=" + View.Busy + "; text=" + Text);
        }
        public void Dispose() { Port.Release.TrySetResult(); run.Dispose(); }
    }

    private sealed class BlocksPort(bool holdFirst, ImmutableArray<BlockAssignment> initial = default) : IMatchConnection
    {
        private readonly object gate = new();
        private GameSnapshot state = Projection(initial);
        internal GameSnapshot State { get { lock (gate) return state; } }
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(GameCommand command)
        {
            string attacker = "", blocker = "";
            lock (gate) {
                if (!GameCommandPermissions.Allows(state, command)) throw new InvalidOperationException("Unscoped combat command.");
                if (command is ToggleBlock toggle) {
                    attacker = CardViews.FindZoneActionSource(state, toggle.Attacker)!.Value.Card.CombatId;
                    blocker = CardViews.FindZoneActionSource(state, toggle.Blocker)!.Value.Card.CombatId;
                } else if (command is not CompleteBlockers) throw new InvalidOperationException("Unexpected combat command.");
                Commands.Enqueue(command);
            }
            if (holdFirst && Commands.Count == 1) { Started.TrySetResult(); await Release.Task; }
            lock (gate) {
                if (command is CompleteBlockers) { state = state with { Revision = state.Revision + 1, Decision = null, DecisionOwnerId = null,
                    PhaseKey = "COMBAT_DAMAGE", Combat = state.Combat! with { DeclarationId = "" } }; return; }
                var pairs = state.Combat!.Attackers.SelectMany(attack => attack.BlockerIds.Select(id => new BlockAssignment(attack.CardId, id))).ToList();
                var pair = new BlockAssignment(attacker, blocker); if (!pairs.Remove(pair)) pairs.Add(pair);
                state = Projection(pairs.ToImmutableArray(), state.Revision + 1);
            }
        }
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }

    private static GameSnapshot Projection(ImmutableArray<BlockAssignment> assignments = default, long revision = 1)
    {
        var pairs = assignments.IsDefault ? ImmutableArray<BlockAssignment>.Empty : assignments;
        string prompt = "group-block-" + revision;
        Card Token(string id, bool incoming) => new() { VisualId = id, CombatId = id, Key = prompt + ":" + id,
            Name = incoming ? "Incoming Soldier Token" : "Soldier Token", Type = "Creature \u2014 Soldier", GamePieceType = "token",
            Power = 1, Toughness = 1, Attacking = incoming, Blocking = pairs.Any(pair => pair.BlockerId == id),
            Defender = incoming ? "You" : pairs.FirstOrDefault(pair => pair.BlockerId == id)?.AttackerId };
        var blockers = Enumerable.Range(0, 100).Select(index => Token("blocker-" + index.ToString("D3"), false)).ToImmutableArray();
        var attackers = Enumerable.Range(0, 100).Select(index => Token("attacker-" + index.ToString("D3"), true)).ToImmutableArray();
        return new("combat-group-presentation", "playing", null, null, 0, 8, "Declare blockers", "COMBAT_DECLARE_BLOCKERS", 1,
            Enumerable.Range(0, 4).Select(seat => new Player(seat, seat == 0 ? "You" : "Seat " + seat, 40, seat == 0, false,
                ImmutableDictionary<string, int>.Empty, [new("Battlefield", seat is 0 or 1 ? 100 : 0, seat == 0 ? blockers : seat == 1 ? attackers : [], null),
                    new("Hand", 0, [], null), new("Library", 50, [], null), new("Graveyard", 0, [], null), new("Command", 0, [], null), new("Exile", 0, [], null)])).ToImmutableArray(),
            [], new(attackers.Select(card => new Attack(card.CombatId, 0,
                pairs.Where(pair => pair.AttackerId == card.CombatId).Select(pair => pair.BlockerId).ToImmutableArray(),
                blockers.Select(blocker => blocker.CombatId).ToImmutableArray()) { Defender = new("player", "0", 0, "You") }).ToImmutableArray(), [], null) {
                    DeclarationId = "actual-declaration-8"
                }, new(prompt, new(), new BlockPayload(new("Confirm blocks", true)))) {
                    Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, ActionClass = "blocks"
                };
    }
}
