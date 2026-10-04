# Testing Mana Table

Build the adapter first with `mvn -pl forge-api -am verify` from the repository
root. Run npm commands below from `forge-desktop` after `npm ci`.

| Check | Command | What it covers |
| --- | --- | --- |
| Java compilation, style, tests and engine JAR | `mvn -pl forge-api -am verify` (root) | Adapter and required upstream modules |
| JavaScript syntax | `npm run check` | Host, renderer, tooling and test sources |
| Fast unit tests | `npm run test:unit` | Phase guidance, external deck URL rules, runtime resolution |
| Real-engine integration | `npm run test:engine` | Persistence/revisions, matches, Commander, multiplayer, presets, response availability |
| UI smoke | `npm run test:smoke` | Cold startup, renderer reload, deck workflow, casting, match lifecycle, conditional response skipping |
| Complete UI suite | `npm run test:ui` | All `tests/*.spec.cjs` interaction scenarios |
| Reusable encounter suite | `npm run test:encounters` | Shared automated scenarios, participant handoff, notes and failure artifacts |

`npm test` retains its existing meaning: the Playwright UI suite. It does not
include the Node engine or unit suites. `test:engine` runs files serially because
each starts a Java process with up to 2 GB of heap. The UI suite also uses one
worker. These are integration tests with real resource scans; allow minutes for
the larger suites and keep their timeouts meaningful.

Match pointer tests should follow the visible interaction: approach the hand
from the table's bottom edge and hover an exposed card strip; approach a covered
battlefield card from the playmat so the fan opens a gap. Keyboard focus is also
a supported way to lift a card. Direct center clicks can hit an overlapping card;
keep real hit testing enabled instead of bypassing it with forced clicks.

## Run a focused regression

### Native lobby and deck confirmation

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-lobby --engine-resources <package>/engine --profile <fresh-test-profile> --size 1280x720
```

`LobbySmoke` uses production pointer hit testing, the real deck builder and four
independent Forge processes. It covers deck picking/review/cancel/confirm, disabled
starts, AI-choice preservation, separate confirmation/readiness, readiness loss
on builder entry, synchronized guest joins, host start gating, a real four-player
match, return to lobby, native invite error recovery, guest ready/edit flows and
incomplete drafts. Router forwarding is disabled, and guest connections use
loopback. All engines and saved decks use isolated profiles. Run at 1280x720 and
1200x900, and review `lobby-results.json`, the progress log and twelve captures.
The checks also cover searchable deck and AI pickers, empty search results,
isolated AI seat selection, full-list pagination and section filtering, card
previews, readiness preservation while browsing/reselecting the current deck,
and opening the exact reviewed printing in the builder after withdrawing readiness.
The social checks use native text and keyboard handling, four real engine clients,
saved display names, delivery to every seat, stale table rejection, unread state,
per-player and persisted whole-table mute, emotes, and maximum-length message
expansion. During a real main-phase decision they verify typing and sending chat
do not advance the game or change its decision scope. History survives return to
lobby and clears when leaving the table.

`--smoke-social --profile <isolated-profile>` runs the graphics-free conversation
replica regression and writes `social-results.json`. It covers stale and repeated
snapshots, old-table replies, unread counts, mutes and reused-seat identity. Java
`TableConversationTest` covers bounded immutable history, ordering, rate limits,
text/name validation, sender attribution and equal local/TCP transcripts across
reconnects. `SyncTransportTest` covers exactly-once receipts and protocol rejection.

The lifecycle check completes each seat's starting-player and keep-hand choices
before conceding guests. A separate stress run that conceded three guests in
succession during Pregame reached a Forge null-state error (`takesAction` was
null while accessing `Player.getCardsIn`). That pregame concession issue remains
unfixed; the lobby work does not change rules or concession handling. The normal
post-opening return and subsequent join are covered by the passing regression.

### Native deck workshop

Build `Mana.Table.Tests` as below, then run each mode with its own fresh profile:

```powershell
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-workshop-adapter --engine-resources <package>/engine --engine-jar forge-api/target/forge-engine.jar --profile <adapter-test-profile>
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-workshop --engine-resources <package>/engine --engine-jar forge-api/target/forge-engine.jar --profile <ui-test-profile> --size 1280x720
```

The adapter mode uses real catalog and persisted documents to cover combined
filters, pagination, alternate faces, create/edit, atomic rejection, stale
revision/document guards, section moves, undo/redo, singleton and color-identity
validation, exact-printing duplication, and export/import round trips.
It writes `workshop-adapter-results.json`.

The UI mode drives production pointer hit testing and search text handling:
build a legal deck from an empty draft, adjust counts, move cards, undo/redo,
filter by leader identity/type/mana, reject old search results, copy to clipboard,
rename, duplicate, reopen, return from AI setup, and page through a full precon.
Quantity/name dialog values enter the same production actions through the test
port. It records `workshop-ui-results.json` and three UI captures with deck/catalog
snapshots. Review at 1280x720 and 1200x900; cached art is optional. Run the commander
scenario below to cover readiness and actual match setup after workshop changes.

### Native deck import and commander selection

`DeckCommandersTest` runs with Maven `verify` and covers atomic section moves,
replacement, valid/invalid partnerships, Backgrounds, duplicates, sideboard cards,
clearing, stale revisions and undo. The native `--smoke-deck-building` mode uses
real imported lists and pointer clicks to set/replace commanders, reopen saved
decks, create partners, ready the same deck for multiplayer, invalidate readiness
when editing, and start a four-player AI game with the exact command zone.

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-deck-building --engine-resources <package>/engine --engine-jar forge-api/target/forge-engine.jar --profile <test-profile>
```

