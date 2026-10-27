using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiTargetContinuationRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = Targets("current-targets", 1, productive: true, complete: true);
        var semantics = new DecisionSemantics(DecisionIntent.Exile) { EpisodeId = "actual-target-input" };
        var frame = new AiDecisionFrame(state, semantics: semantics);
        var selected = state.Players[1].Zone("Battlefield").Cards.Single(card => card.Highlighted);
        check(frame.Allows(new SelectCard(state.Id, state.Decision!.Id, selected.Key))
                && (await Evaluate(frame)).Command is SelectCard { Card: "current-targets:productive" },
            "The target policy preserves the engine's selected-card undo authority but adds only an unselected current productive target");
        check((await Evaluate(frame)).Command is not CompleteSelection,
            "An enabled target completion does not discard another productive optional target merely because the minimum is already satisfied");
        var renamed = state with { Decision = state.Decision! with { Context = new(Message: "Translated unrelated description") },
            Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Select(card => card with { Name = "Different printed title", Text = "Different rules prose" }).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray() };
        check((await Evaluate(new(renamed, semantics: semantics))).Command is SelectCard { Card: "current-targets:productive" },
            "Current target continuation follows typed admissibility rather than card names or rejection prose");
        var exhausted = Targets("fresh-completion-scope", 2, productive: false, complete: true);
        var completedFrame = new AiDecisionFrame(exhausted, semantics: semantics);
        var completion = await Evaluate(completedFrame);
        check(completion.Command is CompleteSelection complete && completedFrame.Allows(complete)
                && !completedFrame.Allows(complete with { DecisionId = state.Decision.Id }),
            "A valid target subset completes its fresh scope when only selected undo candidates remain, even below the maximum target count");
        var locked = Targets("locked-current-scope", 3, productive: false, complete: false);
        check((await Evaluate(new(locked, semantics: semantics))).Status == AiSeatStatus.Unavailable,
            "Exhausting current target additions cannot invent disabled completion or toggle an already selected target");

        using var bounded = new AiSeatSession(state.Id, 0, new CasualCommanderPolicy(), 17,
            new(TimeSpan.FromSeconds(10), workUnits: 8));
        int accepted = 0;
        for (int step = 0; step < 12; step++) {
            // A transport acceptance need not change selection. This models
            // the observed lifecycle failure without inventing engine success.
            var noProgress = Targets("refreshed-target-scope-" + step, step + 1, productive: true, complete: false);
            bounded.Observe(new(noProgress, semantics: semantics));
            if ((await bounded.EvaluateAsync()).Status == AiSeatStatus.BudgetExceeded) break;
            var answer = bounded.TakeCommand();
            if (answer?.Command is not SelectCard) break;
            bounded.Report(answer, AiExecutionReceipt.Accepted); accepted++;
        }
        check(accepted == 8 && bounded.View.Status == AiSeatStatus.BudgetExceeded && bounded.TakeCommand() == null
                && bounded.History.Decisions.Where(record => record.Outcome == AiDecisionOutcome.Accepted)
                    .Select(record => record.Seed).Distinct().Count() == 1,
            "Fresh target prompt IDs cannot reset the actual input episode's finite work or deterministic seed after repeated accepted no-progress attempts");
        var newInput = Targets("genuinely-new-target-input", 30, productive: true, complete: false);
        bounded.Observe(new(newInput, semantics: semantics with { EpisodeId = "different-actual-target-input" }));
        check((await bounded.EvaluateAsync()).Status == AiSeatStatus.Ready,
            "A distinct target input receives its own allowance while refreshed scopes of the prior input remain bounded");
    }

    private static async Task<(AiSeatStatus Status, DecisionCommand? Command)> Evaluate(AiDecisionFrame frame)
    {
        using var session = new AiSeatSession(frame.State.Id, 0, new CasualCommanderPolicy(), 17,
            new(TimeSpan.FromSeconds(5), workUnits: 1024));
        session.Observe(frame);
        var result = await session.EvaluateAsync();
        return (result.Status, session.TakeCommand()?.Command);
    }

    private static GameSnapshot Targets(string scope, long revision, bool productive, bool complete)
    {
        Card Face(string id, bool selectable, bool highlighted, int power = 3) => new() {
            Key = scope + ":" + id, VisualId = id, CombatId = id, Name = "Printed target",
            Type = "Creature", Power = power, Toughness = power, Selectable = selectable, Highlighted = highlighted
        };
        var players = ImmutableArray.Create(
            new Player(0, "You", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 0, [], null)]),
            new Player(1, "First opponent", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 2, [Face("selected-undo", true, true), Face("unproductive", false, false, 99)], null)]),
            new Player(2, "Second opponent", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [Face("productive", productive, false)], null)]));
        return new("target-continuation-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            players, [], null, new(scope, new(Message: "Current selection"),
                new TargetSelectionPayload(new("Done", complete), new("Cancel", false), new(0, 3), []))) {
                    Revision = revision, BoardRevision = revision, DecisionOwnerId = 0
                };
    }
}
