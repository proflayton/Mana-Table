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

## AI candidate evidence

An AI candidate also needs the five gates in
[AI release evaluation](Mana-Table-Testing.md#ai-release-evaluation). This is
additional evidence for the exact candidate, alongside required source CI,
native packaging, UI, installer and update checks. A passing short smoke game
does not prove a whole game can finish or that an opponent is enjoyable.

The manual `--evaluate-ai-release` profile runs nine controlled native policy
journeys and four natural games using the varied deck manifests in cyclic seat
rotations. It reuses one JVM. Each game has a 160-turn/180-second ceiling; a
ceiling is inconclusive, never a replacement for the engine's pre-cleanup
outcome. Preserve the first failure or cutoff. Do not retry until a sample
passes, lower its target, or change the budget to relabel it green.
The flexible-output journey specifically checks that the unmodified policy
chooses red from a blue/red land for an actual blue/red bill, then pays the
remaining blue and observes the same commander resolve. A solver's hypothetical
assignment alone is insufficient evidence of the produced color.
The contribution journey also requires actual convoke progress through zero,
current Done and the same resolved commander; thousands of accepted card clicks
without changing that progress cannot certify completion.
The partner-counter journey pays for both Pir and Toothy through the normal
policies, then checks the next actual draw, Toothy's trigger and Pir's replacement
against the same creature's exact two counters and 3/3 result. Harmless opposing
commanders keep that controlled continuation independent of combat targeting.

Retain `ai-release-evaluation.json`, the full source revision, engine artifact
manifest, exact runtime hashes and private trace/replay locations. Record the
test-only fixture launcher hash separately from the shipping engine hash.
The runner requires the exact source release package's resources and matching
packaged AI, shared planning, contract and adapter assemblies; retain the package/source receipt,
resource path and build-metadata hash. Previously installed resources do not
provide full candidate-release proof. The fixture launcher is test-only and
must not be included in the production engine. CI runs the controlled goldens
in its existing initialized JVM; the four-game manual profile does not add a CI
job or replace any required source or package checks.

The aggregate's `automatedReady` is not approval: `releaseApproved` remains false
and `humanGameplayReview.status` remains pending. Record a review of the tested
candidate, actual gameplay journeys, findings and their disposition before
promoting AI behavior. A pending review cannot be counted as a passed gate.
Human review is not inferred from win rate, equal targeting or an automated
report. Keep private decisions, scope handles and replays out of public release
downloads and public issue attachments.

## Automatic native releases

[Mana Table native Windows release](../../.github/workflows/mana-table-native-release.yml)
runs for native, engine or shared engine regression changes pushed to `master`.
Pull requests run the same verification without publishing. A manual Actions run
can build a selected branch; only a successful run for the current `master` can
publish a release.

The native workflow consumes the exact-source Java engine artifact, packages
the MonoGame app once, and runs its required native logic, headless real-engine,
UI and installer/update checks. The existing `--smoke-headless-engine` journey
exercises the shipping C# adapter against that packaged engine. Repository
documentation and CI contracts belong to the lightweight `desktop` check;
there is no second client or Node engine test pipeline.

Harness compilation and the four engine-independent presentation checks run
before waiting for the Java artifact. Their elapsed times are logged separately.
The real-engine headless check still waits for the completed package and uses
its Forge resources; the harness is hashed only after these checks pass.

Pinned tools have separate graphics, installer and update artifacts. UI shards
download only Mesa, the installer protocol check only Inno, and the installed SDK
check only Velopack. Migration and production packaging receive Inno and Velopack;
the windowed installer lifecycle receives Mesa and Inno. Every consumer retains
the pinned archive hash checks, and Inno retains its publisher verification.
`tools.ps1 -DownloadOnly` accepts `-Graphics` and `-Installer` selectors; omitting
both preserves the existing combined download behavior for local callers.

Downstream UI, installer and SDK jobs check out only their required scripts and
probe projects using directory cones. Root and ancestor files remain available
for repository configuration and native packaging helpers. The package producer
retains a complete checkout for tracked resources and the full source archive.
The consumers still download and verify the immutable runtime, source or receipt,
engine and harness inputs required by their existing journeys; smaller checkouts
do not replace those inputs with local builds or cached product binaries.

The isolated installed SDK update lifecycle needs only the immutable tools
artifact and runs alongside packaging. Product migration waits for both tools and
the package, retaining its real Forge-backed health check. Both complete
lifecycles remain required by the Windows aggregate, with separate evidence
artifacts; parallel scheduling does not relax publication checks.

Publication waits for the **same master source SHA** to pass Java 17/21,
`desktop`, the native verification aggregate and applicable iOS compatibility
checks. The [read-only prerequisite helper](../../scripts/mana-table/wait-release-ci.cjs)
checks those completed runs. It never merges, reruns or cancels CI.

The [production installer workflow](../../.github/workflows/mana-table-native-installer.yml)
has separate callers: PRs and non-master builds have read-only permissions;
trusted master builds can upload verified downloads into a private release draft
while UI and migration checks finish. Both callers use the same installer build.
PR builds retain their downloadable Actions artifact and never create a release.

Final publication downloads only a small staging receipt, then checks the exact
remote asset inventory, sizes, SHA256 digests, source/version/run identity and
current master. It also requires the same-source Java, repository, native and
applicable iOS checks. Only then is the draft made public and marked Latest.
The publication job explicitly requires a successful Windows aggregate while
allowing the intentional skip of the inactive installer caller upstream.
The [release helper](../../scripts/mana-table/release-assets.cjs) checks master
before staging and immediately before promotion. Failed gates leave a private
draft; an advanced master cannot publish it. Existing public assets are never
replaced by a rerun. Staging is preparation, not a published or updater-visible
release.

Runtime and source ZIPs are transferred as individual raw Actions artifacts;
small manifests and the UI harness retain their own artifacts. Every consumer
checks the runtime bytes and exact source identity before use. Installer smoke
jobs receive a source receipt instead of downloading the full source ZIP.
The producer issues that receipt only after hashing the actual archive, binding
its name, size and SHA256 to the version, commit, build metadata and runtime ZIP.
Only isolated smoke identities can use this receipt. The production installer
still downloads and hashes the actual source ZIP, and publication still binds the
complete corresponding source download. Existing real-archive callers remain
supported; `sourceArchiveBytes` is an additive release metadata field used by the
receipt path.

### Release timing target

The target is **five minutes from merge into `master` to a public native release
discoverable through Updates**. Record both merge-to-public and workflow-start-to-
public durations, along with the source SHA, run and attempt. Keep queueing,
checkout, transfer and required verification time in the end-to-end measurement.
Use completed step timings and artifact sizes to identify the last dependency;
a faster individual job can move that dependency to another job.

The target is not a measured guarantee. Record cache/runner conditions where
known and compare multiple runs before claiming a repeatable improvement. All
required source, native, lifecycle and publication checks remain mandatory.

### Measuring artifact transfers

The optional `benchmark_transport` manual-dispatch input runs only a transport
benchmark. It skips all normal build/release jobs and cannot stage or publish a
release. For a candidate branch:

```powershell
gh workflow run mana-table-native-release.yml --ref <candidate-branch> -f benchmark_transport=true
```

The benchmark transfers the pinned public 0.2.89 runtime ZIP through wrapped and
raw uploads/downloads, with two samples per mode in balanced order. Every output
must have the exact original length, SHA256 and filename. Its JSON report records
completed action durations and run/attempt/source identity; it excludes initial
input download, queueing and verification. Network, cache and runner differences
still affect measurements. This is a transport comparison, not a controlled
end-to-end release speed claim or a new app validation run.

For manual dispatch, only current `master` may publish. Dispatch cannot recover
the original push range, so it conservatively requires an exact-source iOS push
run even when a native-only change would not have triggered one. A missing run
blocks publication; do not bypass the gate or substitute a previous green SHA.

Versions are `0.2.<workflow run number>` and tags are `mana-native-v<version>`.
Each release contains setup, portable and complete-source downloads with SHA256
checksums. `SOURCE.txt` and `build.json` identify the exact source and engine hash.
The source ZIP is exported directly from the committed Git tree. Fresh releases
never copy a prior installation, personal decks, image caches, or test profiles.

Share this permanent link with friends:

<https://github.com/proflayton/Mana-Table/releases/latest/download/ManaTable-Setup.exe>

The managed application lives in `%LOCALAPPDATA%\ManaTable`, with versioned
contents under `current`, an `Update.exe` helper, a stable root launcher and a
package cache. The former flat Inno installation normally lives in
`%LOCALAPPDATA%\Programs\Mana Table`. Decks, artwork and preferences remain in
`%LOCALAPPDATA%\ManaTableNative`; updates and uninstall retain that separate profile.
Players at a multiplayer table should use the same release. Packaged release
builds offer an **Updates** panel and a saved **Check on startup** preference.
Checks notify first and fetch metadata only. Application downloads start only
after **Download update**. Downloading and **Preparing** can continue while the
app is used. **Restart to update** stays blocked during a game, connected friends
table, pending action, failed deck save or unfinished input. The app checks the
prepared files again and exits normally so Java can shut down before replacement.
An ordinary launch never applies a waiting update automatically. A completed SDK
restart is acknowledged after the app's startup succeeds.

Velopack **1.2.161** supplies package caching, native binary patches, full-package
fallback and apply/restart. The selected strategy determines the displayed
download size. Missing bases, unsuitable chains or failed patches use a complete
package; the UI identifies that fallback. A small transfer still needs room for
a full cached base, reconstructed package and temporary expanded files. Sizes
depend on the actual versions and runtime/resource changes, not a fixed saving.

The stable channel accepts public, non-draft, non-prerelease `mana-native-v*`
tags. Each release's `releases.win-x64.json` describes only its attached full and
delta packages. `update-manifest.json` separately binds package ID, version,
source revision, full-package identity and every ZIP file entry's size/SHA256.
The adapter validates reconstructed and cached contents against that contract;
it does not require identical ZIP compression/layout. Unexpected, missing or
modified files fail verification. A verified cached target can become Ready
again after an ordinary launch without another download.

### Compatible migration and portable copies

Existing updater clients select the highest `installer-manifest.json` and
understand **installer protocol1**. Keep that manifest and canonical
`ManaTable-Setup.exe` compatible. The canonical executable remains genuine Inno
Setup; it embeds the genuine Velopack setup under a separate name. Renaming a
Velopack setup to masquerade as protocol1 would break old clients' verified
handoff arguments. They need one complete bridge update before receiving patches.

The bridge installs into the separate managed root and checks its exact app,
build and real Forge catalog before retiring the old Inno installation. It keeps
the same explicit profile and recreates shortcuts after old uninstall. Failed
first-install health retains the old installation. This migration gate differs
from the SDK's recovery when an update cannot replace a locked application;
there is no claim of automatic rollback for arbitrary later application failures.

The shipping composition assembly remains `ManaTable.dll`; the application and
window are in `Mana.Game.dll`. Both participate in package content hashes,
installer/update validation and migration health evidence. A launcher-only
hash cannot establish which game UI was installed. The entry point remains
`ManaTable.exe`, and existing profiles require no migration for this split.

The source candidate also packages the device-independent mixer in
`Mana.Audio.dll`. Its bytes are declared in `build.json` and checked when a CI
consumer restores the exact-source package. Fixtures reject a missing, changed
or undeclared audio assembly; the source receipt also binds its entry through
the complete runtime archive hash.

Managed portable layouts can use the SDK update path from their writable root.
Older flat portable ZIPs keep the manual download/extract route; extract a newer
complete ZIP into a separate directory or install the app. Development builds
link to releases. Updater discovery selects the compatible native channel;
the generic GitHub Latest endpoint is not its discovery channel.
Builds are unsigned, so Windows can show an unknown publisher or reputation
prompt until code signing is configured.

## Build a native installer locally

Use a clean committed Windows checkout, JDK 17, the .NET 9 SDK, a .NET 10 runtime
for the pinned Velopack CLI, and Inno Setup 6.
CI pins its JDK/SDK, Inno compiler and software graphics driver in the native
workflow; consult that file when matching CI. From the repository root, first
verify Forge with `mvn -pl forge-api -am verify`, then run:

```powershell
./mana-native/package.ps1 -Release -Version 0.2.0 -OutputDirectory dist/ManaTable-Native-0.2.0
$vpk = ./mana-native/ci/update-tools.ps1 -Directory .tools/update-tools
./mana-native/build-update-bundles.ps1 -InputPackageDirectory dist/ManaTable-Native-0.2.0 -Version 0.2.0 -OutputDirectory dist/update-bundles -VpkDll $vpk
./mana-native/build-installer.ps1 -InputPackageDirectory dist/ManaTable-Native-0.2.0 -Version 0.2.0 -OutputDirectory dist/installer -ManagedSetup dist/update-bundles/ManaTable-win-x64-Setup.exe
```

`JAVA_HOME` selects the JDK used to create a fresh bundled runtime. The clean
package uses only tracked card-resource files and creates a new .NET publish
directory. Packaging consumes an existing JAR; it does not build Java or prove
that the JAR matches the checkout. `-EngineJar <verified.jar>` selects another
JAR, so retain its source SHA and SHA256 and require matching engine source before
calling the result an exact-source release. `build.json` records the checkout SHA
and JAR hash; that alone is not independent proof of the JAR's source.
Installer compilation checks the package version, engine, portable ZIP, and source
archive hashes. `-CompilerPath <ISCC.exe>` selects a portable Inno Setup compiler.
The bridge additionally requires the genuine setup's sibling bundle report and
content manifest to match that same input build and source. For subsequent
bundles, pass a previous published `-PreviousFullPackage` and its independently
verified `-PreviousFullSha256`; without a base the bundle contains a full update.
The bundle builder pins CLI/SDK versions, creates full/delta artifacts and their
content proof, and disables installer shortcut creation until the migration
health gate succeeds. Change runtime versions deliberately so unrelated releases
do not accidentally replace unchanged dependencies.
These commands create local artifacts, not a public GitHub release and not a
completed CI gate. Review the [required checks](Mana-Table-Testing.md#ci-and-review)
and installer tests before a handoff. Use a new output directory/versioned path
for each candidate and retain earlier packages.

`mana-native/installer/test-installer.ps1 -InputPackageDirectory <package> -Version
<version> -CompilerPath <ISCC.exe> -TestDirectory <isolated-directory>` builds an
installer with a separate smoke-test application identity. It tests installation,
reinstallation, bundled Java, client startup and uninstall while retaining a saved
profile sentinel. It never installs the production app or uses the normal profile.

`mana-native/updates/test-installed-updates.ps1 -TestDirectory <fresh-directory>
-VpkDll <pinned-vpk.dll>` exercises a genuine installed SDK test executable with a
real Windows locator and package cache. Its sequence covers delta preparation
and restart, cancellation of the owned native patch helper, damaged-patch full
fallback, retaining a prepared update on ordinary launch, explicit restart and
the same profile. These controlled fixture sizes are not production bandwidth
predictions. Product migration has a separate
`mana-native/installer/test-managed-migration.ps1` test: it uses unique Inno/SDK
identities, the real native executable and bundled Forge health query.

## Verify a published native handoff

Use the release tag's commit and its completed master workflow run as the source
of truth. A green PR run, an uploaded Actions artifact or an unpublished draft
does not establish that the public download contains the final merged source.

- Download Setup, portable ZIP, complete-source ZIP, their checksum files and
  `installer-manifest.json` from that release. Verify all three SHA256 values.
- Extract the portable ZIP into a new directory. Check `build.json` version,
  `sourceRevision` and production flag against the tag and workflow. Compare its
  engine hash with `engine/forge-engine.jar` and its recorded client hashes with
  the corresponding DLLs. Check `SOURCE.txt` identifies the same revision.
- Verify the update manifest's version, source, architecture, installer protocol,
  installer byte count and SHA256 against the downloaded Setup executable.
  Check the source ZIP matches the committed release tree.
- Verify the managed release index and `update-manifest.json` agree with the
  actual full/delta assets and source. Download and validate the full package's
  complete file-entry contract. Keep the canonical protocol1 setup check as
  well; new metadata does not replace compatibility for existing clients.
- Start the extracted app with a fresh isolated `--profile`, wait for the real
  process exit and retain startup evidence. Run the installer lifecycle and
  update-handoff tests with their separate smoke identity before handoff; do not
  use a player's installed app as the test destination.
- Verify updater discovery, preparation and explicit restart from a genuine
  installed or supported portable base. Test a failed patch's complete-package
  fallback and retain the same-profile health acknowledgement. A standalone
  command-line delta reconstruction does not prove the installed update flow.

Retain the release URL, exact source SHA, workflow run, download hashes and test
evidence together. If master advances during publication, verify which revision
actually became public before sharing the permanent Latest link.

## Native preview packages

`mana-native/package.ps1 -EngineResources <existing-package>/engine` publishes the
MonoGame client with bundled .NET and Java runtimes, card resources, notices and
source. By default it uses `forge-api/target/forge-engine.jar`; pass
`-EngineJar <verified-jar>` to reuse a tested adapter without replacing a local
build artifact. Keep the JAR's actual source/hash with the preview evidence when
it differs from the client checkout. `-SkipBuild` uses the existing
`.tools/native-publish` output; it does not verify that output matches current source.
Always choose a new `-OutputDirectory` and verify the client and engine hashes.
Native profiles live in `%LOCALAPPDATA%\ManaTableNative`; use `--profile` with an
isolated path to review the first-session welcome without changing saved decks
or play preferences. A completed native package updates `dist/latest-native.json`;
the checkout launcher uses that manifest and reports a missing native package
when none exists. The preview is marked `release: false`, is not
offered through the stable updater, and is not an installer-publication gate.
