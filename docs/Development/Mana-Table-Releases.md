# Packaging and releases

The current package script builds **Windows x64** on Windows x64. It bundles
Electron, a Java runtime, `forge-engine.jar`, and the required Forge resources.
Other platform packaging is not implemented.

## Downloads for testers

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
git tag mana-table-v0.1.0-beta.34
git push origin mana-table-v0.1.0-beta.34
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
