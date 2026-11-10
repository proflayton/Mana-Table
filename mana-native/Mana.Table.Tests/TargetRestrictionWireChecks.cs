using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static class TargetRestrictionWireChecks
{
    internal static JsonNode Wire() => JsonNode.Parse("""
        {"id":"target-restriction-game","status":"playing","viewerId":0,"revision":5,"boardRevision":5,
         "turn":3,"phase":"Main phase","phaseKey":"MAIN1","activePlayerId":0,"decisionOwnerId":0,"actionClass":"selection","stack":[],
         "players":[{"id":0,"name":"You","life":40,"mana":{},"zones":[{"name":"Battlefield","count":3,"cards":[
           {"visualId":"omnath","combatId":"omnath","key":"blocked-key","name":"Omnath, Locus of Mana","type":"Legendary Creature","power":1,"toughness":1},
           {"visualId":"greaves","combatId":"greaves","key":"greaves-key","name":"Lightning Greaves","type":"Artifact — Equipment"},
           {"visualId":"staff","combatId":"staff","key":"staff-key","name":"Staff of Domination","type":"Artifact"}]}]},
           {"id":1,"name":"Opponent 1","life":40,"mana":{},"zones":[{"name":"Battlefield","count":1,"cards":[
             {"visualId":"legal-target","combatId":"legal-target","key":"legal-key","name":"Grizzly Bears","type":"Creature","power":2,"toughness":2,"selectable":true}]}]},
           {"id":2,"name":"Opponent 2","life":40,"mana":{},"zones":[]},
           {"id":3,"name":"Opponent 3","life":40,"mana":{},"zones":[]}],
         "prompt":{"id":"choose-staff-target","kind":"input","task":"selection","purpose":"targets","min":1,"max":1,
           "ok":"Confirm","okEnabled":false,"cancel":"Cancel","cancelEnabled":true,"playerChoices":[],
           "sourceCardId":"staff","sourceControllerId":0,"sourceZone":"Battlefield",
           "sourceCard":{"visualId":"staff","combatId":"staff","name":"Staff of Domination","type":"Artifact"},
           "targetRestrictions":[{"cardId":"omnath","reason":"shroud","detail":"This can't be the target of spells or abilities.","sourceCardId":"greaves"}]}}
        """)!;

    internal static GameSnapshot Decode(JsonNode wire) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(wire))!;

    internal static void Run(Action<bool, string> check)
    {
        var state = Decode(Wire()); var blocked = state.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == "omnath");
        var explanation = TargetRestrictionFacts.For(state, blocked);
        check(explanation is { Reason: TargetRestrictionReason.Shroud, SourceCardId: "greaves" }
                && TargetRestrictionFacts.PublicCard(state, explanation.SourceCardId)!.Name == "Lightning Greaves"
                && !GameCommandPermissions.Allows(state, new SelectCard(state.Id, state.Decision!.Id, blocked.Key)),
            "The real snapshot decoder binds a read-only engine reason and public granting source without authorizing the blocked card");
        foreach (bool explicitNull in new[] { false, true }) {
            var old = Wire(); if (explicitNull) old["prompt"]!["targetRestrictions"] = null; else old["prompt"]!.AsObject().Remove("targetRestrictions");
            check(((TargetSelectionPayload)Decode(old).Decision!.Payload).TargetRestrictions.IsEmpty,
                "An older provider's missing or null target explanations remain empty without guessing rules from card keywords");
        }
        var future = Wire(); future["prompt"]!["targetRestrictions"]![0]!["reason"] = "future-restriction";
        future["prompt"]!["targetRestrictions"]![0]!.AsObject().Remove("detail");
        var futureState = Decode(future);
        check(((TargetSelectionPayload)futureState.Decision!.Payload).TargetRestrictions[0].Reason == TargetRestrictionReason.Unknown
                && TargetRestrictionFacts.For(futureState, futureState.Viewer!.Zone("Battlefield").Cards[0]) == null,
            "A future explanation code remains nonauthorizing unknown with no hint rather than disconnecting an otherwise valid game");
        var bad = new List<(string Name, Action<JsonNode> Change)> {
            ("hidden target", node => node["players"]![0]!["zones"]![0]!["cards"]![0]!["faceDown"] = true),
            ("hidden grant", node => node["players"]![0]!["zones"]![0]!["cards"]![1]!["faceDown"] = true),
            ("unknown target", node => node["prompt"]!["targetRestrictions"]![0]!["cardId"] = "unpublished"),
            ("unknown grant", node => node["prompt"]!["targetRestrictions"]![0]!["sourceCardId"] = "unpublished"),
            ("legal target contradiction", node => node["players"]![0]!["zones"]![0]!["cards"]![0]!["selectable"] = true),
            ("nonbattlefield target", node => node["players"]![0]!["zones"]![0]!["name"] = "Hand"),
            ("duplicate target", node => node["prompt"]!["targetRestrictions"]!.AsArray().Add(node["prompt"]!["targetRestrictions"]![0]!.DeepClone())),
            ("invalid row", node => node["prompt"]!["targetRestrictions"] = new JsonArray(7)),
            ("invalid reason", node => node["prompt"]!["targetRestrictions"]![0]!["reason"] = 7),
            ("missing detail", node => node["prompt"]!["targetRestrictions"]![0]!.AsObject().Remove("detail")),
            ("invalid source identity", node => node["prompt"]!["targetRestrictions"]![0]!["sourceCardId"] = ""),
            ("unbounded detail", node => node["prompt"]!["targetRestrictions"]![0]!["detail"] = new string('x', 1025)),
            ("entity cost", node => node["prompt"]!["purpose"] = ""),
            ("library search", node => node["prompt"]!["librarySearch"] = new JsonObject {
                ["libraryOwnerId"] = null, ["destination"] = "Hand", ["cardName"] = "Basic land" })
        };
        foreach (var (name, change) in bad) {
            var node = Wire(); change(node); bool rejected = false;
            try { Decode(node); } catch (IOException) { rejected = true; }
            check(rejected, "Target explanation admission rejects " + name + " at the existing snapshot boundary");
        }
        future["prompt"]!["targetRestrictions"]![0]!["sourceCardId"] = "hidden-source";
        bool hiddenFuture = false; try { Decode(future); } catch (IOException) { hiddenFuture = true; }
        check(hiddenFuture, "An unknown future reason cannot bypass the public-identity fence for its granting source");
        var legal = state.Players[1].Zone("Battlefield").Cards[0];
        check(GameCommandPermissions.Allows(state, new SelectCard(state.Id, state.Decision!.Id, legal.Key))
                && TargetRestrictionFacts.For(state, legal) == null,
            "A legal neighboring target retains its exact current command key without acquiring blocked-target feedback");
        var selectedWire = Wire(); selectedWire["players"]![1]!["zones"]![0]!["cards"]![0]!["highlighted"] = true;
        var selectedState = Decode(selectedWire); var selectedCard = selectedState.Players[1].Zone("Battlefield").Cards[0];
        check(selectedCard.Highlighted && TargetRestrictionFacts.For(selectedState, selectedCard) == null
                && GameCommandPermissions.Allows(selectedState, new SelectCard(selectedState.Id, selectedState.Decision!.Id, selectedCard.Key)),
            "An already-selected legal card keeps its ordinary deselection command and never receives a blocked-target explanation");
    }
}
