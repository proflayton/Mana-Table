# AI experiment examples

These local development examples compare full v6 with a compatible v6 attachment
ablation. Cancellation, required-answer handling, provider facts and decision
budgets remain equal. The control is not the historical v5 binary.

Build `Mana.Table.Tests` in Release, install Node 24, and adjust `runtime.dotnetPath`
in a copied spec if .NET is installed elsewhere. Paths are relative to the spec
file; when moving a copy, resolve its input/runtime paths first. Keep output
profiles outside the harness, source resources and other immutable inputs.

From the repository root:

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
$driver = 'mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.dll'
dotnet $driver --ai-experiment-plan mana-native/experiments/attachment-replay.v1.json --profile "$env:TEMP/Mana-Attachment-Replay-01"
dotnet $driver --ai-experiment-run "$env:TEMP/Mana-Attachment-Replay-01" --jobs 1
dotnet $driver --ai-experiment-analyze "$env:TEMP/Mana-Attachment-Replay-01"
```

The replay example schedules ten jobs, evaluating five unchanged synthetic-game
captures once per arm. They are known development regressions. The report gives
both raw case counts and a primary macro average across three declared families,
so the renamed positive does not give its family twice another family's weight.
Owned captures explicitly identify their capability context and empty diagnostic
history. A captured `replay` input instead retains its original history, seed,
plan and allowance. Neither format executes a gameplay command.

The natural example additionally needs a built production engine, its matching
resources, and `JAVA_HOME` or an explicit `runtime.javaPath`. Review the generated
lock's duration and disk estimates before launching. It schedules 32 fresh games:
four focal decks, four seats, two arms, with three fixed full-v6 reference seats.
The policy seed is pinned; Forge's rules randomness is uncontrolled. These are
blocked independent games, not identical-shuffle pairs. Actual starting player
is recorded from turn one when observed and otherwise remains unknown.

```powershell
dotnet $driver --ai-experiment-plan mana-native/experiments/attachment-natural-pilot.v1.json --profile "$env:TEMP/Mana-Attachment-Pilot-01"
dotnet $driver --ai-experiment-run "$env:TEMP/Mana-Attachment-Pilot-01" --jobs 1 --max-jobs 2
```

`--max-jobs` limits newly launched jobs in this invocation; it never shrinks the
locked sample. Repeating Run continues never-reserved jobs and preserves first
attempts. A two-game execution chunk is an incomplete development run, not a
completed balanced experiment. V1 supports sequential execution only. Time/turn
cutoffs remain unresolved, and the analyzer reports planned and started-outcome
bounds rather than treating them as draws or discarding them.

Reports are private under `analysis/<identity>/{summary.json,report.md}`. The
analysis does not need the engine or original runtime artifacts to remain
installed, but it requires the locked analyzer and retained evidence. It emits
descriptive results with no confidence interval or general strength claim.
Current coverage counters and causal-consequence verification can be unavailable;
they are recorded as unknown. Sampled natural-game replay requests and compact
timing records are not an exhaustive game archive.

Never edit a lock or reuse an orphaned coordinator lease to force continuation.
Reconcile owned processes and immutable receipts before explicit recovery; an
uncertain worker is not automatically retried. A previous critical safety,
evidence or cleanup failure permanently stops that lock, including on a later
Run invocation. Investigate it and use a new source/spec and new lock.
Worker exit 0 means a committed
measurement record, which may report a cutoff or policy failure. The report's
status, receipts and guardrails determine what was actually measured.

See the [framework design](../../docs/Development/Mana-Table-AI-Experiments-Design.md)
for the intended evaluation layers, limitations and later confirmation work.
