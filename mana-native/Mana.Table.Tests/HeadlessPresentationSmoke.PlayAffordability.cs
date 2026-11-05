using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;
using Mana.Magic;
using Mana.Planning;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private const string AffordabilityCardId = "current-funding-card";

    private static void CheckPlayAffordability(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        // The first path uses an omitted funding fact and the real source reader.
        // Its baseline uses compatible pre-fix Game code; version/EOL limits are bound separately.
        CheckUnknownOfferPaint(profile, check, trace, performance);
        CheckAffordabilityWire(check);
        foreach (var knowledge in new[] { ActionAffordability.Unknown, ActionAffordability.Unavailable, ActionAffordability.Payable }) {
            using var run = new Runner(profile, "play-funding-" + knowledge, AffordabilityState() with {
                Players = AffordabilityState().Players.Select(player => player with {
                    Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                        Selectable = true, PlayAffordability = knowledge
                    }).ToImmutableArray() }).ToImmutableArray()
                }).ToImmutableArray()
            }, trace, performance);
            InstallAffordabilityOffers(run);
            run.Tick("current-funding-fact");
            check(HasAffordabilityPaint(run) == (knowledge == ActionAffordability.Payable),
                "A selectable hand face paints availability only for current positive payment evidence: " + knowledge);
            run.ClickCard(AffordabilityCardId);
            run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "current-funding-offer");
            check(HasAffordabilityPaint(run) == (knowledge == ActionAffordability.Payable)
                    && run.Engine.Replies.Count == 0,
                "The focused source face keeps payment knowledge separate from its legal manual offer: " + knowledge);
            if (knowledge == ActionAffordability.Payable) {
                var previous = run.Engine.State.Viewer!.Zone("Hand").Cards.Single();
                run.Present(run.Engine.State with { Players = run.Engine.State.Players.Select(player => player with {
                    Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                        PlayAffordability = ActionAffordability.Unknown
                    }).ToImmutableArray() }).ToImmutableArray()
                }).ToImmutableArray() });
                run.Settle("payment-evidence-retired");
                check(!HasAffordabilityPaint(run)
                        && !CardAffordability.CanHighlight(run.Engine.State, previous, "Hand", true),
                    "A new input retires positive payment paint and cannot reuse the earlier physical source handle");
            }
        }
        CheckOtherAffordabilityRoles(profile, check, trace, performance);
        CheckPublicAffordabilityPaint(profile, check);
    }

    private static void CheckUnknownOfferPaint(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        using var run = new Runner(profile, "unknown-legal-play-offer", AffordabilityState(), trace, performance);
        InstallAffordabilityOffers(run);
        run.Tick("unfunded-hand-source");
        var compact = run.Frame.Cards.Single(card => card.Id == AffordabilityCardId);
        run.Input(run.PointFor(compact.Hit), label: "hover-unfunded-source");
        bool hoverPaint = run.Frame.Drawing.Commands.OfType<DrawCommand.CardFocus>().Any(command => command.Pose == compact.Display);
        run.ClickCard(AffordabilityCardId);
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "timing-legal-offer-returned");
        run.Input(new Point(20, 60), label: "pointer-away-from-source");
        run.Settle("unknown-offer-current");
        check(!HasAffordabilityPaint(run),
            "A current timing-legal source offer does not paint a hand card as funded when payment knowledge is absent");
        check(hoverPaint && run.Engine.Replies.Count == 0,
            "An unfunded readable card retains its separate pointer-focus outline without committing a play");
        var current = run.Engine.State;
        var card = current.Viewer!.Zone("Hand").Cards.Single();
        run.Engine.ReplyOverride = command => {
            if (command is not UseOfferedAction offered || !GameCommandPermissions.Allows(current, offered)
                || offered.Card != card.Key || offered.ActionId != current.Decision!.Id + ":manual-cast")
                throw new InvalidOperationException("The manual attempt lost its exact current source offer.");
            run.Engine.Replies.Add(command);
            return Task.CompletedTask;
        };
        run.ClickHit("read-card-action");
        run.Until(() => !run.View.Busy && run.Engine.Replies.Count == 1, "explicit-unfunded-manual-attempt");
        check(run.Engine.Replies.Single() is UseOfferedAction && run.Engine.State.Viewer!.Zone("Hand").Cards.Single().Key == card.Key,
            "Unknown funding preserves one explicit current offered manual attempt without changing or funding the card");
    }

    private static GameSnapshot AffordabilityState()
    {
        var source = new Card { VisualId = AffordabilityCardId, CombatId = AffordabilityCardId,
            Name = "Declared five-mana source", Type = "Creature", ManaCost = "{5}", Selectable = false };
        var state = Example();
        return state with { Id = "play-funding-presentation", PriorityPlayerId = 0, DecisionOwnerId = 0,
            Players = state.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone with {
                    Count = player.Id == 0 && zone.Name == "Hand" ? 1 : zone.Name == "Library" ? 80 : 0,
                    Cards = player.Id == 0 && zone.Name == "Hand" ? [source] : []
                }).ToImmutableArray()
            }).ToImmutableArray() };
    }

    private static void InstallAffordabilityOffers(Runner run) => run.Engine.SourceActionRead = (game, decision, key) =>
        new(game, decision, key, AffordabilityCardId, [new(decision + ":manual-cast", "cast", "Cast declared source")]) {
            Revision = run.Engine.State.Revision
        };

    private static bool HasAffordabilityPaint(Runner run)
    {
        var face = run.Frame.Cards.Single(card => card.Id == AffordabilityCardId);
        return run.Frame.Drawing.Commands.OfType<DrawCommand.CardAction>().Any(command => command.Pose == face.Display);
    }

    private static void CheckAffordabilityWire(Action<bool, string> check)
    {
        ActionAffordability Decode(string? fact)
        {
            var raw = JsonSerializer.SerializeToNode(new {
                id = "funding-wire", status = "playing", viewerId = 0, revision = 1, turn = 1,
                phase = "Main phase", phaseKey = "MAIN1", activePlayerId = 0, priorityPlayerId = 0, decisionOwnerId = 0,
                players = AffordabilityState().Players, stack = Array.Empty<object>(),
                prompt = new { id = "funding-input", kind = "input", task = "priority", okEnabled = true }
            })!;
            var card = raw["players"]![0]!["Zones"]![0]!["Cards"]![0]!.AsObject();
            card.Remove(nameof(Card.PlayAffordability));
            if (fact != null) card["playAffordability"] = JsonNode.Parse(fact);
            return new ForgeCommandScope().ObserveRaw(JsonSerializer.SerializeToElement(raw))!.Viewer!.Zone("Hand").Cards.Single().PlayAffordability;
        }
        check(new Card().PlayAffordability == ActionAffordability.Unknown && Decode(null) == ActionAffordability.Unknown,
            "Legacy card construction and an omitted provider payment fact stay Unknown rather than enum-zero Payable");
        foreach (string bad in new[] { "null", "0", "1", "true", "{}", "[]", "\"FutureFunding\"", "\"payable\"" })
            check(Decode(bad) == ActionAffordability.Unknown,
                "The ordinary provider decoder cannot acquire positive funding from an invalid optional fact: " + bad);
        foreach (var value in Enum.GetValues<ActionAffordability>())
            check(Decode(JsonSerializer.Serialize(value.ToString())) == value,
                "The ordinary provider decoder preserves a closed payment fact: " + value);
        JsonElement Spectator(ActionAffordability knowledge, bool topCard = false) => JsonSerializer.SerializeToElement(new {
            id = "public-funding-wire", status = "playing", viewerId = -1, revision = 1, turn = 1,
            phase = "Main phase", phaseKey = "MAIN1", activePlayerId = 0,
            players = new[] { new Player(0, "Public player", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new(topCard ? "Graveyard" : "Battlefield", 1, topCard ? [] : [new Card { Name = "Public face", VisualId = "public-face",
                    PlayAffordability = knowledge }], topCard ? new Card { Name = "Public top", VisualId = "public-top",
                    PlayAffordability = knowledge } : null)]) }, stack = Array.Empty<object>()
        });
        check(ForgeSnapshotDecoder.Decode(Spectator(ActionAffordability.Unknown), spectator: true) != null,
            "A spectator retains ordinary public faces with no viewer-specific payment claim");
        check(ForgeSnapshotDecoder.Decode(Spectator(ActionAffordability.Unknown, topCard: true), spectator: true) != null,
            "A public pile top remains readable without borrowing a private payment claim");
        foreach (var privateFact in new[] { ActionAffordability.Payable, ActionAffordability.Unavailable }) {
            bool rejected = false, topRejected = false;
            try { ForgeSnapshotDecoder.Decode(Spectator(privateFact), spectator: true); }
            catch (IOException) { rejected = true; }
            try { ForgeSnapshotDecoder.Decode(Spectator(privateFact, topCard: true), spectator: true); }
            catch (IOException) { topRejected = true; }
            check(rejected && topRejected, "A spectator face or pile top cannot inherit a viewer-specific payment fact: " + privateFact);
        }
        var configured = ForgeEngine.DecodeConfiguredSimulation(JsonSerializer.SerializeToElement(ConfiguredSimulationWireChecks.Receipt()),
            ConfiguredSimulationWireChecks.Setup());
        foreach (var malformed in new[] {
            configured.State with { Players = configured.State.Players.SetItem(0, null!) },
            configured.State with { Players = configured.State.Players.SetItem(0, configured.State.Players[0] with { Zones = default }) }
        }) {
            bool rejected = false;
            try { ConfiguredSimulationFacts.IsReady(configured, malformed); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "The optional pile funding guard preserves closed rejection of null players and missing zones");
        }
    }

    private static void CheckOtherAffordabilityRoles(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        var state = AffordabilityState();
        var card = state.Viewer!.Zone("Hand").Cards.Single() with { Key = "current-hand-key", PlayAffordability = ActionAffordability.Payable };
        check(!CardAffordability.CanHighlight(state with { PriorityPlayerId = 1 }, card, "Hand", true)
                && !CardAffordability.CanHighlight(state with { PriorityPlayerId = null }, card, "Hand", true)
                && !CardAffordability.CanHighlight(state with { DecisionOwnerId = null }, card, "Hand", true)
                && !CardAffordability.CanHighlight(state, card, "Hand", false),
            "A positive payment fact requires explicit current ownership and cannot override foreign priority or missing interaction permission");
        var payment = state with { Decision = new Decision("payment", new(), new PaymentPayload(new("Pay", false), new("Cancel", true))) };
        check(CardAffordability.CanHighlight(payment, card, "Hand", true)
                && CardAffordability.CanHighlight(state, card, "Battlefield", true),
            "Required payment selections and battlefield ability cues retain their existing interaction projection");

        var modal = state.Viewer.Zone("Hand").Cards.Single() with { PlayAffordability = ActionAffordability.Payable,
            OtherFace = new("Declared land face", "Land", "", "", null, null, "Declared land face", "back") };
        using var run = new Runner(profile, "funded-modal-card", state with { Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" || player.Id != 0 ? zone : zone with { Cards = [modal] }).ToImmutableArray()
        }).ToImmutableArray() }, trace, performance);
        run.Engine.SourceActionRead = (game, decision, key) => new(game, decision, key, AffordabilityCardId,
            [new(decision + ":cast", "cast", "Cast front face"), new(decision + ":land", "play", "Play land face")]) { Revision = run.Engine.State.Revision };
        run.Tick("modal-source");run.ClickCard(AffordabilityCardId);
        run.Until(() => run.Frame.Hits.Count(hit => hit.Intent is MatchIntent.UseSourceAction) == 2, "modal-source-offers");
        check(HasAffordabilityPaint(run) && run.Frame.Hits.Count(hit => hit.Intent is MatchIntent.UseSourceAction) == 2
                && run.View.ManaJourney?.FundingStatus != ManaFundingStatus.Funded && run.Engine.Replies.Count == 0,
            "One funded modal route may light its physical card while both offers remain manual and neither borrows per-route funding knowledge");
    }

    private static void CheckPublicAffordabilityPaint(string profile, Action<bool, string> check)
    {
        using var run = new PlayAccessRun(profile, "public-play-funding", new Point(1600, 900));
        const string id = Mana.Scenarios.PlayAccessScenario.ExileSpellId;
        bool Painted()
        {
            var face = run.Frame.Cards.Single(card => card.Id == id);
            return run.Frame.Drawing.Commands.OfType<DrawCommand.CardAction>().Any(command => command.Pose == face.Display);
        }
        check(!Painted() && run.Connection.State.PlayAccesses.Single(access => access.CardId == id).Routes.Any(route => route.CanStart),
            "Public-zone permission to begin a route does not itself paint an unknown payment as funded");
        run.ClickCard(id);
        run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"));
        check(!Painted() && run.Connection.Commands.IsEmpty,
            "An unknown-funding public source remains inspectable with its current manual offer and no positive cue");
        var state = run.Connection.State;
        run.Publish(state with { Revision = state.Revision + 1, DecisionOwnerId = state.ViewerId, PriorityPlayerId = state.ViewerId,
            Players = state.Players.Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.VisualId != id ? card
                : card with { PlayAffordability = ActionAffordability.Payable }).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray() });
        check(Painted() && run.Connection.Commands.IsEmpty,
            "A public-zone face uses the same current positive payment projection as the hand and focused reader");
    }
}
