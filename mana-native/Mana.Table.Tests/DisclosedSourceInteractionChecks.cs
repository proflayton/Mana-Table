using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// A finite provider contract journey, not a simulation of permission-granting
// Magic cards. Exercise the actual current pile face, reader and pointer drop.
internal static class DisclosedSourceInteractionChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string directory = Path.Combine(profile, "disclosed-source-" + size.X);
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            var top = new Card { Key = "library-top-offer", VisualId = "disclosed-library-top", Name = "Offered library creature",
                Type = "Creature", Power = 3, Toughness = 4, Selectable = false };
            var example = PresentationSmoke.Example();
            var state = example with { Id = "disclosed-source", ActivePlayerId = 0, Revision = 1, Combat = null, Stack = [],
                PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
                Players = example.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Cards = [], Count = zone.Name == "Library" ? 60 : 0,
                    TopCard = player.Id == 0 && zone.Name == "Library" ? top : null
                }).ToImmutableArray() }).ToImmutableArray(),
                Decision = new() { Id = "offered-library-prompt", Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true } };
            var provider = new DisclosedSourceConnection(state);
            var setup = new InteractionScenarioEngine();
            var services = new TableServices(setup, setup, provider, setup.InitializeAsync) { SourceActions = provider };
            using var app = new TableApplication(services, directory, initialState: state) { Automated = true };
            app.Initialize(); var view = app.TestPort;
            double seconds = 0;
            Card inspectedSource = top; string inspectedZone = "Library";
            PresentationFrame frame = Tick(new(20, 60));
            Point Physical(Point logical) {
                var mapped = RenderViewport.Fit(size, 900).ToPhysical(logical.ToVector2());
                return new((int)MathF.Ceiling(mapped.X), (int)MathF.Ceiling(mapped.Y));
            }
            PresentationFrame Tick(Point logical, ButtonState left = ButtonState.Released, ButtonState right = ButtonState.Released) {
                seconds += 1.0 / 60; var at = Physical(logical);
                var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
                app.Update(time, new(at.X, at.Y, 0, left, default, right, default, default), new(), size);
                return app.ReadPresentation();
            }
            Point AtHit(string id) {
                var hit = frame.Hits.Single(hit => hit.Id == id);
                for (int y = hit.Bounds.Top + 2; y < hit.Bounds.Bottom - 2; y += 3)
                    for (int x = hit.Bounds.Left + 2; x < hit.Bounds.Right - 2; x += 3) {
                        var point = new Point(x, y);
                        if (view.HitAt(RenderViewport.Fit(size, 900).ToLogical(Physical(point))) is { } actual
                            && actual.Id == hit.Id && actual.Scope == hit.Scope) return point;
                    }
                throw new InvalidOperationException("No exposed current hit " + id + " at " + size);
            }
            Point Source() => AtHit(frame.Cards.Single(card => card.Card.VisualId == inspectedSource.VisualId && card.Zone == inspectedZone).Hit.Id);
            void OpenReader(bool rightClick = false) {
                var point = Source();
                frame = rightClick ? Tick(point, right: ButtonState.Pressed) : Tick(point, ButtonState.Pressed);
                frame = Tick(point);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!frame.Hits.Any(hit => hit.Id == "read-card-action") && DateTime.UtcNow < deadline) {
                    frame = Tick(new(20, 60)); Thread.Yield();
                }
                check(frame.Cards.Any(card => card.Card.VisualId == inspectedSource.VisualId && card.Focused)
                        && frame.Hits.Any(hit => hit.Id == "read-card-action") && provider.Reads.Count > 0
                        && view.Overlay.Length == 0 && !frame.Hits.Any(hit => hit.Id is "activate-ability" or "inspect-zone-use"),
                    "The actual " + inspectedZone + " card uses one nonmodal reader and its exact provider offer at " + size);
            }
            OpenReader(rightClick: true);
            check(provider.Commands.IsEmpty, "Right-click inspection discovers an offer without sending a command at " + size);
            var held = Source(); frame = Tick(held, ButtonState.Pressed); frame = Tick(new(800, 420), ButtonState.Pressed);
            provider.Replace(provider.State with { Revision = 2,
                Players = provider.State.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name != "Library" ? zone : zone with { TopCard = null }).ToImmutableArray()
                }).ToImmutableArray(), Decision = provider.State.Decision! with { SourceCard = top, SourceCardId = top.VisualId } });
            view.Accept(provider.State); frame = Tick(new(800, 420));
            check(!app.AcceptedDrag && provider.Commands.IsEmpty && !frame.Cards.Any(card => card.Focused),
                "Revoking the physical disclosed top cancels a held cast even while its historical decision source remains at " + size);

            state = state with { Revision = 3, Decision = state.Decision! with { Id = "fresh-library-prompt" } };
            provider.Replace(state); view.Accept(state); frame = Tick(new(20, 60));
            OpenReader();
            held = Source(); frame = Tick(held, ButtonState.Pressed); frame = Tick(new(800, 420), ButtonState.Pressed);
            frame = Tick(new(800, 420));
            var completion = DateTime.UtcNow.AddSeconds(5);
            while ((view.Busy || view.Match?.Decision?.Intent != DecisionIntent.Mana) && DateTime.UtcNow < completion) {
                frame = Tick(new(20, 60)); Thread.Yield();
            }
            check(view.Error.Length == 0 && provider.Commands.Single() is UseOfferedAction command
                    && command.GameId == state.Id && command.DecisionId == state.Decision!.Id
                    && command.Card == top.Key && command.ActionId == DisclosedSourceConnection.OfferId
                    && view.Match?.Decision is { Id: "pay-library-cast", Intent: DecisionIntent.Mana },
                "A disclosed top with no generic Selectable flag drags through its exact offered command and presents required payment at " + size);

            var grave = top with { Key = "graveyard-current-offer", VisualId = "graveyard-source", Name = "Offered graveyard spell", Type = "Sorcery" };
            state = state with { Revision = 5, Decision = state.Decision! with { Id = "graveyard-priority" },
                Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name == "Library" ? zone with { TopCard = null }
                        : zone.Name == "Graveyard" ? zone with { Cards = [grave], Count = 1 } : zone).ToImmutableArray()
                }).ToImmutableArray() };
            provider.Replace(state); view.Accept(state); frame = Tick(new(20, 60));
            var pile = AtHit("zone:0:Graveyard"); frame = Tick(pile, ButtonState.Pressed); frame = Tick(pile);
            inspectedSource = grave; inspectedZone = "Graveyard";
            OpenReader(rightClick: true);
            check(view.ZoneName == "Graveyard" && provider.Commands.Count == 1,
                "Browsing and right-clicking a public zone preserves the gallery without casting its selected card at " + size);
            var action = AtHit("read-card-action"); frame = Tick(action, ButtonState.Pressed);
            var revoked = state with { Revision = 6, Decision = state.Decision! with { Id = "revoked-graveyard", SourceCard = grave },
                Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name == "Graveyard" ? zone with { Cards = [], Count = 0 } : zone).ToImmutableArray()
                }).ToImmutableArray() };
            provider.Replace(revoked); view.Accept(revoked); frame = Tick(action);
            check(provider.Commands.Count == 1 && provider.Attempts == 1 && !view.Busy && !frame.Cards.Any(card => card.Focused)
                    && !frame.Hits.Any(hit => hit.Id == "read-card-action"),
                "Removing a public-zone occurrence between press and release revokes its reader and exact offered action despite the historical source face at " + size);
        }
    }

    private sealed class DisclosedSourceConnection(GameSnapshot initial) : IMatchConnection, ISourceActions
    {
        internal const string OfferId = "exact-library-cast";
        private GameSnapshot state = initial;
        internal GameSnapshot State => Volatile.Read(ref state);
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        private int attempts;
        internal int Attempts => Volatile.Read(ref attempts);
        internal ConcurrentQueue<string> Reads { get; } = new();
        internal void Replace(GameSnapshot next) => Volatile.Write(ref state, next);
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey) {
            var current = State;
            var top = current.Viewer!.Zones.SelectMany(zone => zone.TopCard is { } face ? zone.Cards.Append(face) : zone.Cards)
                .SingleOrDefault(card => card.Key == sourceCardKey);
            if (current.Id != gameId || current.Decision is not { Intent: DecisionIntent.Priority } decision || decision.Id != decisionId
                || top is not { FaceDown: false } || top.Key != sourceCardKey)
                throw new InvalidOperationException("Query did not name the current disclosed source.");
            Reads.Enqueue(sourceCardKey);
            return Task.FromResult(new SourceActionSet(gameId, decisionId, sourceCardKey, top.VisualId,
                [new(OfferId, "cast", top.Name)]));
        }
        public Task ExecuteAsync(GameCommand command) {
            Interlocked.Increment(ref attempts);
            var current = State;
            var top = current.Viewer!.Zone("Library").TopCard;
            if (command is not UseOfferedAction offered || current.Decision?.Intent != DecisionIntent.Priority
                || offered.GameId != current.Id || offered.DecisionId != current.Decision.Id
                || top is not { FaceDown: false } || offered.Card != top.Key || offered.ActionId != OfferId)
                throw new InvalidOperationException("The provider rejected an unavailable or expired Library cast.");
            command.Validate(); Commands.Enqueue(command);
            Replace(current with { Revision = current.Revision + 1,
                Decision = new() { Id = "pay-library-cast", Kind = "input", Intent = DecisionIntent.Mana,
                    SourceCard = top, SourceCardId = top.VisualId, OkEnabled = false, CancelEnabled = true } });
            return Task.CompletedTask;
        }
    }
}
