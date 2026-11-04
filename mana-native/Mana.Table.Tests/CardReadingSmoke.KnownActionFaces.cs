using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal sealed partial class CardReadingSmoke
{
    private void QueueKnownActionFaceChecks()
    {
        const string identity = "rendered-owned-manifest";
        const string name = "Fathom Seer";
        var physical = new Card { Name = "Face-down card", CombatId = identity, FaceDown = true,
            Type = "Creature", Power = 2, Toughness = 2 };
        var known = new Card { Name = name, Type = "Creature — Illusion", ManaCost = "{1}{U}",
            Text = "When Fathom Seer is turned face up, draw two cards.", Power = 1, Toughness = 3 };
        string PaintedText() => Regex.Replace(string.Join(" ", view.Frame!.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null
        }).Where(text => text != null)), @"\s+", " ");
        GameSnapshot? granted = null;
        Step(0, () => {
            var seed = PresentationSmoke.Example();
            Publish(seed with { Id = "rendered-known-action-face", PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
                Players = seed.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                    Count = zone.Name == "Library" ? 80 : zone.Name == "Battlefield" && player.Id == 0 ? 1 : 0,
                    Cards = zone.Name == "Battlefield" && player.Id == 0 ? [physical] : []
                }).ToImmutableArray() }).ToImmutableArray(), Stack = [], Combat = null
            });
            var ordinary = scenario.State;
            granted = ordinary with { Revision = ordinary.Revision + 1, ActionSources = [
                new(ordinary.Decision!.Id, "Battlefield", 0, ordinary.Viewer!.Zone("Battlefield").Cards.Single()) { KnownFace = known }
            ] };
            scenario.SourceActionRead = (game, decision, key) => new(game, decision, key, identity,
                [new("rendered-turn-face-up", "activate", "Turn face up — pay {1}{U}")]) { Revision = view.Match!.Revision };
            view.Accept(granted); view.SetTableStudy(true); Move(new(10, 70));
        });
        Prepared(() => view.Frame!.Cards.Any(card => card.Id == identity && card.Card.FaceDown), 2,
            () => MoveHit(view.Hits.Last(hit => hit.Card?.CombatId == identity).Id),
            "The ordinary masked owner occurrence must be exposed before its real pointer gesture"); Click();
        Prepared(() => ReadingReady(identity) && view.Hits.Any(hit => hit.Id == "read-card-action"), 2, () => {
            var reader = view.Frame!.Cards.Single(card => card.Id == identity);
            var readouts = ManaPaymentSmoke.ReaderSeatReadoutGeometry(view);
            Check(readouts.Clear && readouts.CoveredTargetsRetired && readouts.CoveredPlans > 0 && readouts.UncoveredVisible,
                "The rendered reader's complete current surface occludes covered seat paint and hits while other seat readouts stay visible");
            Check(reader.Focused && reader.Card.FaceDown && reader.Hit.Card!.FaceDown
                && reader.Card.VisualId.Length == 0 && reader.Card.Power == 2 && reader.Card.Toughness == 2,
                "A rendered owner reader uses a separate private printed face while its physical occurrence and hit remain masked 2/2");
            Capture("20-owned-face-down-reader"); MoveHit("read-card-rules");
        }, "The normal source-action reader must open from the owned masked battlefield occurrence"); Click();
        Prepared(() => PaintedText().Contains(name) && PaintedText().Contains("draw two cards")
            && PaintedText().Contains("Artwork"), 2, () => {
            var readouts = ManaPaymentSmoke.ReaderSeatReadoutGeometry(view);
            Check(readouts.Clear && readouts.CoveredTargetsRetired && readouts.UncoveredVisible,
                "The rendered Rules face and current state remain clear of seat paint and covered input targets");
            Check(PaintedText().Contains("2/2") && PaintedText().Contains("Face down") && !PaintedText().Contains("1/3"),
                "Rendered private rules retain actual face-down 2/2 state instead of printed creature characteristics");
            Capture("21-owned-face-down-rules");
            view.Accept(granted! with { Revision = granted!.Revision + 1, ActionSources = [] }); Move(new(10, 70));
        }, "The existing Rules flip must reveal the granted printed text");
        Prepared(() => view.Frame!.Cards.All(card => !card.Focused) && !PaintedText().Contains(name), 2, () => {
            Check(view.Hits.All(hit => hit.Id is not ("read-card-action" or "read-card-rules")),
                "Withdrawing ordinary private reading removes its rendered face and action targets");
            Capture("22-owned-face-down-grant-revoked");
            var seed = scenario.State;
            Publish(seed with { Id = "rendered-opponent-face-down", ActionSources = [], Players = seed.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Count = player.Id == 1 ? 1 : 0, Cards = player.Id == 1 ? [physical] : []
                }).ToImmutableArray()
            }).ToImmutableArray() });
        }, "A withdrawn private face must collapse before the next captured audience");
        Prepared(() => view.Frame!.Cards.Any(card => card.Id == identity && card.Card.FaceDown), 2, () => {
            Check(!PaintedText().Contains(name) && view.Frame!.Cards.All(card => !card.Focused),
                "The rendered opponent occurrence stays masked without an owner grant");
            Capture("23-opponent-face-down-mask");
        }, "The opponent's ordinary masked physical card must reach its table position");
    }
}
