# Testing Mana Table

Mana Table has one client: the Windows MonoGame app. Use the smallest check that
covers the change. Reuse the existing Java and native harnesses; do not add a
second client or rules simulator to test the shipping app.

Run commands from the repository root. Native development requires the .NET 9
SDK; engine work requires JDK 17+ and Maven 3.8.1+. Node is only for dependency-free
documentation and CI tools. See [native setup](../../mana-native/README.md) for
a complete engine/runtime/resource bundle.

## Canonical interaction coverage

The shared client check runs `InteractionContractMatrix`: named cases cover every
concrete `GameCommand` variant, `DecisionIntent` and `DecisionPurpose`, with a
closed-command/enum coverage guard, fixed-seed permission/visibility transitions, authority rejection and
required continuations. It uses production command builders and `ClientSession`
with a finite scripted authority. Its two-provider alias case verifies that
provider handles cannot be replayed as semantic commands. It is interaction-contract
coverage, not a Magic rules simulator or proof of every possible card combination.
The matrix uses no JVM, graphics device or network; it prints its measured runtime.

Real Forge goldens use the existing `canonical-interaction` TestNG group: paid
casting/payment, private partner search, Toothy's last-known counters and draws,
prepared-spell casting, pending versus prevented damage, combat allocation and persistent Speed. The
partner search retains four recipient variants. These methods already run once
in normal Java CI; grouping them adds no duplicate engine startups. The focused
command selects their classes as well as the group so upstream modules do not
start empty TestNG processes:

```powershell
mvn -pl forge-api -am test '-Dgroups=canonical-interaction' '-Dtest=SourceActionsTest,PartnerChoiceExperienceTest,ToothyLeavesBattlefieldTest,PendingEffectTest,AllocationProjectionTest,PlayerStatesExperienceTest' '-Dsurefire.failIfNoSpecifiedTests=false'
```

This is a representative real-rules lane alongside enumerated coverage of the
current interaction command variants. Neither replaces visual review of the corresponding
window journeys, nor supplies the complete authoritative Forge oracle needed to
establish another rules provider's equivalence. Record the case, seed, provider
and first differing boundary when diagnosing a failure; use existing scenario
evidence rather than starting a JVM for each generated contract row.

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

The existing `--smoke-headless-presentation` lane includes `TokenArtworkChecks`:
enter Tokens through the production builder, save/reset exact printing choices,
retain the save acknowledgement and card count, reject an old revision's hit,
and switch definitions while an obsolete gallery response is held in flight.
It runs at small and large sizes with enlarged text, without another CI process.
`DeckTokensTest` in the Java API suite covers newest-first exact definitions,
reprint image addressing, save/reopen/duplicate, undo/redo/reset, stale edits,
unchanged playable versions and actual `TokenInfo` creation with two independent
deck choices. These checks do not approve the gallery's visual clarity; inspect
real token images in the game window as well.

- `--smoke-presentation`: presentation, card-reader, scope, focus, geometry and bounded priority-batch regressions over a controlled provider.
- `--smoke-headless-presentation`: production `TableApplication` composition and pointer input over controlled snapshots.
- `--smoke-headless-priority-flow`: authored stack, responder, recipient and library-choice handoffs.
- `--smoke-headless-clutter`: controlled crowded-field representation and reported effect targets.

These modes create no game window or graphics device. They exercise the same
application and typed-frame composition used by the real window. Preserve their
result/trace JSON when diagnosing a failure; submitted drawing data does not
prove that a physical window is clear or correctly clipped.

The presentation lane also runs the existing `AdvanceRegression` cases and writes
`advance-results.json`: required decisions, visible checkpoints, stale/duplicate
observations, bounded waiting, cancellation and retired-session results. They
start no JVM or graphics device. `--smoke-advance` remains available to run those
same cases alone. Client checks cover separate Auto/quiet intent authority and
their shared batch lifecycle; no additional CI job is needed.

The same presentation lane runs `PublicActivityCursorChecks` for join/reset,
repeated observations, bounded-history gaps and shared feedback consumption.
Its production application journey runs in `--smoke-headless-presentation` and
switches the viewed seat within one game, checking that readers and retained
feedback from the previous seat are revoked. Existing board checks cover held
cards and repeated pose preparation across frame-buffer swaps.

`MatchPresentationLifecycleChecks` shares that presentation lane and verifies
baseline, viewer/rematch reset, mana/priority revocation and public departures.
The existing seat-switch application case also checks revocation immediately
after acceptance, before preparing another frame, so stale hit intents cannot
survive until the next paint.

`PresentationReadChecks`, `DecisionDraftChecks` and `DeckInteractionChecks` run
in the same presentation lane. They cover repeated frame reads without advancing
state, publication on Update, revoked or altered intents, prompt/viewer draft
resets, bounded selections/allocations and deck navigation/reader lifetimes.
The window only reads the prepared frame. Headless timing measures Update,
including frame preparation; reading the cached frame is not a new composition
or an FPS measurement. These checks supply behavior evidence, not UX acceptance.

