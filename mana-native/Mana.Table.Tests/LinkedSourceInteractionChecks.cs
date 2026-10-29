using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Provider offers and public checkpoints are authored; the production reader,
// current command scopes and actual pointer gestures remain under test.
internal static class LinkedSourceInteractionChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string directory = Path.Combine(profile, "linked-source-" + size.X);
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            var initial = new ExperienceLabSession(PreparedSpellScenario.Id).State;
            var provider = new LinkedSourceConnection(initial);
            var setup = new InteractionScenarioEngine();
            using var presentation = new PresentationRun(new(setup, setup, provider, setup.InitializeAsync) { SourceActions = provider },
                directory, initial, size);
            var view = presentation.View;
            PresentationFrame frame = null!;
            Point Physical(Point logical) {
                var at = RenderViewport.Fit(size, 900).ToPhysical(logical.ToVector2());
                return new((int)MathF.Ceiling(at.X), (int)MathF.Ceiling(at.Y));
            }
            void Tick(Point logical = default, ButtonState left = ButtonState.Released) {
                var at = Physical(logical);
                frame = presentation.Tick(TimeSpan.FromSeconds(.05),
                    new(at.X, at.Y, 0, left, default, default, default, default));
            }
            void Until(Func<bool> ready) {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!ready()) {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("Linked spell presentation did not settle at " + size);
                    Tick(new(20, 60)); Thread.Yield();
                }
            }
            Point AtHit(string id) {
                var hit = frame.Hits.Last(hit => hit.Id == id);
                for (int y = hit.Bounds.Top + 2; y < hit.Bounds.Bottom - 2; y += 3)
                    for (int x = hit.Bounds.Left + 2; x < hit.Bounds.Right - 2; x += 3) {
                        var point = new Point(x, y);
                        if (view.HitAt(RenderViewport.Fit(size, 900).ToLogical(Physical(point))) is { } actual
                            && actual.Id == hit.Id && actual.Scope == hit.Scope) return point;
                    }
                throw new InvalidOperationException("No exposed linked spell hit " + id + " at " + size);
            }
            void Click(string id) { var at = AtHit(id); Tick(at, ButtonState.Pressed); Tick(at); }
            string SourceHit(string id) => frame.Cards.Single(card => card.Id == id && card.Zone == "Battlefield").Hit.Id;
            bool LinkedReady() => frame.Hits.Any(hit => hit.Id == "read-card-action")
                && frame.Hits.Any(hit => hit.Id.StartsWith("source-spell:", StringComparison.Ordinal));
            void Publish(GameSnapshot state) { provider.Replace(state); presentation.QueueObservation(state); Tick(new(20, 60)); }
            Tick(new(20, 60));
            check(view.BattlefieldLabels(PreparedSpellScenario.SourceId).Any(label => label.Kind == "prepared")
                    && initial.Viewer!.Zone("Battlefield").Cards.First().States!.Value.Count(state => state.Kind == "ability") == 2,
                "Prepared has a source-attached sigil even when flying and ward fill the keyword slots at " + size);
            Click(SourceHit(PreparedSpellScenario.SourceId)); Until(LinkedReady);
            var text = frame.Observe().Text.Select(item => item.Value).ToArray();
            check(provider.Commands.IsEmpty && view.Overlay.Length == 0
                    && frame.Cards.Single(card => card.Id == PreparedSpellScenario.SourceId).Focused
                    && text.Contains("Ancestral Recall") && text.Contains("{U}") && text.Contains("Exile"),
                "Reading the prepared creature keeps its complete face and exposes its linked spell name, mana and exile origin without committing at " + size);
            string linkedId = frame.Hits.First(hit => hit.Id.StartsWith("source-spell:", StringComparison.Ordinal)).Id;
            var linkedPoint = AtHit(linkedId); Tick(linkedPoint);
            var preview = frame.Hits.SingleOrDefault(hit => hit.Id.StartsWith("source-spell-preview:", StringComparison.Ordinal));
            check(preview != null && !preview.Bounds.Intersects(view.DecisionPanelBounds)
                    && !preview.Bounds.Intersects(frame.Cards.Single(card => card.Id == PreparedSpellScenario.SourceId).Display.Bounds)
                    && !preview.Bounds.Intersects(frame.Hits.Last(hit => hit.Id == "source-actions-surface").Bounds)
                    && frame.Hits.Where(hit => hit.Id == "battlefield-reader-state").All(hit => !preview.Bounds.Intersects(hit.Bounds))
                    && preview.Card == null && provider.Commands.IsEmpty,
                "The linked spell expands read-only beside the full source without covering state, cast controls or the current decision at " + size);
            Tick(linkedPoint, ButtonState.Pressed); Tick(linkedPoint);
            check(provider.Commands.IsEmpty, "Clicking the linked reading surface cannot cast or select its preview face at " + size);
            Tick(new(20, 60));
            var oldAction = frame.Hits.Last(hit => hit.Id == "read-card-action");
            var castPoint = AtHit("read-card-action"); Tick(castPoint, ButtonState.Pressed);
            var unprepared = initial with { Revision = initial.Revision + 1,
                Players = initial.Players.Select(player => player.Id != 0 ? player : player with {
                    Zones = player.Zones.Select(zone => zone.Name == "Exile" ? zone with { Cards = [], Count = 0 }
                        : zone.Name == "Battlefield" ? zone with { Cards = zone.Cards.Select(card => card.VisualId == PreparedSpellScenario.SourceId
                            ? card with { States = card.States!.Value.Where(state => state.Key != "prepared").ToImmutableArray() } : card).ToImmutableArray() } : zone).ToImmutableArray()
                }).ToImmutableArray() };
            provider.Replace(unprepared); presentation.QueueObservation(unprepared); Tick(castPoint);
            check(provider.Commands.IsEmpty && !view.Activate(oldAction)
                    && !frame.Hits.Any(hit => hit.Id is "read-card-action" || hit.Id.StartsWith("source-spell", StringComparison.Ordinal))
                    && !view.BattlefieldLabels(PreparedSpellScenario.SourceId).Any(label => label.Kind == "prepared"),
                "Losing the prepared link between press and release revokes its cast, preview and marker while preserving the creature at " + size);

            Publish(initial with { Revision = initial.Revision + 2 }); Until(LinkedReady);
            Click("read-card-action"); Until(() => !view.Busy);
            check(provider.Commands.SingleOrDefault() is UseOfferedAction command
                    && command.Card == initial.Viewer!.Zone("Battlefield").Cards.First().Key
                    && command.ActionId == LinkedSourceConnection.OfferId,
                "One explicit spell cast submits its current opaque offer through the creature source rather than the read-only linked card at " + size);

            var observing = initial with { Revision = initial.Revision + 3, Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1 };
            Publish(observing);
            Click(SourceHit("opponent-prepared-emeritus"));
            Until(() => frame.Cards.Any(card => card.Id == "opponent-prepared-emeritus" && card.Focused && card.Display.Size.X >= 409));
            var descriptions = frame.Observe().Text.Select(item => item.Value);
            check(descriptions.Any(value => value.Contains("Prepared", StringComparison.Ordinal))
                    && !frame.Hits.Any(hit => hit.Id is "read-card-action" || hit.Id.StartsWith("source-spell:", StringComparison.Ordinal))
                    && provider.Commands.Count == 1 && view.BattlefieldLabels("opponent-prepared-emeritus").Any(label => label.Kind == "prepared"),
                "Another player's prepared state remains readable off-priority without granting a cast or losing its source cue at " + size);
        }
    }

    private sealed class LinkedSourceConnection(GameSnapshot initial) : IMatchConnection, ISourceActions
    {
        internal const string OfferId = "opaque-linked-spell";
        private GameSnapshot state = initial;
        internal GameSnapshot State => Volatile.Read(ref state);
        internal ConcurrentQueue<GameCommand> Commands { get; } = new();
        internal void Replace(GameSnapshot next) => Volatile.Write(ref state, next);
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey) {
            var current = State;
            var source = current.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards).Single(card => card.Key == sourceCardKey);
            if (gameId != current.Id || decisionId != current.Decision?.Id) throw new InvalidOperationException("Stale source query.");
            var spell = current.Viewer!.Zone("Exile").Cards.SingleOrDefault();
            ImmutableArray<SourceAction> offers = source.VisualId == PreparedSpellScenario.SourceId && spell != null
                ? [new(OfferId, "cast", spell.Name + " — " + spell.ManaCost) {
                    Card = spell with { Key = "", Selectable = false, Highlighted = false }, Zone = "Exile" }] : [];
            return Task.FromResult(new SourceActionSet(gameId, decisionId, sourceCardKey, source.VisualId, offers));
        }
        public Task ExecuteAsync(GameCommand command) {
            var current = State;
            if (command is not UseOfferedAction offered || offered.GameId != current.Id || offered.DecisionId != current.Decision?.Id
                || offered.Card != current.Viewer!.Zone("Battlefield").Cards.First().Key || offered.ActionId != OfferId
                || current.Viewer.Zone("Exile").Cards.Length != 1) throw new InvalidOperationException("Stale linked cast.");
            command.Validate(); Commands.Enqueue(command);
            Replace(current with { Revision = current.Revision + 1, Decision = null });
            return Task.CompletedTask;
        }
    }
}
