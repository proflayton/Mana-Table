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

## Basic land acquisition candidate

`library-acquisition-replay.v1.json` compares frozen full v6 with the explicit
`casual-commander.v7.library-acquisition.v1` candidate. It uses four unchanged
owned captures from synthetic real-provider rule journeys: direct Battlefield,
direct Hand, staged Battlefield/Hand, and a renamed staged equivalent. The
independent `acquire-basic-lands` grader requires useful current basic-land
indices, rather than merely an accepted empty answer. The two staged cases
share one family weight. Capture provenance is retained beside the fixtures.

Use this spec with the same Plan/Run/Analyze commands above and a fresh profile.
The corresponding `library-acquisition-natural-pilot.v1.json` retains the full
32-game schedule and fixed full-v6 reference seats. Neither template promotes
the candidate to the shipping default.

Replay proves selection, not acquisition. The private provider continuation
below compares actual baseline/candidate resolutions under synthetic pregame
setup. Pass a matching built production JAR, fixture JAR, and engine resource
bundle containing `runtime/bin/java.exe` and `forge-res`:

```powershell
dotnet $driver --ai-library-acquisition-journey --fixture-mode bundle --engine-jar forge-api/target/forge-engine-fixtures.jar --production-engine-jar forge-api/target/forge-engine.jar --engine-resources <engine-bundle> --profile "$env:TEMP/Mana-Acquisition-Bundle-01"
```

Other modes are `direct-battlefield`, `direct-hand`, and `renamed-bundle`; each
needs a fresh profile. This test-only launcher never runs in the production JAR.
The journey retains exact owned frames, proposals, receipts and consequences.
Hidden Hand moves remain anonymous in public activity. A bounded private fixture
audit independently binds those moves to IDs already disclosed in the acting
seat's Hand and the resolved execution; it is never policy input. This is a
mechanic regression, not a natural-game strength measurement.

## Uncertain own-life exposure candidate

`uncertain-life-risk-replay.v1.json` separately compares full v6 with
`casual-commander.v6.uncertain-life-risk.v1`. Its eight authored contract-input
replays freeze a conservative four-life/card stress preference, known zero/one
life controls and ungraded high-life/low-exposure parity cases. These are not
real-provider captures or general-strength evidence. Renamed/boundary variants
share one mechanic family; the known fixed-loss controls form the second.

Use the same Plan/Run/Analyze commands with a fresh profile. Original replay
history, seed and allowance are preserved; no game command is executed. The
independent `prefer-opponent-draw` and `prefer-fixed-self-loss` endpoints grade
fixture-declared structural preferences, never the candidate score/threshold or
actual hidden damage. Safety, exact-v6 fallback parity, real-provider causal
continuation and human gameplay acceptance are separate checks. Shipping v6
remains unchanged, and this candidate cannot compose the library candidate or
attachment ablation. See the [frozen risk design](../../docs/Development/Mana-Table-AI-Uncertain-Life-Risk-Experiment.md).