The test uses an isolated profile, disables router forwarding, and writes
`deck-building-results.json`, a progress log, and five UI captures. Card art is
optional; the offline rules-text fallback exercises the same controls.

### Game rules: hand size and cleanup

Run `mvn -pl forge-api -am -Dtest=RulesRegressionTest -Dsurefire.failIfNoSpecifiedTests=false test`
from the repository root. This suite also runs in the normal Maven `verify` check.
`RulesScenario` creates a real four-human Commander game, keeps opening hands,
and sends the production seat commands. All libraries contain Forests; the coin
toss winner chooses seat zero to start, so outcomes do not depend on shuffle or
starting-player randomness. Named card effects use Forge's real scripts and
registered-player starting permanents. Those fixtures deliberately bypass deck
construction/setup restrictions; they never mutate a running game or replace
the rules implementation.

The cases cover default seven-card cleanup, the first player's multiplayer draw,
no premature end-step discard, each seat's own cleanup, rejected Auto/Confirm/Cancel,
multiple-card selection, increased/reduced/zero/fixed limits, unlimited hands from
lands and artifacts, unlimited plus a reduction, destruction of Reliquary Tower,
and a discard trigger resolving with priority for all four players before the next
turn. Failures preserve full private seat snapshots and an action trace under
`forge-api/target/rules-regressions/<scenario>/trace.json`; these are isolated test
games. Add new focused rule scenarios here as behavior expands. This is a Forge
regression suite, not a claim of parity with a second engine.

