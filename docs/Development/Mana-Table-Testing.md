# Testing Mana Table

Mana Table has one client: the Windows MonoGame app. Use the smallest check that
covers the change. Reuse the existing Java and native harnesses; do not add a
second client or rules simulator to test the shipping app.

Run commands from the repository root. Native development requires the .NET 9
SDK; engine work requires JDK 17+ and Maven 3.8.1+. Node is only for dependency-free
documentation and CI tools. See [native setup](../../mana-native/README.md) for
a complete engine/runtime/resource bundle.

## Choose the relevant check

| Change | Starting point |
| --- | --- |
| Client/session, contracts, persistence or updates | `dotnet run --project mana-native/Mana.Client.Tests/Mana.Client.Tests.csproj -c Release` |
| Presentation and scoped input | Build `mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj`, then run the relevant headless mode |
| Java projection, rules or synchronization | `mvn -pl forge-api -am verify`, or the affected Java test selection |
| Actual Forge/native boundary | Native `--smoke-headless-engine`, startup, adapter or multiplayer journey relevant to the change |
| Visible layout, artwork or motion | Relevant real-window journey and human review |
| Repository documentation/CI tools | `node scripts/mana-table/check-docs.cjs`, `node scripts/mana-table/check.cjs`, and affected `scripts/mana-table/tests/*.test.cjs` |

Do not rebuild or repeat unrelated journeys after the relevant checks pass.
A new source or package change, failed check, or unresolved concern can justify
another run. Controlled projections verify presentation and commands; they do
not prove Magic rules. Mechanical results do not establish visual clarity or a
comfortable interaction.

`StateEffectsConcessionTest` covers real concession during participant traversal
and terminal cleanup. Its focused controller regression also checks timestamp
ordering, removal fallback and nullable search overrides through public APIs on
an unstarted table, including bounded concurrent reads while overrides change.

## Native headless checks

