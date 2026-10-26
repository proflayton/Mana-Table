using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckPlayAccess(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            foreach (string cardId in new[] { PlayAccessScenario.ExileSpellId, PlayAccessScenario.FlashbackId,
                PlayAccessScenario.BorrowedId, PlayAccessScenario.ExileLandId }) {
                using var run = new PlayAccessRun(profile, "access-play-" + cardId + "-" + size.X, size);
                run.ScrollTo(cardId);
                var original = run.Frame.Cards.Single(card => card.Id == cardId);
                int hand = run.Connection.State.Viewer!.Zone("Hand").Count;
                run.ClickCard(cardId);
                run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0
                        && run.Frame.Cards.Single(card => card.Id == cardId).Focused
                        && run.Frame.Hits.Any(hit => hit.Id == "source-actions-surface")
                        && run.Connection.State.Viewer!.Zone("Hand").Count == hand,
                    "An adjacent card opens its whole face and current source offer without casting, changing its real zone or inflating the hand at " + size);
                var reader = run.Frame.Cards.Single(card => card.Id == cardId && card.Focused).Display.Bounds;
                var actions = run.Frame.Hits.Last(hit => hit.Id == "source-actions-surface").Bounds;
                check(run.Frame.Hits.Where(hit => hit.Id is "confirm" or "cancel" or "ability-auto-pass-open"
                        || hit.Id.StartsWith("ability-pass:", StringComparison.Ordinal) || hit.Id.StartsWith("ability-undo:", StringComparison.Ordinal))
                        .All(hit => !hit.Bounds.Intersects(reader) && !hit.Bounds.Intersects(actions)),
                    "Live priority and ability-yield controls never overlap the enlarged card or its current cast offers at " + size);
                var current = run.Connection.State;
                var offer = run.Connection.Offers.Last(item => item.SourceCardId == cardId);
                run.Click("read-card-action");
                run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
                check(run.Connection.Commands.Single() is UseOfferedAction used && used.GameId == current.Id
                        && used.DecisionId == current.Decision!.Id && used.Card == offer.SourceCardKey
                        && used.ActionId == offer.Actions.Single().Id
                        && current.PlayAccesses.Single(access => access.CardId == cardId).Routes.All(route => route.Id != used.ActionId)
                        && run.Connection.State.Viewer!.Zone("Hand").Count == hand,
                    "Committing an adjacent card uses the queried opaque current source offer, never the presentation route ID or a fabricated hand action at " + size);
                if (cardId != PlayAccessScenario.ExileLandId) {
                    check(run.Connection.State.Stack.Single().Card?.VisualId == cardId
                            && run.Connection.State.Players.Single(player => player.Id == original.Hit.PlayerId).Zone(original.Zone)
                                .Cards.All(card => card.VisualId != cardId),
                        "The card leaves its actual zone owner for one physical stack occurrence, including another player's exile at " + size);
                }
                trace.Add(new { scenario = "play-access.v1", size, cardId, source = original.Zone,
                    actualOwner = original.Hit.PlayerId, commands = run.Connection.Commands.Count,
                    note = "Production pointer and command check; visual clarity still requires review." });
            }

            foreach (string change in new[] { "new-prompt", "permission-withdrawn", "face-withdrawn" }) {
                using var run = new PlayAccessRun(profile, "access-stale-" + change + "-" + size.X, size);
                run.ClickCard(PlayAccessScenario.ExileSpellId);
                run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
                var old = run.Frame.Hits.Last(hit => hit.Id == "read-card-action");
                var at = run.At(old);
                run.Tick(at, ButtonState.Pressed);
                var state = run.Connection.State;
                var next = state with { Revision = state.Revision + 1,
                    Decision = change == "new-prompt" ? state.Decision! with { Id = state.Decision!.Id + ":replacement" } : state.Decision };
                if (change == "permission-withdrawn") next = next with { PlayAccesses = [] };
                if (change == "face-withdrawn") next = next with { Players = next.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name != "Exile" ? zone : zone with {
                        Cards = zone.Cards.Where(card => card.VisualId != PlayAccessScenario.ExileSpellId).ToImmutableArray()
                    }).ToImmutableArray()
                }).ToImmutableArray() };
                run.Publish(next);
                run.Tick(at);
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0 && !run.View.CurrentHit(old),
                    "Replacing a prompt, withdrawing a grant or withdrawing a face between press and release prevents the captured adjacent-card action from reaching the provider at " + size);
                if (change == "face-withdrawn") check(run.Frame.Cards.All(card => card.Id != PlayAccessScenario.ExileSpellId)
                        && run.Frame.Hits.All(hit => hit.Card?.VisualId != PlayAccessScenario.ExileSpellId),
                    "A lost disclosure removes the adjacent face, enlarged reader and all corresponding pointer targets immediately at " + size);
            }

            using (var run = new PlayAccessRun(profile, "access-overflow-" + size.X, size, 5)) {
                var firstHand = run.Frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Pose)).ToArray();
                string last = run.Frame.Scene.PlayAccess.Groups.SelectMany(group => group.Cards).Last().Scene.Id;
                run.ScrollTo(last);
                run.ClickCard(last);
                run.Until(() => run.Frame.Cards.Any(card => card.Id == last && card.Focused));
                check(run.Connection.Commands.IsEmpty && run.Connection.State.Viewer!.Zone("Hand").Count == 11
                        && run.Frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Pose)).SequenceEqual(firstHand)
                        && run.Frame.Scene.PlayAccess.Scroll > 0 && !run.Frame.Hits.Any(hit => hit.Id is "play-access-next" or "play-access-prev"),
                    "Continuous access scrolling reaches and reads the last actual card beside eleven in hand without paging, moving the hand or casting at " + size);
            }
            using (var run = new PlayAccessRun(profile, "access-off-priority-" + size.X, size, 1)) {
                run.ClickCard(PlayAccessScenario.ExileSpellId);
                run.Until(() => run.Frame.Cards.Any(card => card.Id == PlayAccessScenario.ExileSpellId && card.Focused));
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0 && run.Connection.Offers.IsEmpty
                        && run.Frame.Hits.All(hit => hit.Id != "read-card-action")
                        && run.Connection.State.PlayAccesses.Any(access => access.CardId == PlayAccessScenario.ExileSpellId),
                    "An off-priority grant remains readable beside the hand while no cast query, cast control or provider action is created at " + size);
            }
        }
        using (var run = new PlayAccessRun(profile, "access-multiple-routes", new(1540, 980), 4)) {
            string id = PlayAccessScenario.FlashbackId;
            run.ScrollTo(id);
            var start = run.At(run.Frame.Cards.Single(card => card.Id == id).Hit);
            var drop = new Point(900, 470);
            run.Tick(start, ButtonState.Pressed); run.Tick(drop, ButtonState.Pressed);
            run.Until(() => run.Connection.Offers.Any(offer => offer.SourceCardId == id && offer.Actions.Length == 2), drop, ButtonState.Pressed);
            var offer = run.Connection.Offers.Last(item => item.SourceCardId == id);
            run.Tick(drop);
            run.Until(() => run.Frame.Cards.Any(card => card.Id == id && card.Focused)
                && run.Frame.Hits.Any(hit => hit.Id == "source-action:" + offer.Actions[1].Id));
            check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0
                    && run.Frame.Cards.Count(card => card.Id == id) == 1,
                "Dropping a card with two cast routes opens its single full face and both real offers without choosing a route for the player");
            run.Click("source-action:" + offer.Actions[1].Id);
            run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
            check(run.Connection.Commands.Single() is UseOfferedAction chosen && chosen.ActionId == offer.Actions[1].Id,
                "Choosing the second cast route submits that exact current opaque offer for the shared physical card");
        }
        CheckDelayedPlayAccess(profile, check);
        CheckPlayAccessDrag(profile, check);
        CheckResolvingPlayAccess(profile, check);
        CheckStackPlayAccessFocus(profile, check);
        performance.Add(new { scenario = "play-access.v1", note = "Finite presentation and pointer journeys; not a performance or human-clarity score." });
    }

    private static void CheckStackPlayAccessFocus(string profile, Action<bool, string> check)
    {
        using var run = new PlayAccessRun(profile, "access-stack-hover-and-flight", new(1540, 980), reduced: false);
        string id = PlayAccessScenario.ExileSpellId;
        run.ClickCard(id);
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
        var route = run.Frame.Hits.Last(hit => hit.Id == "read-card-action");
        var click = run.CenterOrAt(route);
        run.Tick(click, ButtonState.Pressed); run.Tick(click);
        run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1, click);
        var spell = run.Frame.Cards.Single(card => card.Id == id && card.Zone == "Stack");
        bool ControlsClear() => run.Frame.Hits.Where(hit => hit.Id is "confirm" or "cancel" or "ability-auto-pass-open"
                || hit.Id.StartsWith("ability-pass:", StringComparison.Ordinal) || hit.Id.StartsWith("ability-undo:", StringComparison.Ordinal))
            .All(hit => run.Frame.Cards.Where(card => card.Playing).All(card => !hit.Bounds.Intersects(card.Display.Bounds)));
        bool clear = ControlsClear();
        check(spell.Display.Contains(click) || run.View.ReferencePreviewCardId == null && run.View.ReferencePreviewBounds == null,
            "Leaving the pointer at the actual cast-route click does not open an unsolicited stack preview outside the moving physical face");
        // The stack header is an explicit reading target larger than its art.
        // Hovering its header-only part must not create a full-card preview.
        var header = run.HeaderOutsideFace(spell);
        run.Tick(header);
        clear &= ControlsClear();
        check(run.View.ReferencePreviewCardId == null && run.View.ReferencePreviewBounds == null,
            "A physical spell's enlarged header hit area retains explicit reading without treating empty header space as a hovered card");
        spell = run.Frame.Cards.Single(card => card.Id == id && card.Zone == "Stack");
        run.Tick(spell.Display.Center.ToPoint());
        clear &= ControlsClear();
        check(run.View.ReferencePreviewCardId == id && run.View.ReferencePreviewBounds != null,
            "Hovering the actual displayed physical stack face still previews that exact current spell");

        int movingFrames = 0;
        for (int frame = 0; frame < 55; frame++) {
            var moving = run.Frame.Cards.Where(card => card.Playing).ToArray();
            if (moving.Length == 0) break;
            movingFrames++;
            clear &= ControlsClear();
            run.Tick(new(20, 60));
        }
        check(movingFrames > 0 && clear && run.Connection.Commands.Count == 1,
            "Ordinary priority and yield controls never cover the physical spell anywhere along its cast flight, and hover creates no game command");
    }

    private static void CheckResolvingPlayAccess(string profile, Action<bool, string> check)
    {
        using var run = new PlayAccessRun(profile, "access-resolving-observation", new(1540, 980), reduced: false);
        string id = PlayAccessScenario.ExileSpellId;
        run.Connection.DeferCastObservation = true;
        var old = run.Frame.Cards.Single(card => card.Id == id).Hit;
        var start = run.At(old);
        var drop = new Point(900, 470);
        var held = drop.ToVector2() + new Vector2(0, -72);
        run.Tick(start, ButtonState.Pressed); run.Tick(drop, ButtonState.Pressed);
        run.Until(() => run.Connection.Offers.Any(offer => offer.SourceCardId == id), drop, ButtonState.Pressed);
        run.Tick(drop);
        run.Until(() => run.Connection.Commands.Count == 1 && !run.View.Busy && run.View.Match is { Status: "resolving", Decision: null });
        for (int n = 0; n < 6; n++) run.Tick(drop);
        var waiting = run.Frame.Cards.Single(card => card.Id == id);
        check(!run.View.Busy && waiting.Zone == "Exile" && waiting.Card.Key.Length == 0
                && Vector2.Distance(waiting.Display.Center, held) < 3 && run.View.CardPaintCount(id) == 1
                && !run.View.CurrentHit(old) && run.Frame.Hits.All(hit => hit.Id is not ("confirm" or "cancel" or "read-card-action"))
                && run.Connection.Commands.Count == 1,
            "A prompt-null resolving observation retains only the submitted card's visible held pose after command completion, while old action keys and priority controls are revoked");
        run.Publish(run.Connection.CompleteCastObservation());
        var arriving = run.Frame.Cards.Single(card => card.Id == id);
        check(arriving.Zone == "Stack" && arriving.Playing && Vector2.Distance(arriving.Display.Center, held) < 200
                && run.View.CardPaintCount(id) == 1 && run.Connection.Commands.Count == 1,
            "The eventual authoritative stack occurrence continues from the held pose across the resolving gap without replaying a command or returning to the exile shelf");
    }

    private static void CheckDelayedPlayAccess(string profile, Action<bool, string> check)
    {
        foreach (string result in new[] { "one-route", "two-routes", "new-prompt", "permission-withdrawn", "face-withdrawn", "escape" }) {
            bool multiple = result == "two-routes";
            using var run = new PlayAccessRun(profile, "access-delayed-" + result, new(1540, 980), multiple ? 4 : 0);
            string id = multiple ? PlayAccessScenario.FlashbackId : PlayAccessScenario.ExileSpellId;
            run.ScrollTo(id);
            run.Connection.DelaySourceReads = true;
            var before = run.Connection.State;
            var source = run.Frame.Cards.Single(card => card.Id == id);
            var start = run.At(source.Hit);
            var drop = new Point(900, 470);
            run.Tick(start, ButtonState.Pressed);
            run.Tick(drop, ButtonState.Pressed);
            run.Until(() => run.Connection.DeferredReads.Count == 1, drop, ButtonState.Pressed);
            // Release while the provider task is still incomplete. The exact
            // eventual offer cannot be known or manufactured by the UI yet.
            run.Tick(drop);
            for (int n = 0; n < 4; n++) run.Tick(drop);
            var retained = run.Frame.Cards.Single(card => card.Id == id);
            check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0
                    && run.Connection.State == before && run.Frame.Cards.Count(card => card.Id == id) == 1
                    && Vector2.Distance(retained.Display.Center, drop.ToVector2() + new Vector2(0, -72)) < 3,
                "Releasing before the source query finishes retains one held public card and sends no speculative command or hand mutation");

            if (result is "new-prompt" or "permission-withdrawn" or "face-withdrawn") {
                var next = before with { Revision = before.Revision + 1,
                    Decision = result == "new-prompt" ? before.Decision! with { Id = before.Decision!.Id + ":replacement" } : before.Decision };
                if (result == "permission-withdrawn") next = next with {
                    PlayAccesses = before.PlayAccesses.Where(access => access.CardId != id).ToImmutableArray()
                };
                if (result == "face-withdrawn") next = next with { Players = before.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name != source.Zone ? zone : zone with {
                        Cards = zone.Cards.Where(card => card.VisualId != id).ToImmutableArray()
                    }).ToImmutableArray()
                }).ToImmutableArray() };
                run.Publish(next);
            } else if (result == "escape") {
                run.Tick(drop, keys: new(Keys.Escape)); run.Tick(drop);
            }

            run.Connection.DelaySourceReads = false;
            var offer = run.Connection.CompleteSourceRead();
            if (result == "one-route") {
                run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
                check(run.Connection.Commands.Single() is UseOfferedAction cast && cast.GameId == before.Id
                        && cast.DecisionId == before.Decision!.Id && cast.Card == offer.SourceCardKey
                        && cast.ActionId == offer.Actions.Single().Id && run.Connection.State.Stack.Single().Card?.VisualId == id,
                    "A released play resumes only after its source query supplies the exact current opaque offer, and sends it once");
            } else if (multiple) {
                run.Until(() => run.Frame.Cards.Any(card => card.Id == id && card.Focused)
                    && run.Frame.Hits.Any(hit => hit.Id == "source-action:" + offer.Actions[1].Id));
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0 && run.Frame.Cards.Count(card => card.Id == id) == 1,
                    "A delayed response containing two routes opens one full card with both exact offers and never selects the first automatically");
                run.Click("source-action:" + offer.Actions[1].Id);
                run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
                check(run.Connection.Commands.Single() is UseOfferedAction chosen && chosen.ActionId == offer.Actions[1].Id,
                    "The delayed route chooser submits the player's explicitly chosen second opaque offer");
            } else {
                for (int n = 0; n < 12; n++) { run.Tick(drop); Thread.Yield(); }
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0
                        && run.Frame.Cards.All(card => card.Id != id
                            || Vector2.Distance(card.Display.Center, drop.ToVector2() + new Vector2(0, -72)) > 100),
                    "A new prompt, withdrawn grant, lost face or Escape cancels a released play and its held pose before a late source response can act");
                // The next explicit source read cannot complete until the old
                // in-flight callback has retired. This supplies an observation
                // barrier for the late result without sleeps or private hooks.
                run.ScrollTo(PlayAccessScenario.ExileLandId);
                run.ClickCard(PlayAccessScenario.ExileLandId);
                run.Until(() => run.Frame.Cards.Any(card => card.Id == PlayAccessScenario.ExileLandId && card.Focused)
                    && run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
                check(run.Connection.Commands.IsEmpty && run.Connection.Attempts == 0,
                    "After the cancelled query's callback retires, an unrelated current reader still cannot resurrect or submit the abandoned play");
            }
        }
    }

    private static void CheckPlayAccessDrag(string profile, Action<bool, string> check)
    {
        foreach (bool reduced in new[] { false, true }) {
            using var run = new PlayAccessRun(profile, "access-drag-" + reduced, new(1540, 980), reduced: reduced);
            string id = PlayAccessScenario.ExileSpellId;
            var from = run.Frame.Cards.Single(card => card.Id == id);
            var start = run.At(from.Hit);
            var drop = new Point(900, 470);
            run.Tick(start, ButtonState.Pressed);
            run.Tick(drop, ButtonState.Pressed);
            run.Until(() => run.Connection.Offers.Any(offer => offer.SourceCardId == id), drop, ButtonState.Pressed);
            var offer = run.Connection.Offers.Last(item => item.SourceCardId == id);
            run.Tick(drop);
            run.Until(() => !run.View.Busy && run.Connection.Commands.Count == 1);
            check(run.Connection.Commands.Single() is UseOfferedAction command && command.Card == offer.SourceCardKey
                    && command.ActionId == offer.Actions.Single().Id && run.Connection.State.Stack.Single().Card?.VisualId == id,
                "Dragging a physical exile card directly onto the table waits for and submits exactly its current opaque cast offer");
            var moving = run.Frame.Cards.Single(card => card.Id == id);
            check(moving.Zone == "Stack" && moving.Playing == !reduced && run.View.CardPaintCount(id) == 1
                    && (reduced || Vector2.Distance(moving.Display.Center, drop.ToVector2() + new Vector2(0, -72)) < 260),
                "An adjacent-card cast preserves the held physical occurrence into the stack flight; reduced motion settles directly without a duplicate fan face"
                    + $" (reduced={reduced}, zone={moving.Zone}, playing={moving.Playing}, paints={run.View.CardPaintCount(id)}, pose={moving.Display.Center}, anchor={from.Display.Center}, held={drop.ToVector2() + new Vector2(0, -72)}, frame={run.Frame.Stamp.Revision}, state={run.Connection.State.Revision})");
            run.Until(() => run.Frame.Cards.Single(card => card.Id == id).Playing == false);
            var stack = run.Frame.Cards.Single(card => card.Id == id);
            var next = run.Connection.Pass();
            run.Publish(next);
            moving = run.Frame.Cards.Single(card => card.Id == id);
            check(moving.Zone == "Battlefield" && moving.Playing == !reduced
                    && (reduced || Vector2.Distance(moving.Display.Center, stack.Display.Center) < 200)
                    && run.Frame.Scene!.PlayAccess.Groups.SelectMany(group => group.Cards).All(item => item.Scene.Id != id),
                "Resolution carries the same occurrence from its real stack pose to the battlefield and retires its old adjacent permission");
        }
    }

    private sealed class PlayAccessRun : IDisposable
    {
        private readonly TableApplication app;
        private readonly Point size;
        private double seconds;
        internal PlayAccessConnection Connection { get; }
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;

        internal PlayAccessRun(string profile, string name, Point size, int stage = 0, bool reduced = true)
        {
            this.size = size;
            string directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = reduced, Sounds = false, TextScale = 1.3f }.Save(directory);
            Connection = new(stage);
            var setup = new InteractionScenarioEngine();
            app = new(new(setup, setup, Connection, setup.InitializeAsync) { SourceActions = Connection },
                directory, initialState: Connection.State) { Automated = true };
            app.Initialize();
            for (int n = 0; n < (reduced ? 4 : 40); n++) Tick(new(20, 60));
        }

        private Point Physical(Point logical)
        {
            var at = RenderViewport.Fit(size, 900).ToPhysical(logical.ToVector2());
            return new((int)MathF.Ceiling(at.X), (int)MathF.Ceiling(at.Y));
        }
        internal void Tick(Point logical = default, ButtonState left = ButtonState.Released, KeyboardState? keys = null)
        {
            seconds += .05; var at = Physical(logical);
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.05)),
                new(at.X, at.Y, 0, left, default, default, default, default), keys ?? new(), size);
            Frame = app.ReadPresentation();
            if (View.Error.Length > 0) throw new InvalidOperationException(View.Error);
        }
        internal void Publish(GameSnapshot state) { Connection.Replace(state); View.Accept(state); Tick(new(20, 60)); }
        internal void Until(Func<bool> ready, Point point = default, ButtonState left = ButtonState.Released)
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!ready()) {
                if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Play-access presentation did not settle at " + size);
                Tick(point, left); Thread.Yield();
            }
        }
        internal Point At(TableHit hit)
        {
            for (int y = Math.Max(0, hit.Bounds.Top + 2); y < Math.Min(900, hit.Bounds.Bottom - 2); y += 3)
                for (int x = Math.Max(0, hit.Bounds.Left + 2); x < Math.Min(1600, hit.Bounds.Right - 2); x += 3) {
                    var at = new Point(x, y);
                    if (View.HitAt(RenderViewport.Fit(size, 900).ToLogical(Physical(at))) is { } current
                        && current.Id == hit.Id && current.Scope == hit.Scope) return at;
                }
            throw new InvalidOperationException("No exposed play-access pointer target " + hit.Id + " at " + size);
        }
        private bool Matches(TableHit hit, Point logical) => View.HitAt(RenderViewport.Fit(size, 900).ToLogical(Physical(logical))) is { } current
            && current.Id == hit.Id && current.Scope == hit.Scope;
        internal Point CenterOrAt(TableHit hit) => Matches(hit, hit.Bounds.Center) ? hit.Bounds.Center : At(hit);
        internal Point HeaderOutsideFace(PresentedCard card)
        {
            var hit = Frame.Hits.Last(item => item.Id == card.Hit.Id && item.Scope == card.Hit.Scope);
            Point? best = null;
            float distance = -1;
            for (int y = Math.Max(0, hit.Bounds.Top + 3); y < Math.Min(900, hit.Bounds.Bottom - 3); y += 5)
                for (int x = Math.Max(0, hit.Bounds.Left + 3); x < Math.Min(1600, hit.Bounds.Right - 3); x += 5) {
                    var point = new Point(x, y);
                    if (card.Display.Contains(point) || !Matches(hit, point)) continue;
                    float candidate = Math.Min(Vector2.DistanceSquared(point.ToVector2(), card.Display.Center),
                        Vector2.DistanceSquared(point.ToVector2(), card.Rest.Center));
                    if (candidate > distance) { best = point; distance = candidate; }
                }
            return best ?? throw new InvalidOperationException("The physical spell exposes no header-only pointer target at " + size);
        }
        internal void Click(string id) { var at = At(Frame.Hits.Last(hit => hit.Id == id)); Tick(at, ButtonState.Pressed); Tick(at); }
        internal void ClickCard(string id) => Click(Frame.Cards.Single(card => card.Id == id).Hit.Id);
        internal void ScrollTo(string id)
        {
            for (int attempt = 0; attempt < 50; attempt++) {
                if (Frame.Cards.Any(card => card.Id == id && Frame.Hits.Any(hit => hit.Id == card.Hit.Id))) {
                    try { _ = At(Frame.Cards.Single(card => card.Id == id).Hit); return; }
                    catch (InvalidOperationException) { }
                }
                var at = Frame.Scene!.PlayAccess.Bounds.Center;
                Tick(at, keys: new(Keys.Right)); Tick(at);
            }
            throw new InvalidOperationException("Continuous access scroll did not expose " + id + " at " + size);
        }
        public void Dispose() => app.Dispose();
    }

    private sealed class PlayAccessConnection : IMatchConnection, ISourceActions
    {
        private readonly ExperienceLabSession lab = new(PlayAccessScenario.Id);
        private GameSnapshot state;
        private int attempts;
        internal GameSnapshot State => Volatile.Read(ref state);
        internal int Attempts => Volatile.Read(ref attempts);
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal ConcurrentQueue<SourceActionSet> Offers { get; } = new();
        internal bool DelaySourceReads { get; set; }
        internal bool DeferCastObservation { get; set; }
        private GameSnapshot? deferredCastObservation;
        internal ConcurrentQueue<(SourceActionSet Offer, TaskCompletionSource<SourceActionSet> Response)> DeferredReads { get; } = new();
        internal PlayAccessConnection(int stage) => state = stage == 0 ? lab.State : lab.Replay(stage)[^1];
        internal void Replace(GameSnapshot next) => Volatile.Write(ref state, next);
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            var current = State;
            if (current.Id != gameId || current.Decision is not { } decision || decision.Id != decisionId)
                throw new EngineCommandRejectedException("Stale access query.");
            var card = current.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
                .SingleOrDefault(item => item.Key == sourceCardKey && !item.FaceDown)
                ?? throw new EngineCommandRejectedException("Undisclosed access source.");
            var access = current.PlayAccesses.SingleOrDefault(item => item.CardId == card.VisualId);
            var actions = decision.Payload is PriorityPayload && current.DecisionOwnerId == current.ViewerId && access != null
                ? access.Routes.Where(route => route.CanStart).Select(route => new SourceAction(
                    decisionId + ":" + sourceCardKey + ":access:" + route.Id,
                    route.Kind == PlayAccessKind.PlayLand ? "play" : "cast",
                    route.Detail + (access.Routes.Length > 1 ? " — " + route.SourceName : ""))).ToImmutableArray()
                : [];
            var offered = new SourceActionSet(gameId, decisionId, sourceCardKey, card.VisualId, actions);
            Offers.Enqueue(offered);
            if (DelaySourceReads) {
                var response = new TaskCompletionSource<SourceActionSet>();
                DeferredReads.Enqueue((offered, response));
                return response.Task;
            }
            return Task.FromResult(offered);
        }
        internal SourceActionSet CompleteSourceRead()
        {
            if (!DeferredReads.TryDequeue(out var read)) throw new InvalidOperationException("No controlled source query is awaiting completion.");
            if (!read.Response.TrySetResult(read.Offer)) throw new InvalidOperationException("Controlled source query was already completed.");
            return read.Offer;
        }
        public Task ExecuteAsync(GameCommand command)
        {
            Interlocked.Increment(ref attempts);
            var current = State;
            if (command is not UseOfferedAction use || use.GameId != current.Id || current.Decision is not { } decision || use.DecisionId != decision.Id
                || decision.Payload is not PriorityPayload || current.DecisionOwnerId != current.ViewerId || current.PriorityPlayerId != current.ViewerId
                || !Offers.Any(offer => offer.GameId == current.Id && offer.DecisionId == decision.Id
                    && offer.SourceCardKey == use.Card && offer.Actions.Any(action => action.Id == use.ActionId))
                || !current.PlayAccesses.Any(access => current.Players.Single(player => player.Id == access.PlayerId).Zone(access.Zone).Cards
                    .Any(card => card.Key == use.Card && card.VisualId == access.CardId && !card.FaceDown)
                    && access.Routes.Any(route => route.CanStart && use.ActionId == decision.Id + ":" + use.Card + ":access:" + route.Id)))
                throw new EngineCommandRejectedException("Only a current queried access offer can cast in this authored journey.");
            var next = lab.Reply(command);
            if (DeferCastObservation) {
                deferredCastObservation = next with { Revision = current.Revision + 2 };
                Replace(current with {
                    Revision = current.Revision + 1, Status = "resolving", Decision = null,
                    PriorityPlayerId = null, DecisionOwnerId = null, ActionClass = "resolving",
                    Players = current.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                        Cards = zone.Cards.Select(card => card with { Key = "", Selectable = false }).ToImmutableArray()
                    }).ToImmutableArray() }).ToImmutableArray(),
                    PlayAccesses = current.PlayAccesses.Select(access => new PlayAccess(access.PlayerId, access.Zone, access.CardId,
                        access.Routes.Select(route => route with { CanStart = false }).ToImmutableArray())).ToImmutableArray()
                });
            } else Replace(next);
            Commands.Enqueue(command);
            return Task.CompletedTask;
        }
        internal GameSnapshot CompleteCastObservation()
        {
            var next = deferredCastObservation ?? throw new InvalidOperationException("No cast observation is deferred.");
            deferredCastObservation = null;
            return next;
        }
        internal GameSnapshot Pass() => lab.Reply(new PassPriority(lab.State.Id, lab.State.Decision!.Id));
    }
}
