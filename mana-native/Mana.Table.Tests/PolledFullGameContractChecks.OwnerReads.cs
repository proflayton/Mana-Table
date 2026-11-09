using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

internal static partial class PolledFullGameContractChecks
{
    internal static async Task RunOwnerReadAdmissionControlsAsync(string profile, Action<bool, string> check)
    {
        var initial = Priority() with { PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
            PriorityObservation = new(1, 0, 0, 0, false, false), BoardRevision = 1 };
        using var run = new DriverCase(profile, "quiet-owner-read-admission", initial, initial,
            passing: ConnectedPlayerPassingMode.FullControl);
        run.Driver.Client.SetPriorityObservationReady(false);
        run.Port.PriorityContinuationAvailable = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Port.ExecuteGate = async _ => { entered.TrySetResult(); await release.Task; };
        run.Port.AfterCommand = command => command is AcknowledgePriorityObservation ack
            ? run.Port.RichState = run.Port.State with { PriorityObservation = new(ack.BoardRevision,
                ack.PublicActivitySequence, ack.BoardRevision, ack.PublicActivitySequence, true, false) } : null;
        try {
            await run.Prime();
            run.Driver.Client.SetPriorityObservationReady(true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(run.Driver.Client.PriorityObservationInFlight && !run.Driver.Client.Busy,
                "The controlled quiet operation owns its wire without using Busy as an observation authority flag");
            await run.Driver.ObserveAsync();
            check(run.Port.OwnedReads == 0 && run.Policy.Evaluations == 0,
                "A current rich discovery cannot begin while any non-Busy quiet owner still owns the connection (observed rich reads: " + run.Port.OwnedReads + ")");
            release.TrySetResult();
            await run.Driver.Client.WaitForQueuedWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(run.Driver.Client.PriorityObservationInFlight && run.Port.OwnedReads == 0,
                "Completed metadata wire retains its ownership until the posted result is applied and released");
            await run.Driver.ObserveAsync();
            check(!run.Driver.Client.PriorityObservationInFlight && run.Port.OwnedReads == 1 && run.Policy.Evaluations == 0,
                "After owner application the same ordinary frame receives one rich enrichment without a policy command");
        } finally { release.TrySetResult(); run.Driver.Client.SetPriorityObservationReady(false); }
        await RunOwnerReadMarkerControlAsync(profile, check);
        await CurrentReadTickets(profile, check);
        await RichMismatchConvergesThroughOrdinary(profile, check);
        await EqualReacceptRetiresRichRead(profile, check);
        await ManualReadBootstrap(profile, check);
        await RunOwnerReadLineageControlAsync(profile, check);
        await RunManualSourceLineageControlAsync(profile, check);
    }

    internal static async Task RunManualSourceLineageControlAsync(string profile, Action<bool, string> check)
    {
        var state = Priority(); var port = new Port(state, state) { UseSource = true, HoldSource = true };
        string directory = Path.Combine(profile, "manual-source-operation-lineage"); Directory.CreateDirectory(directory);
        using var evidence = new AiCampaignDiagnostics(directory);
        using var driver = new ConnectedPlayerDriver(port, port, port, port, port, evidence, 261109,
            MixedGameDriveMode.ManualStepped, new ScriptedPolicy(true), passing: ConnectedPlayerPassingMode.FullControl);
        var localDeclines = new List<ClientDiagnostic>(); driver.Client.DiagnosticRecorded += localDeclines.Add;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await driver.ObserveAsync(); var pending = driver.StepAsync();
        try {
            await port.SourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            driver.Client.Post(() => driver.Client.Run(async () => { entered.TrySetResult(); await release.Task; }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(driver.Client.Busy && port.Commands.Count == 0,
                "A manual offered action retains its source query while a separately admitted general operation owns the lane");
            port.ReleaseSource.TrySetResult(Offers(state));
            Exception? observed = null;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception error) { observed = error; }
            check(observed == null && driver.RetiredProposals == 1 && driver.ClientAdmissions == 0
                    && driver.ProviderAcceptances == 0 && port.Commands.Count == 0
                    && localDeclines.All(row => row.Stage != ClientDiagnosticStage.LocalDeclined),
                "Manual source completion retires an intervened operation lineage before attempting Submit (observed retirements: " + driver.RetiredProposals + ")");
        } finally {
            release.TrySetResult(); port.ReleaseSource.TrySetResult(Offers(state));
            await driver.Client.WaitForQueuedWorkAsync().WaitAsync(TimeSpan.FromSeconds(5)); driver.Client.Pump();
        }
        var fresh = state with { Revision = state.Revision + 1 };
        port.State = port.RichState = fresh;
        var settled = fresh with { Revision = fresh.Revision + 1, Decision = null };
        port.AfterExecute = settled; port.ReleaseOrdinary.TrySetResult(settled);
        check(await driver.ObserveAsync(), "A settled manual owner can acquire fresh current enrichment after local source-lineage retirement");
        await driver.StepAsync();
        check(driver.ClientAdmissions == 1 && driver.ProviderAcceptances == 1 && port.SourceReads == 2
                && port.Commands.ToArray() is [UseOfferedAction] && ReferenceEquals(driver.Client.State, settled),
            "Fresh manual authority dispatches one legitimate newly read source action without replaying the retired proposal");
    }

    internal static async Task RunOwnerReadLineageControlAsync(string profile, Action<bool, string> check)
    {
        var initial = Priority() with { PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
            PriorityObservation = new(1, 0, 0, 0, false, false), BoardRevision = 1 };
        using var run = new DriverCase(profile, "saved-enrichment-operation-lineage", initial, initial,
            passing: ConnectedPlayerPassingMode.FullControl);
        var client = run.Driver.Client; client.SetPriorityObservationReady(false); run.Port.PriorityContinuationAvailable = true;
        await run.Prime(); await run.Driver.ObserveAsync();
        client.Accept(initial);
        check(await run.Driver.ObserveAsync() && run.Port.OwnedReads == 1,
            "Identical ordinary republication can reuse a validated enrichment inside the same operation lineage");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Port.ExecuteGate = async _ => { entered.TrySetResult(); await release.Task; };
        run.Port.ExecuteError = new EngineCommandRejectedException("Controlled quiet rejection without a returned frame.");
        client.SetPriorityObservationReady(true);
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.SetPriorityObservationReady(false); release.TrySetResult();
            await client.WaitForQueuedWorkAsync().WaitAsync(TimeSpan.FromSeconds(5)); client.Pump();
            Exception? policyError = null;
            try { await run.Driver.StepAsync(); } catch (Exception error) { policyError = error; }
            check(policyError == null && run.Policy.Evaluations == 0 && run.Port.Commands.ToArray() is [AcknowledgePriorityObservation],
                "A failed intervening quiet operation cannot reuse cached enrichment for a new policy command (observed policy evaluations: " + run.Policy.Evaluations + ")");
            run.Port.ExecuteError = null;
            var retired = new EngineObservationSupersededException(); run.Port.OwnedError = retired;
            Exception? observed = null;
            try { await run.Driver.ObserveAsync(); } catch (Exception error) { observed = error; }
            check(ReferenceEquals(observed, retired) && run.Port.OwnedReads == 2,
                "Invalid saved enrichment requires a fresh current-admission read and preserves its actual typed retirement");
            run.Port.OwnedError = null; run.Ticks = 1000;
            check(!await run.Driver.ObserveAsync() && run.Port.OwnedReads == 2,
                "Publication accepted before an intervening operation cannot satisfy a newly retired read's ordinary-refresh fence");
            await client.WaitForQueuedWorkAsync().WaitAsync(TimeSpan.FromSeconds(5)); await Drain(client);
            check(await run.Driver.ObserveAsync() && run.Port.OwnedReads == 3 && run.Policy.Evaluations == 0,
                "One post-retirement ordinary publication permits one fresh owned enrichment without replaying the old proposal");
        } finally { release.TrySetResult(); client.SetPriorityObservationReady(false); }
    }

    internal static async Task RunOwnerReadRelatedAsync(string profile, Action<bool, string> check)
    {
        await RunOwnerReadAdmissionControlsAsync(profile, check);
        await IndependentObservationOwners(profile, check);
        await FailedOperationIsNotDiscovery(profile, check);
        await AcceptedInputProgress(profile, check);
        await AcceptedInputRetirement(profile, check);
        await SlowPollCannotStarveDecision(profile, check);
        await DuePollCannotStarveAuto(profile, check);
    }

    internal static async Task RunOwnerReadMarkerControlAsync(string profile, Action<bool, string> check)
    {
        var initial = Priority();
        using var run = new DriverCase(profile, "unaccepted-rich-read-marker", initial, initial,
            passing: ConnectedPlayerPassingMode.FullControl);
        await run.Prime();
        var retired = new EngineObservationSupersededException(); run.Port.OwnedError = retired;
        Exception? observed = null;
        try { await run.Driver.ObserveAsync(); } catch (Exception error) { observed = error; }
        check(ReferenceEquals(observed, retired) && run.Port.OwnedReads == 1 && run.Port.Commands.Count == 0,
            "A bare retired rich discovery keeps its typed result and grants no command or policy credit");
        run.Port.OwnedError = null;
        run.Ticks = 1000;
        check(!await run.Driver.ObserveAsync() && run.Port.OwnedReads == 1,
            "A retired enrichment schedules ordinary discovery instead of repeatedly querying the unchanged failed frame");
        await run.Driver.Client.WaitForQueuedWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Drain(run.Driver.Client);
        await run.Driver.ObserveAsync();
        check(run.Port.OwnedReads == 2 && run.Port.Commands.Count == 0 && run.Policy.Evaluations == 0,
            "An unaccepted rich discovery cannot mark a freshly reaccepted equal ordinary frame as already enriched (observed rich reads: " + run.Port.OwnedReads + ")");
    }

    private static async Task CurrentReadTickets(string profile, Action<bool, string> check)
    {
        var initial = Priority();
        // Lifecycle admission is a ClientSession contract. The connected-game
        // driver intentionally rejects Reset as a lost live match.
        var client = new ClientSession(new Port(initial, initial));
        var bootstrap = client.TryCaptureCurrentRead();
        check(bootstrap is { Snapshot: null, IsCurrent: true },
            "The owner read stamp permits an unbound manual bootstrap without granting a command");
        client.Accept(initial);
        check(!bootstrap!.IsCurrent, "The first accepted publication retires the unbound read stamp");
        var same = client.TryCaptureCurrentRead(); client.Accept(initial);
        check(same is { IsCurrent: false } && client.TryCaptureCurrentRead() is { IsCurrent: true },
            "Reaccepting an identical snapshot retires a read stamp even when its reference and payload stay equal");
        var old = client.TryCaptureCurrentRead(); client.Reset();
        check(old is { IsCurrent: false }, "Reset and equal-frame restoration cannot revive a previous read epoch");
        var resetFrame = initial with { Id = initial.Id + "-fresh-world" };
        check(client.Accept(resetFrame), "A reset read fixture binds a new game without resurrecting its retired game identity");
        var audience = client.TryCaptureCurrentRead();
        bool away = client.Accept(resetFrame with { ViewerId = 1, Revision = initial.Revision + 1, Decision = null });
        bool back = client.Accept(resetFrame with { Revision = initial.Revision + 2 });
        check(away && back && audience is { IsCurrent: false }, "Returning to an earlier viewer cannot revive an ABA-retired read stamp");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Run(async () => { entered.TrySetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(client.TryCaptureCurrentRead() == null,
            "General foreground connection work excludes current-read admission independently of command kind");
        release.TrySetResult();
        await client.WaitForQueuedWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(client.TryCaptureCurrentRead() == null,
            "A returned foreground worker cannot grant a read before its posted completion releases ownership");
        client.Pump();
        var closed = client.TryCaptureCurrentRead(); client.Close();
        check(closed is { IsCurrent: false } && client.TryCaptureCurrentRead() == null,
            "Close retires captured read stamps and refuses new admissions without transport work");
    }

    private static async Task RichMismatchConvergesThroughOrdinary(string profile, Action<bool, string> check)
    {
        var initial = Priority(); var newer = initial with { Revision = initial.Revision + 1 };
        using var run = new DriverCase(profile, "rich-mismatch-convergence", initial, newer,
            passing: ConnectedPlayerPassingMode.FullControl);
        await run.Prime(); run.Ticks = 1000; run.Port.State = newer; run.Port.HoldNextRead = true;
        check(!await run.Driver.ObserveAsync() && run.Port.OwnedReads == 1,
            "A different rich publication is incomplete and schedules ordinary owner discovery without adopting its state");
        await run.Port.NextReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(!await run.Driver.ObserveAsync() && run.Port.OwnedReads == 1
                && ReferenceEquals(run.Driver.Client.State, initial) && run.Policy.Evaluations == 0,
            "A held ordinary refresh prevents a tight rich-read loop and grants no comparison or policy credit");
        run.Port.ReleaseNextRead.TrySetResult(newer);
        await run.Driver.Client.WaitForQueuedWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(await run.Driver.ObserveAsync() && run.Port.OwnedReads == 2
                && ReferenceEquals(run.Driver.Client.State, newer) && run.Policy.Evaluations == 0,
            "One fresh ordinary publication permits one matching current enrichment after the rejected rich payload");
        check(await run.Driver.ObserveAsync() && run.Port.OwnedReads == 2,
            "Only the successfully admitted enriched publication suppresses duplicate rich discovery");
    }

    private static async Task EqualReacceptRetiresRichRead(string profile, Action<bool, string> check)
    {
        var initial = Priority();
        using var run = new DriverCase(profile, "equal-publication-rich-ticket", initial, initial,
            passing: ConnectedPlayerPassingMode.FullControl);
        await run.Prime(); run.Port.HoldOwned = true;
        var pending = run.Driver.ObserveAsync();
        await run.Port.OwnedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Driver.Client.Accept(initial);
        run.Port.ReleaseOwned.TrySetResult(new(initial,
            new(initial.Revision, ActionDomainCoverage.Complete, [], []), new()));
        check(!await pending && run.Driver.RetiredProposals == 1 && run.Policy.Evaluations == 0 && run.Port.Commands.Count == 0,
            "A held rich read cannot enrich an identical payload reaccepted under a newer owner publication stamp");
        run.Port.HoldOwned = false;
        check(await run.Driver.ObserveAsync() && run.Port.OwnedReads == 2,
            "The fresh equal publication can receive one new enrichment without reviving its old ticket");
    }

    private static async Task ManualReadBootstrap(string profile, Action<bool, string> check)
    {
        var initial = Priority(); var port = new Port(initial, initial);
        string directory = Path.Combine(profile, "manual-owner-read-bootstrap"); Directory.CreateDirectory(directory);
        using var evidence = new AiCampaignDiagnostics(directory);
        using var driver = new ConnectedPlayerDriver(port, port, port, port, port, evidence, 261109,
            MixedGameDriveMode.ManualStepped, passing: ConnectedPlayerPassingMode.FullControl);
        check(await driver.ObserveAsync() && ReferenceEquals(driver.Client.State, initial)
                && port.OwnedReads == 1 && port.Reads == 0 && port.Commands.Count == 0,
            "A current manual bootstrap uses one stamped rich read without an ordinary-poll or command adapter");
        var retired = new EngineObservationSupersededException(); port.OwnedError = retired;
        Exception? observed = null;
        try { await driver.ObserveAsync(); } catch (Exception error) { observed = error; }
        await driver.StepAsync();
        check(ReferenceEquals(observed, retired) && driver.Last == null && port.Commands.Count == 0,
            "Retired manual enrichment cannot leave the earlier observation available for policy dispatch");
    }
}
