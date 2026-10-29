using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiRouteRetryRegression
{
    private const string Original = "first-disclosure", Renewed = "renewed-disclosure", Stable = "provider-occurrence-route";
    private static readonly DecisionReply Enabled = new("Continue", true), Disabled = new("Continue", false);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var session = Session();
        var original = Priority(Original, revision: 1);
        var started = await Choose(session, Frame(original, Stable));
        check(started?.Command is UseOfferedAction, "An unattempted provider-bound route can begin normally");
        session.Report(started!, AiExecutionReceipt.Accepted);

        var payment = original with { Revision = 2, BoardRevision = 2, PriorityPlayerId = null, ActionClass = "mana",
            Decision = new("actual-payment", new(), new PaymentPayload(Disabled, Enabled)) };
        var paymentFrame = new AiDecisionFrame(payment, domain: new(1, ActionDomainCoverage.Complete, [], []),
            semantics: new() { UnpaidMana = ["{2}", "{R}"] });
        var cancelled = await Choose(session, paymentFrame);
        check(cancelled?.Command is CancelPayment, "The actual payment continuation declines an unfunded bill rather than fabricating a paid result");
        session.Report(cancelled!, AiExecutionReceipt.Accepted);

        var renewed = Priority(Renewed, revision: 3);
        var guarded = await Choose(session, Frame(renewed, Stable));
        check(guarded?.Command is PassPriority && session.History.AttemptedRoutes.Count == 1,
            "A cancelled route stays guarded when its source disclosure and opaque offer change but the provider's rules-occurrence route remains the same");
        session.Report(guarded!, AiExecutionReceipt.Accepted);

        var otherCopy = Priority("distinct-physical-copy", revision: 4);
        var distinct = await Choose(session, Frame(otherCopy, "other-provider-occurrence-route"));
        check(distinct?.Command is UseOfferedAction,
            "A distinct physical copy with its own provider-bound stable route is not suppressed by another occurrence's retry guard");
        session.Report(distinct!, AiExecutionReceipt.Accepted);

        var otherRoute = Priority(Renewed, revision: 5);
        var alternative = await Choose(session, Frame(otherRoute, "different-route-on-renewed-source"));
        check(alternative?.Command is UseOfferedAction,
            "A genuinely different offered route on the same disclosed source remains available");
        session.Report(alternative!, AiExecutionReceipt.Accepted);

        var nextTurn = Priority("later-disclosure", revision: 6, turn: 9);
        var later = await Choose(session, Frame(nextTurn, Stable));
        check(later?.Command is UseOfferedAction && session.History.AttemptedRoutes.IsEmpty,
            "A new turn releases the prior turn's retry guard without permanently suppressing the provider route");

        using var shortHistory = Session(historyCapacity: 2);
        var first = await Choose(shortHistory, Frame(original, Stable));
        shortHistory.Report(first!, AiExecutionReceipt.Accepted);
        for (int revision = 2; revision <= 4; revision++) {
            var acknowledgment = original with { Revision = revision, BoardRevision = revision,
                Decision = new("required-ack-" + revision, new(), new ConfirmationPayload(Enabled)) };
            var answer = await Choose(shortHistory, new(acknowledgment));
            shortHistory.Report(answer!, AiExecutionReceipt.Accepted);
        }
        var afterEviction = await Choose(shortHistory, Frame(Priority(Renewed, revision: 5), Stable));
        check(afterEviction?.Command is PassPriority && shortHistory.History.AttemptedRoutes.Count == 1
                && shortHistory.History.Decisions.All(record => record.Command != nameof(UseOfferedAction)),
            "Evicting diagnostic records cannot reopen a stable route through a renewed source disclosure");

        var legacy = new AiSeatHistory([], []) { AttemptedRoutes = [new(8, Original, "")] };
        check(legacy.HasAttemptedRoute(8, Original, "") && !legacy.HasAttemptedRoute(8, Renewed, "")
                && !legacy.HasAttemptedRoute(9, Original, "") && !legacy.HasAttemptedRoute(8, Renewed, Stable),
            "Absent stable identity preserves only the original exact-source guard and cannot suppress unrelated sources, known routes or later turns");
    }

    private static AiSeatSession Session(int historyCapacity = 128) => new("route-retry-game", 0, new CasualCommanderPolicy(), 17,
        new(TimeSpan.FromSeconds(5), workUnits: 1024, historyCapacity: historyCapacity));

    private static async Task<AiSubmission?> Choose(AiSeatSession session, AiDecisionFrame frame)
    {
        session.Observe(frame);
        await session.EvaluateAsync();
        return session.TakeCommand();
    }

    private static AiDecisionFrame Frame(GameSnapshot state, string stableId)
    {
        var card = state.Viewer!.Zone("Hand").Cards.Single();
        string offerId = "current-offer-" + state.Revision;
        var semantics = new ActionSemantics(offerId, ActionEffectKind.Draw, [ActionEffectKind.Draw], [], ActionAffordability.Payable) {
            StableId = stableId
        };
        var domain = new DecisionActionDomain(state.Revision, ActionDomainCoverage.Complete,
            [new(state.Id, state.Decision!.Id, card.Key, card.VisualId, [new(offerId, "cast", "Untrusted display description")])], [semantics]);
        return new(state, domain: domain);
    }

    private static GameSnapshot Priority(string sourceId, long revision, int turn = 8)
    {
        var card = new Card { VisualId = sourceId, CombatId = sourceId, Key = sourceId + ":key-" + revision,
            Name = "Disclosed action source", Type = "Instant" };
        return new("route-retry-game", "playing", null, null, 0, turn, "Main", "MAIN1", 0, [
            new(0, "Acting seat", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", 1, [card], null), new("Battlefield", 0, [], null)]),
            new(1, "Other seat", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        ], [], null, new("current-priority-" + revision, new(), new PriorityPayload(Enabled))) {
            Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, PriorityPlayerId = 0
        };
    }
}
