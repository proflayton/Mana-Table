using System.Collections.Immutable;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    // The provider models the exported input protocol, not Magic rules. Forge's
    // engine regression separately proves the legal counter-payment candidates.
    private static void CheckCounterCost(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        foreach (bool focused in new[] { false, true }) CheckCounterCostJourney(profile, check, trace, performance, focused);
        foreach (bool adding in new[] { true, false }) CheckFocusedCounterTarget(profile, check, trace, performance, adding);
    }

    private static void CheckCounterCostJourney(string profile, Action<bool, string> check, List<object> trace, List<object> performance, bool focused)
    {
        const string sourceId = "counter-cost-source", offerId = "opaque-counter-activation";
        var source = new Card { VisualId = sourceId, CombatId = sourceId, Name = "Counter payment source", Type = "Creature",
            Power = 0, Toughness = 1, Text = "{1}{U}, Remove a counter from this creature: Draw a card.",
            Counters = ImmutableDictionary<string, int>.Empty.Add("-1/-1", 4).Add("Charge", 1) };
        var initial = Example() with { Id = "counter-cost-journey", PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            Players = Example().Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Count = zone.Name == "Library" ? 30 : player.Id == 0 && zone.Name == "Battlefield" ? 1 : 0,
                Cards = player.Id == 0 && zone.Name == "Battlefield" ? [source] : []
            }).ToImmutableArray() }).ToImmutableArray() };
        using var run = new Runner(profile, "counter-cost-source-choice" + (focused ? "-focused" : ""), initial, trace, performance);
        Card CurrentSource() => run.Engine.State.Viewer!.Zone("Battlefield").Cards.Single();
        DecisionContext Context(string message) => new(Message: message,
            Source: new(sourceId, CurrentSource() with { Key = "", Selectable = false }, run.Engine.State.ViewerId, "Battlefield"));
        void Present(DecisionPayload payload, string message, string actionClass) => run.Engine.Present(run.Engine.State with {
            ActionClass = actionClass, PriorityPlayerId = null,
            Decision = new("counter-cost-step", Context(message), payload)
        });
        run.Engine.SourceActionRead = (game, decision, key) => {
            var state = run.Engine.State;
            if (game != state.Id || decision != state.Decision?.Id || key != CurrentSource().Key)
                throw new InvalidOperationException("Counter cost reader requested an obsolete occurrence.");
            return new(game, decision, key, sourceId, state.Decision.Payload is PriorityPayload
                ? [new(offerId, "activate", source.Text)] : []);
        };
        run.Engine.ReplyOverride = command => {
            var state = run.Engine.State;
            if (!GameCommandPermissions.Allows(state, command)) throw new InvalidOperationException("Counter cost command has no current authority.");
            switch (command) {
                case UseOfferedAction { ActionId: offerId } offered when state.Decision?.Payload is PriorityPayload && offered.Card == CurrentSource().Key:
                    Present(new AbilityChoicePayload(new([new(4, "Draw a card", null)], new(1, 1), [])), "Choose an ability", "choice"); break;
                case ChooseOptions { Choices: [4] } when state.Decision?.Payload is AbilityChoicePayload:
                    Present(new PaymentPayload(new("Pay", true), new("Cancel", true)), "Pay {1}{U}.", "mana"); break;
                case PayMana:
                    // Reproduce the missing GUI-declared candidate: the source
                    // exists and is reported, but that never authorizes a click.
                    Present(new EntitySelectionPayload(new("OK", false), new("Cancel", true), [], CounterPayment: new(1, 1)),
                        "Select this creature to remove 1 counter from.", "choice"); break;
                case SelectCard selected when state.Decision?.Payload is EntitySelectionPayload && selected.Card == CurrentSource().Key:
                    Present(new OptionsPayload(new([new(7, "-1/-1", null), new(12, "Charge", null)], new(1, 1), []),
                        new(1, Context("").Source!, [new(7, "M1M1", "-1/-1", 4), new(12, "CHARGE", "Charge", 1)])),
                        "Select a counter type to remove.", "choice"); break;
                case ChooseOptions { Choices: [12] } when state.Decision?.Payload is OptionsPayload:
                    run.Engine.Present(state with {
                        Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                                Cards = zone.Cards.Select(card => card with { Counters = card.Counters.Remove("Charge") }).ToImmutableArray()
                            }).ToImmutableArray()
                        }).ToImmutableArray(),
                        Activity = [new(1, 8, "MAIN1", "counters", 0, "Removed a Charge counter.", sourceId, source.Name) {
                            Change = new("counters", "Charge", 1, 0), Cause = new(sourceId, source.Name, "Draw a card.", 0, "ability", "paid-draw")
                        }],
                        ActionClass = "priority", Decision = new("paid-counter", Context("The activation is pending."), new PriorityPayload(new("Pass", true)))
                    }); break;
                default: throw new InvalidOperationException("Unexpected counter-cost continuation: " + command.GetType().Name);
            }
            run.Engine.Replies.Add(command);
            return Task.CompletedTask;
        };

        run.Tick("counter-cost-initial");
        run.ClickCard(sourceId);
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "counter-source-inspected");
        check(run.Frame.Cards.Single(card => card.Id == sourceId).Focused && run.Engine.Replies.Count == 0,
            "Inspecting a counter-bearing source discovers its exact activation without paying or selecting a counter");
        run.ClickHit("read-card-action");
        run.Until(() => !run.View.Busy && run.Frame.Hits.Any(hit => hit.Id == "choice:4"), "counter-activation-menu");
        run.ClickHit("choice:4");
        run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Payload is PaymentPayload, "counter-mana-payment");
        // Keep the source reader open across the payment→cost-input boundary.
        run.ClickCard(sourceId); run.Settle("counter-source-during-payment");
        check(run.Frame.Cards.Single(card => card.Id == sourceId).Focused, "A counter-cost source can be inspected while mana payment is pending");
        run.ClickHit("confirm");
        run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Payload is EntitySelectionPayload, "undeclared-counter-candidate");
        int before = run.Engine.Replies.Count;
        run.ClickCard(sourceId); run.Settle("missing-candidate-remains-read-only");
        check(run.Engine.Replies.Count == before && run.Engine.Replies.All(command => command is not SelectCard),
            "A reported cost source with no published Selectable authority stays read-only instead of being auto-selected from its prompt text");
        run.Input(new Point(20, 60), keys: new KeyboardState(Keys.Escape), label: "close-read-only-cost-details");
        run.Input(new Point(20, 60), label: "return-to-counter-cost");

        // The corrected engine projection publishes its actual legal candidate.
        var current = run.Engine.State;
        run.Present(current with { Players = current.Players.Select(player => player.Id != current.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = zone.Cards.Select(card => card with { Selectable = true }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() });
        run.Settle("published-counter-candidate");
        string decision = run.View.Match!.Decision!.Id, key = CurrentSource().Key;
        if (focused) {
            run.View.SetTableStudy(false); run.View.OpenBattlefield(run.View.Match.ViewerId);
            run.Settle("focused-counter-candidate");
            var tile = run.Frame.Hits.Single(hit => hit.Id.StartsWith("battlefield-focus-card:", StringComparison.Ordinal)
                && hit.Card?.VisualId == sourceId);
            run.ClickHit(tile.Id);
        } else run.ClickCard(sourceId);
        run.Until(() => !run.View.Busy && (run.View.Match?.Decision?.Payload is OptionsPayload || run.View.Overlay == "inspect"), "choose-current-counter-source");
        check(run.Engine.Replies.Count == before + 1 && run.Engine.Replies.Last() is SelectCard selected
                && selected.DecisionId == decision && selected.Card == key && run.View.Overlay.Length == 0,
            "After payment, a published counter-cost candidate submits its exact current SelectCard through the actual "
                + (focused ? "focused battlefield" : "battlefield") + " gesture rather than opening details");
        check(run.View.Selections.Count == 0 && run.Engine.Replies.Last() is SelectCard,
            "Multiple removable counter types remain an explicit decision after the cost source is chosen");
        check(run.Frame.Hits.Any(hit => hit.Id == "counter-payment:12")
                && run.Frame.Hits.All(hit => hit.Id is not ("open-decision" or "choicesdone")),
            "The counter-type cost stays beside its current permanent with direct debit choices instead of a generic modal and second confirmation");
        if (!focused) {
            run.ClickCard(sourceId); run.Settle("read-donor-before-counter-payment");
            check(run.Frame.Cards.Single(card => card.Id == sourceId).Focused && run.Engine.Replies.Count == before + 1,
                "Reading the exact mixed-counter donor keeps the actual type choice available without paying it");
        }
        run.Input(run.PointFor(run.Hit("counter-payment:12")), label: "hover-counter-debit"); run.Settle("hover-counter-debit");
        check(run.Engine.Replies.Count == before + 1 && CurrentSource().Counters.GetValueOrDefault("Charge") == 1,
            "Hovering the explicit counter debit preserves the current counter until its own click");
        run.ClickHit("counter-payment:12");
        run.Until(() => !run.View.Busy && run.Engine.Replies.Last() is ChooseOptions { Choices: [12] }, "choose-counter-type");
        check(run.Engine.Replies is [UseOfferedAction, ChooseOptions { Choices: [4] }, PayMana, SelectCard, ChooseOptions { Choices: [12] }],
            "Only the explicit current counter-type choice completes the nontrivial payment continuation, with no replayed activation or extra priority pass");
        run.Settle("counter-debit-settled");
        check(CurrentSource().Counters.GetValueOrDefault("Charge") == 0
                && run.Frame.Cards.Any(card => card.Id == sourceId && card.Card.Counters.GetValueOrDefault("Charge") == 0),
            "The resulting counter total reaches the same physical permanent rather than leaving its old inspected face behind");
    }

    private static void CheckFocusedCounterTarget(string profile, Action<bool, string> check, List<object> trace, List<object> performance, bool adding)
    {
        const string id = "counter-effect-target", decoyId = "counter-effect-ineligible";
        int owner = adding ? 0 : 1;
        var target = new Card { VisualId = id, CombatId = id, Name = "Public counter recipient", Type = "Creature", Power = 2, Toughness = 2,
            Selectable = true, Counters = adding ? ImmutableDictionary<string, int>.Empty : ImmutableDictionary<string, int>.Empty.Add("Charge", 2) };
        var decoy = target with { VisualId = decoyId, CombatId = decoyId, Name = "Ineligible public creature", Selectable = false };
        var initial = Example() with { Id = "focused-counter-target-" + adding, ActionClass = "selection", PriorityPlayerId = null, DecisionOwnerId = 0,
            Decision = new("target-input", new(Message: adding ? "Choose target creature to put a counter on." : "Choose target permanent to remove a counter from."),
                new TargetSelectionPayload(new("OK", false), new("Cancel", true), new(1, 1), [])),
            Players = Example().Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Count = zone.Name == "Library" ? 30 : zone.Name == "Battlefield" && player.Id == owner ? 2 : 0,
                Cards = zone.Name == "Battlefield" && player.Id == owner ? [target, decoy] : []
            }).ToImmutableArray() }).ToImmutableArray()
        };
        using var run = new Runner(profile, "focused-counter-target-" + (adding ? "own-add" : "opponent-remove"), initial, trace, performance);
        run.Engine.ReplyOverride = command => {
            var state = run.Engine.State;
            var recipient = state.Players.Single(player => player.Id == owner).Zone("Battlefield").Cards.Single(card => card.VisualId == id);
            if (command is not SelectCard selected || !GameCommandPermissions.Allows(state, command) || selected.Card != recipient.Key)
                throw new InvalidOperationException("Focused counter target did not use its current authorized occurrence.");
            run.Engine.Replies.Add(command);
            run.Engine.Present(state with { Decision = null, ActionClass = "resolving" });
            return Task.CompletedTask;
        };
        run.Tick("required-counter-target");
        run.View.SetTableStudy(false); run.View.OpenBattlefield(owner); run.Settle("inspect-required-targets");
        string Tile(string identity) => run.Frame.Hits.Single(hit => hit.Id.StartsWith("battlefield-focus-card:", StringComparison.Ordinal)
            && hit.Card?.VisualId == identity).Id;
        run.ClickHit(Tile(decoyId)); run.Settle("ineligible-target-read-only");
        check(run.Engine.Replies.Count == 0 && run.View.Match?.Decision?.Payload is TargetSelectionPayload,
            "An ineligible focused counter recipient remains inspectable without submitting a target or inferring eligibility from its counters");
        run.Input(new Point(20, 60), keys: new KeyboardState(Keys.Escape), label: "close-ineligible-target-details");
        run.Input(new Point(20, 60), label: "return-to-required-target");
        string decision = run.View.Match!.Decision!.Id;
        string key = run.View.Match.Players.Single(player => player.Id == owner).Zone("Battlefield").Cards.Single(card => card.VisualId == id).Key;
        run.ClickHit(Tile(id));
        run.Until(() => !run.View.Busy && run.Engine.Replies.Count == 1, "choose-focused-counter-target");
        check(run.Engine.Replies.Single() is SelectCard selected && selected.DecisionId == decision && selected.Card == key && run.View.Overlay.Length == 0,
            "A focused " + (adding ? "own add-counter" : "opponent remove-counter") + " target uses the current exact SelectCard rather than its inspection action");
    }
}
