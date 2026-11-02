using System.Diagnostics;
using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine
{
    public bool SupportsEncounterStart => current is { EncounterStartAvailable: true, Failure: null };

    public async Task<Lobby> BeginEncounterAsync(string tableId, string matchSetupConsent, string? tempoRevision = null)
    {
        if (string.IsNullOrWhiteSpace(tableId) || string.IsNullOrWhiteSpace(matchSetupConsent))
            throw new ArgumentException("Starting an encounter needs the current table and reviewed setup.");
        var owner = RequireEncounterStart();
        var lobby = MapLobby(await Request("startEncounter", EncodeEncounterStart(tableId, matchSetupConsent, tempoRevision)));
        EnsureEncounterOwner(owner, tableId, lobby.StartState?.AttemptId ?? "");
        ValidateStartReply(lobby, tableId);
        return lobby;
    }

    public Task<Lobby> RetryEncounterStartAsync(string tableId, string attemptId)
        => ChangeEncounterStartAsync("retryEncounterStart", tableId, attemptId);

    public Task<Lobby> CancelEncounterStartAsync(string tableId, string attemptId)
        => ChangeEncounterStartAsync("cancelEncounterStart", tableId, attemptId);

    private async Task<Lobby> ChangeEncounterStartAsync(string method, string tableId, string attemptId)
    {
        if (string.IsNullOrWhiteSpace(tableId) || string.IsNullOrWhiteSpace(attemptId))
            throw new ArgumentException("Changing admission needs the current table and encounter attempt.");
        var owner = RequireEncounterStart();
        var lobby = MapLobby(await Request(method, EncodeEncounterStartScope(tableId, attemptId)));
        EnsureEncounterOwner(owner, tableId, attemptId);
        ValidateStartReply(lobby, tableId, attemptId);
        if (method == "cancelEncounterStart" && lobby.StartState!.Phase != EncounterStartPhase.Cancelled)
            throw new IOException("The admission provider did not acknowledge the cancelled attempt.");
        return lobby;
    }

    internal static object EncodeEncounterStart(string tableId, string matchSetupConsent, string? tempoRevision)
        => new { tableId, matchSetupConsent, tempoRevision };
    internal static object EncodeEncounterStartScope(string tableId, string attemptId) => new { tableId, attemptId };

    internal static void ValidateStartReply(Lobby lobby, string tableId, string? attemptId = null)
    {
        EncounterStartFacts.Validate(lobby);
        if (lobby.TableId != tableId || lobby.Mode != "hosting" || !lobby.EncounterStartSupported || lobby.StartState == null
            || attemptId != null && lobby.StartState.AttemptId != attemptId)
            throw new EncounterStartException(EncounterStartFailure.ScopeChanged, tableId, attemptId ?? "",
                "The encounter admission provider or table changed.");
    }

    private EngineProcess RequireEncounterStart()
        => current is { EncounterStartAvailable: true, Failure: null } owner ? owner
            : throw new NotSupportedException("This provider does not expose durable encounter admission.");

    private void EnsureEncounterOwner(EngineProcess owner, string tableId, string attemptId)
    {
        if (current != owner || owner.Failure != null)
            throw new EncounterStartException(EncounterStartFailure.ScopeChanged, tableId, attemptId,
                "The encounter admission connection changed.");
    }

    // Compatibility for callers awaiting the existing setup port. The player
    // flow uses BeginEncounterAsync and ordinary lobby polling instead.
    public async Task StartAsync()
    {
        var before = await LobbyAsync();
        if (before.MatchSetup?.RulesetId != MatchRulesets.CooperativeCommander) { await Request("multiplayerStart"); return; }
        if (!SupportsEncounterStart || !before.EncounterStartSupported) throw new NotSupportedException("This table does not expose durable encounter admission.");
        var owner = RequireEncounterStart();
        var lobby = await BeginEncounterAsync(before.TableId, before.MatchSetupConsent!, before.Tempo?.Revision);
        string attempt = lobby.StartState!.AttemptId, party = lobby.PartyProgress?.PartyId ?? "";
        long revision = lobby.Revision;
        var elapsed = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(30);
        TimeSpan Remaining() => budget > elapsed.Elapsed ? budget - elapsed.Elapsed
            : throw new EncounterStartException(EncounterStartFailure.TimedOut, before.TableId, attempt,
                "The encounter is still preparing. Its admission remains available in the lobby.");
        try {
            while (true) {
                EnsureEncounterOwner(owner, before.TableId, attempt);
                ValidateStartReply(lobby, before.TableId, attempt);
                if (lobby.StartState!.Phase is EncounterStartPhase.Failed or EncounterStartPhase.Cancelled)
                    throw new EncounterStartException(lobby.StartState.Phase == EncounterStartPhase.Cancelled
                        ? EncounterStartFailure.Cancelled : EncounterStartFailure.Failed, before.TableId, attempt,
                        lobby.StartState.Error ?? "The encounter start was cancelled.");
                if (lobby.MatchActive) {
                    var raw = await Request("matchState").WaitAsync(Remaining());
                    EnsureEncounterOwner(owner, before.TableId, attempt);
                    var state = ForgeSnapshotDecoder.Decode(raw, certifiedUndo: owner.CertifiedUndoAvailable);
                    ValidateStartedFrame(lobby, state, party);
                    owner.Commands.Observe(state);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25)).WaitAsync(Remaining());
                var next = await LobbyAsync().WaitAsync(Remaining());
                EnsureEncounterOwner(owner, before.TableId, attempt);
                if (next.TableId != before.TableId) ValidateStartReply(next, before.TableId, attempt);
                if (next.Revision < revision) continue;
                revision = next.Revision; lobby = next;
            }
        } catch (TimeoutException) {
            throw new EncounterStartException(EncounterStartFailure.TimedOut, before.TableId, attempt,
                "The encounter is still preparing. Its admission remains available in the lobby.");
        }
    }

    internal static void ValidateStartedFrame(Lobby lobby, GameSnapshot? state, string partyId)
    {
        try { EncounterStartFacts.ValidateMatch(lobby, state); }
        catch (InvalidOperationException error) { throw new IOException("The admission did not publish its actual rules frame.", error); }
        if (!lobby.MatchActive || lobby.StartState is not { Phase: EncounterStartPhase.Starting } admission
            || state?.Encounter is not { } identity || identity.EncounterId != admission.AttemptId || identity.PartyId != partyId)
            throw new EncounterStartException(EncounterStartFailure.ScopeChanged, lobby.TableId, lobby.StartState?.AttemptId ?? "",
                "The admission did not publish this encounter's actual rules frame.");
    }
}
