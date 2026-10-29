# Native Windows client development

`Mana.Table` is the shipping launcher; `Mana.Game` contains the MonoGame application and window. Forge runs
as a separate Java process and owns the rules, AI, legal actions and private
information. MonoGame is the sole Mana Table client.

This guide follows the checked-out source, which can be ahead of the public
installer. The table includes the complete battlefield layout, explicit card
readers, compact selections, card-based assignments and connection recovery.
Player counters, progress tracks and statuses share one seat display and contextual
explanations. Ordered selections show numbered positions on card faces and text
choices; lists above twelve items use the numbered gallery while every battlefield
card remains rendered. Engine effect markers use their rules text instead of
printed-card artwork.
Colored indicators around each player's icon show unspent mana with exact counts,
including during payment. Gains pulse and losses fade; reduced motion shows the
current values immediately. Pool inspection explains the current totals without
changing a game decision; step changes never clear a pool locally.
Check a package's `SOURCE.txt` and `build.json` for its exact revision.
For playing instructions, use [START-HERE.txt](START-HERE.txt).

The builder's Tokens view lists exact tokens created by the deck and offers full
printing faces. Clicking a face autosaves the choice for this deck; Use newest
removes a pin and follows the newest released printing in the bundled catalog.
Choices survive reopening, duplication and multiplayer deck selection, and use
the normal undo/redo history. Token artwork warms with the selected deck. Token
definitions and live characteristics remain authoritative engine state.

## Table interaction and state

Hover highlights a card without enlarging it. During ordinary priority, clicking
a hand, battlefield or command-zone card opens its reader; clicking that card
again, empty space or Escape dismisses it. The full original face stays visible,
with concrete engine-authorized actions, effective P/T, damage and typed counters
beside it. Actions have no previous/next pages; unusually long lists scroll.
Dragging a playable hand card onto the table also plays it. A deliberate drag
from an enlarged reader shrinks the held card above the pointer; it can be dropped
inside the former reader area after leaving its original hand slot. Corner marks
show the legal region and the held card lights on a legal drop. Escape or
right-click cancels. An available hand land's secondary Play land shortcut uses
quiet text with hover feedback.
Required selections, mana payment and combat retain their direct clicks and
compact faces. Right-click inspects a currently visible face without choosing it.

The opening Keep/Mulligan decision also uses the normal hand reader. Reading
never keeps, redraws or plays a card. Required London mulligan bottom-card clicks
continue to select the actual cards, and entering that choice closes the reader.

Live three/four-player games use the reviewed shared-table layout by default.
The Experience Lab also offers it through `--table-study`. Same-name
permanents share state-specific groups, tapped faces carry a prominent curved
arrow, and clicking a non-target player portrait opens the two-field view.
Whole table or Escape returns. Full readers keep current P/T on the card with
larger live details on the right. `--classic-table` starts the previous layout
when comparing behavior or troubleshooting.

In shared multiplayer games, battlefield cards face their seat: left/right cards
turn inward, far-seat cards face that player, and your cards stay upright. Current
stats and full card readers remain upright. Tapping is relative to the seat's
direction. Camera focus keeps the same world positions and orientations.
Two-player faces retain their existing upright framing.
Source-only until the next release: WASD and arrow keys pan the battlefield or
an unfolded card collection; Shift doubles the speed, the wheel zooms and Home
returns to the overview. Held keyboard movement and middle-drag directly track
the camera; portrait focus and returning retain their normal transitions.
Typing, reading, captured card gestures, dialogs and an inactive game window
suspend keyboard navigation. Holding a camera direction pauses automatic passing.
The same source candidate gates all physical input by window focus. Background
clicks/keys/wheel/text are ignored; deactivation cancels unfinished captures, and
regaining focus requires released buttons and keys before a new gesture acts.
Network updates, accepted commands and the existing Auto mode remain independent.
This candidate also ships a quiet priority chime after two seconds of human-held
priority, with a five-minute temporary mute on the portrait bell. It respects
global Sounds, deduplicates repeated stack-drain notices and works in the
background without accepting background input. Clicking the bell again unmutes.
Resource faces keep portrait dimensions and turn sideways when tapped; packing
reserves their turning footprint. Tapped art dims, and compact faces reserve a
corner for the tap cue before fitting names. Hover reveals a complete name when
the face is too small. `tapped-resources.v1` compares three tapped lands, one
untapped, all untapped and mixed matching lands in the Experience Lab.

