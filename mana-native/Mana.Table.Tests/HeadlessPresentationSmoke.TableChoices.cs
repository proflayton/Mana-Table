using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckTableChoices(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 900) }) {
            foreach (bool reduced in new[] { false, true }) {
                using var run = new ChoiceRun(profile, $"public-plan-observer-{size.X}-{reduced}", size,
                    ChoiceState(PublicChoiceKind.Block, 1), reduced);
                int assignedArrows = run.Frame.Drawing.Commands.OfType<DrawCommand.Arrow>().Count();
                var preview = new TableChoicePreview(new(TableInspectionKind.BattlefieldCard, "choice-source"),
                    new(TableChoiceTargetKind.BattlefieldCard, "choice-target-a"));
                run.Port.RemotePreview = preview;
                run.Until(() => run.View.TableChoiceCues is [{ Preview: var value }] && value == preview);
                check(ChoicePaint(run, "choice-source", "choice-target-a") && assignedArrows > 0
                        && run.Frame.Drawing.Commands.OfType<DrawCommand.Arrow>().Count() == assignedArrows,
                    $"Remote consideration has separated dashes and an open arrowhead at the exact card edges while assigned combat retains its solid arrows at {size.X}, reduced={reduced}");
                var other = run.Card("choice-source-other");
                check(!run.Frame.Drawing.Commands.OfType<DrawCommand.CardFrame>().Any(command => command.Width == 2
                        && command.Color == new Color(187, 148, 224) && command.Pose.Center == other.Display.Center
                        && command.Pose.Size == other.Display.Size + new Vector2(9)) && run.Rules.Commands.IsEmpty,
                    $"A considered source is an exact occurrence, not another same-named card or a game command at {size.X}, reduced={reduced}");
                run.Capture("considered-block");
                var oldPose = run.Card("choice-source").Display.Center;
                bool continuous = true;
                for (int index = 0; index < 8; index++) {
                    run.Input(new(10, 30), elapsed: TimeSpan.FromMilliseconds(50), keys: new(Keys.W));
                    continuous &= ChoicePaint(run, "choice-source", "choice-target-a");
                }
                check(continuous && run.Card("choice-source").Display.Center != oldPose,
                    $"A peer's consideration follows the physical source and destination throughout camera movement at {size.X}, reduced={reduced}");
                run.Input(new(10, 30), keys: new(Keys.Home)); run.Input(new(10, 30));
                run.Click(run.CardPoint("choice-source"));
                run.Until(() => run.Card("choice-source").Focused);
                var reading = run.Card("choice-source");
                check(reading.Display.Size != reading.Rest.Size && ChoicePaint(run, "choice-source", "choice-target-a")
                        && run.Rules.Commands.IsEmpty,
                    $"Locally enlarging a considered card leaves its peer rim and dashed line at the resting table slot at {size.X}, reduced={reduced}");
                run.Capture("reader-keeps-physical-plan");
                run.Input(new(10, 30), keys: new(Keys.Escape)); run.Input(new(10, 30));
                preview = preview with { Target = new(TableChoiceTargetKind.BattlefieldCard, "choice-target-b") };
                run.Port.RemotePreview = preview;
                run.Until(() => run.View.TableChoiceCues is [{ Preview: var value }] && value == preview);
                check(ChoicePaint(run, "choice-source", "choice-target-b") && !ChoicePaint(run, "choice-source", "choice-target-a"),
                    $"Changing a considered blocker destination replaces the old line rather than leaving a pointer trail at {size.X}, reduced={reduced}");
                run.Port.RemotePreview = null;
                run.Until(() => run.View.TableChoiceCues.IsEmpty);
                check(ChoiceLines(run).Length == 0 && run.Rules.Commands.IsEmpty,
                    $"Cancelling another player's consideration removes its line without removing assigned combat or submitting a game action at {size.X}, reduced={reduced}");
                run.Port.RemotePreview = preview;
                run.Until(() => !run.View.TableChoiceCues.IsEmpty);
                run.Input(new(10, 30), active: false);
                check(run.View.TableChoiceCues.IsEmpty && ChoiceLines(run).Length == 0,
                    $"Background input clears received public plans instead of painting activity beneath another application at {size.X}, reduced={reduced}");
            }

            using (var run = new ChoiceRun(profile, "public-plan-local-player-target-" + size.X, size,
                ChoiceState(PublicChoiceKind.Attack, 1))) {
                var preview = new TableChoicePreview(new(TableInspectionKind.BattlefieldCard, "choice-source"),
                    new(TableChoiceTargetKind.Player, "0"));
                run.Port.RemotePreview = preview;
                run.Until(() => run.View.TableChoiceCues is [{ Preview: var value }] && value == preview);
                var geometry = run.View.TableScene!.Seat(0).Geometry;
                var hero = geometry.Hero;
                var heads = ChoiceLines(run).GroupBy(line => line.To).Where(group => group.Count() == 2).ToArray();
                float phaseRadius = geometry.TurnControls.Markers.Max(marker => Vector2.Distance(marker, hero))
                    + TurnControlLayout.MarkerRadius;
                bool targetRing = run.Frame.Drawing.Commands.OfType<DrawCommand.Ring>().Any(command => command.Center == hero
                    && command.Radius == 88 && command.Width == 1.5f && command.Color == new Color(187, 148, 224) * .65f);
                check(targetRing && run.Frame.Hits.Any(hit => hit.Id.StartsWith("phase-stop:", StringComparison.Ordinal))
                        && heads.Length == 1 && Vector2.Distance(heads[0].Key, hero) > phaseRadius + 1
                        && heads[0].All(wing => Vector2.Distance(wing.From, hero) > phaseRadius)
                        && run.Rules.Commands.IsEmpty,
                    "A peer's considered attack at your seat shows a quiet portrait ring and its open arrowhead clears the entire visible phase wheel at " + size.X);
                run.Capture("considered-attack-clear-phase-wheel");
            }

            using (var run = new ChoiceRun(profile, "public-plan-attacker-" + size.X, size, ChoiceState(PublicChoiceKind.Attack, 0))) {
                run.Click(run.CardPoint("choice-source"));
                run.Input(run.PlayerPoint(1));
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice?.Target is { Kind: TableChoiceTargetKind.Player, Id: "1" });
                check(run.View.SelectedCombat == "choice-source" && run.Port.Updates.Last().Choice is {
                        Source.Kind: TableInspectionKind.BattlefieldCard, Source.Id: "choice-source" }
                        && run.Rules.Commands.IsEmpty,
                    "Selecting a real attacker and pointing at its legal defending player publishes consideration without staging an attack at " + size.X);
                run.Input(run.CardPoint("choice-walker"));
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice?.Target is { Kind: TableChoiceTargetKind.BattlefieldCard, Id: "choice-walker" });
                check(run.Rules.Commands.IsEmpty,
                    "Moving from a defending player to a legal planeswalker replaces the considered endpoint without a rules command at " + size.X);
                run.Click(run.CardPoint("choice-source-other"));
                run.Input(run.PlayerPoint(1));
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice?.Source.Id == "choice-source-other");
                check(run.Port.Updates.Last().Choice?.Target is { Kind: TableChoiceTargetKind.Player, Id: "1" }
                        && run.Rules.Commands.IsEmpty,
                    "Selecting a second same-named attacker publishes that exact physical occurrence rather than a group representative at " + size.X);
                run.Input(run.PlayerPoint(1), keys: new(Keys.Escape)); run.Input(run.PlayerPoint(1));
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice == null);
                check(run.View.SelectedCombat == null && run.Rules.Commands.IsEmpty,
                    "Escape clears selected-attack consideration without staging or confirming a declaration at " + size.X);
                run.Click(run.CardPoint("choice-source")); run.Click(run.PlayerPoint(1));
                run.Until(() => run.Rules.Commands.Count == 1);
                check(run.Rules.Commands.Single() is ToggleAttackPlayer { Attacker: "choice-source-key", Player: 1 },
                    "Only the ordinary deliberate target click submits the exact scoped attack after noncommitting consideration at " + size.X);
            }

            using (var run = new ChoiceRun(profile, "public-plan-blocker-" + size.X, size, ChoiceState(PublicChoiceKind.Block, 0))) {
                run.Input(run.CardPoint("choice-source"), ButtonState.Pressed);
                run.Input(run.CardPoint("choice-target-a"), ButtonState.Pressed);
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice?.Target is { Kind: TableChoiceTargetKind.BattlefieldCard, Id: "choice-target-a" });
                check(run.Port.Updates.Last().Choice?.Source.Id == "choice-source" && run.Rules.Commands.IsEmpty,
                    "Dragging a blocker over a legal attacking creature shares its considered pairing before any drop at " + size.X);
                run.Input(run.CardPoint("choice-target-a"), ButtonState.Pressed, keys: new(Keys.Escape));
                run.Input(run.CardPoint("choice-target-a"));
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice == null);
                check(run.Rules.Commands.IsEmpty,
                    "Cancelling a blocker drag clears its preview and the later mouse release does not stage a block at " + size.X);
                run.Click(run.CardPoint("choice-source")); run.Click(run.CardPoint("choice-target-b"));
                run.Until(() => run.Rules.Commands.Count == 1);
                check(run.Rules.Commands.Single() is ToggleBlock { Attacker: "choice-target-b-key", Blocker: "choice-source-key" },
                    "The existing block click still submits its ordinary exact scoped assignment after a cancelled preview at " + size.X);
            }

            using (var run = new ChoiceRun(profile, "public-plan-public-target-" + size.X, size, ChoiceState(PublicChoiceKind.Target, 0))) {
                run.Input(run.CardPoint("choice-target-a"));
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice?.Target is { Kind: TableChoiceTargetKind.BattlefieldCard, Id: "choice-target-a" });
                check(run.Port.Updates.Last().Choice is { Source.Id: "choice-source" } && run.Rules.Commands.IsEmpty,
                    "A current public ability source uses the actual selectable battlefield hit for its considered target without selecting it at " + size.X);
                run.Capture("public-source-target-aim");
                run.Input(run.PlayerPoint(1));
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice?.Target is { Kind: TableChoiceTargetKind.Player, Id: "1" });
                check(run.Rules.Commands.IsEmpty,
                    "A public-source target preview follows a legal player recipient without submitting SelectPlayer at " + size.X);
                run.Publish(run.Rules.State with { Revision = 101, BoardRevision = 11, PublicChoice = null });
                run.Until(() => run.Port.Updates.LastOrDefault()?.Choice == null);
                check(run.View.TableChoiceCues.IsEmpty && run.Rules.Commands.IsEmpty,
                    "Retiring a public target context clears consideration while leaving the required rules choice unanswered at " + size.X);
            }
        }

        foreach (string kind in new[] { "private-hand", "search", "generic-choice", "missing-context" }) {
            var state = ChoiceState(PublicChoiceKind.Target, 0) with { PublicChoice = null };
            var source = kind == "private-hand" ? state.Viewer!.Zone("Hand").Cards[0] : state.Viewer!.Zone("Battlefield").Cards[0];
            var context = new DecisionContext(Source: new DecisionSource(CardId: source.VisualId, Card: source,
                Zone: kind == "private-hand" ? "Hand" : "Battlefield"));
            DecisionPayload payload = kind == "generic-choice"
                ? new EntitySelectionPayload(new("Confirm", false), new("Cancel", true), [], new(1, 1))
                : new TargetSelectionPayload(new("Confirm", false), new("Cancel", true), new(1, 1), [1],
                    Search: kind == "search" ? new("Hand", "Basic land") : null);
            state = state with { Decision = new("private-" + kind, context, payload) };
            using var run = new ChoiceRun(profile, "public-plan-negative-" + kind, new(1000, 740), state);
            run.Input(run.CardPoint("choice-target-a"));
            for (int index = 0; index < 20; index++) run.Input(run.CardPoint("choice-target-a"), elapsed: TimeSpan.FromMilliseconds(50));
            check(run.Port.Updates.Count > 0 && run.Port.Updates.All(update => update.Choice == null && update.ChoiceContextId == null)
                    && run.View.TableChoiceCues.IsEmpty && run.Rules.Commands.IsEmpty,
                "Hovering a public candidate during " + kind + " cannot publish a choice, disclose a source or answer the engine");
            if (kind == "search") run.Capture("hidden-search-no-public-plan");
        }
    }

    private static DrawCommand.Line[] ChoiceLines(ChoiceRun run) => run.Frame.Drawing.Commands.OfType<DrawCommand.Line>()
        .Where(command => command.Width == 2 && command.Color == new Color(187, 148, 224) * .9f).ToArray();

    private static bool ChoicePaint(ChoiceRun run, string sourceId, string targetId)
    {
        var sourceCard = run.Card(sourceId); var targetCard = run.Card(targetId);
        var source = sourceCard.Focused ? sourceCard.Rest : sourceCard.Display;
        var target = targetCard.Focused ? targetCard.Rest : targetCard.Display;
        var end = target.EdgeToward(source.Center, 6); var start = source.EdgeToward(end, 6);
        var lines = ChoiceLines(run);
        var wings = lines.Where(line => Vector2.Distance(line.To, end) < .01f).ToArray();
        var dashes = lines.Where(line => Vector2.Distance(line.To, end) >= .01f).ToArray();
        return lines.Length > 3 && wings.Length == 2 && wings[0].From != wings[1].From
            && dashes.Length > 1 && Vector2.Distance(dashes[0].From, start) < .01f
            && dashes.All(line => Vector2.Distance(line.From, line.To) <= 9.01f)
            && Vector2.Distance(dashes[0].To, dashes[1].From) >= 5.9f
            && !run.Frame.Drawing.Commands.OfType<DrawCommand.Arrow>().Any(command => command.Color == new Color(187, 148, 224) * .9f)
            && run.Frame.Drawing.Commands.OfType<DrawCommand.CardFrame>().Any(command => command.Width == 2
                && command.Color == new Color(187, 148, 224) && command.Pose.Center == source.Center
                && command.Pose.Size == source.Size + new Vector2(9));
    }

    private static GameSnapshot ChoiceState(PublicChoiceKind kind, int actor)
    {
        int other = actor == 0 ? 1 : 0;
        Card Body(string id, string name, string type = "Creature", int toughness = 3) => new() {
            VisualId = id, CombatId = id, Key = id + "-key", Name = name, Type = type,
            Power = 3, Toughness = toughness, Selectable = true
        };
        var source = Body("choice-source", "Same named public creature");
        var sources = new[] { source, Body("choice-source-other", source.Name, toughness: 4) with { Blocking = kind == PublicChoiceKind.Block } };
        var targets = new[] { Body("choice-target-a", "First public attacker"), Body("choice-target-b", "Second public attacker"),
            Body("choice-walker", "Public defending planeswalker", "Planeswalker") };
        var players = Example().Players.Select(player => player with {
            Priority = false, Zones = player.Zones.Select(zone => zone.Name switch {
                "Battlefield" => zone with { Count = player.Id == actor ? sources.Length : player.Id == other ? targets.Length : 0,
                    Cards = player.Id == actor ? sources.ToImmutableArray() : player.Id == other ? targets.ToImmutableArray() : [] },
                "Hand" => zone with { Cards = zone.Cards.Select(card => card with { Key = card.VisualId + "-key" }).ToImmutableArray() },
                _ => zone
            }).ToImmutableArray()
        }).ToImmutableArray();
        var context = new DecisionContext(Source: kind == PublicChoiceKind.Target
            ? new DecisionSource(CardId: source.VisualId, Card: source, Zone: "Battlefield") : null);
        DecisionPayload payload = kind switch {
            PublicChoiceKind.Attack => new AttackPayload(new("Confirm attackers", true)),
            PublicChoiceKind.Block => new BlockPayload(new("Confirm blockers", true)),
            _ => new TargetSelectionPayload(new("Confirm targets", false), new("Cancel", true), new(1, 1), [other])
        };
        var defenders = new Defender[] { new("player", other.ToString(), other, "Defending player"),
            new("card", "choice-walker", other, "Public defending planeswalker") }.ToImmutableArray();
        Combat? combat = kind switch {
            PublicChoiceKind.Attack => new([], sources.Select(card => new AttackOption(card.CombatId, defenders)).ToImmutableArray(), null),
            PublicChoiceKind.Block => new(targets.Take(2).Select((card, index) => new Attack(card.CombatId, actor,
                index == 0 ? ["choice-source-other"] : [], ["choice-source", "choice-source-other"]) {
                    Defender = new("player", actor.ToString(), actor, "Defending player"), Blocked = index == 0
                }).ToImmutableArray(), [], null),
            _ => null
        };
        return Example() with { Revision = 100, BoardRevision = 10, Players = players,
            DecisionOwnerId = actor, PriorityPlayerId = null, ActivePlayerId = kind == PublicChoiceKind.Block ? other : actor,
            Phase = kind == PublicChoiceKind.Attack ? "Declare attackers" : kind == PublicChoiceKind.Block ? "Declare blockers" : "Main phase",
            PhaseKey = kind == PublicChoiceKind.Attack ? "COMBAT_DECLARE_ATTACKERS" : kind == PublicChoiceKind.Block ? "COMBAT_DECLARE_BLOCKERS" : "MAIN1",
            ActionClass = kind == PublicChoiceKind.Attack ? "attack" : kind == PublicChoiceKind.Block ? "block" : "selection",
            Decision = actor == 0 ? new("choice-decision", context, payload) : null,
            Combat = combat == null ? null : combat with { DeclarationId = "choice-declaration" },
            PublicInspection = null, PublicChoice = new("choice-context", 10, actor, kind,
                kind == PublicChoiceKind.Target ? new(TableInspectionKind.BattlefieldCard, "choice-source") : null)
        };
    }

    private sealed class ChoiceRun : IDisposable
    {
        internal readonly ChoiceRules Rules;
        internal readonly ChoicePort Port;
        internal readonly PresentationRun App;
        internal TableApplication.Probe View => App.View;
        internal Presentation.PresentationFrame Frame => App.Frame;
        private readonly string directory;
        private Point pointer = new(10, 30);
        private ButtonState lastLeft = ButtonState.Released;
        internal ChoiceRun(string profile, string name, Point viewport, GameSnapshot initial, bool reduced = true)
        {
            var state = initial with { Id = name };
            Rules = new(state); Port = new(Rules);
            directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = reduced }.Save(directory);
            var setup = new InteractionScenarioEngine();
            App = new(TestTableServices.For(setup) with { Match = Rules, Presence = Port }, directory, state, viewport);
            App.EnableLiveTableStudy(); Input(pointer); Input(pointer);
        }
        internal void Input(Point at, ButtonState left = ButtonState.Released, bool active = true,
            TimeSpan? elapsed = null, KeyboardState? keys = null)
        {
            pointer = at; lastLeft = left;
            App.Tick(elapsed ?? TimeSpan.FromMilliseconds(10), new(at.X, at.Y, 0, left,
                ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released), keys ?? new(), active: active);
        }
        internal Presentation.PresentedCard Card(string id) => Frame.Cards.Single(card => card.Zone == "Battlefield" && card.Card.VisualId == id);
        internal Point CardPoint(string id) => PointFor(Card(id).Hit);
        internal Point PlayerPoint(int id) => PointFor(Frame.Hits.Last(hit => hit.Intent is MatchIntent.UsePlayer use && use.PlayerId == id));
        internal void Click(Point point) { Input(point); Input(point, ButtonState.Pressed); Input(point); }
        internal Point PointFor(TableHit hit)
        {
            var mapping = RenderViewport.Fit(App.Viewport, Frame.DesignHeight);
            for (int y = 1; y < 10; y++) for (int x = 1; x < 10; x++) {
                var logical = new Point(hit.Bounds.Left + hit.Bounds.Width * x / 10, hit.Bounds.Top + hit.Bounds.Height * y / 10);
                var physical = mapping.ToPhysical(logical.ToVector2()).ToPoint();
                if (View.HitAt(mapping.ToLogical(physical))?.Id == hit.Id) return physical;
            }
            throw new InvalidOperationException("No exposed public-plan pointer point: " + hit.Id);
        }
        internal void Publish(GameSnapshot state) { Rules.State = state; App.QueueObservation(state); Input(pointer); }
        internal void Until(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (!ready()) {
                if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Public plan did not reach its required state.");
                Input(pointer, lastLeft); Thread.Sleep(1);
            }
            Input(pointer, lastLeft);
        }
        internal void Capture(string name)
        {
            var options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
            File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new {
                viewport = App.Viewport, pointer, frame = Frame.Observe(),
                choices = View.TableChoiceCues, updates = Port.Updates.ToArray(),
                drawing = new { Frame.Drawing.TextScale, Frame.Drawing.Scale,
                    commands = Frame.Drawing.Commands.Select(command => new {
                        type = command.GetType().Name, data = JsonSerializer.SerializeToElement(command, command.GetType(), options)
                    }).ToArray() }
            }, options));
        }
        public void Dispose() => App.Dispose();
    }

    private sealed class ChoiceRules(GameSnapshot state) : IMatchConnection
    {
        internal GameSnapshot State = state;
        internal readonly ConcurrentQueue<GameCommand> Commands = new();
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ExecuteAsync(GameCommand command)
        {
            if (!GameCommandPermissions.Allows(State, command)) throw new InvalidOperationException("The plan fixture received an unscoped game command.");
            Commands.Enqueue(command); return Task.CompletedTask;
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }

    private sealed class ChoicePort(ChoiceRules rules) : ITablePresence
    {
        public bool SupportsTableChoices => true;
        internal readonly ConcurrentQueue<TablePresenceUpdate> Updates = new();
        internal TableChoicePreview? RemotePreview;
        public Task<TablePresenceResult> ExchangePresenceAsync(TablePresenceUpdate update, CancellationToken cancellation = default)
        {
            Updates.Enqueue(update);
            var state = rules.State;
            var context = state.PublicChoice;
            var preview = context?.PlayerId == state.ViewerId ? update.Choice : RemotePreview;
            var choices = context != null && PublicChoiceFacts.AllowsPreview(state, preview)
                ? ImmutableArray.Create(new TableChoiceCue(context.PlayerId, context.Id, preview!, 1500)) : [];
            return Task.FromResult<TablePresenceResult>(new TablePresenceResult.Current(new(update.GameId,
                update.BoardRevision, update.Sequence, []) { Choices = choices }));
        }
    }
}