The priority artwork-handoff case also runs in that lane. It retains the exact
priority prompt while loading owns the screen, then verifies that the same
ability control becomes available without a command when artwork settles.
Its window journey uses deterministic artwork and waits for that handoff rather
than asserting on gameplay controls behind the loading screen.

The shared client runner includes `LatestReadRegression`,
`DeckWorkspaceRegression` and `AppUpdateSessionRegression`. Controlled completion
gates cover coalesced reads, stale failures, deck revision/metadata/fingerprint
admission, delete/restore epochs, explicit review retry, notify-first discovery,
download cancellation races, prepared-release identity, blockers changing during
verification, explicit restart and cancellation cleanup on shutdown. These use
the existing runners and CI jobs; they add no JVM startup or graphics window.
Window journeys for Build/Tune/Test, printings and Updates still exercise the
production application over those owners. Passing lifecycle checks establishes
transition safety, not human visual clarity or approval.

Shared client checks also run `SourceActionSessionRegression`: delayed provider
reads, seat/source/revision revocation, explicit unsupported versus empty results,
timeouts, manual retry and shutdown use one scoped action-query lifecycle. Native
journeys consume semantic `DecisionPurpose` values; Forge prompt class names stay
inside the adapter. The real-engine opening/payment journey verifies that mapping.

Prepared-spell regressions reuse `SourceActionsTest` and the existing native
presentation lane. The real engine creates Emeritus of Ideation's copy in exile,
casts that exact spell through the source, then checks unpreparing and revoked
links. The `prepared-spell.v1` Experience Lab case provides authored four-seat
checkpoints for source reading, the actual spell face, cancellable targeting,
priority, the draw result and preparing again. Its scripted transition checks
are not a rules implementation. Review the visible prepared marker and linked
spell affordance at the small and large window sizes; passing the lifecycle
checks does not establish visual clarity or human acceptance.

The same presentation lane exercises paired-deck editing through the production
app: discover an absent named partner, add it, select the compatible pair, wait
for the persistence acknowledgment, return to Play and reopen. Delayed saves,
rejection/retry, stale responses and library-list failures retain their distinct
states. The real `DeckCommandersTest` checks discovery metadata independently of
the existing legality checks. No extra CI job or JVM per UI case is required.

`ImmutableStateChecks` traverses the entire match graph and every request/result
of the catalog, printing, deck/workshop/analysis, setup and conversation ports,
rejecting writable nested collections/properties. Provider construction buffers
and consumer casts cannot change an accepted revision or queued metadata edit.
It retains preceding match and workshop views while publishing new revisions,
checks JSON array/object round trips and distinguishes omitted metadata fields
from explicit empty clears. It runs in the existing graphics-free client lane.
The native
presentation lane inspects the emitted UI dependency closure and rejects a
provider/host reference or use of the aggregate engine by `Mana.Game`.

The existing `--smoke-headless-engine` journey also verifies the actual Forge
metadata mapping: a plan-only patch preserves roles and Considering categories,
and explicit empty collections clear the overrides without changing playable
deck contents or their version. It reuses that journey's initialized engine
before starting the same Commander game; it adds no separate JVM or CI job.

That same initialized engine also covers explicit setup association: repeated
setup preserves editor revision, undo and an active practice sample; switching
setup reports the requested document and rejects a write for the former owner at
the same numeric revision. `DeckWorkshopTest` covers those transitions with small
offline fixtures, explicit-open practice invalidation and failed/missing setup
switches preserving the current document. The existing presentation lane tests
the production ready-message validator's required `explicit-deck-setup`
capability without another process.

The production headless run includes saved-deck deletion and Undo through the
actual controls, delayed observations, restart recovery and stale-command guards.
The existing `--smoke-deck-experience` window journey covers that same lifecycle
against Forge, including restoring the original saved bytes and preserving an
unrelated open deck. Deletion requires the optional `deck-library` capability.

The existing presentation run includes `DecisionDockPresentationChecks` and the
two-player continuity checks in `SharedTableStudyChecks` and `StudyFocusChecks`.
They cross the old seven-group boundary from both viewer seats and exercise the
shipping live-table entry, required opponent targeting and battlefield inspection.
Use `two-player-table.v1` for immediate visual review of those projected states.
The same run checks explicit pan/zoom and reversal in `BoardViewChecks`, live
counter/P/T updates during movement, Escape, current player targets and stable
canonical occurrences in `StudyFocusChecks`. `DrawingFrameChecks` rejects leaking
clip scopes; `EffectTransitionChecks` protects the camera-mapped family captions
from counter receipts. No additional CI process is introduced. Review actual
motion in `four-player-table.v1` and `crowded-counters.v1`; checks cannot approve it.
The same run also includes the
scoped authored `DecisionFlowLabChecks`. The production headless run includes
`DecisionDockInteractionChecks`: an actual stop-removal gesture clears only that
saved stop, dispatches an authorized Auto pass and leaves another player's
required choice unanswered. Reading a current card pauses dispatch without
introducing a temporary priority surface. Delayed replies, fresh polls, Hold,
rejection and uncertain acknowledgements exercise the real serialized command
path. Quiet-pass controls retain their heading and position through handoffs;
required local targets preempt them immediately. The existing `--smoke-auto-dock`
window journey captures routine Auto, a real response and required blockers.
`decision-flow.v1` supplies the corresponding window-review
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

