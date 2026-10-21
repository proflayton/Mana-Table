# Native Windows client development

`Mana.Table` is the MonoGame client shipped in the Windows installer. Forge runs
as a separate Java process and owns the rules, AI, legal actions and private
information. The Electron reference client has its own
[development guide](../forge-desktop/README.md).

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

## Table interaction and state

Hover highlights a card without enlarging it. During ordinary priority, clicking
a hand, battlefield or command-zone card opens its reader; clicking that card
again, empty space or Close card dismisses it. Play card and Activate ability are
explicit reader actions; dragging a playable hand card onto the table also plays
it. Required selections, mana payment and combat retain their direct clicks and
compact faces. Right-click inspects a currently visible face without choosing it.

The battlefield groups Creatures, Support and Lands. Sparse boards keep projected
lanes; crowded boards switch together to upright cells within separate player
regions. Dense headers identify the owner, current status and exact role counts,
while life, mana and public player state remain beside the player icon. Compact
groups label their name, current power/toughness, marked damage, counters and
matching-copy count as applicable. Only copies with the same public state share
a fan; face-down cards retain anonymous labels. State markers remain compact and
inspection supplies the full details. Labels and faces target the same occurrence.

Decision text uses the actual rendered font and width to grow above the action
buttons. Read all opens the complete current message when it exceeds the dock;
help pages are measured to fit above their navigation controls, including at
larger text settings. Reading and paging do not answer the pending decision.

## Prerequisites

- Windows x64, Git, and the **.NET 9 SDK**. The projects target `net9.0` and
  `net9.0-windows`; installing a runtime alone does not provide the build tools.
- **JDK 17+** and **Maven 3.8.1+** for the Forge adapter. Set `JAVA_HOME` to the
  JDK directory. Native release packaging needs its `bin/jlink.exe`.
- Node.js **22.12+** with npm for the shared Node/desktop regression suites.
  Node is not needed to build or launch the MonoGame client itself.
- Inno Setup 6 only when compiling an installer. A source run or portable preview
  does not need it.

The first Maven/NuGet restore needs network access. Players using the installer
need no separate Java or .NET installation. Non-Windows native runs and packages
are not supported.

Run the commands below from the repository root. In PowerShell, use `npm.cmd`
if local policy prevents running `npm.ps1`.

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
`--profile <directory>` for development and acceptance runs. `FORGE_USER_DATA`
belongs to Electron's runtime resolution and does not redirect the native app.
Keep player profiles, caches, packages and test output out of commits.

Retain `native-error.log`, `client.log` and `engine.log` from the test profile when
present, plus the exact revision, package/JAR, window size, cards and pending
decision. A protocol mismatch requires a complete compatible client/engine build.
Native previews have `release:false` metadata and do not join the stable update
channel. Released packages can check/download updates; installation waits until
the game and friends' table are closed and current work is complete.

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
correctness. Java regressions and shared Node tests establish the relevant real
engine behavior. Read the [testing guide](../docs/Development/Mana-Table-Testing.md)
for each mode's setup, result files, keyboard/reduced-motion checks and full CI.

Inspect both the rendered design surface and the physical window, including
letterboxing. Do not infer window readability from a 1600px design capture alone.
For MonoGame-only changes, npm is not a native build step; shared transport,
Java and release changes still need their appropriate cross-client checks.
Keep a PR in draft until its final commit's applicable CI has completed; local
passes and earlier-head CI do not establish readiness.

## Where to change things

| Layer | Owns |
| --- | --- |
| `Mana.Contracts` | Additive catalog/setup/match DTOs, scoped replies and visibility contracts |
| `Mana.Client` | Snapshot ordering, serial commands, Auto advancement, assets and updates |
| `Mana.Magic` | Card classification, projected legal actions, combat/assignment guides and Magic art |
| `Mana.Forge` | Java process/protocol lifetime and mapping between Forge JSON and typed contracts |
| `Mana.Renderer` | Canvas/text/pixel upload, poses, layouts, geometry, motion and effects |
| `Mana.Table` | Composition, native controls, setup, builder, overlays and table input/rendering |
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
shared session and the separate Electron implementation in more detail.
