# Mana Table

Mana Table is a Windows desktop deck workshop and playable Magic: The Gathering
table. The app uses a native MonoGame client with Forge's rules engine
and AI. It is an independent community fork, currently in beta.

## Play on Windows

[Download ManaTable Setup](https://github.com/proflayton/Mana-Table/releases/latest/download/ManaTable-Setup.exe),
run it, and open Mana Table. The installer includes Java and .NET and installs
for your Windows account. A portable Windows x64 ZIP is also available on the
[Releases page](https://github.com/proflayton/Mana-Table/releases/latest).

The current source opens on your selected deck and commander, with **Play with AI**
and **With friends** in the same Home. **Decks** opens saved lists, precons and
imports; a valid saved list can **Play** without a separate confirmation step.
Build or import a Commander list, try a bundled precon, practice opening hands,
and test changes against Forge AI. Tables default to four seats; a host can choose
two, three or four total seats. Friends can join a directly hosted table
through an invite. Everyone at the table should use the same build.
Three people can play a three-seat Commander game, or the host can add AI to
play at four seats. The host chooses each AI deck.

The builder includes autosave, Undo/Redo, categories, Considering, saved versions,
and deck analysis. Review recommendations, inspect the exact list used in an
earlier test, and compare it with your current deck. Public Archidekt decks can
be imported and manually refreshed through a preview and confirmation; local
edits are not uploaded to Archidekt. During a game, the engine handles costs,
legal actions, targeting, priority, phases and combat.

Your decks and preferences live in `%LOCALAPPDATA%\ManaTableNative`, separately
from the installation. **Updates** notifies you first; downloads start only when
you choose. Managed installations download smaller patches where possible,
prepare and check the new files in the background, then offer **Restart to
update** after you leave your game and friends table and finish your work.
Older installed builds receive one compatible installer update to enable this
layout. See the [player guide](mana-native/START-HERE.txt) for controls, network
setup, portable limitations and update instructions.

## Project guides

| I want to… | Guide |
| --- | --- |
| Play, import a deck, or invite friends | [Native player guide](mana-native/START-HERE.txt) |
| Build and run the Windows app | [Native development](mana-native/README.md) |
| Contribute a change | [Contributing](CONTRIBUTING.md) |
| Understand the app and engine boundaries | [Architecture](docs/Development/Mana-Table-Architecture.md) |
| Run checks or diagnose a regression | [Testing](docs/Development/Mana-Table-Testing.md) |
| Reproduce edge cases and review the native experience | [Experience Lab and human review](docs/Development/Mana-Table-Experience-Review.md) |
| Package a preview or understand publication | [Packaging and releases](docs/Development/Mana-Table-Releases.md) |
| Extend projected state or game commands | [Engine API contract](forge-api/README.md) |

## Build from source

The native client targets **Windows x64** and requires the **.NET 9 SDK**.
Building Forge requires **JDK 17+** and **Maven 3.8.1+**. Contributor checks use
Node 24 for repository documentation and CI tools.
Java CI covers JDK 17 and 21. Set `JAVA_HOME` to your JDK directory.

From a checkout, these commands compile and verify the engine, then build the app:

```powershell
mvn -pl forge-api -am verify
dotnet build mana-native/Mana.Table/Mana.Table.csproj -c Release
```

Running also requires the bundled engine resources and Java runtime. Follow the
[native setup guide](mana-native/README.md) for a complete first package and an
isolated development profile. Rebuilding the target JAR alone does not refresh a
previously packaged engine.

After packaging, [Launch Mana Table.cmd](Launch%20Mana%20Table.cmd) opens the build
recorded in `dist/latest-native.json`. `Launch Workshop.cmd` is a compatibility alias. These launchers do not
build a fresh clone or select a Git branch.

## Beta scope

Matches are not durable saves: closing the app ends the local game, and friends
need a running host. Direct hosting has no relay. Deck checks and bundled format
data do not establish live online format legality. Unusual card interactions
still need broader playtesting. Card art is fetched from Scryfall and cached;
the bundled card scripts provide the catalog and rules.

A release is tied to the revision recorded in its `SOURCE.txt` and `build.json`.
Current source can include changes absent from the latest installer. Open pull
requests are pending work; their features are not available just because the
source or documentation mentions them.

## Forge and licensing

Mana Table reuses [Card-Forge/forge](https://github.com/Card-Forge/forge), including
its rules, AI, card scripts and shared human controller. Existing `forge-*`
module names are retained for compatibility and upstream maintenance.

The repository is [GPL-3.0-or-later](LICENSE). Preserve Forge attribution and
runtime notices when distributing builds. Magic: The Gathering and card artwork
belong to their respective owners; this project is not affiliated with Wizards
of the Coast. The [upstream overview](docs/upstream/README.md) and
[upstream contribution notes](docs/upstream/CONTRIBUTING.md) are retained separately.
