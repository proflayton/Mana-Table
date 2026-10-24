using System.Collections.Immutable;
using System.Collections.Concurrent;
using Mana.Client;
using Mana.Contracts;

internal static class SourceActionSessionRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var first = new SourceActionContext("game", "priority", 4, 0, "source-key", "public-occurrence");
        var action = new SourceAction("opaque-action", "cast", "Cast Toothy, Imaginary Friend");
        var missing = new SourceActionSession(null, _ => throw new Exception("Unsupported provider queued work."));
        missing.Observe(first);
        check(missing.View.Status == SourceActionStatus.Unsupported && missing.TryCreateCommand(first, action.Id) == null,
            "An absent source-action capability stays explicitly unsupported and cannot create a command");

        var lifecycle = new Driver();
        lifecycle.Provider.Supported = false;
        lifecycle.Session.Observe(first);
        check(lifecycle.Session.View.Status == SourceActionStatus.Unsupported && lifecycle.Provider.Requests.Length == 0,
            "An uninitialized provider does not receive a speculative action query");
        lifecycle.Provider.Supported = true;
        lifecycle.Session.Observe(first);
        var empty = await lifecycle.Request(1);
        empty.Result.SetResult(Catalog(first));
        await lifecycle.Until(() => lifecycle.Session.View.Status == SourceActionStatus.Ready);
        long stableGeneration = lifecycle.Session.View.Generation;
        lifecycle.Session.Observe(first);
        check(lifecycle.Session.View.Actions.Count == 0 && lifecycle.Session.View.Generation == stableGeneration
            && lifecycle.Provider.Requests.Length == 1 && lifecycle.Session.TryCreateCommand(first, action.Id) == null,
            "Negotiated support can become ready with an empty authoritative catalog; unchanged observations do not restart it");

        var switching = new Driver();
        switching.Session.Observe(first);
        var originalRead = await switching.Request(1);
        var skipped = first with { SourceCardKey = "intermediate-key", SourceCardId = "intermediate-face" };
        var current = first with { SourceCardKey = "current-key", SourceCardId = "current-face" };
        switching.Session.Observe(skipped);
        switching.Session.Observe(current);
        check(switching.Provider.Requests.Length == 1 && switching.Session.View.Status == SourceActionStatus.Loading,
            "Switching sources coalesces reads without displaying the previous source's actions");
        originalRead.Result.SetResult(Catalog(first, action));
        var currentRead = await switching.Request(2);
        check(currentRead.Key == current.SourceCardKey && switching.Session.View.Actions.Count == 0
            && switching.Session.TryCreateCommand(first, action.Id) == null,
            "A delayed old catalog cannot publish; only the latest disclosed source is queried next");
        var returnedActions = new[] { action };
        currentRead.Result.SetResult(Catalog(current, returnedActions));
        await switching.Until(() => switching.Session.View.Status == SourceActionStatus.Ready);
        returnedActions[0] = action with { Id = "mutated-provider-array" };
        var reply = switching.Session.TryCreateCommand(current, action.Id);
        check(reply == new UseOfferedAction(current.GameId, current.DecisionId, Card: current.SourceCardKey, ActionId: action.Id)
            && switching.Session.View.Actions[0] == action && switching.Session.TryCreateCommand(current, "unknown-action") == null,
            "Only a copied current catalog's exact opaque action creates the scoped source command");
        var changedContexts = new[] { current with { GameId = "another-game" }, current with { DecisionId = "payment" },
            current with { Revision = current.Revision + 1 }, current with { ViewerId = 1 },
            current with { SourceCardKey = "new-key" }, current with { SourceCardId = "new-occurrence" } };
        check(changedContexts.All(changed => switching.Session.TryCreateCommand(changed, action.Id) == null),
            "Game, decision, publication, viewer, source handle and occurrence changes all revoke an old action");
        switching.Provider.Supported = false;
        check(switching.Session.TryCreateCommand(current, action.Id) == null,
            "Capability revocation prevents dispatch even before the next reader observation");

        var revoked = new Driver();
        revoked.Session.Observe(first);
        var revokedRead = await revoked.Request(1);
        revoked.Session.Clear();
        revokedRead.Result.SetResult(Catalog(first, action));
        await revoked.Until(() => revoked.Applied > 0);
        check(revoked.Session.View is { Context: null, Status: SourceActionStatus.Idle, Actions.Count: 0 }
            && revoked.Session.TryCreateCommand(first, action.Id) == null && revoked.Provider.Requests.Length == 1,
            "Closing or losing visibility of a source discards its late private catalog without issuing a command");

        var viewerChange = new Driver();
        viewerChange.Session.Observe(first);
        var oldViewerRead = await viewerChange.Request(1);
        var otherViewer = first with { ViewerId = 2 };
        viewerChange.Session.Observe(otherViewer);
        oldViewerRead.Result.SetResult(Catalog(first, action));
        var newViewerRead = await viewerChange.Request(2);
        check(viewerChange.Session.View.Actions.Count == 0 && viewerChange.Session.TryCreateCommand(otherViewer, action.Id) == null,
            "A seat change invalidates a private read even when the provider's game, decision and source strings are identical");
        newViewerRead.Result.SetResult(Catalog(otherViewer));
        await viewerChange.Until(() => viewerChange.Session.View.Status == SourceActionStatus.Ready);

        var invalid = new Driver();
        invalid.Session.Observe(first);
        var invalidRead = await invalid.Request(1);
        invalidRead.Result.SetResult(Catalog(first with { SourceCardId = "wrong-source" }, action));
        await invalid.Until(() => invalid.Session.View.Status == SourceActionStatus.Stale);
        invalid.Session.Observe(first);
        check(invalid.Session.View.Actions.Count == 0 && invalid.Provider.Requests.Length == 1
            && invalid.Session.TryCreateCommand(first, action.Id) == null,
            "A mismatched provider catalog is explicitly stale and cannot trigger an automatic retry loop");
        invalid.Session.Retry();
        var retry = await invalid.Request(2);
        retry.Result.SetResult(Catalog(first, action));
        await invalid.Until(() => invalid.Session.View.Status == SourceActionStatus.Ready);
        check(invalid.Session.TryCreateCommand(first, action.Id) != null,
            "Explicit Retry can replace a stale catalog with a fresh scoped action");

        var unsupported = new Driver();
        unsupported.Session.Observe(first);
        (await unsupported.Request(1)).Result.SetException(new NotSupportedException("Remote host has no action catalog."));
        await unsupported.Until(() => unsupported.Session.View.Status == SourceActionStatus.Unsupported);
        unsupported.Session.Observe(first);
        check(unsupported.Provider.Requests.Length == 1 && unsupported.Session.View.Actions.Count == 0,
            "A host that explicitly rejects the optional capability stays unsupported rather than appearing to have zero legal actions");

        var revoking = new Driver();
        revoking.Session.Observe(first);
        var beforeRevocation = await revoking.Request(1);
        revoking.Session.Observe(current);
        revoking.Provider.Supported = false;
        beforeRevocation.Result.SetException(new NotSupportedException("Host revoked its advertised capability."));
        await revoking.Until(() => revoking.Session.View.Status == SourceActionStatus.Unsupported);
        check(revoking.Session.View.Context == current && revoking.Provider.Requests.Length == 1
            && revoking.Session.TryCreateCommand(current, action.Id) == null,
            "Capability loss during an obsolete read blocks the queued current-source query without requiring another UI observation");
        revoking.Provider.Supported = true;
        revoking.Session.Observe(current);
        var afterRevocation = await revoking.Request(2);
        afterRevocation.Result.SetResult(Catalog(current));
        await revoking.Until(() => revoking.Session.View.Status == SourceActionStatus.Ready);
        check(afterRevocation.Key == current.SourceCardKey,
            "Renegotiating a revoked capability restarts only the latest still-disclosed source");

        var malformed = new Driver();
        malformed.Session.Observe(first);
        (await malformed.Request(1)).Result.SetResult(Catalog(first, action, action));
        await malformed.Until(() => malformed.Session.View.Status == SourceActionStatus.Failed);
        check(malformed.Session.View.Actions.Count == 0 && malformed.Session.TryCreateCommand(first, action.Id) == null,
            "Ambiguous duplicate action handles fail the read without widening command authority");

        var spell = new Card { VisualId = "linked-exile-occurrence", Name = "A disclosed linked spell", ManaCost = "{U}" };
        var linkedAction = action with { Card = spell, Zone = "Exile" };
        var linked = new Driver();
        linked.Session.Observe(first);
        var linkedRead = await linked.Request(1);
        linkedRead.Result.SetResult(Catalog(first, linkedAction));
        await linked.Until(() => linked.Session.View.Status == SourceActionStatus.Ready);
        check(linked.Session.View.Actions.Single().Card == spell
                && linked.Session.View.Actions.Single().Zone == "Exile"
                && linked.Session.TryCreateCommand(first, action.Id) is { Card: "source-key" },
            "A disclosed linked spell is immutable reading material; its cast retains the offering source handle");
        var changedLink = first with { Revision = first.Revision + 1 };
        linked.Session.Observe(changedLink);
        check(linked.Session.View.Actions.Count == 0 && linked.Session.TryCreateCommand(first, action.Id) == null,
            "A new publication immediately removes a linked spell preview and its old source cast before refresh finishes");
        var refreshedLink = await linked.Request(2);
        linked.Session.Clear();
        refreshedLink.Result.SetResult(Catalog(changedLink, linkedAction));
        await linked.Until(() => linked.Applied == 2);
        check(linked.Session.View is { Context: null, Actions.Count: 0 },
            "A linked spell query finishing after its source reader closes cannot resurrect the face or cast");

        var linkedViewer = new Driver();
        linkedViewer.Session.Observe(first);
        var privateLink = await linkedViewer.Request(1);
        linkedViewer.Session.Observe(otherViewer);
        privateLink.Result.SetResult(Catalog(first, linkedAction));
        var nextSeatLink = await linkedViewer.Request(2);
        check(linkedViewer.Session.View.Actions.Count == 0,
            "An in-flight linked face for a previous viewer is never published into the replacement viewer's reader");
        nextSeatLink.Result.SetResult(Catalog(otherViewer));
        await linkedViewer.Until(() => linkedViewer.Session.View.Status == SourceActionStatus.Ready);

        foreach (var unsafePreview in new[] { spell with { Key = "another-action-handle" }, spell with { Selectable = true },
                     spell with { Highlighted = true }, spell with { FaceDown = true } }) {
            var invalidLink = new Driver();
            invalidLink.Session.Observe(first);
            (await invalidLink.Request(1)).Result.SetResult(Catalog(first, linkedAction with { Card = unsafePreview }));
            await invalidLink.Until(() => invalidLink.Session.View.Status == SourceActionStatus.Failed);
            check(invalidLink.Session.View.Actions.Count == 0 && invalidLink.Session.TryCreateCommand(first, action.Id) == null,
                "A linked preview cannot carry another action handle, selection authority or a concealed face");
        }

        var timed = new Driver(TimeSpan.FromMilliseconds(250));
        timed.Session.Observe(first);
        var timedOutRead = await timed.Request(1);
        await timed.Until(() => timed.Session.View.Status == SourceActionStatus.Failed);
        timed.Session.Observe(first);
        check(timed.Provider.Requests.Length == 1 && timed.Session.View.Actions.Count == 0,
            "A bounded action-query timeout does not cause a background retry storm");
        timed.Session.Retry();
        var timedRetry = await timed.Request(2);
        timedRetry.Result.SetResult(Catalog(first, action));
        await timed.Until(() => timed.Session.View.Status == SourceActionStatus.Ready);
        timedOutRead.Result.SetResult(Catalog(first, action with { Id = "late-timed-out-action" }));
        check(timed.Session.TryCreateCommand(first, action.Id) != null
            && timed.Session.TryCreateCommand(first, "late-timed-out-action") == null,
            "An underlying provider finishing after timeout cannot replace the successful explicit retry");

        var closing = new Driver();
        closing.Session.Observe(first);
        var closingRead = await closing.Request(1);
        closing.Session.Close();
        long closedGeneration = closing.Session.View.Generation;
        closing.Session.Observe(current);
        closing.Session.Retry();
        closingRead.Result.SetResult(Catalog(first, action));
        await closing.Until(() => closing.Applied > 0);
        check(closing.Session.View is { Context: null, Status: SourceActionStatus.Idle, Actions.Count: 0 }
            && closing.Session.View.Generation == closedGeneration && closing.Provider.Requests.Length == 1
            && closing.Session.TryCreateCommand(first, action.Id) == null,
            "A closed action session rejects future observations and late completions without resurrecting authority");
    }

    private static SourceActionSet Catalog(SourceActionContext context, params SourceAction[] actions)
        => new(context.GameId, context.DecisionId, context.SourceCardKey, context.SourceCardId, actions.ToImmutableArray());

    private sealed class Driver
    {
        private readonly ConcurrentQueue<Action> completions = new();
        internal readonly ControlledProvider Provider = new();
        internal readonly SourceActionSession Session;
        internal int Applied;
        internal Driver(TimeSpan? timeout = null) => Session = new(Provider, completion => {
            completions.Enqueue(completion);
        }, timeout);
        internal async Task<Request> Request(int count)
        {
            await Until(() => Provider.Requests.Length >= count);
            return Provider.Requests[count - 1];
        }
        internal async Task Until(Func<bool> completed)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do {
                while (completions.TryDequeue(out var completion)) { completion(); Applied++; }
                if (completed()) return;
                await Task.Delay(1);
            } while (DateTime.UtcNow < deadline);
            throw new TimeoutException($"Source-action test did not reach its expected observation; status={Session.View.Status}.");
        }
    }

    private sealed record Request(string Game, string Decision, string Key)
    {
        internal TaskCompletionSource<SourceActionSet> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledProvider : ISourceActions
    {
        private readonly ConcurrentQueue<Request> requests = new();
        internal bool Supported = true;
        public bool SupportsSourceActions => Supported;
        internal Request[] Requests => requests.ToArray();
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            var request = new Request(gameId, decisionId, sourceCardKey);
            requests.Enqueue(request);
            return request.Result.Task;
        }
    }
}