Build the separate test executable once:

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
$driver = 'mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.dll'
dotnet $driver --smoke-presentation --profile '<fresh-profile>'
```

The test flags belong to `Mana.Table.Tests`, not the shipped `ManaTable.exe`.
Choose an explicit fresh `--profile` for every independent journey.

- `--smoke-presentation`: pure presentation, card-reader, scope, focus and geometry regressions.
- `--smoke-headless-presentation`: production `TableApplication` composition and pointer input over controlled snapshots.
- `--smoke-headless-priority-flow`: authored stack, responder, recipient and library-choice handoffs.
- `--smoke-headless-clutter`: controlled crowded-field representation and reported effect targets.

These modes create no game window or graphics device. They exercise the same
application and typed-frame composition used by the real window. Preserve their
result/trace JSON when diagnosing a failure; submitted drawing data does not
prove that a physical window is clear or correctly clipped.

The existing presentation run includes `DecisionDockPresentationChecks` and the
scoped authored `DecisionFlowLabChecks`. The production headless run includes
`DecisionDockInteractionChecks`: an actual stop-removal gesture clears only that
saved stop, dispatches an authorized Auto pass and leaves another player's
required choice unanswered. Reading a current card must pause both Auto and its
visible status. `decision-flow.v1` supplies the corresponding window-review
sequence; authored replies and passing checks do not supply rules or UX proof.

The pending-effect journey in `--smoke-headless-presentation` distinguishes a
captured `+3` counter addition/three damage from a current total of twenty, keeps
unknown damage unknown, preserves the context on the stack, and checks read-only
source inspection and exact target-key/prompt submission. These use controlled
snapshots, not the rules engine.

The source-only actual-result checks reuse these modes rather than adding a
separate runner. `ImpactFeedbackChecks` in `--smoke-presentation` covers explicit
damage recipients, damage versus life loss, activity-ID deduplication, public
departures, concealment, anonymous recipients, older payloads and JSON round trips.
`EffectTransitionChecks` also composes the actual application to distinguish a
`+2 Charge` increment from six settled counters and unchanged current 6/7. It
checks receipt collision handling, blocked marker gaps, reduced motion and expiry.
`--smoke-headless-presentation` invokes `ImpactFeedbackChecks.RunPresentation`
for recipient-attached damage/life results, death between observations and the
absence of routine success/public-outcome panels. Grouped-recipient checks cover
uniform per-copy amounts, exact subsets, different amounts, repeated hits,
latest-burst replacement and offscreen focused recipients. These assertions protect
state and drawing relationships; they do not establish visual clarity.

## Real Forge coverage

Java API, rules and synchronization regressions live in
[`forge-api/src/test/java/forge/api`](../../forge-api/src/test/java/forge/api).
The Maven reactor builds the adapter and its required engine modules. For a
focused selection, retain `-Dsurefire.failIfNoSpecifiedTests=false` so dependency
modules without that test do not fail:

```powershell
mvn -pl forge-api -am verify '-Dtest=ToothyLeavesBattlefieldTest,SyncTransportTest' '-Dsurefire.failIfNoSpecifiedTests=false'
```

`QuietYieldTest` reproduces explicit passing with an affordable Lightning Bolt
through upkeep, the normal draw and empty combat, including another seat
choosing no attackers. It also checks interruption by draw triggers, effect
draws, land plays, actual combat and queued required choices. The native
`--smoke-headless-presentation` journey clicks the real Pass for now control,
observes other seats without answering them, submits the next same-token pass,
and leaves a new stack response unanswered. Client checks cover older activity
payloads and conservative defaults. These establish command behavior, not human
approval of the presentation.

`PendingEffectTest` exercises All Will Be One with Chandra entering at three
loyalty, then adding one to reach four, and a nonloyalty counter case. Projection
checks cover unknown amounts, known zero, divided totals and concealed sources.
Its prevention regression casts Chandra normally into All Will Be One while an
opponent controls Urza's Armor: the captured trigger and pending stack report
base three, then the real engine reports actual two damage and life `40 → 38`,
including the other seats' public projections. `MatchActivityTest` covers the
typed event payload and visibility boundaries separately. Run this focused
selection when modifying the base/actual contract:

```powershell
mvn -pl forge-api -am verify '-Dtest=PendingEffectTest,MatchActivityTest' '-Dsurefire.failIfNoSpecifiedTests=false'
```

The corresponding native lab cases remain authored; they cannot establish these
rules results or replace this real-engine regression.

Native real-engine journeys use the bundled Java runtime, the actual adapter
protocol, and matching card resources:

```powershell
dotnet $driver --smoke-headless-engine --engine-resources '<package>/engine' --engine-jar '<verified-current.jar>' --profile '<fresh-profile>'
```

`--smoke-headless-engine` operates a real Commander opening, land/commander actions
and concession through production input without a GPU. Startup, workshop adapter
and multiplayer modes cover their own boundaries; choose them when affected.
Their entry points are in [Program.cs](../../mana-native/Mana.Table.Tests/Program.cs).

`--smoke-lobby-three-player` stops opening replies when any seat first observes
`MAIN1`, then waits for all three private views to agree on the playing game,
turn, active seat and usable priority owner. A transient phase label alone is
not readiness. Its existing 45-second opening deadline retains the latest views
on failure; it does not pass the first main phase while gathering them.

Use an immutable engine JAR whose source/hash is known. Rebuilding
`forge-api/target/forge-engine.jar` does not refresh an existing package. Do not
replace a JAR or package while another process uses it. A controlled UI fixture
cannot substitute for a real engine regression.

## Experience clarity and recovery

Use [Experience review](Mana-Table-Experience-Review.md) to open the shared,
versioned Experience Lab cases and review actual screens and transitions.
The lab uses authored projections, cached art and an isolated profile. It does
not run Magic rules or mutate real decks.

The source-only `damage-results.v1` case follows a surviving creature's reported
three damage, another damage event with a public departure between observations,
then a pending base-three player target and authored actual-two result at 38 life.
Its final pair makes two Bears take one damage each and merge with an already
damaged third Bear; the shared face must show one per-copy amount and two-of-three
membership rather than an invented two-damage hit.
`counter-damage.v1` retains the separate captured increment versus current loyalty
journey. Review their actual target gesture, pending state, impact and settled
current values at small and large windows, with realistic artwork and reduced
motion. Confirm that increment receipts leave names, counter totals and P/T
uncovered, and that a crowded field does not become overlapping transient labels.
Record graphical and human review for the exact candidate; these new journeys
remain pending human review and release verification until that evidence exists.

The equipment journey checks its attachment border at the destination pixel
density with antialiasing coverage, alongside exact current stats, occurrence
relationships and scoped pointer targets. A thin logical stroke need not contain
an opaque texel of its original RGB color after MSAA. These checks establish
rendering and input behavior, not perceived visual clarity.

For a relevant graphical regression, use the same test driver, for example:

```powershell
dotnet $driver --smoke-card-reading --size 1000x740 --profile '<fresh-profile>'
```

Other focused modes include `--smoke-priority-flow`,
`--smoke-cluttered-changes`, `--smoke-recovery`, `--smoke-state-clarity`,
`--smoke-allocation`, and `--smoke-deck-experience`. Consult the
[native guide](../../mana-native/README.md#focused-verification) and driver rather
than running every mode for every change.

For layout/input changes, inspect the smallest supported physical window and a
larger window, including affected text-size and motion settings. Look at actual
`-window.png` captures and perform the relevant clicks, not only the design
surface. Preserve the observed problem and manual follow-up; test totals,
screenshots, and timing measurements are not human acceptance.

For gesture changes, retain the actual before, held, release and result states.
Exercise cancellation arriving with release, and changes to the exact action key,
permission or visibility during the gesture. Slow background observations must
not flicker controls or retarget a held press or armed intent. Check cold and
warm image caches when image loading affects the journey. Use the relevant
existing harness and record human feedback separately from these assertions.

## Profiles and artifacts

The normal app profile is `%LOCALAPPDATA%\ManaTableNative`. Development and test
runs use `--profile <fresh-directory>`; never use a player's installed profile
or production installation for lifecycle tests.

Retain the exact source revision, package/build metadata and engine hash with
reproduction steps. A dirty candidate also needs the reviewed assembly hash,
base commit and retained diff identity; the base commit alone cannot identify
the tested binary. Useful artifacts include:

- `native-error.log`, `client.log`, and `engine.log` in the selected profile;
- the journey's result, observation or semantic-trace JSON;
- physical-window captures or a recording for visual/input failures;
- Maven Surefire reports and engine decision traces for Java failures.

Keep profiles, caches, packages and test output out of commits. Inspect the first
failure before rerunning, weakening an assertion, or adding a retry.

## Packaged verification

Use a fresh extracted native package or a newly built matching preview, as
described in [Releases](Mana-Table-Releases.md). Check `SOURCE.txt`, `build.json`
and the engine hash before attributing a result to a revision.

Real-engine test modes accept `--engine-resources <package>/engine` and an
optional explicitly verified `--engine-jar`. The production executable accepts
its documented app options, not test harness flags.

Installer and updater lifecycle scripts use separate smoke application
identities and profiles. Follow the [release guide](Mana-Table-Releases.md) for
the applicable installer, managed migration and installed-update checks.
Fixture downloads do not establish real release bandwidth, and a command-line
patch reconstruction does not establish installed restart behavior.

## CI and review

Keep a PR in draft until its final head passes the applicable required checks:

| Check | Responsibility |
| --- | --- |
| `desktop` in [Mana Table](../../.github/workflows/mana-table.yml) | Lightweight repository tools, documentation and CI contracts; the historical check name does not denote a second client |
| `Verify and package Windows x64` in [native Windows release](../../.github/workflows/mana-table-native-release.yml) | Shipping native package and its required client, engine, UI and installer/update evidence |
| `Test with Java 17` and `Test with Java 21` in [Test build](../../.github/workflows/test-build.yaml) | Engine/API reactor verification on both JDKs |
| Applicable [iOS compatibility gate](../../.github/workflows/ios-compat-gate.yml) | Upstream iOS/MobiVM compatibility when affected |

The explicit workflow steps and `mana-native/ci/native-ui.ps1 -ListOnly` define
automatic coverage. A local mode's existence does not mean CI runs it. The
extended inventory remains available for targeted investigation; it is not the
default local checklist.

Record the exact tested head and completed workflow links. Earlier-commit
success, an uploaded intermediate engine artifact, or an unpublished release
draft does not establish success for the final source. Native publication checks
the merged master revision and matching artifact identity again. Human review
remains separate from these automated gates.