Auras and Equipment in the shared layout fan beneath their exact host. Point at
an exposed artwork edge to identify it and highlight its host; click to read that
specific card. Re-equipping moves the existing occurrence. The layout reserves
the fan's footprint before packing neighboring cards, including foreign-controlled
Auras; controller lookup and engine actions retain the attachment's real seat.
`attachments.v1` in the Experience Lab covers multiple attachments, Equipment
counters, re-equipping, detachment, a tapped host and a crowded mixed battlefield.

Visible faces preload, and the selected local deck warms the artwork cache before
and during play. Reading takes precedence over background downloads; hidden
opponent cards are never requested. A first uncached image can still need network.
Source-only priority redesign: Auto waits through quiet opponent priority windows
even with an instant available. A new spell, ability, public action or combat
declaration returns an available response to the player. A refreshed prompt or
replacement quiet token cannot acknowledge that action; explicitly acting or
choosing Pass once allows routine Auto to resume. Your own main-phase plays and
required choices remain explicit. F enables full control; a bare Ctrl toggles Hold
on release. Pressing Ctrl pauses Auto while a chord is pending; Ctrl+Z does not
change Hold or optional-payment preferences.

Source-only certified Undo adapts [MTGO's Ctrl+Z](https://www.mtgo.com/getting-started/getting-started-tips-tricks)
and [Arena's contextual arrow](https://magic.wizards.com/en/news/mtg-arena/mtg-arena-state-game-january-2021-01-21) to
one supported manual mana-source tap at priority, outside payment. **Undo tap
<full source name>** sits beside the current priority action. The provider must
certify restoration of the whole activation; the client never infers it from an
untapped card. Clicking the control or pressing Ctrl+Z consumes that exact offer
once. Auto preserves this voluntary correction window until an explicit pass or
another action; there is no timed delay, modal or generic rollback. Reading and
camera movement preserve the offer. While bulk playability is unverified, dragging
an own hand/command-zone card or an already granted public-zone play source asks
for that exact current source action. Release waits for the answer; it never
claims affordability or submits a guessed cast. Restored projections discard obsolete readers,
motion and cues while keeping the camera and earlier history. Human review is
pending; the source does not enable the legacy game-action Undo or Redo.

Wait for action offers the same bounded continuation explicitly on either
player's empty-stack turn. It stops at a required attacker/blocker decision or
turn boundary; it does not promise to finish the turn. Take control stays in the
same position and holds Auto for the current turn, including while an already
sent pass reconciles. The first-land warning also protects deliberate waiting
on your own turn. These controls never enable Forge's separate end-turn yield.

Source-only bulk decisions extend that explicit consent: **Decline N + resolve
queue** declines the engine-certified equivalent optional costs while yielding
priority for the whole captured queue, including interleaved sources. It shows
source-effect and queue progress separately and stops for another choice or new
response. Supported providers may circulate only the captured priority windows
on the server; reading and **Take control** withdraw that seat's exact grant.
Manual actions first take control and require a fresh action after priority
returns. **Group blocks** selects actual creature/attacker groups, previews
one-to-one assignments through current eligibility, then stages those exact
pairs. **Confirm blocks** remains a separate gesture after review; the engine
retains authority over joint blocking restrictions. Human review is pending.

The next source iteration places turn and passing controls beside your portrait.
Six compact phase markers show recurring stops on your own turn; the current
phase light and a saved pause mark remain visually distinct. Hover describes the
exact scope, such as "Stop on my end step"; clicking a saved mark removes it.
The active opponent has a read-only actual-phase cue at their portrait. Your
controls stay in place as the turn moves; there are not four competing sets.

Auto/Full control and Hold use portrait-side icons with hover explanations;
F toggles Auto and Ctrl holds priority for the current turn. Wait for action,
Take control and the readable Pass once action live beside that same portrait.
Required targets, searches and payment remain at their source/decision surfaces.
These views consume the existing decision model and automation gate; the layout
does not grant new autoplay permissions or a guaranteed engine phase waypoint.
Human visual review of this iteration is pending; earlier preview acceptance
applies to the preview that was shown.

The battlefield groups Creatures, Support and Lands. Sparse boards keep projected
lanes; crowded boards switch together to upright cells within separate player
regions. Dense headers identify the owner, current status and exact role counts,
while life, mana and public player state remain beside the player icon. Compact
groups label their name, current power/toughness, marked damage, counters and
matching-copy count as applicable. Only copies with the same public state share
a fan; face-down cards retain anonymous labels. State markers remain compact and
inspection supplies the full details. Labels and faces target the same occurrence.

Battlefields opens a continuously scrolling, owner-selectable workspace with
card art, full names, current stats, typed counter chips and matching-copy counts.
Animated lands appear with Creatures; Other lands contains the noncreature lands.
Recent reported effects keep otherwise-equal affected and unaffected copies
separate while that effect remains in the retained history.

Decision text uses the actual rendered font and width to grow above the action
buttons. Read all opens the complete current message when it exceeds the dock;
help pages are measured to fit above their navigation controls, including at
larger text settings. Reading and paging do not answer the pending decision.

When the engine supplies a typed pending damage effect, target selection instead
shows its value beside the source and keeps the captured counter increment and
full recipient name visible. The same context continues onto the stack. For All
Will Be One, adding one loyalty gives a captured one-damage value even when that
planeswalker now has four loyalty. These are values before replacement or prevention, not promised
results; unknown amounts remain unknown. Source and counter-recipient hover
previews are read-only. Pending-value presentation shipped in 0.2.69; human review remains pending.
`counter-damage.v1` in the Experience Lab is an authored reproduction, not a rules test.

The next source iteration carries results onto the affected card or portrait:
reported damage has a brief red amount, life changes have a separate signed
amount, and counter additions briefly show their exact increment beside the
counter badge. Current P/T, marked damage, counters and life remain authoritative
after those cues expire. Ordinary successful resolutions and draws keep their
prose in History instead of opening panels over the battlefield. A failed
resolution still receives a notice. The `damage-results.v1` lab case covers
surviving and dying recipients and prevention; its checkpoints are authored.
These result cues and the crisp rendering integration are source-only until the
next verified release; human review is pending.

## Prerequisites

- Windows x64, Git, and the **.NET 9 SDK**. The projects target `net9.0` and
  `net9.0-windows`; installing a runtime alone does not provide the build tools.
- **JDK 17+** and **Maven 3.8.1+** for the Forge adapter. Set `JAVA_HOME` to the
  JDK directory. Native release packaging needs its `bin/jlink.exe`.
- Node.js **22.12+** for repository documentation and CI tools.
  Node is not needed to build or launch the MonoGame client itself.
- Inno Setup 6 only when compiling an installer. A source run or portable preview
  does not need it.
- The pinned Velopack **1.2.161** CLI requires a **.NET 10 runtime** when building
  full/delta update bundles. The application and SDK integration remain .NET 9.
  `ci/update-tools.ps1` verifies the official CLI archive before extraction.

The first Maven/NuGet restore needs network access. Players using the installer
need no separate Java or .NET installation. Non-Windows native runs and packages
are not supported.

Run the commands below from the repository root.

## Build and run with a complete engine bundle

```powershell
$env:JAVA_HOME = 'C:\path\to\jdk-17'
$env:PATH = "$env:JAVA_HOME\bin;$env:PATH"
java -version
mvn -version
dotnet --version
mvn -pl forge-api -am verify
dotnet build mana-native/Mana.Table/Mana.Table.csproj -c Release
```

The Maven command builds `forge-api/target/forge-engine.jar` and its required
modules. Android/iOS toolchains are not needed. Building `Mana.Table` alone does
not supply the Java runtime, engine JAR or card resources required at launch.

For a working-tree preview, extract a verified native portable package and use
its complete `engine` directory as the resource/runtime source. Package into a
new directory, explicitly selecting the freshly built adapter:

```powershell
$engineBundle = 'C:\path\to\extracted-package\engine'
$preview = Join-Path $PWD ('dist\native-dev-' + [guid]::NewGuid().ToString('N'))
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\mana-native\package.ps1 `
  -EngineResources $engineBundle -EngineJar .\forge-api\target\forge-engine.jar `
  -OutputDirectory $preview
if ($LASTEXITCODE -ne 0) { throw 'Native preview packaging failed.' }
$nativeProfile = Join-Path $PWD ('.tools\profiles\native-' + [guid]::NewGuid().ToString('N'))
& (Join-Path $preview 'ManaTable.exe') --profile $nativeProfile
```

The script creates a fresh self-contained .NET publish and copies the selected
JAR plus the resource/runtime bundle into the new preview. It does not change
the original package. A working-tree preview can include uncommitted changes;
retain the diff with its test evidence because `build.json` records the checkout
HEAD. Reuse old resources only when the required card scripts
and other packaged resources are unchanged; resource changes need a fresh
matching bundle. See [packaging and releases](../docs/Development/Mana-Table-Releases.md)
for a clean, committed `-Release` build and installer verification. `-Release`
rejects a dirty checkout; use the preview route while editing.

After closing the preview, you can instead run the source client against that
new preview's engine directory. Never open two clients on the same profile:

```powershell
dotnet run --project mana-native/Mana.Table/Mana.Table.csproj -c Release -- `
  --engine-resources (Join-Path $preview 'engine') --profile $nativeProfile
```

**The production client loads `forge-engine.jar` from `--engine-resources`.**
Rebuilding `forge-api/target/forge-engine.jar` does not refresh a previously
packaged bundle. After Java edits, rebuild Maven and create another fresh preview
with that JAR before launching it. The production client has no `--engine-jar`
option; the separate test executable supports that override. Restart the client
to use the changed Java engine. Do not replace a running package's files.

The bundle must contain:

```text
engine/
  forge-engine.jar
  forge-res/          # matching packaged card/resource directories
  runtime/bin/java.exe
```

Without `--engine-resources`, the client looks for `engine` beside its executable.
A lone `ManaTable.exe` or a bare `dotnet build` output cannot run a real game.
The repository launcher opens the package selected by `dist/latest-native.json`;
it does not build the app or necessarily open the source you just edited.

## Profiles and troubleshooting

Native runs default to `%LOCALAPPDATA%\ManaTableNative`. Use an explicit fresh
`--profile <directory>` for development and review runs.
Keep player profiles, caches, packages and test output out of commits.

Retain `native-error.log`, `client.log` and `engine.log` from the test profile when
present, plus the exact revision, package/JAR, window size, cards and pending
decision. A protocol mismatch requires a complete compatible client/engine build.
Native previews have `release:false` metadata and do not join the stable update
channel. Released packages check for updates without downloading application
content. Download and preparation require an explicit click; restart waits until
the game and friends' table are closed and current work is complete. Downloaded
updates are never applied automatically on ordinary startup.

The update adapter is in `Mana.Client/Updates`: it issues a sized plan, delegates
native patching and full fallback to pinned Velopack, and verifies every package
file against the published content manifest before calling it ready. ZIP layout
differences are accepted only when all file contents match. The SDK cache retains
a full base package and needs additional temporary preparation space; transfer
savings do not imply equally small disk use. `Updates` can recover a verified
prepared package after an ordinary restart without downloading it again.

Flat Inno installations use the existing protocol1 installer path for their
one-time migration; the canonical setup remains a genuine Inno bridge. Managed
and flat portable copies have different update capabilities. See the
[release guide](../docs/Development/Mana-Table-Releases.md) for layout, artifacts,
migration and isolated lifecycle verification.

## Focused verification

Start with the headless client checks and build the separate test driver:

```powershell
dotnet run --project mana-native/Mana.Client.Tests/Mana.Client.Tests.csproj -c Release
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
```

`Mana.Table.Tests.exe` owns controlled fixtures and automation; those flags and
scenario engines are not shipped in `ManaTable.exe`. It is a Windows GUI
executable, so wait for its process and inspect the real exit code:

```powershell
$driver = Join-Path $PWD 'mana-native\Mana.Table.Tests\bin\Release\net9.0-windows\Mana.Table.Tests.exe'
$case = Join-Path $PWD ('.tools\profiles\reading-' + [guid]::NewGuid().ToString('N'))
$arguments = @('--smoke-card-reading', '--profile', ('"' + $case + '"'), '--size', '1000x740')
$process = Start-Process -FilePath $driver -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Card reading failed: $($process.ExitCode). Inspect $case" }
```

Choose checks for the changed behavior:

| Check | Covers |
| --- | --- |
| `--smoke-presentation` | Headless presentation rules, signed-stride/alpha pixel transfer and motion timing |
| `--smoke-card-reading` | Compact hover, explicit reading/inspection and Auto presentation beats |
| `--smoke-card-states` | Flying/phasing, player state and mana, explicit readers, decision readability and numbered ordered choices in one bounded journey |
| `--smoke-compact-selection` | Real reveal/choice input, inspection, compact faces and exact scoped selections |
| `--smoke-full-battlefield` | All battlefield occurrences, role groups, public labels, exposure, hit regions and dense layout |
| `--smoke-equipment` | Exact attachment relationships, readable badges, hover connections and re-equipping |
| `--smoke-allocation` / `--smoke-hand-placement` | Scoped draft/assignment and real hand/source gestures |
| `--smoke-recovery` / `--smoke-state-clarity` | Interrupted connections, named decisions, public counters and larger text |
| `--smoke-deck-experience` | Reviewed recommendations, frozen test lists and whole-list source confirmations |
| `--smoke-engine-startup` / `--smoke-workshop-adapter` | Real Forge startup and native adapter behavior |
| `--smoke-lobby-ai-seats` | Host open/AI controls, compatible deck choices and guest read-only seats |
| `--smoke-lobby-three-player` | Three human seats, safe lobby resizing, readiness and real three-player startup |

Repeat affected GPU journeys at **1000x740** and **1540x980** with fresh profiles.
Real-engine modes also need `--engine-resources <bundle>/engine`; use
`--engine-jar <fresh-verified.jar>` when selecting a rebuilt JAR in the test driver.
Controlled UI checks establish rendering/input behavior, not Forge rules
correctness. Java regressions and real-engine native journeys cover the engine
boundary. Read the [testing guide](../docs/Development/Mana-Table-Testing.md)
for each mode's setup, result files, keyboard/reduced-motion checks and full CI.

Inspect both the rendered design surface and the physical window, including
letterboxing. Do not infer window readability from a 1600px design capture alone.
Node is not a native build step. Shared transport, Java and release changes
still need their appropriate checks.
Keep a PR in draft until its final commit's applicable CI has completed; local
passes and earlier-head CI do not establish readiness.

## Where to change things

| Layer | Owns |
| --- | --- |
| `Mana.Contracts` | Catalog/setup ports, deeply immutable match state, semantic commands and visibility contracts |
| `Mana.Client` | Snapshot ordering, serial commands, Auto advancement, assets and updates |
| `Mana.Planning` | Contracts-only bounded mana funding assignments and resource-scope validation shared by AI and player previews |
| `Mana.AI` | Whole-seat policy, scoped knowledge, current action mapping and automated-seat lifecycle |
| `Mana.Magic` | Card classification, projected legal actions, combat/assignment guides and Magic art |
| `Mana.Forge` | Java process/protocol lifetime and mapping between Forge JSON and typed contracts |
| `Mana.Audio` | Device-independent master/bus mix, typed cue routing and active voice lifetime |
| `Mana.Renderer` | Canvas/text/pixel upload, poses, layouts, geometry, motion and effects |
| `Mana.Game` | Native controls, setup, builder, overlays and table input/rendering; no provider dependency |
| `Mana.Table` | Shipping composition, provider lifetime, installation and update bootstrap |
| `Mana.Client.Tests` / `Mana.Table.Tests` | Headless checks and isolated native/adapter journeys |
| `forge-api` and shared Forge modules | Authoritative rules, public/private projection, actions and persistence |

Use prompt-scoped keys to act and opaque occurrence IDs to correlate visuals.
Never recover a hidden identity from a name, art, earlier snapshot or source
lookup. Keep rules and target legality in Forge. Update the player/development
guides when the behavior, setup, CLI, persisted data or packaging changes.
Player-state labels, kinds, values, progress ceilings and explanations come from
`forge-game`'s `PublicPlayerStateRegistry` through the API. Native rendering handles
the declared kinds generically, including MAX at a progress maximum. Current
coverage is positive counters, Speed, monarch, initiative and the city's blessing;
adding another mechanic requires an explicit engine descriptor and visibility
review, not a card-name rule in the renderer. Table state and card/player relations
remain separate. See the [API contract](../forge-api/README.md#playable-desktop-integration)
for the provider and regression policy.
The [architecture map](../docs/Development/Mana-Table-Architecture.md) explains the
shared session and production presentation pipeline in more detail.
