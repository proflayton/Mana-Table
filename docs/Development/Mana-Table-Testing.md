# Testing Mana Table

The Windows x64 MonoGame client is the current player app. Run Maven and .NET
commands from the repository root. Native development needs JDK 17, Maven and
the .NET 9 SDK; CI also uses Node 24 for the shared engine tests. Electron is a
separate client: run its npm commands from `forge-desktop` after `npm ci`.
See the [native development guide](../../mana-native/README.md) for a working
client/engine bundle and isolated source runs.

| Check | Command | What it covers |
| --- | --- | --- |
| Java compilation, style, tests and engine JAR | `mvn -pl forge-api -am verify` (root) | Adapter and required upstream modules |
| Full Java test reactor | `mvn -U -B clean test` (root, once with JDK 17 and once with JDK 21) | The separate `Test build` workflow; broader than the focused adapter reactor |
| Native client logic | `dotnet run --project mana-native/Mana.Client.Tests/Mana.Client.Tests.csproj -c Release` | Graphics-free client, contract, persistence and updater regressions |
| Native UI test build | `dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release` | Builds the separate Windows GPU/input test executable; does not run its journeys |
| Maintained documentation | `node forge-desktop/scripts/check-docs.cjs` (root), or `npm run check:docs` in `forge-desktop` | Local links and heading fragments; does not verify that prose, commands or feature claims are correct |
| JavaScript syntax | `npm run check` | Host, renderer, tooling and test sources |
| Fast unit tests | `npm run test:unit` | Phase guidance, external deck URL rules, runtime resolution |
| Real-engine integration | `npm run test:engine` | Persistence/revisions, matches, Commander, multiplayer, presets, response availability |
| UI smoke | `npm run test:smoke` | Cold startup, renderer reload, deck workflow, casting, match lifecycle, conditional response skipping |
| Complete UI suite | `npm run test:ui` | All `tests/*.spec.cjs` interaction scenarios |
| Reusable encounter suite | `npm run test:encounters` | Shared automated scenarios, participant handoff, notes and failure artifacts |

The npm rows exercise Electron or shared JavaScript services, not the native UI.
`npm test` retains its existing meaning: the Playwright UI suite. It does not
include the Node engine or unit suites. `test:engine` runs files serially because
each starts a Java process with up to 2 GB of heap. The UI suite also uses one
worker. These are integration tests with real resource scans; allow minutes for
the larger suites and keep their timeouts meaningful. Do not replace a JAR while
another build or test uses it; use verified immutable copies for overlapping runs.