`--smoke-headless-engine` operates a real Commander opening, a land play, a paid
command-zone cast and concession through production input without a GPU. Its
same opening now takes two Commander mulligans and verifies that reading a
bottom-card candidate does not select it, its explicit reader action selects it,
and final confirmation removes it from the kept hand. Headless presentation
checks cover the same noncommitting hand reader for engine inputs and staged
card lists, including deselection and prompt replacement during a press. It
opens the commander's current source actions, drags it into play and activates
a Forest during its actual mana payment. Startup, workshop adapter
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

The existing headless presentation lane also runs `AttachmentStackChecks` over
the production shared layout. `SharedTableStudyChecks` additionally runs the
`tapped-resources.v1` checkpoints for every viewer, verifying that the same
resource dimensions turn into a different outline without moving their centers
or crossing reserved lanes. The lab case is the visual tap-recognition review.

`AttachmentStackChecks` covers
the production shared layout: exact occurrence/controller retention, exposed
pointer targets, switching and closing readers, counter-bearing Equipment,
seat-facing layouts for every viewer, upright readers from actual left/top/right
pointer gestures, independently tapped faces and rotated attachment edges,
re-equipping/detaching, nested and foreign-controlled attachments, distinct
reference/visual ID domains, malformed links, two-player views and crowded fields.
It does not start another CI process. Use `attachments.v1` for real-window review
at 1000 x 740 and a larger window, including enlarged text, motion/reduced motion
and camera focus. Agent screenshots and automated invariants do not establish
human acceptance of the fan or its small exposed targets.

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

### Capture a real saved deck workspace

The existing native driver also supports `--capture-deck-workspace`. Prepare an
isolated seed profile using the app: import and select a realistic saved deck,
set text size to 130%, and let its authorized artwork populate that profile's
`art` cache. Close the app, then copy that isolated seed to a separate profile
for each window size. Do not point this journey at a player's installed profile.
It requires a saved selected document and a complete real Forge engine bundle;
it does not create the synthetic analysis fixture used by other regressions.

After building the same driver above, run:

```powershell
dotnet $driver --capture-deck-workspace --profile '<isolated-seeded-profile-1000>' --engine-resources '<package-engine-directory>' --size 1000x740
dotnet $driver --capture-deck-workspace --profile '<isolated-seeded-profile-1540>' --engine-resources '<package-engine-directory>' --size 1540x1000
```

The journey follows actual pointer targets through Home, Build, Tune, an engine
relationship (or a card-linked finding when available), full-card reading, Test,
a draw-only sample and Home again. It saves `workspace-*-window.png` native-window
captures, matching design surfaces/state JSON and `workspace-capture-results.json`
in the chosen profile. Missing optional analysis content is not invented. It can
save a practice sample, so use a fresh copy of the seed for another capture.
Its bounded wait and accepted-pointer checks are capture safety only; inspect
the native windows and interact manually before recording human acceptance.

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

Within native verification, the installed SDK update lifecycle starts after the
immutable tools artifact is available. It can run alongside packaging. The
product's managed migration lifecycle still waits for both tools and the actual
package and verifies the real Forge-backed application. Both jobs remain required
by the Windows aggregate and upload separate failure evidence. This scheduling
change retains the full lifecycle tests; runner availability still affects time.

`test-native-ci.ps1` also runs `test-source-receipt.ps1`. These cheap fixtures
exercise production rejection of receipt-only input and smoke rejection of
missing, changed or mismatched source, version, build metadata, archive hashes,
sizes and runtime-embedded metadata. Production installers continue using the
actual source ZIP; only isolated smoke identities use its producer-bound receipt.
Both paths run in the existing native checks and installer/migration jobs.

The installer workflow has a read-only PR caller and a master-only staging
caller. The required Windows aggregate expects exactly its authorized caller to
succeed and the other to be skipped; every remaining dependency must succeed.
Repository tests execute that actual aggregate script for both contexts and
exercise release staging/promotion failures through injected API fixtures.
Private drafts do not establish a successful release or bypass final source CI.

The [manual transport benchmark](Mana-Table-Releases.md#measuring-artifact-transfers)
is outside the required aggregate and never runs on ordinary PRs or releases.
Its exact-byte checks and action timings compare wrapped versus raw transport;
they do not replace native or installer tests. Record the benchmark run and
payload identity when reporting results, separately from full-build wall time.

Record the exact tested head and completed workflow links. Earlier-commit
success, an uploaded intermediate engine artifact, or an unpublished release
draft does not establish success for the final source. Native publication checks
the merged master revision and matching artifact identity again. Human review
remains separate from these automated gates.
