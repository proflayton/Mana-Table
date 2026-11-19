using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static partial class AiHostLoopRegression
{
    // Explicitly authored provider/clock control of the production C# scheduler;
    // this does not claim a real rules-game transition or natural outcome.
    private static async Task ControlledMixedPublicRead(Action<bool, string> check)
    {
        var clock = new Clock(); var policy = new CountActualPolicy(); var diagnostics = new MixedDiagnostics();
        var provider = new Port(clock) { Reply = read => read switch {
            1 => BoardBatch(1, 1, humans: 2),
            2 => BoardBatch(2, 2, humans: 2),
            3 => MixedPriority(2, 3),
            _ => BoardBatch(2, 4, humans: 2)
        } };
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock, diagnostics: diagnostics);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        check(provider.Reads == 1 && policy.Calls == 0 && provider.Commands.IsEmpty && provider.Controls.IsEmpty,
            "A mixed table's first privately owned batch establishes a baseline without policy, consent or a speculative read");
        clock.Advance(40);
        try { await Until(() => clock.IdlePending == 1); }
        catch { Console.WriteLine($"Mixed control interrupted: reads={provider.Reads}; policy={policy.Calls}; commands={provider.Commands.Count}; fault={host.LastFault?.ReasonCode}; error={diagnostics.Error}"); throw; }
        Console.WriteLine("Controlled mixed-read witness: reads=" + provider.Reads + "; starts=" + string.Join(',', provider.ReadStarts)
            + "; policy=" + policy.Calls + "; commands=" + provider.Commands.Count + "; controls=" + provider.Controls.Count);
        check(provider.Reads == 4 && provider.ReadStarts.SequenceEqual([0L, 40L, 40L, 40L])
            && policy.Calls == 1 && provider.Commands.ToArray() is [PassPriority { DecisionId: "independent-mixed-priority" }]
            && provider.Controls.IsEmpty && Accepted(host) == 1 && host.LastFault == null,
            "Mixed public progress earns one independent fresh read before the real policy answers its separately offered current input, followed by ordinary post-ACK refresh");
        clock.Advance(39);
        check(provider.Reads == 4 && policy.Calls == 1 && provider.Commands.Count == 1,
            "The unchanged mixed follow-up restores40ms through39ms without renewing policy, consent or read urgency");
    }

    private static ImmutableArray<AutomatedSeatObservation> MixedPriority(long board, long revision)
        => BoardBatch(board, revision, humans: 2).Select(row => row with { State = row.State with {
            Phase = "First main", PhaseKey = "MAIN1", ActivePlayerId = 0, DecisionOwnerId = 2, PriorityPlayerId = 2,
            Decision = row.SeatId == 2 ? new("independent-mixed-priority", new(), new PriorityPayload(new("Pass", true))) : null,
            ActionClass = row.SeatId == 2 ? "priority" : "resolving"
        }, Domain = row.SeatId == 2 ? new(revision, ActionDomainCoverage.Complete, [], []) : DecisionActionDomain.None }).ToImmutableArray();

    private static async Task StaticHumanModal(Action<bool, string> check)
    {
        var clock = new Clock(); var policy = new CountActualPolicy();
        // Private AI views may identify the foreign required-choice owner but
        // never carry that human's choice payload or current action handles.
        var provider = new Port(clock) { SupportsPriority = true, Reply = read => BoardBatch(10, read, humans: 2)
            .Select(row => row with { State = row.State with { DecisionOwnerId = 0, PriorityPlayerId = null,
                Decision = null, ActionClass = "wait", Phase = "First main", PhaseKey = "MAIN1" } }).ToImmutableArray() };
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        clock.Advance(40); await Until(() => clock.IdlePending == 1);
        clock.Advance(39);
        check(provider.Reads == 2 && policy.Calls == 0 && provider.Commands.IsEmpty && provider.Controls.IsEmpty,
            "A static human-owned modal with only metadata revisions retains40ms through39ms without invoking an AI policy or consent control");
        clock.Advance(1); await Until(() => clock.IdlePending == 1);
        check(provider.ReadStarts.SequenceEqual([0L, 40L, 80L]) && policy.Calls == 0 && provider.Commands.IsEmpty
                && provider.Controls.IsEmpty && host.LastFault == null,
            "Repeated ordinary private observations of the unchanged human modal stay idle and never disclose or answer that human input");
    }

    private static async Task MixedOwnerRecovery(Action<bool, string> check, bool inconsistent = false)
    {
        var clock = new Clock();
        var provider = new Port(clock) { Reply = read => read switch {
            1 => BoardBatch(100, 100, humans: 2),
            2 or 3 => Broken(read),
            4 => BoardBatch(50, 103, humans: 2),
            5 => BoardBatch(51, 104, humans: 2),
            6 => BoardBatch(100, 105, humans: 2),
            _ => BoardBatch(101, 99 + read, humans: 2)
        } };
        ImmutableArray<AutomatedSeatObservation> Broken(int read) {
            var rows = BoardBatch(100, 99 + read, humans: 2);
            return !inconsistent ? [rows[0]] : rows.SetItem(1, rows[1] with { State = rows[1].State with {
                Players = rows[1].State.Players.Select(player => player.Id == 0 ? player with { Eliminated = true } : player).ToImmutableArray()
            } });
        }
        await using var host = new AiTableHost(provider, _ => new Policy(), 42, operationClock: clock);
        await host.StartAsync(); await Until(() => clock.IdlePending == 1);
        for (int read = 2; read <= 6; read++) {
            clock.Advance(40); await Until(() => clock.IdlePending == 1);
            check(provider.Reads == read && provider.Commands.IsEmpty && host.LastFault == null,
                "Private ownership retains its certificate and B100 ceiling through partial/inconsistent batches, B50/B51 rollback and complete catch-up baseline: " + inconsistent + "/" + read);
        }
        clock.Advance(40); await Until(() => clock.IdlePending == 1);
        Console.WriteLine("Mixed recovery witness: starts=" + string.Join(',', provider.ReadStarts));
        check(provider.Reads == 8 && provider.ReadStarts.SequenceEqual([0L, 40L, 80L, 120L, 160L, 200L, 240L, 240L])
            && provider.Commands.IsEmpty && host.LastFault == null,
            "Only strict B101 progress after the complete B100 recovery baseline earns a fresh read; missing command sessions never erase scheduling high-water");
    }

    private static async Task MixedOwnershipShapes(Action<bool, string> check)
    {
        foreach (string mode in new[] { "single-owned", "eliminated-withdrawal", "no-live-owned", "new-owned" }) {
            var clock = new Clock();
            ImmutableArray<AutomatedSeatObservation> Rows(int read) {
                var rows = BoardBatch(read <= 2 ? read : mode == "new-owned" ? 3 : 2, read, humans: 2);
                if (mode == "single-owned" || mode == "new-owned" && read == 1) return [rows[0]];
                if (mode is "eliminated-withdrawal" or "no-live-owned") {
                    rows = rows.Select(row => row with { State = row.State with { Players = row.State.Players.Select(player =>
                        mode == "eliminated-withdrawal" && read > 1 && player.Id == 2 || mode == "no-live-owned" && player.Id >= 2 ? player with { Eliminated = true } : player).ToImmutableArray()
                    } }).ToImmutableArray();
                    if (mode == "eliminated-withdrawal" && read > 1) return [rows[1]];
                }
                return rows;
            }
            var provider = new Port(clock) { Reply = Rows };
            await using var host = new AiTableHost(provider, _ => new Policy(), 42, operationClock: clock);
            await host.StartAsync(); await Until(() => clock.IdlePending == 1);
            clock.Advance(40); await Until(() => clock.IdlePending == 1);
            int expected = mode is "single-owned" or "eliminated-withdrawal" ? 3 : 2;
            check(provider.Reads == expected && provider.Commands.IsEmpty && host.LastFault == null,
                "Mixed progress uses private ownership and complete public elimination proof, without inferring roles from names: " + mode);
            if (mode == "new-owned") {
                clock.Advance(40); await Until(() => clock.IdlePending == 1);
                check(provider.Reads == 4 && provider.ReadStarts.SequenceEqual([0L, 40L, 80L, 80L]),
                    "A newly certified private owner first baselines all owned views before later mixed progress requests a fresh read");
            }
        }
    }

    private static async Task MixedReadWorkBound(Action<bool, string> check)
    {
        var clock = new Clock();
        var provider = new Port(clock) { Reply = read => BoardBatch(Math.Min(read, 6), read, humans: 2), HoldRead = 3 };
        await using var host = new AiTableHost(provider, _ => new Policy(), 42, operationClock: clock);
        try {
            await host.StartAsync(); await Until(() => clock.IdlePending == 1);
            clock.Advance(40); await provider.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(1000);
            check(provider.Reads == 3 && provider.ReadStarts.SequenceEqual([0L, 40L, 40L]) && provider.Commands.IsEmpty,
                "A slow independent progress read stays serial; advancing clock cannot create concurrent reads, policy or timer-driven spinning");
            provider.Release.TrySetResult(); await Until(() => clock.IdlePending == 1);
            check(provider.Reads == 7 && provider.Commands.IsEmpty && host.LastFault == null,
                "Five eligible advanced mixed batches earn at most five extra reads, and unchanged follow-up restores ordinary idle");
        } finally { provider.Release.TrySetResult(); }
    }

    private sealed class CountActualPolicy : IAiPolicy
    {
        private readonly CasualCommanderPolicy inner = new();
        private int calls; internal int Calls => Volatile.Read(ref calls);
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        { Interlocked.Increment(ref calls); return inner.EvaluateAsync(request, cancellation); }
    }
    private sealed class MixedDiagnostics : IAiHostDiagnostics
    {
        internal Exception? Error;
        public void Report(AiHostFault fault, Exception? error) => Error ??= error;
    }
}
