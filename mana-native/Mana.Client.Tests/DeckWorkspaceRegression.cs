using System.Collections.Concurrent;
using Mana.Client;
using Mana.Contracts;

internal static class DeckWorkspaceRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var provider = new Provider();
        var posts = new Posts();
        var session = new DeckWorkspaceSession(provider, provider, posts.Enqueue);
        var first = new DeckWorkspaceContext("first", 3);
        var last = new DeckWorkspaceContext("last", 8);
        session.Observe(first); session.Pump(true, false);
        var initial = await provider.NextWorkshop();
        var retired = session.Capture();
        session.Observe(new("intermediate", 4)); session.Observe(last); session.Pump(true, false);
        check(provider.WorkshopCalls == 1, "Deck selection changes share one in-flight workspace read");
        initial.Result.SetException(new IOException("retired document failed"));
        await posts.Apply();
        var latest = await provider.NextWorkshop();
        check(latest.Context == last && session.View.WorkshopError == "", "Intermediate selections coalesce and an old read failure cannot label the new deck");
        latest.Result.SetResult(Workshop(last, 2)); await posts.Apply();
        check(session.WorkshopCurrent && session.View.Workshop?.DeckId == last.DeckId
            && session.View.Tests.Single().DeckId == last.DeckId, "Workshop data and test history are admitted together for the selected saved revision");
        check(!session.AcceptWorkshop(retired, Workshop(first, 99)), "A retired document's metadata completion cannot change the current workspace");

        var review = session.ReviewAsync();
        var oldAnalysis = await provider.NextAnalysis();
        check(session.AcceptWorkshop(session.Capture(), Workshop(last, 3)), "An accepted metadata edit advances workspace authority");
        check(!await review, "A foreground review loses authority when its captured preferences change");
        oldAnalysis.Result.SetResult(Analysis(last, 2)); await posts.Apply();
        session.Pump(true, true);
        var newAnalysis = await provider.NextAnalysis();
        check(session.View.Analysis == null && !session.AnalysisCurrent, "A same-document analysis cannot become current after a newer metadata edit");
        newAnalysis.Result.SetResult(Analysis(last, 3)); await posts.Apply();
        check(session.AnalysisCurrent && session.View.Analysis!.MetadataRevision == 3,
            "The replacement analysis must match both current preferences and the playable fingerprint");

        var badAnalysis = Analysis(last, 3) with { Report = Analysis(last, 3).Report with { Fingerprint = "another-list" } };
        check(!session.AcceptAnalysis(session.Capture(), badAnalysis) && session.AnalysisCurrent,
            "A matching document revision cannot authorize a different playable fingerprint");
        var sample = new HandPractice("test-last", "version-last", [], 90, 0, 0, "sample");
        check(!session.AcceptPractice(session.Capture(), sample with { VersionId = "older-list" }, [Test(last)]),
            "Draw-only samples cannot be attached to a different playable version");
        check(session.AcceptPractice(session.Capture(), sample, [Test(last)]) && session.View.Hand == sample,
            "A matching practice result updates the sample and its versioned test record together");
        var beforeDelete = session.Capture();
        session.Clear(); session.Observe(last);
        check(!session.AcceptWorkshop(beforeDelete, Workshop(last, 500)) && session.View.Hand == null,
            "Clearing and restoring the same document revision does not resurrect old reads, writes or hand samples");

        session.Pump(true, false);
        var failed = await provider.NextWorkshop();
        failed.Result.SetException(new IOException("workshop offline")); await posts.Apply();
        check(session.View.WorkshopStatus == DeckWorkspaceReadStatus.Failed && session.View.WorkshopError == "workshop offline",
            "A current workshop failure is explicit and does not silently retry every frame");
        session.Pump(true, false);
        check(provider.WorkshopCalls == 3, "Failed workshop reads wait for an explicit retry");
        session.RefreshWorkshop(); session.Pump(true, false);
        var retried = await provider.NextWorkshop();
        retried.Result.SetResult(Workshop(last, 4)); await posts.Apply();
        check(session.WorkshopCurrent && session.View.WorkshopError == "", "Retry recovers the same selected deck without retaining a previous failure");

        // Explicit review begins once and its acceptance is pumped even while a
        // foreground loading scene makes automatic background reads ineligible.
        var foreground = session.ReviewAsync();
        var foregroundRead = await provider.NextAnalysis();
        session.Pump(false, false);
        foregroundRead.Result.SetResult(Analysis(last, 4)); await posts.Apply();
        check(await foreground && session.AnalysisCurrent, "Foreground review completes while the application is busy displaying its loading scene");

        session.RefreshWorkshop(); session.Pump(true, false);
        var beforeExternalEdit = await provider.NextWorkshop();
        beforeExternalEdit.Result.SetResult(Workshop(last, 4)); await posts.Apply();
        var externalEdit = session.ReviewAsync();
        var stalePreferences = await provider.NextAnalysis();
        stalePreferences.Result.SetResult(Analysis(last, 5)); await posts.Apply();
        bool mismatchFailed = false;
        try { await externalEdit; } catch (InvalidOperationException) { mismatchFailed = true; }
        check(mismatchFailed && !session.AnalysisCurrent, "A provider preference change during analysis rejects the mismatched review explicitly");
        var refreshPreferences = session.ReviewAsync();
        var refreshedPreferences = await provider.NextWorkshop();
        refreshedPreferences.Result.SetResult(Workshop(last, 5));
        var refreshedAnalysis = await provider.NextAnalysis();
        refreshedAnalysis.Result.SetResult(Analysis(last, 5)); await posts.Apply();
        check(await refreshPreferences && session.AnalysisCurrent,
            "Retrying a metadata mismatch refreshes the workshop prerequisite before accepting another review");

        session.RefreshWorkshop(); session.Pump(true, false);
        var wrongDocument = await provider.NextWorkshop();
        wrongDocument.Result.SetResult(Workshop(first, 4)); await posts.Apply();
        check(session.View.WorkshopStatus == DeckWorkspaceReadStatus.Failed && !session.WorkshopCurrent,
            "Provider results with a different saved document are rejected rather than painted under the current deck");

        var retryPrerequisite = session.ReviewAsync();
        var prerequisite = await provider.NextWorkshop();
        prerequisite.Result.SetResult(Workshop(last, 5));
        var afterPrerequisite = await provider.NextAnalysis();
        afterPrerequisite.Result.SetResult(Analysis(last, 5)); await posts.Apply();
        check(await retryPrerequisite && session.WorkshopCurrent && session.AnalysisCurrent,
            "An explicit review retries a failed workshop prerequisite and settles its foreground waiter");

        session.RefreshWorkshop(); session.Pump(true, false);
        var closing = await provider.NextWorkshop();
        var closingReview = session.ReviewAsync();
        session.Close(); closing.Result.SetResult(Workshop(last, 4)); await posts.Apply();
        check(!await closingReview && session.View.Context == null && session.View.Workshop == null,
            "Closing revokes queued review waiters and late provider completions");

        var analysisOnlyProvider = new Provider();
        var analysisOnlyPosts = new Posts();
        var analysisOnly = new DeckWorkspaceSession(null, analysisOnlyProvider, analysisOnlyPosts.Enqueue);
        analysisOnly.Observe(first);
        var independentReview = analysisOnly.ReviewAsync();
        var independentRead = await analysisOnlyProvider.NextAnalysis();
        independentRead.Result.SetResult(Analysis(first, 1)); await analysisOnlyPosts.Apply();
        check(await independentReview && analysisOnly.AnalysisCurrent,
            "The optional analysis port remains usable without a workshop capability");
        check(!analysisOnly.AcceptAnalysis(analysisOnly.Capture(), Analysis(first, 0))
            && analysisOnly.View.Analysis!.MetadataRevision == 1,
            "An analysis-only provider cannot roll accepted preferences back through a newer request stamp");
        analysisOnly.Close();
    }

    private static WorkshopState Workshop(DeckWorkspaceContext context, long metadata) =>
        new(context.DeckId, context.Revision, metadata, "version-" + context.DeckId, "", "", [], [], [], null, new([], [], [], ""));
    private static DeckTest Test(DeckWorkspaceContext context) =>
        new("test-" + context.DeckId, context.DeckId, "version-" + context.DeckId, "sample", "", 0, null, "sample", null, [], [], "");
    private static DeckAnalysisState Analysis(DeckWorkspaceContext context, long metadata) => new(metadata,
        new(context.DeckId, context.Revision, "version-" + context.DeckId,
            new("", "unspecified", "", "", "balanced", 0, [], []), new("Commander", 100, true, 1, 0, true, false),
            new(100, 35, 30, 5, 6, 6, 8, 8, [], 10, 3, null, ""), [], [], [], [], []), []);

    private sealed class Posts
    {
        private readonly ConcurrentQueue<Action> queue = new();
        private readonly SemaphoreSlim available = new(0);
        internal void Enqueue(Action callback) { queue.Enqueue(callback); available.Release(); }
        internal async Task Apply()
        {
            if (!await available.WaitAsync(TimeSpan.FromSeconds(3))) throw new Exception("A workspace completion was not posted.");
            if (!queue.TryDequeue(out var callback)) throw new Exception("Workspace completion queue was empty.");
            callback();
        }
    }

    private sealed record WorkshopRead(DeckWorkspaceContext Context, TaskCompletionSource<WorkshopState> Result);
    private sealed record AnalysisRead(DeckWorkspaceContext Context, TaskCompletionSource<DeckAnalysisState> Result);
    private sealed class Provider : IDeckWorkshop, IDeckAnalysis
    {
        private readonly ConcurrentQueue<WorkshopRead> workshops = new();
        private readonly ConcurrentQueue<AnalysisRead> analyses = new();
        private readonly SemaphoreSlim workshopAvailable = new(0), analysisAvailable = new(0);
        private int workshopCalls;
        internal int WorkshopCalls => Volatile.Read(ref workshopCalls);
        public Task<WorkshopState> WorkshopAsync(string deckId, long revision)
        {
            var result = new TaskCompletionSource<WorkshopState>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref workshopCalls);
            workshops.Enqueue(new(new(deckId, revision), result)); workshopAvailable.Release(); return result.Task;
        }
        public Task<DeckAnalysisState> AnalyzeDeckAsync(string deckId, long revision)
        {
            var result = new TaskCompletionSource<DeckAnalysisState>(TaskCreationOptions.RunContinuationsAsynchronously);
            analyses.Enqueue(new(new(deckId, revision), result)); analysisAvailable.Release(); return result.Task;
        }
        internal async Task<WorkshopRead> NextWorkshop()
        {
            if (!await workshopAvailable.WaitAsync(TimeSpan.FromSeconds(3)) || !workshops.TryDequeue(out var result))
                throw new Exception("Expected workspace read did not start.");
            return result;
        }
        internal async Task<AnalysisRead> NextAnalysis()
        {
            if (!await analysisAvailable.WaitAsync(TimeSpan.FromSeconds(3)) || !analyses.TryDequeue(out var result))
                throw new Exception("Expected analysis read did not start.");
            return result;
        }
        public Task<DeckTest[]> GetTestsAsync(string deckId) => Task.FromResult(new[] { Test(new(deckId, 0)) });
        public Task<WorkshopState> UpdateWorkshopAsync(string deckId, long revision, WorkshopMetadataEdit edit) => throw new NotSupportedException();
        public Task<DeckDetails> RestoreWorkshopVersionAsync(string deckId, long revision, string versionId) => throw new NotSupportedException();
        public Task<DeckDetails> MoveWorkshopCardAsync(string deckId, long revision, long metadataRevision, string cardId, string to, string? cutCardId = null) => throw new NotSupportedException();
        public Task<DeckSourcePreview> PreviewDeckSourceAsync(string url) => throw new NotSupportedException();
        public Task<DeckSourcePreview> RefreshDeckSourceAsync(string deckId, long revision) => throw new NotSupportedException();
        public Task<DeckDetails> ApplyDeckSourceAsync(string token, string? deckId, long? revision, string choice) => throw new NotSupportedException();
        public Task<HandPractice> PracticeAsync(string deckId, long revision, string action, int? index = null, string? question = null) => throw new NotSupportedException();
        public Task<DeckTest> StartDeckTestAsync(string deckId, long revision, IReadOnlyList<string> opponents, string question) => throw new NotSupportedException();
        public Task<DeckTest> SaveTestNoteAsync(string deckId, string testId, string note) => throw new NotSupportedException();
        public Task<DeckAnalysisState> SaveAnalysisProfileAsync(string deckId, long revision, long metadataRevision, string fingerprint, AnalysisProfile profile) => throw new NotSupportedException();
        public Task<AnalysisConsiderResult> ConsiderAnalysisAsync(string deckId, long revision, long metadataRevision, string fingerprint, string recommendationId, string note) => throw new NotSupportedException();
        public Task<DeckAnalysisState> ReviewAnalysisAsync(string deckId, long revision, long metadataRevision, string fingerprint, string recommendationId, string decision, string note) => throw new NotSupportedException();
    }
}
