using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

public sealed record DeckWorkspaceContext(string DeckId, long Revision);
public sealed record DeckWorkspaceStamp(DeckWorkspaceContext Context, long Generation);
public enum DeckWorkspaceReadStatus { Idle, Pending, Loading, Ready, Failed }
public sealed record DeckWorkspaceView(DeckWorkspaceContext? Context, long Generation,
    WorkshopState? Workshop, ImmutableArray<DeckTest> Tests, HandPractice? Hand, DeckAnalysisState? Analysis,
    DeckWorkspaceReadStatus WorkshopStatus, DeckWorkspaceReadStatus AnalysisStatus,
    string WorkshopError, string AnalysisError);

/// <summary>
/// Owns the selected document's workshop and analysis read lifecycle. Calls and
/// posted completions run on the session thread. A service result is admitted
/// only for its captured document/revision/generation; analysis additionally
/// requires the accepted workshop metadata and playable fingerprint.
/// </summary>
public sealed class DeckWorkspaceSession(IDeckWorkshop? workshop, IDeckAnalysis? analysis, Action<Action> post)
{
    private bool running, closed, canRead, wantsAnalysis;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<TaskCompletionSource<bool>> reviews = [];
    public DeckWorkspaceView View { get; private set; } = Empty(null, 0);
    public bool WorkshopCurrent => View.Context is { } context && View.WorkshopStatus == DeckWorkspaceReadStatus.Ready
        && View.Workshop is { } state && Matches(context, state);
    public bool AnalysisCurrent => View.Context is { } context && View.AnalysisStatus == DeckWorkspaceReadStatus.Ready
        && View.Analysis is { } state && Matches(context, state)
        && (workshop == null || WorkshopCurrent && Compatible(View.Workshop!, state));

    public void Observe(DeckWorkspaceContext? context)
    {
        if (closed || View.Context == context) return;
        if (context != null && (string.IsNullOrWhiteSpace(context.DeckId) || context.Revision < 0))
            throw new ArgumentException("A workspace requires a saved document and revision.", nameof(context));
        SettleReviews(false);
        wantsAnalysis = false;
        View = Empty(context, View.Generation + 1);
    }

    public void Clear() => Observe(null);
    public DeckWorkspaceStamp? Capture() => !closed && View.Context is { } context ? new(context, View.Generation) : null;
    public bool IsCurrent(DeckWorkspaceStamp? stamp) => !closed && stamp != null && stamp.Context == View.Context && stamp.Generation == View.Generation;

    public void RefreshWorkshop()
    {
        if (closed || View.Context == null) return;
        SettleReviews(false);
        View = View with { Generation = View.Generation + 1,
            WorkshopStatus = workshop == null ? DeckWorkspaceReadStatus.Idle : DeckWorkspaceReadStatus.Pending,
            AnalysisStatus = analysis == null ? DeckWorkspaceReadStatus.Idle : DeckWorkspaceReadStatus.Pending,
            WorkshopError = "", AnalysisError = "" };
    }

    // Pump is called even while the application is busy, so a foreground review
    // can complete. Eligibility controls starting provider work, not admitting it.
    public void Pump(bool eligible, bool showAnalysis)
    {
        canRead = eligible;
        wantsAnalysis = showAnalysis || reviews.Count > 0;
        StartIfNeeded();
    }

