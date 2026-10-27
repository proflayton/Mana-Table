using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mana.Contracts;
using Mana.Forge;
using Mana.Planning;

namespace Mana.Table;

internal static class ManaFundingWireChecks
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    internal static void Run(Action<bool, string> check)
    {
        var original = Observation();
        string originalText = original.ToJsonString();
        var state = Decode(original);
        var payment = (PaymentPayload)state.Decision!.Payload;
        check(payment is { PayerId: 0, FundingContext: not null, UsablePoolManaColors: ["U"] }
                && payment.UnpaidMana.SequenceEqual(["{2}", "{U}"])
                && state.Decision.Context.Source?.Card?.ManaCost == "{99}"
                && original.ToJsonString() == originalText,
            "The immutable payment wire preserves its actual remaining bill and payer instead of reconstructing printed source costs");
        check(ManaFundingPlanner.Evaluate(payment.FundingContext!, payment.UnpaidMana).Status == ManaFundingStatus.Funded
                && GameCommandPermissions.Allows(state, new SpendPoolMana(state.Id, state.Decision.Id, "U"))
                && !GameCommandPermissions.Allows(state, new SpendPoolMana(state.Id, state.Decision.Id, "C")),
            "A read-only exact-bill witness remains distinct from the current narrower floating-mana spending control");
        check(!GameCommandPermissions.Allows(state, new PayMana(state.Id, state.Decision.Id))
                && !GameCommandPermissions.Allows(state, new UseManaSource(state.Id, state.Decision.Id, "viewer-source"))
                && !GameCommandPermissions.Allows(state, new UseOfferedAction(state.Id, state.Decision.Id, "viewer-source", "route-0")),
            "Funding metadata cannot enable a disabled Pay control or turn public resource occurrence IDs into executable action handles");

        var foreignWire = Observation(payer: 1);
        var foreign = Decode(foreignWire);
        var foreignPayment = (PaymentPayload)foreign.Decision!.Payload;
        check(foreignPayment.PayerId == 1 && foreignPayment.FundingContext!.Sources.Single().SourceCardId == "payer-source"
                && GameCommandPermissions.Allows(foreign, new SpendPoolMana(foreign.Id, foreign.Decision.Id, "G"))
                && !GameCommandPermissions.Allows(foreign, new SpendPoolMana(foreign.Id, foreign.Decision.Id, "U")),
            "An explicit actual payer permits only that known player's scoped funding and floating mana while decision control stays with the viewer");
        var unnamedForeign = (JsonObject)foreignWire.DeepClone();
        ((JsonObject)unnamedForeign["prompt"]!).Remove("payerId");
        check(Reject(unnamedForeign), "Foreign resource facts without an explicit payer cannot borrow another player's pool or battlefield");
        var wrongOwner = (JsonObject)foreignWire.DeepClone();
        wrongOwner["prompt"]!["payerId"] = 0;
        check(Reject(wrongOwner), "Changing only the payer revokes a funding context whose resources belong to the previous payer");

        foreach (string field in new[] { "unpaidMana", "fundingContext", "usablePoolManaColors", "payerId" }) {
            var unrelated = Observation();
            unrelated["prompt"] = new JsonObject { ["id"] = "input", ["kind"] = "input", ["task"] = "priority",
                ["okEnabled"] = true, [field] = original["prompt"]![field]!.DeepClone() };
            check(Reject(unrelated), "Current payment metadata is rejected on an unrelated priority payload: " + field);
        }

        var malformed = new List<(string Description, Action<JsonObject> Change)> {
            ("an absent payer", node => node["prompt"]!["payerId"] = 91),
            ("invented floating units", node => Funding(node)["pool"]!["U"] = 2),
            ("a non-battlefield occurrence", node => Source(node)["sourceCardId"] = "source-portrait"),
            ("negative production", node => Route(node)["production"]!["alternatives"]![0]!["colors"]!["G"] = -1),
            ("unknown eligibility enum", node => Route(node)["eligibility"] = 91),
            ("duplicate physical sources", node => ((JsonArray)Funding(node)["sources"]!).Add(Source(node).DeepClone())),
            ("a context action handle", node => Funding(node)["actionId"] = "invented"),
            ("a source action key", node => Source(node)["sourceCardKey"] = "invented"),
            ("a route action handle", node => Route(node)["actionId"] = "invented"),
            ("an invalid applicable color", node => node["prompt"]!["usablePoolManaColors"] = new JsonArray("neutral")),
            ("a duplicated applicable color", node => node["prompt"]!["usablePoolManaColors"] = new JsonArray("U", "U")),
            ("an applicable color absent from the payer pool", node => node["prompt"]!["usablePoolManaColors"] = new JsonArray("B"))
        };
        foreach (var (description, change) in malformed) {
            var invalid = Observation(); change(invalid);
            check(Reject(invalid), "The current payment wire rejects " + description + " without acquiring action authority");
        }
        var legacy = Observation();
        var prompt = (JsonObject)legacy["prompt"]!;
        foreach (string field in new[] { "payerId", "unpaidMana", "fundingContext", "usablePoolManaColors" }) prompt.Remove(field);
        var legacyState = Decode(legacy);
        check(legacyState.Decision!.Payload is PaymentPayload { FundingContext: null, PayerId: null, UsablePoolManaColors: null }
                && GameCommandPermissions.Allows(legacyState, new SpendPoolMana(legacyState.Id, legacyState.Decision.Id, "C")),
            "An older payment wire retains missing funding knowledge and legacy current-viewer pool behavior explicitly");
        var none = Observation(); none["prompt"]!["usablePoolManaColors"] = new JsonArray();
        var noneState = Decode(none);
        check(!GameCommandPermissions.Allows(noneState, new SpendPoolMana(noneState.Id, noneState.Decision!.Id, "U")),
            "A current explicit empty applicability array survives wire decoding as no usable floating mana");

        var sourceSet = new SourceActionSet(state.Id, state.Decision.Id, "original-source-key", "original-source-occurrence",
            [new("original-action", "cast", "Cast the offered route") { CostPreview = new(
                [new(ActionCostKind.Mana, 1, false, "") { ManaSymbols = ["{U}"] }], SemanticKnowledge.Unknown, payment.FundingContext!) }]) {
                Revision = state.Revision };
        var sourceWire = (JsonObject)JsonSerializer.SerializeToNode(sourceSet, Wire)!;
        var decoded = ForgeEngine.DecodeSourceActions(JsonSerializer.SerializeToElement(sourceWire));
        check(decoded.Revision == state.Revision && decoded.Actions.Single() is {
                Id: "original-action", CostPreview: { ManaCostKnowledge: SemanticKnowledge.Unknown, Costs: [{ Kind: ActionCostKind.Mana }],
                    Funding: { Knowledge: SemanticKnowledge.Unknown } } }
                && decoded.Actions.Single().CostPreview!.Funding.Sources.Single().Routes.Single().Eligibility == SemanticKnowledge.Known,
            "The production source-catalog decoder consumes actual string-enum cost, funding and eligibility facts without replacing the original action handle");
        foreach (bool routeHandle in new[] { false, true }) {
            var unsafeSource = (JsonObject)sourceWire.DeepClone();
            var preview = (JsonObject)unsafeSource["actions"]![0]!["costPreview"]!;
            if (routeHandle) preview["funding"]!["sources"]![0]!["routes"]![0]!["actionId"] = "invented";
            else preview["sourceCardKey"] = "invented";
            check(RejectSource(unsafeSource), "The production source-catalog decoder rejects extra executable handles in closed read-only cost and funding metadata");
        }
        var integerEnum = (JsonObject)sourceWire.DeepClone();
        integerEnum["actions"]![0]!["costPreview"]!["manaCostKnowledge"] = 0;
        check(RejectSource(integerEnum), "The source-catalog wire cannot silently replace its named knowledge enum with an unversioned integer value");

        var countedWire = Observation();
        ((JsonObject)countedWire["prompt"]!).Remove("unpaidMana");
        countedWire["prompt"]!["unpaidManaParts"] = JsonSerializer.SerializeToNode(new[] { new ManaBillPart("{129}", 1), new ManaBillPart("{U}", 3) }, Wire);
        Route(countedWire)["production"]!["alternatives"]![0]!["colors"] = new JsonObject { ["C"] = 129, ["U"] = 3 };
        var countedState = Decode(countedWire);
        var countedPayment = (PaymentPayload)countedState.Decision!.Payload;
        check(countedPayment.UnpaidMana.IsEmpty && countedPayment.UnpaidManaParts.SequenceEqual([new("{129}", 1), new("{U}", 3)]),
            "The current public bill keeps 129 generic and three exact blue units as two counted parts without truncating or expanding its wire shape");
        check(ManaFundingPlanner.EvaluateBill(countedPayment.FundingContext!, countedPayment.UnpaidManaParts).Status == ManaFundingStatus.Funded,
            "A counted current bill reaches the shared nominal solver without treating repeated colored demand as one unit");
        var hugeWire = (JsonObject)countedWire.DeepClone();
        hugeWire["prompt"]!["unpaidManaParts"] = JsonSerializer.SerializeToNode(new[] { new ManaBillPart("{U}", int.MaxValue) }, Wire);
        var hugePayment = (PaymentPayload)Decode(hugeWire).Decision!.Payload;
        var hugePlan = ManaFundingPlanner.EvaluateBill(hugePayment.FundingContext!, hugePayment.UnpaidManaParts);
        check(hugePayment.UnpaidManaParts is [{ Symbol: "{U}", Count: int.MaxValue }]
                && hugePlan is { Status: ManaFundingStatus.Unknown, ReasonCode: "mana-bill-bound", WorkSpent: 1, Steps.IsEmpty: true },
            "A huge exact current count remains readable in the payload while bounded planning returns Unknown before allocating repeated shards");
        var ambiguousBill = (JsonObject)countedWire.DeepClone();
        ambiguousBill["prompt"]!["unpaidMana"] = new JsonArray("{U}");
        check(Reject(ambiguousBill), "A provider cannot publish competing legacy and counted remaining bills on one payment input");
        var unrelatedParts = Observation();
        unrelatedParts["prompt"] = new JsonObject { ["id"] = "input", ["kind"] = "input", ["task"] = "priority", ["okEnabled"] = true,
            ["unpaidManaParts"] = countedWire["prompt"]!["unpaidManaParts"]!.DeepClone() };
        check(Reject(unrelatedParts), "Counted payment parts cannot acquire meaning or authority on an unrelated priority decision");
        foreach (var invalidParts in new[] {
            new JsonArray(new JsonObject { ["symbol"] = "{U}", ["count"] = 0 }),
            new JsonArray(new JsonObject { ["symbol"] = "{U}", ["count"] = 1 }, new JsonObject { ["symbol"] = "{U}", ["count"] = 2 }),
            new JsonArray(new JsonObject { ["symbol"] = "{U}", ["count"] = 1, ["actionId"] = "invented" })
        }) {
            var invalid = (JsonObject)countedWire.DeepClone(); invalid["prompt"]!["unpaidManaParts"] = invalidParts;
            check(Reject(invalid), "Counted bill rows reject nonpositive counts, duplicate symbols and extra executable metadata without discarding the actual payment context");
        }
    }

    private static JsonObject Observation(int payer = 0)
    {
        var viewerPool = ImmutableDictionary<string, int>.Empty.Add("U", 1).Add("C", 1);
        var payerPool = ImmutableDictionary<string, int>.Empty.Add("G", 2);
        var production = new ManaProduction(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add(payer == 0 ? "G" : "C", 1))]);
        var funding = new ManaFundingContext(SemanticKnowledge.Unknown, payer == 0 ? viewerPool : payerPool,
            [new(payer == 0 ? "viewer-source" : "payer-source", [new([new(ActionCostKind.Tap, 1, true, "")], production, SemanticKnowledge.Known)])]);
        var players = new[] {
            new Player(0, "Viewer", 40, true, false, viewerPool, [new("Battlefield", 1, [new Card { VisualId = "viewer-source", Type = "Land" }], null)]),
            new Player(1, "Payer", 40, false, false, payerPool, [new("Battlefield", 1, [new Card { VisualId = "payer-source", Type = "Land" }], null)])
        };
        return (JsonObject)JsonSerializer.SerializeToNode(new {
            id = "funding-wire", status = "playing", viewerId = 0, turn = 4, phase = "Main", phaseKey = "MAIN1", activePlayerId = 0,
            revision = 17, boardRevision = 17, decisionOwnerId = 0, players, stack = Array.Empty<object>(),
            prompt = new { id = "input", kind = "input", task = "mana", okEnabled = false, cancelEnabled = true,
                payerId = payer, unpaidMana = new[] { "{2}", payer == 0 ? "{U}" : "{G}" }, fundingContext = funding,
                usablePoolManaColors = new[] { payer == 0 ? "U" : "G" }, sourceCard = new Card { VisualId = "source-portrait", ManaCost = "{99}" },
                sourceCardId = "source-portrait", sourceZone = "Stack", sourceControllerId = 0 }
        }, Wire)!;
    }
    private static JsonObject Funding(JsonObject node) => (JsonObject)node["prompt"]!["fundingContext"]!;
    private static JsonObject Source(JsonObject node) => (JsonObject)Funding(node)["sources"]![0]!;
    private static JsonObject Route(JsonObject node) => (JsonObject)Source(node)["routes"]![0]!;
    private static GameSnapshot Decode(JsonObject wire) => ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(wire))!;
    private static bool Reject(JsonObject wire)
    {
        try { Decode(wire); return false; }
        catch (IOException) { return true; }
    }
    private static bool RejectSource(JsonObject wire)
    {
        try { ForgeEngine.DecodeSourceActions(JsonSerializer.SerializeToElement(wire)); return false; }
        catch (IOException) { return true; }
    }
}
