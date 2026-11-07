using System.Collections.Concurrent;
using System.Text.Json;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;

internal static class AiManaOutputAuthorityRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        // Exact synthetic-CI seat knowledge and rejected command retained from
        // PR124/run42339037841/rotation3. These handles are offline evidence.
        var recorded = Read<AiDecisionReplayCase>("ci124-crystal-vein-payment.json");
        var rejected = Read<UseOfferedAction>("ci124-crystal-vein-rejected-command.json");
        var frame = new AiDecisionFrame(recorded.State, domain: recorded.Domain, semantics: recorded.Semantics);
        var set = frame.SourceActions.Single(source => source.SourceCardKey == rejected.Card);
        var offered = set.Actions.Single(action => action.Id == rejected.ActionId);
        check(recorded.State.Turn == 5 && recorded.State.Decision?.Payload is PaymentPayload
                && offered.QuickMana == null && rejected.ManaOutput?.Colors.GetValueOrDefault("C") == 2
                && frame.Domain.Actions.Single(action => action.ActionId == rejected.ActionId).Costs
                    .Any(cost => cost.Kind == ActionCostKind.Sacrifice && cost.FromSource)
                && !frame.Allows(rejected),
            "The exact retained CI consuming-source command is rejected by AI admission: described two-mana production cannot mint an absent output-selection capability");
        var answer = await AiDecisionReplay.EvaluateAsync(recorded, new CasualCommanderPolicy());
        check(answer.Status == AiSeatStatus.Ready && answer.Proposal is UseOfferedAction corrected
                && corrected.Card == rejected.Card && corrected.ActionId == rejected.ActionId && corrected.ManaOutput == null
                && frame.Allows(corrected) && answer.Evidence?.ReasonCode == "payment-follow-assignment-source",
            "The exact retained Foundry Inspector payment still follows its funded consuming route through the ordinary action without an unauthorized output override");

        var queue = new ConcurrentQueue<Action>();
        var session = new SourceActionSession(new CurrentCatalog(set with { Revision = recorded.State.Revision }), queue.Enqueue);
        try {
            var context = new SourceActionContext(recorded.State.Id, recorded.State.Decision!.Id, recorded.State.Revision,
                recorded.State.ViewerId, set.SourceCardKey, set.SourceCardId);
            session.Observe(context);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (session.View.Status == SourceActionStatus.Loading && DateTime.UtcNow < deadline) {
                while (queue.TryDequeue(out var completion)) completion();
                await Task.Yield();
            }
            check(session.View.Status == SourceActionStatus.Ready
                    && session.TryCreateCommand(context, rejected.ActionId, rejected.ManaOutput!.Colors) == null
                    && session.TryCreateCommand(context, rejected.ActionId) is { ManaOutput: null } ordinary
                    && ordinary == answer.Proposal,
                "The unchanged ordinary source reader agrees with AI admission on the retained action: reject the old override and admit its exact generic capability");
        } finally { session.Close(); }
    }

    private static T Read<T>(string suffix)
    {
        var assembly = typeof(AiManaOutputAuthorityRegression).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal)))!;
        if (stream.Length > 131072) throw new InvalidDataException("Retained source-authority fixture exceeded its bounded case size.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Missing retained source-authority fixture.");
    }
    private sealed class CurrentCatalog(SourceActionSet set) : ISourceActions
    {
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            if (gameId != set.GameId || decisionId != set.DecisionId || sourceCardKey != set.SourceCardKey)
                throw new InvalidOperationException("Retained source fixture received foreign authority.");
            return Task.FromResult(set);
        }
    }
}
