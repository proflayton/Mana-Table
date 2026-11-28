using Mana.Contracts;

namespace Mana.Client;

public sealed partial class ClientSession
{
    private MatchProgressWatch progressWatch = MatchProgressWatch.Empty;
    private CancellationTokenSource? progressRepairLifetime;
    private TimeSpan ProgressTime => time.GetElapsedTime(0, time.GetTimestamp());
    public bool ProgressRecoveryRequired => progressWatch.RepairAttempted;
    private TimeSpan? TimeUntilMatchProgressCheck => progressWatch.Witness == null || progressWatch.RepairAttempted
        || Authority != ClientSessionAuthority.Interactive ? null
            : TimeSpan.FromMilliseconds(Math.Max(0, Math.Ceiling((MatchProgressWatch.Limit - (ProgressTime - progressWatch.Started)).TotalMilliseconds)));

    private void CancelMatchProgressRepair()
    {
        var owner = progressRepairLifetime; progressRepairLifetime = null;
        owner?.Cancel(); owner?.Dispose();
    }

    private void ObserveMatchProgress(GameSnapshot? state)
        => progressWatch = progressWatch.Observe(state != null && MatchProgressWitness.RequiresClientProgress(state)
            ? MatchProgressWitness.From(state, MatchProgressWitness.ClientProgressOwner(state)) : null, ProgressTime);

    private void CheckMatchProgress()
    {
        if (Closed || Authority != ClientSessionAuthority.Interactive || State == null) return;
        ObserveMatchProgress(State);
        if (!progressWatch.Due(ProgressTime) || progressWatch.RepairAttempted || operations.Occupied || Polling) return;
        progressWatch = progressWatch.Repair();
        var context = ClientFailureContext.Capture(replica.Epoch, State);
        ReportFailure(new EngineMatchProgressException("The game stopped making progress. Checking the current decision."),
            ClientFailurePhase.Observation, context);
        if (connection is not IMatchProgressRecovery { SupportsMatchProgressRecovery: true } repair) return;
        string game = State.Id;
        var lifetime = new CancellationTokenSource(); progressRepairLifetime = lifetime;
        var cancellation = lifetime.Token;
        StartOwnedOperation(false, async owner => {
            using var deadline = new CancellationTokenSource(MatchProgressWatch.Limit, time);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellation);
            var token = stopping.Token;
            var task = Task.Run(async () => {
                token.ThrowIfCancellationRequested();
                await repair.ReconcileMatchProgressAsync(game, token);
                token.ThrowIfCancellationRequested();
                return await connection.ObserveAsync();
            });
            _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try {
                var state = await task.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                return new OwnerCompletion.ObservedReceipt(state, ClientFailurePhase.Observation, false);
            } catch (OperationCanceledException error) when (deadline.IsCancellationRequested) {
                throw new EngineMatchProgressException("The game could not restore its current decision in time.", error);
            } finally {
                Post(() => { if (ReferenceEquals(progressRepairLifetime, lifetime)) { progressRepairLifetime = null; lifetime.Dispose(); } });
            }
        }, (owner, error) => ReportFailure(error, ClientFailurePhase.Observation, owner));
    }
}
