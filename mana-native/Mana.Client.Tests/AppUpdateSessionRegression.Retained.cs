using Mana.Client;

internal static partial class AppUpdateSessionRegression
{
    private static async Task RetainedOwnershipAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions(); var source = new Updates();
        var session = new Driver(source, profile, queue, () => "");
        try {
            session.Check(); source.CheckGate.SetResult(source.Release);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
            session.Download(); await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = session.Role; string firstScope = original.Scope;
            var second = session.ReplaceView();
            check(second.Current && !original.Current && second.View.Downloading && source.Downloads == 1
                && !source.DownloadCancelled.Task.IsCompleted,
                "Replacing a view retains the one held download and its phase without requesting cancellation");
            bool duplicateRefused = false;
            try { session.Owner.AttachView(); } catch (InvalidOperationException) { duplicateRefused = true; }
            check(duplicateRefused && second.Current, "A second active update view cannot obtain competing command authority");
            bool startup = second.CheckOnStartup;
            original.ToggleStartupCheck(); original.ReportLocationFailure(new IOException("Retired location"));
            check(!original.Check() && !original.Download() && !original.CancelDownload() && !original.Install(new(""))
                && second.CheckOnStartup == startup && second.View.Error.Length == 0,
                "A retired view cannot start, cancel, install, change preferences or publish an error into the retained workflow");
            long generation = session.View.Generation;
            for (int received = 1; received <= 10000; received++) source.Progress!.Report(new(received, 10000));
            await queue.Pending();
            check(session.View.BytesReceived == 0, "A progress wakeup does not apply the provider's result or pump the owner");
            queue.Drain();
            check(session.View.BytesReceived == 10000 && session.View.Generation == generation && source.Downloads == 1,
                "One owner pump consumes the latest coalesced progress while preserving unchanged control authority");
            var third = session.ReplaceView();
            original.Dispose(); second.Dispose();
            check(third.Current && third.Scope != firstScope && third.Scope != second.Scope
                && !original.CancelDownload() && !second.CancelDownload(),
                "View ABA keeps old scopes revoked and old disposal cannot retire the fresh view");
            check(third.CancelDownload(), "The newly attached current view may cancel the original retained download");
            await source.DownloadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.DownloadGate.SetResult(source.Prepared);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
            check(session.View.Prepared == null && session.View.Notice.Contains("cancelled") && source.Downloads == 1,
                "The current view's cancellation settles the original operation once even when the provider returns a prepared result");
        } finally { source.CompleteOutstanding(); session.Close(); }
    }

    private static async Task RetiredRestartIntentAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions(); var source = new Updates();
        source.Recovered = source.Prepared;
        int launches = 0, exits = 0;
        var session = new Driver(source, profile, queue, () => "", _ => launches++, () => exits++);
        try {
            session.Check(); source.CheckGate.SetResult(source.Release);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Ready);
            var original = session.Role;
            check(session.Install(), "A current view's explicit restart intent begins real payload verification");
            await source.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.ReplaceView(); source.ValidationGate.SetResult();
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Ready);
            check(launches == 0 && exits == 0 && session.View.Prepared == source.Prepared
                && !original.Install(new("")),
                "Retiring a view revokes its not-yet-launched restart intent while retaining the verified ready payload");
            source.ResetValidation();
            check(session.Install(), "The fresh current view can explicitly request its own restart verification");
            await source.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.ValidationGate.SetResult();
            await queue.Until(() => session.View.InstallerLaunched);
            check(launches == 1 && exits == 1 && source.Validations == 2,
                "Only a new explicit current-view intent launches the installer and requests exit once");
        } finally { source.CompleteOutstanding(); session.Close(); }
    }

    private static async Task PassiveCloseAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions(); var source = new Updates();
        var session = new Driver(source, profile, queue, () => "");
        using var cancelledWait = new CancellationTokenSource();
        var cancelled = session.Owner.WaitForQueuedWorkAsync(cancelledWait.Token);
        var independent = session.Owner.WaitForQueuedWorkAsync();
        cancelledWait.Cancel();
        bool waiterCancelled = false;
        try { await cancelled; } catch (OperationCanceledException) { waiterCancelled = true; }
        check(waiterCancelled && !independent.IsCompleted,
            "Cancelling one passive update-work waiter leaves another waiter and operation authority unchanged");
        session.Check(); source.CheckGate.SetResult(source.Release);
        await independent.WaitAsync(TimeSpan.FromSeconds(5));
        check(session.View.Checking && !session.View.Checked,
            "Returned discovery wakes passive waiters without applying its queued terminal result");
        var closing = session.Owner.CloseAsync();
        check(ReferenceEquals(closing, session.Owner.CloseAsync()), "Repeated close shares one actual-work join task");
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        check(session.View.Phase == AppUpdatePhase.Closed && !session.View.Checked && source.Disposals == 0,
            "Close joins the returned provider task without applying its mailbox result or disposing the borrowed provider");
        await session.Owner.WaitForQueuedWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
        session.Owner.Pump(session.Role, new("", true, true));
        check(!session.Check() && !session.Download() && !session.Install() && source.Checks == 1
            && session.View.Phase == AppUpdatePhase.Closed,
            "Close wakes waiters and leaves queued callbacks, startup ticks and retired commands inert without another Game pump");
        source.Dispose(); session.Close();
        check(source.Disposals == 1, "The composition's explicit provider disposal happens once after session settlement");
    }

    private static async Task CallbackFailuresAsync(string profile, Action<bool, string> check)
    {
        var queue = new Completions();
        var source = new Updates { CancelAfterDownloadCleanup = true, ThrowCancellationNotification = true };
        var session = new Driver(source, profile, queue, () => "");
        session.Check(); source.CheckGate.SetResult(source.Release);
        await queue.Until(() => session.View.Phase == AppUpdatePhase.Available);
        session.Download(); await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = session.Owner.CloseAsync();
        try {
            await source.DownloadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!closing.IsCompleted && !session.Role.Current && source.Disposals == 0,
                "A throwing cancellation callback cannot abandon the still-held provider operation during close");
            source.Progress!.Report(new(999, 1000));
        } finally { source.DownloadGate.TrySetResult(source.Prepared); await closing.WaitAsync(TimeSpan.FromSeconds(5)); }
        check(source.DownloadFinished && session.View.Phase == AppUpdatePhase.Closed
            && session.View.Error.Contains("notification") && session.View.BytesReceived == 0,
            "Close settles actual cleanup and records cancellation failure without needing a mailbox or Game callback");
        source.Dispose();
        check(source.Disposals == 1, "Cancellation failure still leaves service disposal with its composition owner");

        queue = new(); source = new(); source.Recovered = source.Prepared;
        int launches = 0, exits = 0;
        session = new(source, profile, queue, () => "", _ => launches++, () => {
            exits++; throw new IOException("Controlled exit callback failure");
        });
        try {
            session.Check(); source.CheckGate.SetResult(source.Release);
            await queue.Until(() => session.View.Phase == AppUpdatePhase.Ready);
            session.Install(); await source.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); source.ValidationGate.SetResult();
            await queue.Until(() => session.View.InstallerLaunched);
            check(launches == 1 && exits == 1 && session.View.Phase == AppUpdatePhase.Installing
                && session.View.Failure == AppUpdateFailure.Installation && !session.Install(),
                "An exit callback failure preserves the known launched installer and cannot authorize another handoff");
            await session.Owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(session.View.Phase == AppUpdatePhase.Closed && session.View.InstallerLaunched && source.Disposals == 0,
                "A throwing owner completion cannot strand pump-independent shutdown or erase a committed launch");
        } finally { source.CompleteOutstanding(); session.Close(); }
    }
}