Native journeys use `--profile <fresh-directory>`; `FORGE_USER_DATA` applies to
Electron. Native real-engine journeys also need `--engine-resources <package>/engine`
and may use `--engine-jar <verified-matching-source.jar>`. See
[native packaging](Mana-Table-Releases.md#build-a-native-installer-locally) to build
the bundled resources. Controlled presentation fixtures do not prove Forge rules.

Native test binaries are Windows GUI programs. Replace quoted angle-bracket
placeholders below with actual paths. The `.exe` examples describe the test
arguments; in PowerShell, wait for the process and check its exit code.
For example, after building the test project:

```powershell
$test = (Resolve-Path 'mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe').Path
$nativeProfile = Join-Path $env:TEMP ('mana-onboarding-' + [guid]::NewGuid().ToString('N'))
$run = Start-Process -FilePath $test -ArgumentList @('--smoke-onboarding', '--profile', "`"$nativeProfile`"", '--size', '1280x720') -WindowStyle Hidden -PassThru -Wait
if ($run.ExitCode -ne 0) { throw "Native onboarding failed: $($run.ExitCode)" }
if (-not (Test-Path (Join-Path $nativeProfile 'onboarding-results.json'))) { throw 'Missing onboarding results' }
```

Keep the profile, results, diagnostics and captures on failure. Use a new profile
for the next journey; a successful process exit without its expected result file
is not sufficient evidence.

Match pointer tests should follow the visible interaction: approach the hand
from the table's bottom edge and hover an exposed card strip; approach a covered
battlefield card from the playmat so the fan opens a gap. Keyboard focus is also
a supported way to lift a card. Direct center clicks can hit an overlapping card;
keep real hit testing enabled instead of bypassing it with forced clicks.

## Run a focused regression

### Experience clarity and recovery

`Mana.Client.Tests` covers stale-state command blocking, uncertain acknowledgements,
fresh observation recovery, separate public chooser/priority roles, anonymous public
characteristics, and version-scoped recommendation decisions. Java
`GameplayClarityProjectionTest` and `PartnerChoiceExperienceTest` check every seat's
public projection and actual Toothy/Pir choices, accepted searches and declined searches.
`DeckExperienceTest` covers bundled legality, immutable tested lists/comparisons and
latest recommendation decisions without changing playable content.

After building `Mana.Table.Tests`, run these modes with fresh profiles at
`1000x740` and `1540x980`:

- `--smoke-recovery`: unreadable file recovery, larger text persistence, reconnect,
  eliminated-player watching and host/guest departure from an errored table.
- `--smoke-state-clarity`: named legal recipients, scoped input revocation, public
  counters and anonymous stats, chooser/controller attribution and actual outcomes.
  It changes text size through Settings and pages nine public counter types at 130%.
- `--smoke-deck-experience --engine-resources <bundle>/engine --engine-jar forge-api/target/forge-engine.jar`:
  real persisted Keep/Reopen/Dismiss, saved test lists and read-only comparison after editing.
  It also drives whole-list source confirmations at 130% using a controlled external
  preview and proves that reviewing or canceling leaves the real saved deck untouched.
  Source application and stale-token behavior have separate offline Java coverage;
  this native journey performs no live Archidekt HTTP or source application.

The new modes are part of the native Windows gate. Their result files are
`recovery-results.json`, `state-clarity-results.json` and `deck-experience-results.json`.
The existing deck-tuning regression separately checks a real AI game and an early
concession. Free-form test questions require the player's observations; aggregate
counts do not automatically answer turn-specific questions.

Recovery, state clarity and full-battlefield captures ending in `-window.png`
save the composited backbuffer at the actual viewport dimensions, including its
scaling and letterboxing. Keep the existing design-surface captures as layout
evidence. Inspect both when changing text or density. Full-battlefield coverage
retains all fifty named occurrences and inspects Alloy Myr among them. Optional
`--smoke-full-battlefield --real-art` preloads real card artwork through the normal
art source before this journey; it requires network access and is separate from
the deterministic CI fixture. Its current characteristics are controlled test
data, not an assertion about the cards' printed rules.

### Native first session

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-onboarding --profile '<fresh-test-profile>' --size 1280x720
```

The first-session regression uses production input and hit testing with a
deterministic engine. It checks welcome routing without implicit deck writes or
network actions, remembered menu visits, returning-profile main menus, directly
playable first tables, and optional F1 help. While the reference is open,
gameplay clicks, shortcuts, text entry and Auto must leave the decision unchanged;
closing the guide preserves Auto and Hold. Run at 1280x720 and 1200x900 and review
the captures and `onboarding-results.json`. Other native setup scenarios bypass
the menu so they continue to exercise their intended controls.

`--smoke-context-guidance --profile <isolated-profile> --size 1280x720` drives
real native gestures for hand/commander plays, abilities, targets, attacks,
blocks and Hold. It verifies that hints do not intercept controls or pause Auto,
retire only after successful use, persist on relaunch, and reset through Settings
and F1. Rejected commands and invalid drags do not teach a control. Hidden,
revoked and opponent cards cannot acquire a false play hint. Cue bounds must
preserve authoritative selection instructions. Repeat at 1200x900 and review
`context-guidance-results.json` plus the captures. Hand gestures approach the
exposed original hand strip after entrance motion, using production hit testing.

`--smoke-loading --profile <isolated-profile> --size 1280x720` gates each engine
request to verify real startup stages, immediate deck acknowledgment, prevention
of duplicate actions, contextual handoff, inline autosave, error/retry and
uninterrupted table polling, replies and Auto. Repeat at 1200x900 and review
`loading-results.json` plus the captures. `--smoke-engine-startup` with
`--engine-resources <package>/engine` exercises real Java startup failure using an
isolated invalid JAR, repairs the copy and retries the same adapter. It verifies
healthy process reuse, retained undo history and idempotent disposal.

### Native equipment

`--smoke-equipment --profile <isolated-profile> --size 1000x740` checks Equipment
and creature badges at all four seats, multiple attachments, exact hover
connections, inspection, re-equipping between identical creatures and removal.
Repeat at `1540x980`; both native CI cases use 130% text and reduced motion.
Inspect `equipment-results.json` and captures `01`–`07`, including their physical
`-window.png` versions. The journey checks connector endpoints, re-equipping,
removal and 50-per-seat density. Grouping must retain attachment differences and
every occurrence. Compact attached creatures reuse their existing readable P/T
chip with a gold border and brown backing; other measured badges fit their card
footprint. Native
fixture results establish rendering and input behavior rather than Forge equip rules.

Java `EquipmentSnapshotTest` drives real equip, re-equip and sacrifice choices
and checks every seat's relationship projection. `AttachmentProjectionTest`
covers opaque occurrence IDs, face-down redaction and hidden-zone revocation.
`LootCombatRegressionTest` separately exercises real blocking with the two Loot
creatures against ordinary, deathtouch and double-strike attackers, checking life,
survival and marked damage instead of deriving outcomes from UI labels.

### Native ability auto-pass

`--smoke-priority-abilities --profile <isolated-profile> --size 1280x720` drives
the contextual pass-until-my-next-turn control through native hit testing. It
checks independent abilities on one card, visible Undo between priority windows,
manual activation, Hold, and restoration after an engine expiry snapshot. Repeat
at 1200x900 and review `priority-ability-results.json` and captures 01–05.
`Mana.Client.Tests` checks serialized Undo during an in-flight pass and prevents
stale snapshots from restoring a preference. `--smoke-auto-dock` retains the
existing dock and Hold regression.

The Java `AbilityAutoPassTest` uses real four-human-seat games with Arid Mesa.
It verifies that a setting does not consume priority or activate the source,
survives three opponents' turns, expires before the next own-turn priority,
allows Undo without a local prompt, preserves land/spell and other ability stops,
and permits manual activation of an ignored Mesa. `PriorityAbilityPassShould`
covers exact instance and ability identity, conservative partial scans, extra
turns, source/control changes and game cleanup. Run these with
`AutomaticPriorityShould`, `EngineApiTest` and the normal rules regressions.

### Native lobby and deck confirmation

`--smoke-lobby-ai-seats` checks the host's open/AI seat controls, compatible AI deck
selection, occupied-human protection and read-only guest presentation. Run at
`1000x740` and `1540x980` with fresh profiles; it uses 130% text. Inspect
`lobby-ai-seat-results.json` and captures `01`–`06`, including their physical
`-window.png` versions. Java and the shared `sync-lobby.test.cjs` scenario
separately establish host/table authorization, joining around AI seats, preserving
human readiness, three-human/one-AI game startup and AI retention on a rematch.

`--smoke-lobby-three-player` chooses 3 players before hosting, joins two real
friends, expands to four seats, removes only the last open seat, readies again
and starts the three-player game with private hand projections. Run both sizes
above; this journey also uses 130% text. Inspect `lobby-three-player-results.json`
and captures `01`–`06` with their physical-window versions. The shared Node
scenario additionally checks stale/missing table IDs, guest authority, occupied
seat protection and a usable first main phase for the three-human game.

Electron `network-lobby.spec.cjs` uses two desktop windows and a third real engine
client. Normal host controls add/remove AI, change its deck before and after
human readiness, preserve three human seats, and start the four-player table.
It checks guest AI labels with no management controls, rejects a guest mutation,
and captures the host at both supported sizes. This file runs in desktop smoke CI.
Its separate three-human scenario starts with a four-seat lobby, follows the
visible Total seats guidance to remove the last open seat, verifies preserved
human decks and reset readiness, and starts a real three-player game without AI.

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-lobby --engine-resources '<package>/engine' --profile '<fresh-test-profile>' --size 1280x720
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

Opening decisions also permit concessions. `PregameConcessionTest` exercises
departure of the starting-player chooser, the first player during mulligans, and
players handling Leyline or Gemstone Caverns opening decisions, then checks that
the remaining players reach their first main phase. It also covers a departed
Chancellor reveal viewer and verifies that terminal opening-choice cancellation
releases the game thread.
`pregame-concession.test.cjs` repeats the lifecycle through local, TCP and mixed
connections: one decision owner concedes, three guests concede in succession,
a guest leaves its seat, results clear every prompt, and the same clients return
and start a rematch with fresh readiness and decision scopes. Run it with
`node --test tests/pregame-concession.test.cjs` from `forge-desktop`.
The native AI smoke check separately covers a sole human conceding during opening
decisions, which ends the game instead of continuing with only AI players.

### Native deck workshop

Build `Mana.Table.Tests` as below, then run each mode with its own fresh profile:

```powershell
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-workshop-adapter --engine-resources '<package>/engine' --engine-jar forge-api/target/forge-engine.jar --profile '<adapter-test-profile>'
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-workshop --engine-resources '<package>/engine' --engine-jar forge-api/target/forge-engine.jar --profile '<ui-test-profile>' --size 1280x720
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-deck-tuning --engine-resources '<package>/engine' --engine-jar forge-api/target/forge-engine.jar --profile '<tuning-test-profile>' --size 1280x900
```

The adapter mode uses real catalog and persisted documents to cover combined
filters, pagination, alternate faces, create/edit, atomic rejection, stale
revision/document guards, section moves, undo/redo, singleton and color-identity
validation, exact-printing duplication, and export/import round trips.
It writes `workshop-adapter-results.json`.

The adapter also exercises durable playable versions, metadata concurrency,
Considering exclusion, actual opening-hand sampling, a real two-player test and
concession, notes, restore, and engine-restart persistence. Preparing opponents
must retain the current editor revision and undo history. The tuning UI mode
drives Build/Test/Results and returns from a real game to the same deck workspace;
its evidence is `deck-tuning-ui-results.json` and native captures.

`DeckWorkshopTest` uses offline Archidekt fixtures to check category and printing
mapping, three-way merges and conflicts, stale previews, no-op checks, and journal
recovery. `DeckPlaytestsTest` checks immutable records, interrupted games, frozen
hand samples, and persistence failure. Observed game counters come from self-only
event aggregates, independently of the capped activity feed; tests distinguish
actual draws from tutors and actual casts from copied spells. Live provider
availability is checked separately and is not a deterministic CI dependency.

The UI mode drives production pointer hit testing and search text handling:
build a legal deck from an empty draft, adjust counts, move cards, undo/redo,
filter by leader identity/type/mana, reject old search results, copy to clipboard,
rename, duplicate, reopen, return from AI setup, and page through a full precon.
Copy list stays disabled on empty drafts and after removing the last card;
Undo restores copying, while populated copies retain their quantities and sections.
Validation issues arrive with each edit: count problems, duplicate copies and
off-color cards appear inline and clear after correction or undo. Assertions use
engine-projected printing IDs and rendered warning observations; the removed
review dialog is never needed to discover a problem. Back to setup returns to
the lobby, and scenarios open confirmation explicitly when selecting a table deck.
Quantity/name dialog values enter the same production actions through the test
port. It records `workshop-ui-results.json` and UI captures with deck/catalog
snapshots. Review at 1280x720 and 1200x900; cached art is optional. Run the commander
scenario below to cover readiness and actual match setup after workshop changes.

### Native deck import and commander selection

`DeckValidationTest` covers structured issues alongside unchanged Forge validity
and the legacy problem message, including copy limits across printings, commander
identity and pairing, size rules, and exceptions granted by card rules.

`DeckImportTest` loads the real card resources and covers common text export
conventions, annotated rows, section boundaries, exact printing/foil requests,
the full 100-card Toph export, and line diagnostics without partial saves.
Run `mvn -pl forge-api -am -Dtest=DeckImportTest -Dsurefire.failIfNoSpecifiedTests=false test`.
The native `--smoke-deck-import` uses the same import path as file and clipboard
input, checks the complete list and automatic commander, opens the saved list
through real controls, rejects a bad row without replacing it, and relaunches
the adapter and client to check persistence. It writes `deck-import-results.json`
and captures to its isolated profile. Pass `--engine-resources <package>/engine`
and `--engine-jar <rebuilt-jar>` when testing a newly built adapter.

`DeckCommandersTest` runs with Maven `verify` and covers atomic section moves,
replacement, valid/invalid partnerships, Backgrounds, duplicates, sideboard cards,
clearing, stale revisions and undo. The native `--smoke-deck-building` mode uses
real imported lists and pointer clicks to set/replace commanders, reopen saved
decks, create partners, ready the same deck for multiplayer, invalidate readiness
when editing, and start a four-player AI game with the exact command zone.

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-deck-building --engine-resources '<package>/engine' --engine-jar forge-api/target/forge-engine.jar --profile '<test-profile>'
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
mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.exe --smoke-cleanup --engine-resources '<package>/engine' --engine-jar forge-api/target/forge-engine.jar --profile '<test-profile>'
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

`--smoke-commander-tax --profile <isolated-profile> --size 1000x740` reviews
independent partner taxes at all four seats, unavailable casts, hover/inspection,
updated tax and a fresh game's cleared indicators. Repeat at 1540x980 and review
`commander-tax-results.json` and captures 01–05. The `commander-tax` presentation
fixture provides the same table. Java `CommanderTaxTest` casts a commander three
times and destroys it twice through real four-seat decisions, checking taxes on
the stack, battlefield and command zone for every viewer. `EngineApiTest` also
covers partner counts, owner versus controller, and concealed cards.

The reading, current-state and compact-selection journeys are separate so that
each has a meaningful bounded deadline. Run all three at both `1000x740` and
`1540x980`:

| Mode | Result file | Journey deadline | Coverage |
| --- | --- | --- | --- |
| `--smoke-card-reading` | `reading-results.json` | 50 seconds | Enlarged cards, original hand-strip access, real click/drag edges, anonymous and identical revealed faces, library faces without choice indices, acknowledgement and visibility revocation |
| `--smoke-card-states` | `card-states-results.json` | 30 seconds | Current counters/keywords, airborne hover, losing flying, phasing out/in and the same displayed occurrence returning to its table pose |
| `--smoke-compact-selection` | `compact-selection-results.json` | 30 seconds | Selection/deselection collapse, neighboring readers, stationary suppression and slow hover handoff through real input |

The `card-states` fixture includes counter stacks, granted combat abilities and a
phased-out Teferi's Isle at every seat. State captures include `06-card-states`,
`07-current-state`, `08-states-removed`, `09-phased-out-land` and `10-phased-in-land`.
These are controlled visual states; the renderer consumes the same fields supplied
by Forge, but the fixture does not establish the cards' actual rules outcomes.
`PhasingSnapshotTest` exercises real four-seat Forge snapshots through the Isle's
first phase-out, all three opponents' turns and its return and untap on turn five.
The native release workflow requires commander-tax and all three journeys at both
sizes before publishing an installer. Use the workflow's current suite list as
the executable inventory rather than copying an old total check count.

`Mana.Table.Tests.exe --smoke-full-battlefield --profile <isolated-profile> --size 1000x740`
checks the complete battlefield through actual native pointer input and GPU
captures. Repeat at 1540x980; both sizes are required by the native release gate.
It checks ten creatures per seat, reading and inspecting an opponent's tenth
creature, and a scoped action on the local tenth creature. Twenty mixed permanents
per seat cover distinct public states, animated lands in the creature cluster,
and anonymous face-down cards without inferred characteristics. Fifty identical
tokens per seat must each be painted and expose a real physical pointer area,
including after enabling reduced motion through Settings. A separate fifty-card
fixture uses distinct names at every seat to catch overlap between many families.
Battlefield paging is absent while normal hand paging remains available. Evidence is
`full-battlefield-results.json`, captures `01`–`09`, and diagnostic captures/JSON
on failure. The controlled engine validates commands; this is a presentation
regression, with real Forge legality exercised by the shared engine suites.

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
The dense fixture should keep side-seat creature stats and every battlefield
occurrence reachable without battlefield paging. Inspect an opening-hand
real-engine capture as well as this dense fixture: opponents must read as seats
around the same surface even with few cards.

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

For native packages, the [Windows workflow](../../.github/workflows/mana-table-native-release.yml)
runs `Mana.Client.Tests`, then `Mana.Table.Tests` against the newly packaged engine
with isolated profiles. It also tests the packaged client through the real
installer lifecycle and updater handoff. `--engine-resources` selects a native
package's engine; the Electron `MANA_TEST_*` variables below do not select a native
executable. Review `build.json`, engine hash, result files and installer evidence
together. Building a package alone does not run these checks.

The remaining commands in this section apply to Electron packages.

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
Its test-only encoded image fixture exercises decoding and GPU mipmaps offline;
a deliberately missing image still checks the rules-text fallback. Run at
1000x740 and 1540x980 in isolated profiles, alongside the separate state and
compact-selection journeys described above. Results are written to
`reading-results.json`. The real Forge
`--smoke-ui-ai` verifies both a double-clicked land and a dragged land.

The native `--smoke-table-feel` switch on `Mana.Table.Tests.exe` runs a deterministic
visual sequence: opponent draw, commander cast, response window, resolution, and
turn/life feedback. It saves seven screenshots and `feel-results.json` into the
isolated `--profile` directory. This checks presentation; real Forge play is
covered separately by `--smoke-ui-ai`. `--smoke-presentation` also checks public
count cues, rematch cleanup, repeated polls, draw geometry, pointer attachment,
and reduced-motion entrance timing. Run interaction checks at both 1280x720 and
1200x900 to keep animated cards and controls usable across aspect ratios.

Keep a PR in draft until its exact final head has successful applicable checks:

| Workflow | Required evidence |
| --- | --- |
| [Mana Table native Windows release](../../.github/workflows/mana-table-native-release.yml), `Verify and package Windows x64` | Fail-closed aggregate of the clean Windows package, .NET client logic, critical native UI shards, installer lifecycle and updater tests |
| [Mana Table](../../.github/workflows/mana-table.yml), `desktop` | Fail-closed aggregate of setup/syntax/unit checks, all shared-engine shards and critical desktop journeys |
| [Test build](../../.github/workflows/test-build.yaml), `Test with Java 17` and `Test with Java 21` | Shipped engine/API reactor `verify` on Linux with both JDKs, using Xvfb; Java 17 also produces the shared engine artifact |
| [iOS compatibility gate](../../.github/workflows/ios-compat-gate.yml), `iOS/MobiVM link audit` | Java transform/link audit when Java, Maven POMs, iOS sources or its workflow change; this does not build or test an installed iOS app |

Native and desktop PR workflows run without path filters; iOS is path-filtered.
Java and desktop run on PRs and `master` pushes, avoiding duplicate feature-branch
push runs. Native push verification/publication is filtered to relevant paths on
`master`; iOS push runs also have path filters. Push applicability is separate and can differ from the PR's aggregate
diff. A missing required run, a pending run, or an earlier commit's success is not a pass.
Record the exact head, completed run links, commands and artifact/source identity.
After merging, the release workflow checks the actual `master` source again before
publication; PR results do not substitute for those master runs.
The native publication job is intentionally skipped on PRs; its Windows
verification job must still pass.

The Java 17 job uploads one source-bound engine JAR early, then continues its full
tests. Uploading that provisional artifact is not a passing Java check. Consumers
require the exact event and source checkout, verify its manifest, byte count and
SHA256, and use that same artifact for gameplay and packaging. Publication still
requires the completed Java 17/21, desktop and applicable iOS checks for `master`.
Foreign-source, missing, failed, skipped and cancelled evidence cannot count as a pass.
Automatic Java checks cover `forge-api` and every required engine dependency;
the complete upstream Forge reactor (including separate upstream clients) remains
available through manual `workflow_dispatch` with `extended=true`.

Engine and UI tests run in isolated shards rather than one long serial job. Automatic
PR/master gates run eight critical native journeys and eight critical desktop UI
tests, each across four workers. Manual `workflow_dispatch` with `extended=true`
retains all 43 native journeys and 23 desktop UI/encounter tests. The full shared
engine suite remains required across eight isolated workers. The
Node shard planner covers every discovered engine file; Commander benchmark and
pregame-concession suites run all three supported transports across their shards.
Each worker retains serial execution and its own profiles and ports. Shard
coverage and artifact provenance have focused unit checks. The performance target
is five minutes; record observed hosted timings instead of treating that target
as an achieved guarantee.

Release resources store tracked card and token scripts in the engine's existing
`cardsfolder.zip` format. This avoids installing tens of thousands of tiny files;
the complete source archive remains unchanged. The packaging fixture verifies
entry names and bytes, while packaged-engine journeys exercise real archive loading.

The desktop workflow runs the named smoke subset, not every `tests/*.spec.cjs`.
The full Electron UI suite is an additional local/release-candidate check.
Native evidence and encounters are retained for 14 days; the desktop failure
artifact is retained for 7 days. Download relevant failure artifacts promptly.
Native evidence includes result/observation/diagnostic/event JSON, captures and
logs; Java failures include Surefire XML and rules-decision traces. Preserve the
first failure and inspect its source/trace before retrying or changing a bound.

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

### Strategy review

`--smoke-analysis` starts a review without configured goals, opens the optional
question-based preferences and changes pace through actual hit testing. Run at
`1000x740` and `1540x980`; inspect findings, preferences, detail and swap captures.
Review physical `-window.png` captures as well as the design surface, including
130% text when checking readability. The journey changes an explicit turn-five
target back to Automatic and verifies shared metadata with an unchanged playable
version. Java coverage includes paired commanders, exclusion of library cards
from commander targets, and actionable findings appearing before information.
`DeckAnalysisTest` covers inferred land targets, explicit overrides, zero-cost
commanders and Limited Sideboard availability without treating Considering as
owned inventory. The Electron strategy-review scenario covers initially collapsed
preferences, direct format/pool question focus, persistence and unchanged playable
lists. These interface checks do not validate arbitrary free-text plans as rules.

The focused Java suite is `DeckAnalysisTest,DeckAnalysisStoreTest,DeckInsightsTest`;
run with `-Dsurefire.failIfNoSpecifiedTests=false` when building the reactor.
It covers self-renewing versus one-shot earthbend, missing components, Lattice
conflicts, cast versus return conditions, player-preserved cards, format/pool/copy
filters, exact land-count probabilities, durable versioned observations, corrupt
metadata preservation, and active-deck/revision checks. `EngineApiTest`,
`DeckValidationTest`, and `DeckImportTest` provide adjacent compatibility coverage.

Electron: `npx playwright test tests/deck-strategy-review.spec.cjs tests/deck-workshop.spec.cjs`.
The strategy test uses real card scripts, saves goals and review notes, preserves
a card, reopens the review, and verifies the playable list is unchanged. Renaming
retains the current version; changing a card and undoing it moves notes between
earlier and current versions. It captures 1000x740 and 1540x980 layouts. Each launch
uses a disposable profile. `npm run test:smoke` includes this suite in desktop CI.

Native test assembly: `--smoke-analysis-adapter` checks the typed contract against
a real Forge process, including profile serialization and persistence.
`--smoke-analysis --size 1000x740` (and `1540x980`) exercises the Analyze tab,
goal controls, analysis sections, and return to the builder. Supply the same
`--engine-resources`, optional `--engine-jar`, and disposable `--profile` options
as the other native smoke tests. Results are `analysis-adapter-results.json` and
`analysis-ui-results.json`; UI captures are named `analysis-*.png`.
The Windows release gate requires the analysis adapter and both UI sizes,
alongside the existing workshop, deck-tuning, and gameplay checks.

For curated interaction changes, check the actual card scripts and primary rules
sources. The Toph and earthbend registry uses Wizards' [Avatar release notes](https://magic.wizards.com/en/news/feature/avatar-the-last-airbender-release-notes)
for battlefield-only type changes and earthbend's return behavior. A repeating
line must explain how the returned object receives its next earthbend effect;
card presence alone is not a simulator proof. Add both a supported line and a
missing-prerequisite regression when extending that registry.

### App updates

The headless `Mana.Client.Tests` runner includes updater regressions with fake
HTTP responses. They cover numeric version ordering, the native stable channel,
the installer protocol manifest, constrained release URLs, exact byte counts and
SHA256 verification, cancellation, partial downloads and cached-file revalidation.
These tests make no network requests and execute no downloaded programs.

`Mana.Table.Tests.exe --smoke-updates --profile <fresh-test-profile> --size 1000x740`
uses controlled asynchronous updates and native input. Run it again at 1540x980.
It covers quiet discovery, progress and cancellation, recoverable errors,
startup-check preferences and guarded installation. The installer callback is a
fixture; these UI tests do not install an app. Evidence is `update-ui-results.json`
and native screenshots. Existing UI fixtures have no real updater client.

`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File mana-native/installer/test-update-installer.ps1 -CompilerPath <ISCC.exe> -TestDirectory <fresh-test-directory>`
tests the actual installer handoff with a separate smoke identity and benign
payload. It uses Windows PowerShell 5.1 to compile that executable fixture;
PowerShell 7 does not support `Add-Type -OutputType WindowsApplication`.
It verifies waiting for the parent, the registered destination, a second
window's mutex and restart with the same profile. A shared startup gate prevents
another window from opening while Setup replaces files. The regular 22-check installer
lifecycle test remains required. The native release workflow runs both installer
tests and both updater UI sizes before publication.

Release publication includes `installer-manifest.json`. Its version, source,
installer protocol, size and SHA256 must match the tested production installer.
The client discovers public native releases with GitHub's [releases API](https://docs.github.com/en/rest/releases/releases),
without an embedded token. It only offers releases with the supported installer
protocol. Old installers without that protocol need a manual bootstrap upgrade.
The installer uses [InitializeSetup](https://jrsoftware.org/ishelp/topic_scriptevents.htm)
to wait before file replacement. [Inno command-line options](https://jrsoftware.org/ishelp/topic_setupcmdline.htm)
disable forced application closure and system restart during this handoff.

### Native hand placements and zone actions

`--smoke-allocation --profile <isolated-profile> --size 1000x740` drives the
card-based damage and numeric assignment screen through actual pointer input.
The required release gates run at 1000x740 and 1540x980. Inspect
`allocation-ui-results.json` and captures `01`–`06`: source/current stats,
six visible recipients plus a seventh-page defender, distinct same-name cards,
public anonymous creature characteristics, marked damage, required assignment
before a defender, per-recipient remaining/max controls, typed amounts, draft
preservation through inspection and battlefield view, generic counter allocation
and planeswalker loyalty, and stale press/release rejection. Engine thresholds
are displayed as assignment requirements; the UI does not predict deaths or
derive damage from the source's power. Real-engine allocation projection and
reply validation are verified separately by the Java/API tests.

`--smoke-hand-placement --profile <isolated-profile> --size 1000x740` drives
normal pointer input through engine-projected extra hand placements, then
graveyard and exile actions. Repeat at `1540x980`; both sizes are required native
release gates. Evidence is `hand-placement-results.json` and captures 01–08.

The journey checks that a single hand click reads, deliberate double-click and
drag use the exact available source, multiple sources require a choice, and a
revoked or phased-out source cannot submit. A placement first enters the stack;
the later optional engine decision selects the hand card on resolution. Ordinary
land-play usage remains separate, and ordinary playable cards keep their normal
route. The inspector exposes an explicit alternate source.

Own graveyard, exile and command zones show available-card counts. Graveyard and
exile cards expose explicit use buttons and inspector actions; opponents receive
no action cues. The engine's chooser supplies Flashback or another actual play
description. Printed rules and card names never create these permissions in the
native client. This controlled UI fixture verifies presentation and commands;
the real Java fixtures independently verify rules, costs and response windows.
