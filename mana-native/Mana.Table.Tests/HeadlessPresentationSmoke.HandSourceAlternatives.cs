using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckHandSourceAlternatives(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        const string sourceId = "modal-hand-card";
        var modal = new Card { VisualId = sourceId, CombatId = sourceId, Name = "Bala Ged Recovery", Type = "Sorcery",
            ManaCost = "{2}{G}", Text = "Return target card from your graveyard to your hand.", Selectable = true,
            OtherFace = new("Bala Ged Sanctuary", "Land", "", "Bala Ged Sanctuary enters tapped.\n{T}: Add {G}.", null, null,
                "Bala Ged Recovery", "back") };
        var initial = Example() with { Id = "hand-source-alternatives", PriorityPlayerId = 0, DecisionOwnerId = 0,
            ActionClass = "priority", Players = Example().Players.Select(player => player with {
                LandPlaysUsed = 0, LandPlayLimit = 1,
                Zones = player.Zones.Select(zone => zone with {
                    Count = zone.Name == "Library" ? 80 : player.Id == 0 && zone.Name == "Hand" ? 1 : 0,
                    Cards = player.Id == 0 && zone.Name == "Hand" ? [modal] : []
                }).ToImmutableArray()
            }).ToImmutableArray() };

        foreach (bool landFirst in new[] { false, true }) {
            using var run = new Runner(profile, "hand-source-alternatives-" + landFirst, initial, trace, performance);
            Card Current() => run.Engine.State.Viewer!.Zone("Hand").Cards.Single();
            string LandOffer() => run.Engine.State.Decision!.Id + ":opaque-land";
            string CastOffer() => run.Engine.State.Decision!.Id + ":opaque-spell";
            run.Engine.SourceActionRead = (game, decision, key) => {
                var current = run.Engine.State;
                if (game != current.Id || decision != current.Decision?.Id || key != Current().Key)
                    throw new InvalidOperationException("The modal-card reader requested an obsolete source.");
                SourceAction land = new(LandOffer(), "play", "Play land (Bala Ged Sanctuary)");
                SourceAction cast = new(CastOffer(), "cast", modal.Text);
                return new(game, decision, key, sourceId, landFirst ? [land, cast] : [cast, land]) { Revision = current.Revision };
            };
            run.Engine.ReplyOverride = command => {
                var state = run.Engine.State;
                if (!GameCommandPermissions.Allows(state, command) || command is not UseOfferedAction offered
                    || offered.ActionId != LandOffer() || offered.Card != Current().Key)
                    throw new InvalidOperationException("Only the exact current land-face offer may be committed in this journey.");
                // A controlled provider acknowledgement, not a reimplementation
                // of land rules. The reader must preserve this authoritative face.
                var land = Current() with { Name = modal.OtherFace!.Name, Type = "Land", ManaCost = "", ArtFace = "back",
                    Text = modal.OtherFace.OracleText, OtherFace = null, Tapped = true, Selectable = false };
                run.Engine.Present(state with { Players = state.Players.Select(player => player.Id != 0 ? player : player with {
                    LandPlaysUsed = 1, Zones = player.Zones.Select(zone => zone.Name switch {
                        "Hand" => zone with { Count = 0, Cards = [] },
                        "Battlefield" => zone with { Count = 1, Cards = [land] }, _ => zone
                    }).ToImmutableArray()
                }).ToImmutableArray() });
                run.Engine.Replies.Add(command); return Task.CompletedTask;
            };
            run.Tick("modal-card-in-hand"); run.ClickCard(sourceId);
            string HitFor(string action) => run.Frame.Hits.Single(hit => hit.Intent is Mana.Table.Interaction.MatchIntent.UseSourceAction use
                && use.Id == action).Id;
            run.Until(() => run.Frame.Hits.Count(hit => hit.Intent is Mana.Table.Interaction.MatchIntent.UseSourceAction) == 2,
                "both-modal-card-offers-visible");
            var castHit = run.Hit(HitFor(CastOffer())); var landHit = run.Hit(HitFor(LandOffer()));
            var face = run.Frame.Cards.Single(card => card.Id == sourceId);
            var castText = ActionText(castHit); var landText = ActionText(landHit);
            check(face.Focused && run.Engine.Replies.Count == 0 && castHit.Bounds.Height >= 38 && landHit.Bounds.Height >= 38
                    && run.Frame.Drawing.Commands.OfType<DrawCommand.Panel>().Any(panel => panel.Bounds == castHit.Bounds)
                    && run.Frame.Drawing.Commands.OfType<DrawCommand.Panel>().Any(panel => panel.Bounds == landHit.Bounds)
                    && castText.Length > 0 && landText.Length > 0
                    && castText.All(text => text.Bold && text.Size >= .70f) && landText.All(text => text.Bold && text.Size >= .70f)
                    && !castHit.Bounds.Intersects(landHit.Bounds) && !face.Display.Bounds.Intersects(landHit.Bounds),
                "Both offered faces of a modal hand card have equally readable, surfaced actions beside its full card, regardless of offer order");

            run.Input(new(20, 60), label: "no-modal-action-pointed"); run.Settle("printed-front-cost-only");
            bool printedVisible = run.View.ManaJourney is { Payment: false } printed && printed.Symbols.SequenceEqual(["{2}", "{G}"])
                && Text(run.Frame).Contains("PRINTED SPELL COST") && run.Engine.Replies.Count == 0;
            check(printedVisible,
                "Before choosing a modal route, the front face's static cost is explicitly labeled PRINTED SPELL COST rather than a land-play bill"
                    + (printedVisible ? "" : " " + System.Text.Json.JsonSerializer.Serialize(new { run.View.ManaJourney,
                        face = run.Frame.Cards.Single(card => card.Id == sourceId).Display.Bounds,
                        focused = run.Frame.Cards.Single(card => card.Id == sourceId).Focused,
                        cast = castHit.Bounds, land = landHit.Bounds, pointed = run.View.HitAt(new Point(20, 60))?.Id,
                        text = Text(run.Frame) })));
            run.Input(run.PointFor(landHit), label: "hover-land-face-without-cost-preview"); run.Settle("land-has-no-borrowed-front-cost");
            check(run.View.ManaJourney == null && run.Engine.Replies.Count == 0,
                "Hovering the offered land face never borrows the front spell's printed mana cost");
            run.Input(run.PointFor(castHit), label: "hover-spell-offer-without-cost-preview"); run.Settle("unreported-offer-cost-remains-unknown");
            check(run.View.ManaJourney == null && run.Engine.Replies.Count == 0,
                "Hovering a spell offer with no scoped cost preview keeps its actual cost unknown instead of substituting the printed face's cost");

            run.ClickHit("read-card-rules"); run.Settle("modal-rules-read-only");
            check(run.Engine.Replies.Count == 0 && run.View.Match!.Decision!.Id == run.Engine.State.Decision!.Id
                    && run.Frame.Hits.Any(hit => hit.Intent is Mana.Table.Interaction.MatchIntent.UseSourceAction use && use.Id == LandOffer()),
                "Reading the modal card's rules preserves both engine offers without playing a face or answering priority");
            run.Input(new(20, 60), keys: new(Keys.R)); run.Input(new(20, 60)); run.Settle("modal-art-restored");

            var stale = run.Hit(HitFor(LandOffer())); var point = run.PointFor(stale);
            run.Input(point, left: ButtonState.Pressed, label: "press-current-land-face");
            check(run.Engine.Replies.Count == 0, "Pressing the land alternative does not commit before release");
            string retiredOffer = LandOffer(); run.Present(run.Engine.State);
            run.Input(point, label: "release-retired-land-face"); run.Settle("modal-offer-replaced");
            check(run.Engine.Replies.Count == 0 && !run.View.CurrentHit(stale),
                "A new decision between land-action press and release rejects the captured offer without a play command");
            run.Until(() => run.Frame.Hits.Any(hit => hit.Intent is Mana.Table.Interaction.MatchIntent.UseSourceAction use
                    && use.Id == LandOffer()), "current-modal-offers-refreshed");
            check(run.Frame.Hits.All(hit => hit.Intent is not Mana.Table.Interaction.MatchIntent.UseSourceAction use || use.Id != retiredOffer),
                "Refreshing the reader retires the old opaque land offer instead of presenting it as current");
            string expectedDecision = run.Engine.State.Decision!.Id, expectedKey = Current().Key, expectedLand = LandOffer();
            run.ClickHit(HitFor(expectedLand));
            run.Until(() => !run.View.Busy && run.Engine.Replies.Count == 1, "explicit-land-face-committed");
            check(run.Engine.Replies.Single() is UseOfferedAction played && played.GameId == initial.Id
                    && played.DecisionId == expectedDecision && played.Card == expectedKey && played.ActionId == expectedLand
                    && run.View.Match!.Viewer!.Zone("Hand").Cards.Length == 0
                    && run.View.Match.Viewer.Zone("Battlefield").Cards is [{ Name: "Bala Ged Sanctuary", Tapped: true }]
                    && run.View.Match.Viewer.LandPlaysUsed == 1,
                "Explicitly choosing Play land submits exactly that current face's opaque offer once and displays the provider's land result");

            DrawCommand.Text[] ActionText(TableHit hit) => run.Frame.Drawing.Commands.OfType<DrawCommand.Text>()
                .Where(text => hit.Bounds.Contains(new Point(text.X, text.Y))).ToArray();
        }

        foreach (bool spellFront in new[] { false, true }) {
            var card = spellFront ? modal : modal with { Name = "Forest", Type = "Basic Land — Forest", ManaCost = "",
                Text = "{T}: Add {G}.", OtherFace = null };
            var single = initial with { Players = initial.Players.Select(player => player.Id != 0 ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Cards = [card] }).ToImmutableArray()
            }).ToImmutableArray() };
            using var run = new Runner(profile, "sole-hand-land-action-" + spellFront, single, trace, performance);
            run.Engine.SourceActionRead = (game, decision, key) => new(game, decision, key, sourceId,
                [new("sole-land-offer", "play", spellFront ? "Play land (Bala Ged Sanctuary)" : "Play land")]);
            run.Tick("single-land-route"); run.ClickCard(sourceId);
            run.Until(() => run.Frame.Hits.Any(hit => hit.Id == "read-card-action"), "sole-land-route-visible");
            var land = run.Hit("read-card-action");
            var text = run.Frame.Drawing.Commands.OfType<DrawCommand.Text>().Where(item => land.Bounds.Contains(new Point(item.X, item.Y))).ToArray();
            bool surfaced = run.Frame.Drawing.Commands.OfType<DrawCommand.Panel>().Any(panel => panel.Bounds == land.Bounds);
            check(run.Engine.Replies.Count == 0 && land.Intent is Mana.Table.Interaction.MatchIntent.UseSourceAction { Id: "sole-land-offer" }
                    && surfaced == spellFront && text.Length > 0 && text.All(item => item.Bold == spellFront && (!spellFront || item.Size >= .70f)),
                spellFront ? "A spell-front modal card keeps its sole offered land face prominent even when no spell action is currently offered"
                    : "An ordinary land's sole offered play keeps the quieter routine action while retaining its exact current command permission");
        }
    }
}
