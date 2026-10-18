# Packaging and releases

Mana Table's current release is the **Windows x64 MonoGame client**. Its installer
bundles .NET, Java, the Forge engine and card data. Players download
[ManaTable-Setup.exe](https://github.com/proflayton/Mana-Table/releases/latest/download/ManaTable-Setup.exe),
open it, and the game installs and launches. No administrator account or separate
runtime installation is needed. Start menu and desktop shortcuts launch it later.

The [latest Releases page](https://github.com/proflayton/Mana-Table/releases/latest)
also offers `ManaTable-Windows-x64.zip` for portable use and a complete matching
`ManaTable-Source.zip`. Extract the whole portable ZIP before opening
`ManaTable.exe`. Other operating systems are not packaged yet.

## Automatic native releases

[Mana Table native Windows release](../../.github/workflows/mana-table-native-release.yml)
runs for native, engine or shared engine regression changes pushed to `master`.
Pull requests run the same verification without publishing. A manual Actions run
can build a selected branch; only a successful run for the current `master` can
publish a release.

The Windows job verifies Forge, runs the Node unit checks, then the complete
serial engine suite
(`node forge-desktop/scripts/test-node.cjs engine`) against that same engine JAR.
These graphics-free gameplay, Commander and multiplayer checks gate native
publication independently of the Electron UI workflow; their TAP output and
diagnostics are saved with the test evidence. Changes to the Node regression
files, their support helpers, runner or engine connection also trigger this gate.
The job then runs the native client and UI regressions against
fresh bundled resources, builds the installer, then tests install, reinstall,
startup and uninstall using a separate test identity and profile. CI uses a
software graphics driver outside the package; players receive the normal client.
Downloads are first uploaded to a draft, then published and marked Latest only
if the tested revision is still the head of `master`. Failed checks prevent
publication. Existing public release assets are never replaced by a rerun.

Versions are `0.2.<workflow run number>` and tags are `mana-native-v<version>`.
Each release contains setup, portable and complete-source downloads with SHA256
checksums. `SOURCE.txt` and `build.json` identify the exact source and engine hash.
The source ZIP is exported directly from the committed Git tree. Fresh releases
never copy a prior installation, personal decks, image caches, or test profiles.

Share this permanent link with friends:

<https://github.com/proflayton/Mana-Table/releases/latest/download/ManaTable-Setup.exe>

The installer updates the same per-user application directory. Close the game
before updating. Decks, artwork and preferences live separately in
`%LOCALAPPDATA%\ManaTableNative`; both updates and uninstall retain that directory.
Players at a multiplayer table should use the same release. In-app automatic
updating is not implemented. Builds are unsigned, so Windows can show an unknown
publisher or reputation prompt until code signing is configured.

## Build a native installer locally

Use a clean committed checkout, JDK 17+, .NET 9 and Inno Setup 6. After verifying
Forge with `mvn -pl forge-api -am verify`, run:

```powershell
./mana-native/package.ps1 -Release -Version 0.2.0 -OutputDirectory dist/ManaTable-Native-0.2.0
./mana-native/build-installer.ps1 -InputPackageDirectory dist/ManaTable-Native-0.2.0 -Version 0.2.0 -OutputDirectory dist/installer
```

`JAVA_HOME` selects the JDK used to create a fresh bundled runtime. The clean
package uses only tracked card-resource files and creates a new .NET publish
directory. `-EngineJar <verified.jar>` can select an already verified Forge build.
Installer compilation checks the package version, engine, portable ZIP, and source
archive hashes. `-CompilerPath <ISCC.exe>` selects a portable Inno Setup compiler.

`mana-native/installer/test-installer.ps1 -InputPackageDirectory <package> -Version
<version> -CompilerPath <ISCC.exe> -TestDirectory <isolated-directory>` builds an
installer with a separate smoke-test application identity. It tests installation,
reinstallation, bundled Java, client startup and uninstall while retaining a saved
profile sentinel. It never installs the production app or uses the normal profile.

## Native preview packages

`mana-native/package.ps1 -EngineResources <existing-package>/engine` publishes the
MonoGame client with bundled .NET and Java runtimes, card resources, notices and
source. By default it uses `forge-api/target/forge-engine.jar`; pass
`-EngineJar <verified-jar>` to reuse a tested adapter without replacing a local
build artifact. `-SkipBuild` uses the existing `.tools/native-publish` output.
Always choose a new `-OutputDirectory` and verify the client and engine hashes.
Native profiles live in `%LOCALAPPDATA%\ManaTableNative`; use `--profile` with an
isolated path to review the first-session welcome without changing saved decks
or play preferences. A completed native package updates `dist/latest-native.json`;
the checkout launcher prefers that manifest and falls back to the Electron beta
when no native package exists. The workflow below describes the Electron release package.

## Downloads for testers

The remaining sections document the **legacy Electron client** and its separate
`mana-table-v*` release workflow. Use the native downloads above for current play.

Published builds live on [Mana Table Releases](https://github.com/proflayton/Mana-Table/releases).
Download the **ManaTable-<version>-windows-x64.zip** asset, extract the whole folder,
and open **Mana Table.exe**. GitHub's automatically generated source archives are
for contributors, not playable builds. Java and the card library are bundled.
Everyone joining a multiplayer game should use the same release.

Close Mana Table before updating, then extract the new ZIP into a new folder.
Clean release builds keep decks, artwork, preferences, and diagnostics in
`%APPDATA%\Mana Table`, independently of the application folder. Existing local
packages with a neighboring `UserData` directory continue using that portable
profile. To move from one of those packages, close both apps and copy its
`UserData` into `%APPDATA%\Mana Table` before the first release launch; retain the
original as a backup and do not overwrite an existing profile without reviewing it.

## Prepare a build

1. Work from a reviewed commit. For a new beta number, update `forge-desktop/package.json`,
   both project version fields in its lockfile, the two version labels in
   `renderer/index.html`, and `BETA.md`. Keep player-visible changes in the guide.
2. From the repository root, run `mvn -pl forge-api -am verify` with JDK 17+.
   Packaging uses the existing JAR; it does not build Java or detect a stale JAR.
3. In `forge-desktop`, run `npm ci`, `npm run doctor`, `npm run check`,
   `npm run test:unit`, `npm run test:engine`, and the relevant UI tests. Use the
   full UI suite for a release candidate.
4. Set `JAVA_HOME` to the JDK containing `bin/jlink.exe`, then run `npm run package`.
   `FORGE_JAVA` selects an engine executable, not the JDK used by `jlink`.

For a **shareable download**, use `npm run package:release` instead. This never
reads the personal `latest-beta.json` manifest and never copies player data.
It creates a Windows ZIP, a `.zip.sha256` checksum, and `dist/latest-release.json`
containing their paths, the unpacked directory, and source revision. It leaves the
personal launcher manifest untouched. The archive step rejects profiles, test
output, diagnostics, and filesystem links inside the package.

## GitHub release workflow

The [release workflow](../../.github/workflows/mana-table-release.yml) runs on
tags named `mana-table-v<version>` and can be rerun manually for an existing tag.
The tag must exactly match `forge-desktop/package.json`. From the reviewed commit:

```sh
git tag mana-table-v0.1.0-beta.37
git push origin mana-table-v0.1.0-beta.37
```

The Windows job builds and verifies Java, runs the desktop unit and engine suites,
builds a clean ZIP, and runs the smoke suite against the packaged executable and
bundled Java. Only after those gates pass does it attach the ZIP and checksum to
a **draft** GitHub release. Beta versions are marked prereleases. Review its notes
and assets, then publish it from GitHub Releases for testers to download. Failed
tests prevent release creation; failure evidence is kept as a workflow artifact.

No signing certificate, hosted game service, or automatic update installer is
configured. Downloads are portable unsigned Windows packages. The workflow uses
GitHub's repository token; no personal access token is required. It refuses to
silently replace an existing release. See the [GitHub CLI release documentation](https://cli.github.com/manual/gh_release_create)
for draft publication and manual recovery after a failed upload.

The script stages files under ignored `.tools/desktop-beta-<timestamp>` and writes
a new `dist/ManaTable-<version>-<timestamp>/Mana Table-win32-x64` directory.
Packages include only the explicit host-file list and the renderer, so update
that list when extracting a new runtime module. The two pinned Three.js runtime
modules and their MIT license are copied into `vendor/three`; npm's development
tree and test helpers are not shipped.

## Data preservation

If `dist/latest-beta.json` exists, packaging copies `UserData/decks` and
`UserData/art` and `UserData/preferences.json` from that previous package to the
new one. Every copied deck JSON is verified with SHA-256; play preferences are
verified byte for byte. The manifest changes only after packaging and copying
succeed; older packages remain in place. It does not copy development `.data`,
live match state, or every Electron preference.

Finish saving and close the player's old app before a release handoff so edits
made after the copy do not remain only in the old package. Do not delete or patch
the old package in place. To launch an older build, run its executable directly;
its profile contains the data last saved in that build.

## Verify and hand off

Use the [packaged smoke commands](Mana-Table-Testing.md#packaged-verification).
Check the expected version and the app's bundled Java by leaving `FORGE_JAVA`
unset. `Launch Mana Table.cmd` reads the generated manifest; the legacy
`Launch Workshop.cmd` delegates to it. A source checkout has no manifest until a
package is built.

Distribute the **entire package directory**, including runtime/resource folders,
`START-HERE.md`, `SOURCE.txt`, and license notices. A local package may contain the
previous player's copied `UserData`; use `npm run package:release` for distributable
artifacts, and retain personal packages separately. Never upload player profiles,
test profiles, or cached artwork as a release artifact.

Preserve GPL and third-party notices and provide the corresponding source revision
with a distribution. `SOURCE.txt` points to this fork and the upstream engine;
record the exact commit in release notes. Creating a local package does not publish
a GitHub release, upload binaries, or modify the source branch.