Run the native adapter's real-game cleanup check after building the test project:

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-cleanup --engine-resources <package>/engine --engine-jar forge-api/target/forge-engine.jar --profile <test-profile>
```

It plays with three Forge AI opponents and verifies the typed selection intent,
current hand limit, Auto guard, shared card-click reply, and the final discard.
It writes `cleanup-results.json`, `cleanup-fixture.json` and `latest.json`.
The existing `--smoke-card-reading` GPU test checks that required hand selections
still work with one click while ordinary card plays require a deliberate gesture.
The native hand and player details display the authoritative `maxHandSize`
(`null` means unlimited); the renderer never enforces a separate hand-size rule.

### Native client and conformance checks

`TableChoiceRegressionTest` plays a real opening-hand Leyline choice with seven
identical cards. It verifies distinct links to the actual hand occurrences and
that only the two submitted copies enter the battlefield. `NetworkZoneSnapshotTest`
checks that library and opposing-hand cards never acquire table-choice links.
`CombatAutoRegressionTest` declares real attacks and blocks at a four-seat table,
including a reach creature blocking a flyer. It rejects Auto at blocker declaration,
checks that polling leaves that decision open, then resolves the chosen block
through combat damage. These all run in Maven `verify`.

The native `--smoke-ui-interactions` suite enables Auto during block assignment
and tests on-table choices through pointer clicks: select/deselect, duplicate names,
two seats, explicit confirmation, ordered choices, and choosing none. The headless
client suite checks presentation deadlines without wall-clock sleeps, including
new spells, attacks, repeated polls, changed prompt handles and required decisions.
`--smoke-card-reading` also verifies that a new spell remains visible before Auto
advances and that republishing its prompt cannot postpone playback indefinitely.

Player-targeting regressions run in `PlayerTargetRegressionTest` (also part of
Maven `verify`). Use the same focused command above with
`-Dtest=PlayerTargetRegressionTest`. The four-seat fixtures play Bojuka Bog through
the normal land-play/trigger flow and cover all four targets, an empty graveyard,
opponent hexproof, and targeting yourself through your own hexproof. Filled
graveyards come from real cleanup discards. Rejected protected-player and Auto
commands must preserve the pending target decision. The repeated Bog libraries
are deliberate deterministic test decks; deck legality is not the subject here.

`InputSelectTargets.canSelectPlayer` shares the actual player-click validation.
The adapter publishes those choices in `playerChoices`, so the native client can
highlight/select targets without copying targeting rules. The native
`--smoke-ui-interactions` test exercises every portrait with real pointer events,
including a forbidden target and a later update that makes that seat selectable.

The headless native checks run with
`dotnet run --project mana-native/Mana.Client.Tests/Mana.Client.Tests.csproj`.
They cover stale polls, serial commands, revoked/duplicate identities, shared
click/drag commands, immediate Auto with response/stop/inspection guards, and conformance failures
for hidden state and private-view divergence. `Mana.Conformance` deliberately
requires a full oracle from both engines; it does not treat the UI scenario
double or the Forge seat audit as a second rules engine.

### Four-player Commander engine migration benchmark

Four-player Commander is the acceptance target for interchangeable Forge and
replacement engines, with a MonoGame presentation consuming their shared contract.
Run `npm run test:benchmark` for the Forge synchronization matrix. It repeats the
same encounter with four local connections, four TCP connections, and a mixed
table. TCP uses loopback, with port forwarding disabled and isolated profiles.
Each seat uses 98 Forests and the partner commanders Anara and Gilanra, avoiding
draw-dependent setup. A guest attacks two opponents while the fourth player
observes. All players cast both commanders; both defenders assign blockers.

The shared deck/encounter specification is `scenarios/commander-four-player.json`.
Five checkpoints compare public game state across all four clients: commanders
ready, attackers assigned, each defender's blockers assigned, and post-combat.
They also compare every client's complete private projection and decision against
the authority's seat snapshot, check hidden hands/libraries, starting life, and
distinct viewers, and reconnect a TCP seat without changing its exact snapshot.
The authority audit requires `-Dmana.test=true`, is available only through the
test host's private IPC, and is never exposed by the network transport.
`test-results/commander-benchmark-*/benchmark.json` records checkpoints and combat
interactions, with the latest observed snapshots on failure. Engine logs remain
in each seat's profile. The action record is diagnostic, not a portable replay.

This is a bounded combat encounter, not yet a complete game or an engine parity
test. Its projection excludes private rules state and engine-specific decision
details. Network snapshots now include per-commander damage totals. Cross-engine
comparison requires canonical identities, controlled random outcomes, a complete
decision trace, and privileged test-only inspection at rules checkpoints.

Expand the benchmark through multiplayer mulligans and turn/priority order,
responses and stack resolution, commander tax and per-commander damage, triggers,
replacement effects, tokens/counters, and player elimination through a winner.
Use scripted decisions before introducing AI. Require explicit unsupported-feature
results while the replacement engine grows. Render the same scenario through
MonoGame separately from the headless rules comparison.

Build `mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj` for the separate
native test executable. Test drivers and scenario engines are not in the shipped
game. Run the adapter lifecycle check with
`Mana.Table.Tests.exe --smoke --profile <new-test-directory>`. It starts four bundled
engines, imports decks, hosts and joins, casts each commander, checks hidden
hands and commander-damage totals, rejects a stale decision, then tests
concessions through victory and returning to the lobby. It records
`smoke-results.json` and a renderer fixture. To capture that state using the
actual MonoGame renderer, launch with `--fixture <table-fixture.json>` and
`--capture <output.png>`. These are development switches; normal play uses
the lobby and table controls. The fixture capture does not exercise live input.

Solo Commander has two additional native checks, each requiring its own test
profile (never use your normal player profile):

- `Mana.Table.Tests.exe --smoke-ai --profile <new-test-directory>` starts a four-precon
  table, verifies selected-deck handling, concedes, then rematches against three
  AIs. It casts the human commander and advances until all three AIs have played
  lands and nonland permanents and combat has occurred. It checks private hands,
  commander-damage totals, old-session and stale-decision rejection, and switching
  back to a multiplayer lobby. Results and a table fixture are saved in the profile.
- `Mana.Table.Tests.exe --smoke-ui-ai --profile <new-test-directory>` runs MonoGame
  against the real Forge adapter. It injects pointer presses and releases through
  the normal hit-testing path to import a precon, select AI decks, start a game,
  keep an opening hand, click a land to play it, concede, return, rematch, drag a
  land onto the battlefield, open/close settings, and switch to the
  friends setup. It uses the normal concession confirmation dialog. Screenshots
  and `ui-results.json` are saved in the profile. Artwork may download as in normal play.

`Mana.Table.Tests.exe --smoke-presentation --profile <new-test-directory>` checks Auto
permission and interruption, legal combat pairs, rotated hit testing, hand layout,
stable identity animation, reduced motion, visibility removal and settings
persistence. It also writes dense battlefield, combat, library-search, color-choice
and allocation fixtures. Render those with `--fixture` and `--capture`; add
`--size 1280x720` to exercise window scaling. These fixtures are synthetic visuals,
separate from the real-engine lifecycle tests. Optional artist assets live in
`Mana.Table/Assets`; visual checks should also work when those files are absent.

The `card-states` presentation fixture includes counter stacks and granted combat
abilities at all four seats. The card-reading smoke also exercises anonymous
revealed faces, identical revealed names, library faces with no choice index,
selection versus acknowledgement, visibility revocation, airborne hover and
removal of flying/counters. It captures `05-revealed-hover`, `06-card-states`,
`07-current-state` and `08-states-removed`. These are synthetic visual states;
the renderer consumes the same counters and combat keywords supplied by Forge.

`Mana.Table.Tests.exe --smoke-ui-interactions --profile <new-test-directory>` exercises
the actual MonoGame pointer path against a deterministic `ICardEngine` test double.
It checks legal/illegal attack and block drops, fresh-handle assignment removal,
crowded-rank selection, block validation, stable stack inspection, stale presses,
and the result screen. Screenshots and `interaction-results.json` are saved.
The double models explicit projections, not Magic rules. The separate real-Forge
AI check also assigns and recalls a human attacker using the same command builder
used by the combat UI. Presentation checks verify damage/tap feedback deduplication,
visibility removal, new-game reset, and rotated arrow endpoints.
Perspective checks cover all four seats, tapped/untapped projected corners and
pointer polygons, projection round trips, depth scaling, local-seat drop bounds,
and airborne motion with effects following the displayed pose. Run pointer tests
at both 1280x720 and a letterboxed size such as 1200x900 after camera/layout edits.
The dense fixture should keep side-seat creature stats visible and row paging
accessible. Inspect an opening-hand real-engine capture as well as this dense
fixture: opponents must read as seats around the same surface even with few cards.

When running from a build output instead of a package, add
`--engine-resources <package/engine>`; tests may also use `--engine-jar <new-adapter.jar>`. In PowerShell, use `Start-Process -Wait
-PassThru -WindowStyle Hidden` and check its `ExitCode`; invoking a Windows GUI executable directly
does not reliably wait for the test to finish.

See [reusable encounters](Mana-Table-Encounters.md) to run the same setup as an
automated regression or a guided human playtest, with optional video recording.

```sh
npm run test:ui -- tests/hand-gestures.spec.cjs tests/hand-readability.spec.cjs
npm run test:ui -- tests/card-selection.spec.cjs tests/land-play.spec.cjs
node --test tests/commander.test.cjs
```

| Change | Useful existing coverage |
| --- | --- |
| Startup/readiness and engine failures | `startup.spec.cjs`, `engine-client.test.cjs` |
| Deck editing/import/export, discovery and review | `engine.test.cjs`, `desktop.spec.cjs`, `deck-workshop.spec.cjs`, `presets.*` |
| Prompts, turn guidance and stale actions | `match.*`, `priority.spec.cjs`, `card-selection.spec.cjs`, `land-play.spec.cjs` |
| Commander/multiplayer | `commander.*`, `multiplayer.*` |
| Hand and 2D fallback layout | `hand-gestures.spec.cjs`, `hand-readability.spec.cjs`, `battlefield-fit.spec.cjs`, `multiplayer.spec.cjs` |
| Combat | `network-combat.test.cjs` (host/guest three-player split attacks and blocks); `network-combat.spec.cjs` (real desktop battlefield targeting and confirmation); `combat.spec.cjs` (AI click, drag and removal); `table-combat-input.spec.cjs` (legality, keyboard, stale selection) |
| Card visibility/inspection | `card-preview.spec.cjs`, `card-faces.spec.cjs`, `library-search.spec.cjs`, `top-library.test.cjs`, `top-library.spec.cjs` |
| Animation/event correlation | `animation-feedback.spec.cjs` |
| 3D continuity, idle rendering and graphics fallback | `table-scene.spec.cjs` (real WebGL and engine); animation-feedback retains the 2D fallback check |
| World-space seats, camera focus and crowded ranks | `table-world.spec.cjs` (two, four and six seats, projected hit targets, no engine action from camera/paging) |
| Anchored controls and independent panel scrolling | `rail-layout.spec.cjs`, `match-scroll.spec.cjs` |
| Lifted-card pixels, retained hand nodes, phase layout stability and turn cues | `table-stability.spec.cjs` (real WebGL plus a copied turn-cue presentation fixture) |
| Casting, cancelling, the stack and revealed hand portraits | `casting-reveal.spec.cjs` |
| Source-aware artifact and manual land mana choices | `mana-choice.spec.cjs`, `network-mana-choice.test.cjs` |

## Profiles and artifacts

`top-library.test.cjs` plays Elven Chorus at both host and guest seats. It checks
private top-card access, legal creature casting, drawing a new top card, cleanup
discards, and losing access after Naturalize removes Chorus. The desktop encounter
checks inspection in both 3D and 2D, and that clicking an unplayable top land does
not advance the game. `match-scroll.spec.cjs` uses inert display snapshots to
check scroll retention during polls, decision updates, and unrelated board changes.

`network-mana-choice.test.cjs` manually pays for spells with Yavimaya Coast at
both host and guest seats. It checks that polling and actions remain responsive
during the ability dialog, cancellation returns to payment without tapping the
land, stale payment actions are rejected, and colored/colorless abilities finish
casting with the correct life change.

Shared helpers live in `tests/support/engine.cjs` and `tests/support/desktop.cjs`.
`tests/support/network-combat.cjs` shares a real three-player Commander encounter
between engine and desktop tests. It plays lands and two partner commanders,
splits attacks across both opponents, retargets and recalls attackers, checks
each defender's legal pairs, adds/removes blocks, verifies every seat receives
assignments before confirmation, and reaches the next main phase. The desktop
version uses pointer clicks and the battlefield confirmation controls at two
window sizes. It is part of the packaged release smoke suite.

They create unique profiles under ignored `test-results/`, respect configured
Java, and never use your normal `.data` or packaged `UserData`. UI tests disable
remote artwork by default and launch hidden Electron windows. The face-image
test uses a stubbed image service to exercise the cache without remote requests.

The UI helper supports both source and packaged builds, removes
`ELECTRON_RUN_AS_NODE`, and disables background throttling. Test bodies retain
their own assertions and close the application in `finally`. Engine tests wait
for ready/error with a bounded timeout and close their child process in `finally`.

For pixel assertions in hidden packaged windows, keep compositor presentation
active with `beginFrameSubscription` and use native `capturePage`, as in
`table-stability.spec.cjs`. CDP capture can stall, and native capture without
fresh presentation can return an earlier frame. The window remains hidden;
closing it ends the subscription. Normalize images to CSS coordinates before
sampling pixels. Source combat screenshots use CDP after arrow geometry settles.

Failures can leave `engine.log`, Playwright error context, and screenshots in
`test-results`. Use a separate output directory when comparing runs:

```sh
npm run test:ui -- --output=test-results/my-change --reporter=line tests/match.spec.cjs
```

## Packaged verification

The desktop test launcher seeds **Full control** in its isolated profile so
encounters retain deterministic response pauses. Pass `preferences: null` to
`launchDesktop` to exercise production defaults, or supply a preference object.
`response-skip.spec.cjs` checks Auto across real turns and verifies cancellation,
phase stops, temporary holds, stale prompts, and persistence. Its opening-land
scenario must advance automatically after the only available play, then wait
for the next turn's land play. `response-skip.test.cjs` also protects affordable
instants and commanders from an automatic pass. `preferences.test.cjs`
covers validation, disk round trips, and corrupt-file recovery.

After `npm run package`, run a fresh test profile against the manifest's build:

```powershell
$env:MANA_TEST_PACKAGED = '1'
npm run test:smoke
Remove-Item Env:MANA_TEST_PACKAGED
```

All UI tests use the shared launcher and support packaged mode. Use
`MANA_TEST_EXECUTABLE` for a particular executable instead; packaged mode takes
precedence when both variables are set. Neither mode uses the player's profile.
For `npm run package:release`, read `dist/latest-release.json` and set
`MANA_TEST_EXECUTABLE` to its `directory` plus `executable`; leave
`MANA_TEST_PACKAGED` unset. The release workflow does this automatically.
`network-mulligan.test.cjs` exercises host and guest mulligans at two- and
three-player tables, including the free multiplayer redraw, repeated mulligans,
bottom-card selection/undo, hidden opponent hands, and progression into turn one.
`network-start.spec.cjs` uses two actual desktop clients to submit selected decks
with Ready, verify start blockers, keep prompt controls stable across polls,
mulligan and choose a bottom card through real clicks, enter turn one, close both
clients, and reopen the host's saved profile. Routine tests leave router forwarding off.
Visual-review screenshots are captured in source runs; packaged tests skip those
captures because the hidden-window compositor can stall, while retaining layout
and interaction assertions.
The optional `node scripts/smoke-package.cjs` also captures preview images and
checks bundled resources; unlike the normal UI suite, it permits artwork fetches.

## CI and review

The native `--smoke-card-reading` scenario exercises pointer input for large
in-place card enlargement, single-occurrence rendering, animated return, fan
browsing, safe single clicks, deliberate double-clicks, interleaved
cards, changed decisions, cancelled drags, one-click required selections,
right-click inspection, and concealed cards. It also checks populated GPU mip
levels and multisample targets, and captures art-backed and offline text previews.
Run at 1280x720 and 1200x900 with a copy of the normal art cache in its isolated
profile. Results are written to `reading-results.json`. The real Forge
`--smoke-ui-ai` verifies both a double-clicked land and a dragged land.

The native `--smoke-table-feel` switch on `Mana.Table.Tests.exe` runs a deterministic
visual sequence: opponent draw, commander cast, response window, resolution, and
turn/life feedback. It saves seven screenshots and `feel-results.json` into the
isolated `--profile` directory. This checks presentation; real Forge play is
covered separately by `--smoke-ui-ai`. `--smoke-presentation` also checks public
count cues, rematch cleanup, repeated polls, draw geometry, pointer attachment,
and reduced-motion entrance timing. Run interaction checks at both 1280x720 and
1200x900 to keep animated cards and controls usable across aspect ratios.

The `Mana Table` workflow builds the focused Java reactor on Windows, checks
JavaScript, runs unit and real-engine tests, then the UI smoke and encounter suites.
Encounter artifacts are retained for 14 days. The full UI suite is a local/release
check. Inherited Forge workflows remain separate and may
also run. A local successful command does not mean a hosted CI run has completed.

Add regression tests for changed behavior, especially hidden information,
persistence, stale prompts, and click/drag boundaries. Reuse existing scenarios
when possible. Do not replace engine-backed assertions with UI-only mocks for
rules behavior. For a flaky failure, retain the profile/log and reproduction
before changing the assertion or adding a retry.

`--smoke-advance` runs 30 graphics-free checks of shared decision batching:
required inputs, saved stops, visual checkpoints, cancellation, duplicate/stale
observations, bounded waits and session retirement. `--smoke-auto-dock` compares
actual dock pixels across empty priority/resolving/new-phase frames, then uses a
real pointer press/release across a prompt change to verify Hold. Run both native
UI sizes (1280x720 and 1200x900). Results: `advance-results.json` and
`auto-dock-results.json`. The real four-player `--smoke-ai` and `--smoke-cleanup`
also use the shared advance runner.

`--smoke-action-feedback` checks ability hover/inspector/command/chooser behavior,
a targeted removal telegraph, an exile-and-token result and reviewable details.
It produces six actual MonoGame captures and `action-feedback-results.json`.
`ActionFeedbackRegressionTest` casts real Ravenform at Grizzly Bears in a four-seat
Forge game, verifies the exact target before resolution, then the actual exile,
1/1 Bird token and retained spell explanation. Presentation tests cover new-token
cues, no duplicate feedback, rematch reset and retargeting pauses. These tests
use isolated profiles; existing card art is reused without generated artwork.
