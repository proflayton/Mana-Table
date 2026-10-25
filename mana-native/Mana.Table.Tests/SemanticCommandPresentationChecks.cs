using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Table.Presentation;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// An independently owned interaction protocol, not a second Magic rules engine.
// Its transitions execute commands and change resources/results; assertions do
// not compare a fixture's supplied snapshots with a copy of those snapshots.
internal static class SemanticCommandPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string directory = Path.Combine(profile, "semantic-command-port-" + viewport.X);
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            var provider = new InteractionPort();
            var setup = new InteractionScenarioEngine();
            var services = new TableServices(setup, setup, provider, () => Task.CompletedTask) { SourceActions = provider };
            using var app = new TableApplication(services, directory, initialState: provider.State) { Automated = true };
            app.Initialize();
            var view = app.TestPort;
            double seconds = 0;
            PresentationFrame frame = Tick(new(20, 60));
            PresentationFrame Tick(Point logical, ButtonState button = ButtonState.Released)
            {
                seconds += 1.0 / 60;
                var physical = RenderViewport.Fit(viewport, 900).ToPhysical(logical.ToVector2());
                var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
                app.Update(time, new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0,
                    button, default, default, default, default), new(), viewport);
                return app.ReadPresentation();
            }
            void Wait(Func<bool> reached, string stage)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!reached() && DateTime.UtcNow < deadline) { frame = Tick(new(20, 60)); Thread.Yield(); }
                if (!reached()) throw new InvalidOperationException("Semantic command journey timed out at " + stage + "; " + view.Error);
                Capture(stage);
            }
            void Capture(string name) => File.WriteAllText(Path.Combine(directory, name + ".json"),
                JsonSerializer.Serialize(new { snapshot = view.Match, frame = frame.Observe(), commands = provider.Commands.Select(command => command.GetType().Name) },
                    new JsonSerializerOptions { WriteIndented = true }));
            void Click(string id)
            {
                var hit = frame.Hits.LastOrDefault(hit => hit.Id == id)
                    ?? throw new InvalidOperationException("No current semantic journey control " + id);
                frame = Tick(hit.Bounds.Center, ButtonState.Pressed);
                frame = Tick(hit.Bounds.Center);
            }
            void ClickCard(string identity)
            {
                var hit = frame.Hits.Last(hit => hit.Card?.VisualId == identity);
                frame = Tick(hit.Bounds.Center, ButtonState.Pressed);
                frame = Tick(hit.Bounds.Center);
            }

            ClickCard(InteractionPort.SpellId);
            Wait(() => frame.Hits.Any(hit => hit.Id == "read-card-action"), "01-read-source");
            check(provider.Commands.Length == 0 && frame.Cards.Any(card => card.Id == InteractionPort.SpellId && card.Focused),
                "Source inspection discovers an independent provider's opaque offered action without submitting at " + viewport);
            Click("read-card-action");
            Wait(() => !view.Busy && frame.Hits.Any(hit => hit.Id == "choice:4"), "02-preview-bearing-ability");
            check(view.Match!.Decision!.Choices.Single().Card is { Name: "Contract Current" }
                && !frame.Hits.Any(hit => hit.Id == "choicesdone") && provider.Commands is [UseOfferedAction],
                "An explicit ability choice keeps its legitimate card preview and remains a source action, not a gallery selection, at " + viewport);
            Click("choice:4");
            Wait(() => !view.Busy && view.Match?.Decision?.Task == DecisionTask.Payment, "03-required-payment");
            check(provider.Commands is [UseOfferedAction, ChooseOptions { Choices: [4] }]
                && !frame.Hits.Any(hit => hit.Id == "confirm"),
                "Choosing an ability yields an unpaid cost and cannot accidentally confirm it at " + viewport);

            ClickCard(InteractionPort.ForestId);
            Wait(() => frame.Hits.Any(hit => hit.Id == "read-card-action"), "04-read-mana-source");
            Click("read-card-action");
            Wait(() => !view.Busy && view.Match?.Viewer?.Mana.GetValueOrDefault("G") == 1, "05-mana-added");
            check(view.Match!.Viewer!.Zone("Battlefield").Cards.Single().Tapped
                && view.Match.Decision?.Task == DecisionTask.Payment && provider.Commands.Last() is UseOfferedAction,
                "The provider's exact mana action taps its own occurrence while required payment remains open at " + viewport);
            Click("confirm");
            Wait(() => !view.Busy && view.Match?.Decision?.Task == DecisionTask.TargetSelection, "06-required-target");
            check(provider.Commands.Last() is PayMana && view.Match!.Viewer!.Mana.GetValueOrDefault("G") == 0
                && !frame.Hits.Any(hit => hit.Id == "confirm"),
                "PayMana spends the floating resource and hands control to a mandatory target instead of passing priority at " + viewport);
            ClickCard(InteractionPort.TargetId);
            Wait(() => !view.Busy && view.Match?.Stack.Length == 1, "07-pending-priority");
            check(provider.Commands.Last() is SelectCard && view.Match!.Stack.Single().Effect?.Amount == 3,
                "The target gesture selects the current occurrence and retains the provider's pending base amount at " + viewport);
            Click("confirm");
            Wait(() => !view.Busy && view.Match?.Decision?.Task is DecisionTask.Options or DecisionTask.AbilityChoice or DecisionTask.Ordering, "08-result-private-choice");
            var target = view.Match!.Players[1].Zone("Battlefield").Cards.Single();
            check(provider.Commands.Last() is PassPriority && target is { Power: 2, Toughness: 3, Damage: 2 }
                && view.Match.Activity.Single().Impact is { Kind: "damage", Amount: 2 }
                && view.Match.Players.Where(player => player.Id != view.Match.ViewerId).All(player => player.Zone("Hand").Cards.IsEmpty)
                && view.Match.Decision!.Choices.Single().Card is { Key.Length: 0, VisualId.Length: 0 },
                "Actual damage stays separate from pending base amount and current P/T; the following private choice exposes no persistent card authority at " + viewport);

            Click("choice:31");
            var confirm = frame.Hits.Single(hit => hit.Id == "choicesdone");
            frame = Tick(confirm.Bounds.Center, ButtonState.Pressed);
            int beforeStaleRelease = provider.Commands.Length;
            provider.RefreshScope(); view.Accept(provider.State);
            frame = Tick(confirm.Bounds.Center, ButtonState.Pressed);
            frame = Tick(confirm.Bounds.Center);
            check(provider.Commands.Length == beforeStaleRelease && view.Match!.Decision?.Task is DecisionTask.Options or DecisionTask.AbilityChoice or DecisionTask.Ordering,
                "A held private confirmation is revoked by a new decision capability before pointer release at " + viewport);
            Click("choice:31"); Click("choicesdone");
            Wait(() => !view.Busy && view.Match?.Decision?.Task == DecisionTask.Priority, "09-private-choice-complete");
            check(provider.Commands.Last() is ChooseOptions { Choices: [31] }
                && view.Match!.Viewer!.Zone("Hand").Cards.Single().Name == "Selected private card"
                && view.Match.Viewer.Zone("Library").Count == 19
                && view.Match.Viewer.Zone("Graveyard").Cards.Single().VisualId == InteractionPort.SpellId,
                "A fresh explicit selection completes the private continuation and changes the provider's hand/library state at " + viewport);
        }
    }

    private sealed class InteractionPort : IMatchConnection, ISourceActions
    {
        internal const string SpellId = "object|spell", ForestId = "object|mana", TargetId = "object|recipient";
        private const string CastOffer = "offer::cast/opaque", ManaOffer = "offer::mana/opaque";
        private readonly object gate = new();
        private readonly ConcurrentQueue<GameCommand> commands = new();
        private int revision;
        private int stage;
        private string targetId = "";
        private GameSnapshot state;
        internal GameCommand[] Commands => commands.ToArray();
        internal GameSnapshot State { get { lock (gate) return state; } }
        public bool SupportsSourceActions => true;

        internal InteractionPort()
        {
            var spell = new Card { VisualId = SpellId, CombatId = SpellId, Name = "Contract Current", Type = "Instant", ManaCost = "{G}",
                Text = "Choose a creature. A reported result can differ from its pending base amount.", Selectable = true };
            var forest = new Card { VisualId = ForestId, CombatId = ForestId, Name = "Forest", Type = "Basic Land", Text = "{T}: Add {G}." };
            var target = new Card { VisualId = TargetId, CombatId = TargetId, Name = "Independent Recipient", Type = "Creature", Power = 2, Toughness = 3 };
            state = new("different-provider/table", "playing", null, null, 0, 3, "Main phase", "MAIN1", 0,
                Enumerable.Range(0, 4).Select(seat => new Player(seat, seat == 0 ? "You" : "Seat " + seat, 40, seat == 0, false, ImmutableDictionary<string, int>.Empty, [
                    new("Hand", seat == 0 ? 1 : 7, seat == 0 ? [spell] : [], null),
                    new("Battlefield", seat is 0 or 1 ? 1 : 0, seat == 0 ? [forest] : seat == 1 ? [target] : [], null),
                    new("Library", 20, [], null), new("Graveyard", 0, [], null), new("Command", 0, [], null), new("Exile", 0, [], null)
                ])).ToImmutableArray(), [], null, Priority());
            Stamp();
        }

        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(State);
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException("This contract journey has no concession operation.");
        internal void RefreshScope() { lock (gate) Stamp(); }
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            lock (gate) {
                if (gameId != state.Id || decisionId != state.Decision?.Id) throw new EngineCommandRejectedException("Expired query.");
                var card = Find(sourceCardKey);
                SourceAction[] actions = stage == 0 && card.VisualId == SpellId ? [new(CastOffer, "cast", "Cast Contract Current")]
                    : stage == 2 && card.VisualId == ForestId && !card.Tapped ? [new(ManaOffer, "activate", "{T}: Add {G}.")] : [];
                return Task.FromResult(new SourceActionSet(gameId, decisionId, sourceCardKey, card.VisualId, actions.ToImmutableArray()));
            }
        }

        public Task ExecuteAsync(GameCommand command)
        {
            lock (gate) {
                if (command.GameId != state.Id || command is not DecisionCommand scoped || scoped.DecisionId != state.Decision?.Id)
                    throw new EngineCommandRejectedException("Expired command.");
                switch (command) {
                    case UseOfferedAction offer when stage == 0 && offer.ActionId == CastOffer && Find(offer.Card).VisualId == SpellId:
                        var source = Object(SpellId);
                        state = state with { Decision = new() { Task = DecisionTask.AbilityChoice, SourceCard = source, SourceCardId = SpellId, SourceZone = "Hand", Min = 1, Max = 1,
                            Choices = [new(4, "Cast for {G}", source with { Key = "", VisualId = "", CombatId = "" })] } };
                        stage = 1; break;
                    case ChooseOptions { Choices: [4] } when stage == 1:
                        state = state with { Decision = new() { Task = DecisionTask.Payment, Message = "Pay {G}.", SourceCard = Object(SpellId), SourceCardId = SpellId } };
                        stage = 2; break;
                    case UseOfferedAction offer when stage == 2 && offer.ActionId == ManaOffer && Find(offer.Card) is { VisualId: ForestId, Tapped: false }:
                        Change(ForestId, card => card with { Tapped = true });
                        state = state with { Players = state.Players.Select(player => player.Id == 0
                            ? player with { Mana = player.Mana.SetItem("G", player.Mana.GetValueOrDefault("G") + 1) } : player).ToImmutableArray(),
                            Decision = state.Decision! with { OkEnabled = true } };
                        break;
                    case PayMana when stage == 2 && state.Viewer!.Mana.GetValueOrDefault("G") >= 1:
                        state = state with { Players = state.Players.Select(player => player.Id == 0
                            ? player with { Mana = player.Mana.SetItem("G", player.Mana["G"] - 1) } : player).ToImmutableArray(),
                            Decision = new() { Task = DecisionTask.TargetSelection, SourceCard = Object(SpellId), SourceCardId = SpellId, SourceZone = "Hand", Min = 1, Max = 1 } };
                        stage = 3; break;
                    case SelectCard selected when stage == 3 && Find(selected.Card).VisualId == TargetId:
                        targetId = Find(selected.Card).VisualId;
                        state = state with { Decision = Priority(), Stack = [new("Contract Current", "A pending base amount.", false, Object(SpellId)) {
                            Id = "pending|different-provider", Effect = new() { Kind = "damage", Amount = 3 },
                            Targets = [new("card", targetId, Object(targetId).Name)]
                        }] };
                        stage = 4; break;
                    case PassPriority when stage == 4:
                        int dealt = state.Stack.Single().Effect!.Amount!.Value - 1;
                        Change(targetId, card => card with { Damage = card.Damage + dealt });
                        var cast = Object(SpellId);
                        state = state with { Stack = [], Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                            Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = zone.Count - 1,
                                Cards = zone.Cards.Where(card => card.VisualId != SpellId).ToImmutableArray() }
                                : zone.Name == "Graveyard" ? zone with { Count = zone.Count + 1, Cards = zone.Cards.Add(cast) } : zone).ToImmutableArray()
                        }).ToImmutableArray(), Activity = [new(1, 3, "MAIN1", "damage", 0, "Reported damage.", targetId, Object(targetId).Name) {
                            Impact = new("damage", dealt, targetId, Object(targetId).Name, SourceCardId: SpellId, SourceName: cast.Name)
                        }], Decision = new() { Task = DecisionTask.Options, Min = 1, Max = 1, Title = "Choose a card", SourceZone = "Library",
                            Choices = [new(31, "Selected private card", new() { Name = "Selected private card", Type = "Land" })] } };
                        stage = 5; break;
                    case ChooseOptions { Choices: [31] } when stage == 5:
                        var selectedCard = state.Decision!.Choices.Single().Card! with { VisualId = "object|chosen", CombatId = "object|chosen" };
                        state = state with { Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                            Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = zone.Count + 1, Cards = zone.Cards.Add(selectedCard) }
                                : zone.Name == "Library" ? zone with { Count = zone.Count - 1 } : zone).ToImmutableArray()
                        }).ToImmutableArray(), Decision = Priority() };
                        stage = 6; break;
                    default: throw new EngineCommandRejectedException("This semantic command does not answer the required task.");
                }
                commands.Enqueue(command); Stamp(); return Task.CompletedTask;
            }
        }

        private Card Find(string key) => state.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards).Single(card => card.Key == key);
        private Card Object(string id) => state.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards).Single(card => card.VisualId == id);
        private static Decision Priority() => new() { Task = DecisionTask.Priority, OkEnabled = true };
        private void Change(string id, Func<Card, Card> update) => state = state with { Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.VisualId == id ? update(card) : card).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() };
        private void Stamp()
        {
            revision++;
            state = state with { Revision = revision, Decision = state.Decision is { } decision ? decision with { Id = "capability/" + revision + "|task" } : null,
                Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Cards = zone.Cards.Select(card => card with { Key = "opaque[" + revision + "]=>" + card.VisualId,
                        Selectable = stage == 0 && card.VisualId == SpellId || stage == 2 && card.VisualId == ForestId && !card.Tapped
                            || stage == 3 && card.VisualId == TargetId }).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray() };
        }
    }
}
