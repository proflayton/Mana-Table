using System.Collections.Immutable;
using Mana.Contracts;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckKnownActionFaces(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        const string identity = "owned-manifest";
        const string printedName = "Fathom Seer";
        var physical = new Card { CombatId = identity, Name = "Face-down card", Type = "Creature",
            FaceDown = true, Power = 2, Toughness = 2 };
        var known = new Card { Name = printedName, Type = "Creature — Illusion", ManaCost = "{1}{U}",
            Text = "When Fathom Seer is turned face up, draw two cards.", Power = 1, Toughness = 3 };
        GameSnapshot Initial(int controller) => Example() with {
            Id = "known-action-face-" + controller, PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            Players = Example().Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone with {
                    Count = zone.Name == "Library" ? 80 : zone.Name == "Battlefield" && player.Id == controller ? 1 : 0,
                    Cards = zone.Name == "Battlefield" && player.Id == controller ? [physical] : []
                }).ToImmutableArray()
            }).ToImmutableArray()
        };

        using (var run = new Runner(profile, "ordinary-owned-face-down-reader", Initial(0), trace, performance)) {
            var ordinary = run.Engine.State;
            var masked = ordinary.Viewer!.Zone("Battlefield").Cards.Single();
            ordinary = ordinary with { Revision = ordinary.Revision + 1, ActionSources = [
                new(ordinary.Decision!.Id, "Battlefield", 0, masked) { KnownFace = known }
            ] };
            int queries = 0;
            run.Engine.SourceActionRead = (game, decision, key) => {
                var current = run.View.Match!;
                if (game != current.Id || decision != current.Decision!.Id || key != masked.Key
                    || CardViews.KnownActionFace(current, masked) == null)
                    throw new InvalidOperationException("Only the current physical masked source may be queried.");
                queries++;
                return new(game, decision, key, identity, [new("turn-face-up", "activate", "Turn face up — pay {1}{U}")]) {
                    Revision = current.Revision
                };
            };
            run.Engine.ReplyOverride = command => {
                if (command is not UseOfferedAction { Card: var key, ActionId: "turn-face-up" }
                    || key != masked.Key || !GameCommandPermissions.Allows(run.View.Match!, command))
                    throw new InvalidOperationException("Only the current opaque turn-face-up offer may be dispatched.");
                run.Engine.Replies.Add(command);
                return Task.CompletedTask;
            };
            run.QueueExact(ordinary); run.Tick("ordinary-private-grant"); run.View.SetTableStudy(true); run.Settle("masked-table");
            check(run.Frame.Cards.Single(card => card.Id == identity).Card.FaceDown && !string.Join(" ", Text(run.Frame)).Contains(printedName),
                "An ordinary owner publication keeps the manifested battlefield card masked before explicit inspection");
            run.ClickCard(identity);
            run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "owned-face-down-source-offer");
            var reader = run.Frame.Cards.Single(card => card.Id == identity);
            var readouts = ManaPaymentSmoke.ReaderSeatReadoutGeometry(run.View);
            check(readouts.Clear && readouts.CoveredTargetsRetired && readouts.CoveredPlans > 0 && readouts.UncoveredVisible,
                "A complete private reader occludes covered seat readouts and their hidden targets while uncovered seat information stays visible");
            check(reader.Focused && reader.Card.FaceDown && reader.Request.Card == masked && reader.Hit.Card == masked
                    && reader.Card.Power == 2 && reader.Card.Toughness == 2 && queries == 1 && run.Engine.Replies.Count == 0,
                "Clicking an owned face-down occurrence opens its separate known face while hits and source discovery retain physical 2/2 authority");
            run.ClickHit("read-card-rules"); run.Settle("private-printed-rules");
            check(string.Join(" ", Text(run.Frame)).Contains(printedName) && string.Join(" ", Text(run.Frame)).Contains("draw two cards")
                    && string.Join(" ", Text(run.Frame)).Contains("2/2") && string.Join(" ", Text(run.Frame)).Contains("Face down")
                    && !string.Join(" ", Text(run.Frame)).Contains("1/3") && run.Engine.Replies.Count == 0,
                "The owner reads printed rules beside current face-down 2/2 state without adopting the printed creature's characteristics");

            var stale = run.Hit("read-card-action"); var at = run.PointFor(stale);
            run.Input(at, left: ButtonState.Pressed, label: "press-offer-before-private-grant-revocation");
            var revoked = ordinary with { Revision = ordinary.Revision + 1, ActionSources = [] };
            run.QueueExact(revoked);
            run.Input(at, label: "release-after-private-grant-revocation"); run.Settle("revoked-private-reader");
            check(run.Frame.Cards.All(card => !card.Focused) && !string.Join(" ", Text(run.Frame)).Contains(printedName)
                    && run.Frame.Hits.All(hit => hit.Id is not ("read-card-action" or "read-card-rules"))
                    && !run.View.CurrentHit(stale) && run.Engine.Replies.Count == 0,
                "Revoking the exact private grant removes reading and actions and rejects an already-pressed offer without leaking the retained face");
            run.ClickCard(identity); run.Settle("masked-without-grant");
            check(run.Frame.Cards.All(card => !card.Focused) && !string.Join(" ", Text(run.Frame)).Contains(printedName)
                    && queries == 1 && run.Engine.Replies.Count == 0,
                "The same owned masked card cannot reopen private reading or query actions after its grant is withdrawn");
            run.ClickHit("close-overlay"); run.Settle("close-public-masked-details");

            run.QueueExact(ordinary with { Revision = revoked.Revision + 1 }); run.Settle("fresh-private-grant");
            run.ClickCard(identity);
            run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "fresh-physical-source-offer");
            run.ClickHit("read-card-action");
            run.Until(() => run.Engine.Replies.Count == 1, "explicit-turn-face-up-dispatch");
            check(run.Engine.Replies.Single() is UseOfferedAction { Card: var sentKey, ActionId: "turn-face-up" }
                    && sentKey == masked.Key && queries == 2 && known.Key.Length == 0 && known.VisualId.Length == 0 && known.CombatId.Length == 0,
                "The existing source-action control dispatches exactly one opaque action on the physical source, never on its handle-free reading descriptor");
        }

        using (var run = new Runner(profile, "ordinary-opponent-face-down-reader", Initial(1), trace, performance)) {
            int queries = 0;
            run.Engine.SourceActionRead = (game, decision, key) => { queries++; throw new InvalidOperationException("An opponent's concealed source is not readable."); };
            run.Tick("opponent-masked-table"); run.View.SetTableStudy(true); run.Settle("opponent-source-visible");
            run.ClickCard(identity); run.Settle("opponent-inspection-denied");
            check(run.Frame.Cards.All(card => !card.Focused) && !string.Join(" ", Text(run.Frame)).Contains(printedName)
                    && run.Frame.Hits.All(hit => hit.Id is not ("read-card-action" or "read-card-rules"))
                    && queries == 0 && run.Engine.Replies.Count == 0,
                "An ordinary opponent face-down occurrence grants neither private inspection, source discovery nor a command");
        }
    }
}
