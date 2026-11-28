using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IMatchProgressRecovery, IAutomatedMatchProgressRecovery
{
    public bool SupportsMatchProgressRecovery => current is { MatchProgressRecoveryAvailable: true, Failure: null } owner
        && owner.Ready.Task.IsCompletedSuccessfully && !owner.Process.HasExited;

    public async Task ReconcileMatchProgressAsync(string gameId, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        if (!SupportsMatchProgressRecovery) throw new NotSupportedException("This engine cannot reconcile match progress.");
        var owner = current!; long generation = worldGeneration;
        var receipt = await Request("matchReconcileProgress", new { sessionId = gameId }, cancellation: cancellation);
        EnsureSimulationOwner(owner, generation);
        ValidateProgressReceipt(receipt, gameId);
    }

    public async Task ReconcileAutomatedMatchProgressAsync(string gameId, int seatId, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        if (!SupportsMatchProgressRecovery) throw new NotSupportedException("This engine cannot reconcile match progress.");
        var owner = current!; long generation = worldGeneration;
        lock (worldScopeGate) foreach (var key in automatedScopes.Keys.Where(key => key.Game == gameId).ToArray()) automatedScopes.Remove(key);
        var receipt = await Request("automatedReconcileProgress", new { sessionId = gameId, seatId }, cancellation: cancellation);
        EnsureSimulationOwner(owner, generation);
        ValidateProgressReceipt(receipt, gameId);
    }

    private static void ValidateProgressReceipt(System.Text.Json.JsonElement receipt, string gameId)
    {
        if (receipt.ValueKind != System.Text.Json.JsonValueKind.Object
            || !receipt.TryGetProperty("gameId", out var game) || game.ValueKind != System.Text.Json.JsonValueKind.String || game.GetString() != gameId
            || !receipt.TryGetProperty("status", out var status) || status.ValueKind != System.Text.Json.JsonValueKind.String
            || status.GetString() is not ("reconciled" or "unchanged" or "retired"))
            throw new IOException("The engine returned an invalid match progress receipt.");
    }
}