    public Task<bool> ReviewAsync()
    {
        if (closed || analysis == null || View.Context == null) return Task.FromResult(false);
        if (AnalysisCurrent) return Task.FromResult(true);
        if (workshop != null && (View.WorkshopStatus == DeckWorkspaceReadStatus.Failed || View.AnalysisStatus == DeckWorkspaceReadStatus.Failed)) {
            View = View with { Generation = View.Generation + 1, WorkshopStatus = DeckWorkspaceReadStatus.Pending, WorkshopError = "" };
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        reviews.Add(completion); wantsAnalysis = true;
        View = View with { AnalysisStatus = DeckWorkspaceReadStatus.Pending, AnalysisError = "" };
        // Explicit review is already authorized by the caller's foreground work.
        StartIfNeeded(explicitReview: true);
        return completion.Task;
    }

    public bool AcceptWorkshop(DeckWorkspaceStamp? stamp, WorkshopState state)
    {
        if (!IsCurrent(stamp) || !Matches(stamp!.Context, state)
            || View.Workshop is { } current && state.MetadataRevision < current.MetadataRevision) return false;
        SettleReviews(false);
        View = View with { Generation = View.Generation + 1, Workshop = state,
            Hand = View.Hand?.VersionId == state.VersionId ? View.Hand : null,
            WorkshopStatus = DeckWorkspaceReadStatus.Ready, WorkshopError = "",
            AnalysisStatus = analysis == null ? DeckWorkspaceReadStatus.Idle : DeckWorkspaceReadStatus.Pending, AnalysisError = "" };
        return true;
    }

    public bool AcceptAnalysis(DeckWorkspaceStamp? stamp, DeckAnalysisState state, WorkshopState? shared = null)
    {
        if (!IsCurrent(stamp) || !Matches(stamp!.Context, state)
            || View.Analysis is { } previous && state.MetadataRevision < previous.MetadataRevision
            || shared != null && (!Matches(stamp.Context, shared)
                || View.Workshop is { } current && shared.MetadataRevision < current.MetadataRevision)
            || workshop != null && (shared ?? View.Workshop) == null
            || workshop != null && !Compatible(shared ?? View.Workshop!, state)) return false;
        View = View with { Generation = View.Generation + 1, Analysis = state, AnalysisStatus = DeckWorkspaceReadStatus.Ready,
            AnalysisError = "", Workshop = shared ?? View.Workshop,
            WorkshopStatus = shared == null ? View.WorkshopStatus : DeckWorkspaceReadStatus.Ready,
            WorkshopError = shared == null ? View.WorkshopError : "",
            Hand = shared == null || View.Hand?.VersionId == shared.VersionId ? View.Hand : null };
        SettleReviews(true);
        return true;
    }

    public bool AcceptPractice(DeckWorkspaceStamp? stamp, HandPractice hand, IEnumerable<DeckTest> tests)
    {
        var records = tests.ToImmutableArray();
        if (!IsCurrent(stamp) || !WorkshopCurrent || hand.VersionId != View.Workshop!.VersionId
            || records.Any(test => test.DeckId != stamp!.Context.DeckId)
            || !records.Any(test => test.Id == hand.TestId && test.VersionId == hand.VersionId)) return false;
        SettleReviews(false);
        View = View with { Generation = View.Generation + 1, Hand = hand, Tests = records,
            AnalysisStatus = View.AnalysisStatus == DeckWorkspaceReadStatus.Loading ? DeckWorkspaceReadStatus.Pending : View.AnalysisStatus };
        return true;
    }

    public bool AcceptTestNote(DeckWorkspaceStamp? stamp, DeckTest test)
    {
        if (!IsCurrent(stamp) || test.DeckId != stamp!.Context.DeckId || !View.Tests.Any(item => item.Id == test.Id)) return false;
        SettleReviews(false);
        View = View with { Generation = View.Generation + 1, Tests = View.Tests.Select(item => item.Id == test.Id ? test : item).ToImmutableArray(),
            WorkshopStatus = View.WorkshopStatus == DeckWorkspaceReadStatus.Loading ? DeckWorkspaceReadStatus.Pending : View.WorkshopStatus,
            AnalysisStatus = View.AnalysisStatus == DeckWorkspaceReadStatus.Loading ? DeckWorkspaceReadStatus.Pending : View.AnalysisStatus };
        return true;
    }

    public void Close()
    {
        if (closed) return;
        closed = true; SettleReviews(false); View = Empty(null, View.Generation + 1); lifetime.Cancel();
    }

    private static DeckWorkspaceView Empty(DeckWorkspaceContext? context, long generation) =>
        new(context, generation, null, [], null, null, context == null ? DeckWorkspaceReadStatus.Idle : DeckWorkspaceReadStatus.Pending,
            DeckWorkspaceReadStatus.Idle, "", "");
    private static bool Matches(DeckWorkspaceContext context, WorkshopState state) => state.DeckId == context.DeckId && state.Revision == context.Revision;
    private static bool Matches(DeckWorkspaceContext context, DeckAnalysisState state) => state.Report.DeckId == context.DeckId && state.Report.Revision == context.Revision;
    private static bool Compatible(WorkshopState state, DeckAnalysisState report) => state.MetadataRevision == report.MetadataRevision && state.VersionId == report.Report.Fingerprint;

    private void StartIfNeeded(bool explicitReview = false)
    {
        if (closed || running || (!canRead && !explicitReview) || Capture() is not { } stamp) return;
        bool readWorkshop = workshop != null && View.WorkshopStatus == DeckWorkspaceReadStatus.Pending;
        bool readAnalysis = analysis != null && wantsAnalysis && !AnalysisCurrent && View.AnalysisStatus != DeckWorkspaceReadStatus.Failed
            && (workshop == null || readWorkshop || WorkshopCurrent);
        if (!readWorkshop && !readAnalysis) return;
        running = true;
        var knownWorkshop = View.Workshop;
        var knownAnalysis = View.Analysis;
        var cancellation = lifetime.Token;
        View = View with { WorkshopStatus = readWorkshop ? DeckWorkspaceReadStatus.Loading : View.WorkshopStatus,
            AnalysisStatus = readAnalysis ? DeckWorkspaceReadStatus.Loading : View.AnalysisStatus };
        _ = Task.Run(async () => {
            WorkshopState? shared = null; DeckTest[]? tests = null; DeckAnalysisState? report = null; Exception? failure = null;
            try {
                if (readWorkshop) {
                    shared = await workshop!.WorkshopAsync(stamp.Context.DeckId, stamp.Context.Revision).WaitAsync(cancellation);
                    tests = await workshop.GetTestsAsync(stamp.Context.DeckId).WaitAsync(cancellation);
                    if (!Matches(stamp.Context, shared) || tests.Any(test => test.DeckId != stamp.Context.DeckId)
                        || knownWorkshop != null && shared.MetadataRevision < knownWorkshop.MetadataRevision)
                        throw new InvalidOperationException("The deck tools returned a different saved document.");
                }
                if (readAnalysis) {
                    report = await analysis!.AnalyzeDeckAsync(stamp.Context.DeckId, stamp.Context.Revision).WaitAsync(cancellation);
                    if (!Matches(stamp.Context, report) || workshop != null && !Compatible(shared ?? knownWorkshop!, report)
                        || knownAnalysis != null && report.MetadataRevision < knownAnalysis.MetadataRevision)
                        throw new InvalidOperationException("The deck review no longer matches this saved version and its preferences.");
                }
            } catch (Exception error) { failure = error; }
            post(() => {
                running = false;
                if (IsCurrent(stamp)) {
                    if (failure != null) {
                        View = View with {
                            WorkshopStatus = readWorkshop ? DeckWorkspaceReadStatus.Failed : View.WorkshopStatus,
                            WorkshopError = readWorkshop ? failure.Message : View.WorkshopError,
                            AnalysisStatus = readAnalysis ? DeckWorkspaceReadStatus.Failed : View.AnalysisStatus,
                            AnalysisError = readAnalysis ? failure.Message : View.AnalysisError };
                        SettleReviews(false, failure);
                    } else {
                        View = View with { Workshop = shared ?? View.Workshop, Tests = tests?.ToImmutableArray() ?? View.Tests,
                            Hand = shared == null || View.Hand?.VersionId == shared.VersionId ? View.Hand : null,
                            Analysis = report ?? View.Analysis,
                            WorkshopStatus = readWorkshop ? DeckWorkspaceReadStatus.Ready : View.WorkshopStatus,
                            AnalysisStatus = readAnalysis ? DeckWorkspaceReadStatus.Ready : View.AnalysisStatus };
                        if (readAnalysis) SettleReviews(true);
                    }
                }
                StartIfNeeded(explicitReview: reviews.Count > 0);
            });
        });
    }

    private void SettleReviews(bool accepted, Exception? failure = null)
    {
        foreach (var completion in reviews) {
            if (failure != null) completion.TrySetException(failure); else completion.TrySetResult(accepted);
        }
        reviews.Clear();
    }
}
