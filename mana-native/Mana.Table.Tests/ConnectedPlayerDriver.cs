using Mana.AI;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Test driver for an ordinary connected player. It never receives the host's
// automated-seat port. The same policy is used to exercise decisions, not to
// claim that a scripted player supplies human UX acceptance.
internal sealed class ConnectedPlayerDriver : IDisposable
{
    private readonly ForgeEngine engine;
    private readonly AiCampaignDiagnostics evidence;
    private readonly long seed;
    private readonly ClientSession client;
    private readonly ReceiptConnection connection;
    private readonly AiCausalHistoryReader history = new();
    private AiSeatSession? policy;
    private AiDecisionStamp? rejected;
    private Exception? failure;
    internal AutomatedSeatObservation? Last { get; private set; }
    internal AiSeatHistory? History => policy?.History;
    internal int ClientAdmissions { get; private set; }
    internal int ProviderAcceptances { get; private set; }

    internal ConnectedPlayerDriver(ForgeEngine engine, AiCampaignDiagnostics evidence, long seed)
    {
        this.engine = engine; this.evidence = evidence; this.seed = seed;
        connection = new(engine);
        client = new(connection, sourceActions: engine, observationDelivery: ObservationDeliveryMode.External);
        client.Failed += error => failure = error;
    }

    internal async Task ObserveAsync()
    {
        client.Pump();
        var own = await engine.ObserveDecisionAsync();
        if (own == null) throw new InvalidDataException("The connected player lost its match.");
        if (!AiCampaignVisibility.PrivateWindowsAreDisclosed(own.State, own.Semantics, own.Domain))
            throw new InvalidDataException("A connected player received an unauthorized private window.");
        if (!client.Accept(own.State)) throw new InvalidDataException("The player's client rejected its current owned observation.");
        Last = new(own.State.ViewerId, own.State, own.Domain, own.Semantics);
        evidence.Observe(Last);
        await history.DrainOwnedAsync(engine, own, evidence.QualificationEvidence);
    }

    internal async Task StepAsync()
    {
        if (Last is not { } observation) return;
        var state = observation.State;
        policy ??= new(state.Id, state.ViewerId, evidence.Record(new CasualCommanderPolicy()), seed,
            new(TimeSpan.FromSeconds(10), 4096));
        policy.ObserveRestoration(state);
        if (state.Status != "playing" || state.Decision == null || state.Viewer?.Eliminated != false) {
            policy.Observe(null); return;
        }
        var frame = new AiDecisionFrame(state, domain: observation.Domain, semantics: observation.Semantics);
        if (rejected == frame.Stamp) throw new InvalidDataException("A rejected player command retained the same authority.");
        rejected = null;
        policy.Observe(frame);
        var view = await policy.EvaluateAsync();
        if (view.Status is AiSeatStatus.Unavailable or AiSeatStatus.Invalid or AiSeatStatus.BudgetExceeded or AiSeatStatus.NeedsRecovery)
            throw new InvalidDataException("The connected-player test policy cannot answer: " + view);
        if (policy.TakeCommand() is not { } ticket) return;
        bool recorded = false;
        try {
            if (ticket.Command is UseOfferedAction offered) {
                var card = CardViews.FindActionSource(state, offered.Card)?.Card
                    ?? throw new EngineCommandRejectedException("The current source is no longer disclosed.");
                client.SourceActions.Observe(new(state.Id, state.Decision.Id, state.Revision,
                    state.ViewerId, card.Key, CardViews.Identity(card)));
                await PumpUntilAsync(() => client.SourceActions.View.Status != SourceActionStatus.Loading);
                if (client.SourceActions.View.Status != SourceActionStatus.Ready)
                    throw new EngineCommandRejectedException("The source query did not retain current authority.");
            }
            failure = null; connection.Accepted = false;
            if (!client.Submit(ticket.Command))
                throw new EngineCommandRejectedException("The connected player's client declined the command.");
            ClientAdmissions++;
            await PumpUntilAsync(() => !client.Busy);
            // Submit is local admission. Only a successful normal rules-port
            // receipt establishes provider acceptance. Neither proves resolution.
            if (connection.Accepted) {
                ProviderAcceptances++;
                evidence.Receipt(observation, ticket.Command, true, null);
                policy.Report(ticket, AiExecutionReceipt.Accepted); recorded = true;
                await history.DrainOwnedAsync(engine, new(observation.State, observation.Domain, observation.Semantics),
                    evidence.QualificationEvidence, force: true);
            }
            if (failure != null) throw failure;
            if (!connection.Accepted) throw new IOException("The player's command has no known provider receipt.");
        } catch (EngineCommandRejectedException error) when (!recorded) {
            evidence.Receipt(observation, ticket.Command, false, error.GetType().Name);
            policy.Report(ticket, AiExecutionReceipt.Rejected); rejected = ticket.Stamp;
        } catch {
            if (!recorded) policy.Report(ticket, AiExecutionReceipt.Unknown);
            throw;
        }
    }

    private async Task PumpUntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!done()) {
            client.Pump();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The connected player's client did not complete its operation.");
            if (!done()) await Task.Delay(1);
        }
    }

    public void Dispose() { client.Close(); policy?.Dispose(); }

    private sealed class ReceiptConnection(IMatchConnection inner) : IMatchConnection
    {
        internal bool Accepted;
        public Task<GameSnapshot?> ObserveAsync() => inner.ObserveAsync();
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Qualification never forces a player concession.");
        public async Task ExecuteAsync(GameCommand command) { await inner.ExecuteAsync(command); Accepted = true; }
    }
}
