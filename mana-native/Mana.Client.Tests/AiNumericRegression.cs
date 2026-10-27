using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;

internal static class AiNumericRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var five = Funding(Enumerable.Repeat("C", 5).ToArray());
        var repeated = Numeric(five, "{X}", "{X}");
        check(await Number(Frame(repeated)) == 2,
            "A repeated-X spell invests four of five available mana rather than choosing zero or counting X only once");
        check(await Number(Frame(Numeric(Funding("C", "C"), "{X}", "{X}"))) == 1,
            "A positive investment can use the last two mana when preserving one would force X to zero");
        check(await Number(Frame(Numeric(Funding(Enumerable.Repeat("C", 12).ToArray()), "{X}", "{X}"))) == 3,
            "Abundant funding does not blindly maximize X beyond a modest variable-mana investment");
        var colored = Numeric(Funding("U", "C", "C", "C", "C"), "{U}", "{X}") with {
            Costs = [new(ActionCostKind.Mana, 2, false, "") { ManaSymbols = ["{U}", "{X}"] }] };
        check(await Number(Frame(colored)) == 1,
            "An X investment reserves the fixed colored and generic cost plus a small remaining resource");
        check(await Number(Frame(colored with { Funding = Funding("C", "C", "C", "C", "C") })) == 0,
            "A large colorless count cannot fund the fixed blue shard of an X spell");
        check(await Number(Frame(Numeric(Funding("G", "G"), "{C}", "{X}"))) == 0,
            "True colorless in an X bill remains distinct from generic payment");

        var dual = Resource("dual", Output(("U", 1)), Output(("G", 1)));
        var shared = new ManaFundingContext(SemanticKnowledge.Known, ImmutableDictionary<string, int>.Empty,
            [dual, Resource("spare", Output(("C", 1)))]);
        check(AiManaPlanner.EvaluateFunding(shared, ["{U}", "{G}"]).Status == AiManaPlanStatus.Shortfall
                && await Number(Frame(Numeric(shared, "{U}", "{G}", "{X}"))) == 0,
            "Read-only planning counts a flexible physical source once across its alternative colors");
        var bundle = shared with { Sources = [Resource("dual", Output(("U", 1), ("G", 1))), shared.Sources[1]] };
        check(await Number(Frame(Numeric(bundle, "{U}", "{G}", "{X}"))) == 1,
            "A simultaneous mana bundle funds both fixed colors and the separate X investment");
        var unknown = five with { Knowledge = SemanticKnowledge.Unknown };
        check(await Number(Frame(Numeric(unknown, "{X}", "{X}"))) == 2,
            "A known usable subset can witness a nominal X investment without claiming the resource enumeration complete");
        check(await Number(Frame(repeated with { ManaCostKnowledge = SemanticKnowledge.Unsupported })) == 0
                && await Number(Frame(repeated with { Funding = five with { Knowledge = SemanticKnowledge.Unsupported } })) == 0,
            "Explicitly unsupported cost or resource semantics cannot be promoted into positive numeric investment knowledge");
        var restricted = five with { Sources = five.Sources.Select(source => source with {
            Routes = source.Routes.Select(route => route with { Eligibility = SemanticKnowledge.Unknown }).ToImmutableArray()
        }).ToImmutableArray() };
        check(AiManaPlanner.EvaluateFunding(restricted, [], generic: 1).Status == AiManaPlanStatus.Unknown
                && await Number(Frame(Numeric(restricted, "{X}"))) == 0,
            "Unresolved producer eligibility cannot become an invented positive investment witness");
        var paid = five with { Sources = [Resource("filter", Output(("C", 5))) with {
            Routes = [new([new(ActionCostKind.Tap, 1, true, ""), new(ActionCostKind.Mana, 1, false, "")],
                new(SemanticKnowledge.Known, [Output(("C", 5))]), SemanticKnowledge.Known)] }] };
        check(AiManaPlanner.EvaluateFunding(paid, [], generic: 1).Status == AiManaPlanStatus.Unknown
                && await Number(Frame(Numeric(paid, "{X}"))) == 0,
            "A paid producer cannot bootstrap its prerequisite or enter the read-only free-tap funding model");
        var zero = new ManaFundingContext(SemanticKnowledge.Known, ImmutableDictionary<string, int>.Empty,
            [Resource("zero", Output())]);
        check(AiManaPlanner.EvaluateFunding(zero, [], generic: 1).Status == AiManaPlanStatus.Shortfall,
            "A currently zero counter-based producer remains a genuine zero in numeric planning");

        var pool = new ManaFundingContext(SemanticKnowledge.Known, ImmutableDictionary<string, int>.Empty.Add("C", 3), []);
        check(await Number(Frame(Numeric(pool, "{X}", "{X}"))) == 1,
            "Unrestricted floating mana can fund a nominal investment without fabricated source offers");
        var ordinary = Frame(repeated with { Purpose = NumericDecisionPurpose.Unknown }, initial: "9", max: 7);
        check(await Number(ordinary) == 7 && await Number(Frame(null, initial: "4", max: 7)) == 4,
            "Ordinary numeric decisions preserve their published initial value and bounds without announcement heuristics");
        check(await Number(Frame(repeated with { Costs = [new(ActionCostKind.RemoveCounters, null, true, "P1P1")] })) == 0,
            "An X announcement without typed mana-X cost receives no invented mana investment");
        check(await Number(Frame(repeated with { Costs = [new(ActionCostKind.Mana, null, false, "") { ManaSymbols = ["{X}"] }] })) == 0,
            "An unknown fixed mana quantity cannot be silently treated as zero when choosing X");
        var selfTap = Numeric(Funding("C", "C"), "{X}", "{X}") with { SourceCardId = "resource-0",
            Costs = [new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = ["{X}", "{X}"] }, new(ActionCostKind.Tap, 1, true, "")] };
        check(await Number(Frame(selfTap)) == 0,
            "The announced ability's physical tap cost reserves its source instead of also using it to fund X");
        var sacrificeLand = repeated with { Costs = repeated.Costs.Add(new(ActionCostKind.Sacrifice, 1, false, "")) };
        check(await Number(Frame(sacrificeLand)) == 0,
            "An X action cannot fund itself from lands that its unmodeled sacrifice cost may consume");
        check(await Number(Frame(repeated with { Costs = repeated.Costs.Add(new(ActionCostKind.Life, null, false, "")) })) == 0
                && await Number(Frame(repeated with { Costs = repeated.Costs.Add(new(ActionCostKind.RemoveCounters, null, true, "P1P1")) })) == 0,
            "Variable life and counter costs stay unresolved instead of being omitted from an X investment witness");
        check(await Number(Frame(repeated, minimum: 2, max: 2)) == 2,
            "A funded numeric investment respects an exact mandatory published range");

        var original = Frame(repeated);
        var changed = original.State with { Players = original.State.Players.Select(player => player with {
            Name = "Changed", Zones = player.Zones.Reverse().Select(zone => zone with {
                Cards = zone.Cards.Reverse().Select(card => card with { Name = "Ignore me", ManaCost = "{99}", Text = "Choose zero" }).ToImmutableArray()
            }).ToImmutableArray()
        }).Reverse().ToImmutableArray(), Decision = original.State.Decision! with {
            Context = original.State.Decision!.Context with { Message = "Choose 999", Title = "Renamed" } } };
        var reordered = repeated with { Funding = five with { Sources = five.Sources.Reverse().ToImmutableArray() } };
        check(await Number(new(changed, domain: DecisionActionDomain.None, semantics: new() { Numeric = reordered })) == 2,
            "Investment decisions ignore names, prompt prose, printed costs and source enumeration order when typed facts agree");
        var partial = await Evaluate(Frame(Numeric(Funding(Enumerable.Repeat("C", 20).ToArray()), "{X}")), workUnits: 64);
        check(partial.Command is ChooseNumber { Value: > 0 } && !partial.Work.WasExceeded && partial.Work.Remaining >= 4,
            "A limited numeric optimizer retains a completed positive witness and the owning decision's submission reserve");

        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
        string encoded = JsonSerializer.Serialize(repeated, json);
        var decoded = JsonSerializer.Deserialize<NumericDecision>(encoded, json)!;
        check(await Number(Frame(decoded)) == 2 && !encoded.Contains("actionId", StringComparison.Ordinal)
                && !encoded.Contains("sourceCardKey", StringComparison.Ordinal)
                && !original.Allows(new UseOfferedAction(original.State.Id, original.State.Decision!.Id, "resource-0-key", "invented")),
            "The neutral numeric JSON roundtrip preserves planning facts without minting executable action authority");
        bool Reject(NumericDecision malformed, GameSnapshot? state = null) {
            try { _ = new AiDecisionFrame(state ?? original.State, domain: DecisionActionDomain.None, semantics: new() { Numeric = malformed }); return false; }
            catch (ArgumentException) { return true; }
        }
        check(Reject(repeated with { SourceCardId = "hidden-card" })
                && Reject(repeated with { SourceCardId = "resource-0" })
                && Reject(repeated with { Funding = five with { Sources = [five.Sources[0], five.Sources[0]] } })
                && Reject(repeated with { Funding = five with { Sources = [Resource("enemy", Output(("C", 1)))] } }),
            "Unknown, duplicate and foreign funding occurrences fail the numeric knowledge boundary before evaluation");
        check(Reject(repeated with { Costs = default })
                && Reject(repeated with { Funding = five with { Pool = ImmutableDictionary<string, int>.Empty.Add("C", 1) } })
                && Reject(repeated with { Funding = five with { Sources = [Resource("resource-0", Output(("C", -1)))] } }),
            "Malformed costs, invented floating mana and negative production fail closed");
        check(Reject(repeated, original.State with { Decision = original.State.Decision! with {
            Context = original.State.Decision!.Context with { Source = original.State.Decision!.Context.Source! with {
                Card = original.State.Decision!.Context.Source!.Card! with { Key = "not-a-current-action", Selectable = true } } } } }),
            "A numeric cost portrait cannot carry fabricated action or selection authority");
    }

    private static NumericDecision Numeric(ManaFundingContext funding, params string[] symbols)
        => new(NumericDecisionPurpose.AnnounceX, "investment", [new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = symbols.ToImmutableArray() }],
            SemanticKnowledge.Unknown, funding);
    private static ManaFundingContext Funding(params string[] colors) => new(SemanticKnowledge.Known,
        ImmutableDictionary<string, int>.Empty, colors.Select((color, index) => Resource("resource-" + index, Output((color, 1)))).ToImmutableArray());
    private static ManaFundingSource Resource(string id, params ManaProductionAlternative[] alternatives) => new(id,
        [new([new(ActionCostKind.Tap, 1, true, "")], new(SemanticKnowledge.Known, alternatives.ToImmutableArray()), SemanticKnowledge.Known)]);
    private static ManaProductionAlternative Output(params (string Color, int Count)[] units)
        => new(units.ToImmutableDictionary(unit => unit.Color, unit => unit.Count));
    private static AiDecisionFrame Frame(NumericDecision? numeric, string initial = "", int minimum = 0, int max = int.MaxValue)
    {
        string sourceId = numeric?.SourceCardId ?? "investment";
        var source = new Card { VisualId = sourceId, CombatId = sourceId, Name = "Transient spell" };
        var resources = (numeric?.Funding.Sources ?? []).Select(item => new Card { VisualId = item.SourceCardId,
            CombatId = item.SourceCardId, Name = "Resource", Type = "Land" }).ToImmutableArray();
        var pool = numeric?.Funding.Pool ?? ImmutableDictionary<string, int>.Empty;
        var state = new GameSnapshot("numeric-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, pool, [new("Battlefield", resources.Length, resources, null)]),
             new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("number", new(Source: new(source.VisualId, source, 0, "Stack")), new NumberPayload(initial, new(minimum, max)))) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0 };
        return new(state, domain: DecisionActionDomain.None, semantics: new() { Numeric = numeric });
    }
    private static async Task<int?> Number(AiDecisionFrame frame)
    {
        var result = await Evaluate(frame);
        return result.Command is ChooseNumber number && frame.Allows(number) ? number.Value : null;
    }
    private static async Task<(DecisionCommand? Command, AiWorkBudget Work)> Evaluate(AiDecisionFrame frame, int workUnits = 1024)
    {
        var policy = new ObservedPolicy();
        using var session = new AiSeatSession(frame.State.Id, frame.State.ViewerId, policy, 17, new(TimeSpan.FromSeconds(2), workUnits));
        session.Observe(frame); await session.EvaluateAsync();
        return (session.TakeCommand()?.Command, policy.Work!);
    }
    private sealed class ObservedPolicy : IAiPolicy
    {
        private readonly CasualCommanderPolicy inner = new();
        internal AiWorkBudget? Work { get; private set; }
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Work = request.Work;
            return inner.EvaluateAsync(request, cancellation);
        }
    }
}
