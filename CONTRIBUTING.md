# Contributing to Mana Table

Start with the [native setup guide](mana-native/README.md), then read the
[architecture map](docs/Development/Mana-Table-Architecture.md). The working
integration branch is **`master`**; base feature branches and pull requests on it.

## Pick the right layer

- **Native UI and interaction:** `mana-native/Mana.Table`, `Mana.Magic`, and
  `Mana.Renderer`. Keep rules decisions in Forge; display authorized snapshots
  and answer the current prompt. Shared client/session logic is in `Mana.Client`.
- **Native engine boundary:** `Mana.Contracts` contains typed state and commands;
  `Mana.Forge` maps the Java process protocol. Keep Java projection, mapping and
  native contracts aligned when changing an API field.
- **Electron maintenance:** `forge-desktop/renderer`, `main.cjs`, `preload.cjs`,
  and their CommonJS helpers form a separate client. Its environment variables,
  UI tests and packaging are described in the [Electron guide](forge-desktop/README.md).
- **API and visibility:** `forge-api/src/main/java/forge/api`. Extend the adapter
  when a UI needs new projected state or an engine-controlled interaction.
- **Card rules and AI:** the existing Forge modules and `forge-gui/res`. Consult
  the [upstream notes](docs/upstream/CONTRIBUTING.md) and card-scripting guides.

Keep unrelated upstream formatting and resource changes out of focused PRs.
Follow the style of the file being changed: Java 17 and the Maven checks for Java;
C# and the existing .NET project boundaries for the native app; plain
JavaScript/CSS for Electron's renderer; CommonJS for Electron and Node tools.
Do not add a framework or broad formatting migration as incidental cleanup.

## Work on a change

```sh
git switch master
git pull --ff-only
git switch -c feature/describe-your-change
```

Describe the behavior being improved. For a game regression, retain a small deck
list and reproduction sequence with the format, table size, relevant cards, phase,
and pending choice. For visual changes, capture the relevant state at 1000×740
and a larger window; check keyboard access and reduced motion when affected.

Run checks appropriate to the change. The [testing guide](docs/Development/Mana-Table-Testing.md)
separates native client logic, native UI, real-engine tests and Electron interactions. New tests
should catch a meaningful behavior or regression, rather than restating the
implementation. Native UI tests live in `mana-native/Mana.Table.Tests`; shared
graphics-free checks live in `Mana.Client.Tests`. Electron and Node tests use
the helpers under `forge-desktop/tests/support` for isolated data and Java discovery.

Never commit generated packages, local profiles, caches, logs, or test output.
They are already ignored. Use native `--profile <disposable-directory>` for
manual runs. Electron uses `MANA_USER_DATA_DIR` or `FORGE_USER_DATA`; those
variables do not isolate the native app. See the testing guide's
[profile and artifact locations](docs/Development/Mana-Table-Testing.md#profiles-and-artifacts).

## Review expectations

A PR should explain the concrete problem, resulting behavior, and checks run.
Include screenshots for visible changes and mention remaining limitations.
Open PRs as drafts and keep them there until the final commit has passed Windows native,
desktop, Java 17, and Java 21 checks, plus any applicable iOS checks. Record that
commit and links to the completed runs in the validation notes, then mark it
ready for review. CI runs on draft PRs. A new commit requires checking its own
results; passing local or earlier-commit checks does
not establish PR readiness. Inspect failure artifacts and fix their cause before
retrying. New regression tests must run in CI. Release publication also waits for
the exact master commit's build checks.

The active master ruleset requires a PR and the `desktop`, `Verify and package
Windows x64`, `Test with Java 17`, and `Test with Java 21` checks, with the branch
up to date. Applicable iOS checks remain part of the contribution's validation.
Local test results and earlier commits are useful evidence but do not replace
these checks. Do not remove a required check's PR trigger to speed up docs work.

Update the relevant documentation in the same change when modifying setup,
controls, IPC, profiles, persisted data or packaging. Do not change persisted
schemas or the action protocol without describing compatibility and migration.

Keep prompt/session checks and hidden-information filtering intact. A visual ID
is not an action key; a card entering a hidden zone must not remain inspectable.
See the [API contract](forge-api/README.md) for the full integration rules.

If an AI coding assistant substantially helped produce a contribution, identify
that in the PR body or a co-author attribution, following the upstream policy.
The contributor remains responsible for understanding and verifying the change.

## Keep documentation current

Each contributor updates the guide for the behavior they change; reviewers
check that the documented command, label and data location match the source.
Use this map to avoid adding competing setup or release instructions:

| Change | Documentation to check |
| --- | --- |
| Product scope, default client, download route | [Root README](README.md) |
| Native controls, setup, data location | [Player guide](mana-native/START-HERE.txt) and [native development](mana-native/README.md) |
| Component or state ownership | [Architecture](docs/Development/Mana-Table-Architecture.md) |
| Protocol, permissions or projected state | [API contract](forge-api/README.md) and affected client guide |
| Regression command, result file or CI check | [Testing](docs/Development/Mana-Table-Testing.md) |
| Packaging, version, installer or updater | [Releases](docs/Development/Mana-Table-Releases.md) |
| Electron-specific behavior or encounters | [Electron development](forge-desktop/README.md) and [encounters](docs/Development/Mana-Table-Encounters.md) |

Run `node forge-desktop/scripts/check-docs.cjs` from the repository root, or
`npm run check:docs` in `forge-desktop`. It checks maintained local links and
heading fragments; a passing result does not verify prose or external websites.
Check instructions from a clean checkout or isolated profile when changing a
setup or packaging command. Label source-only features beside the claim when
the latest published installer does not contain them, and avoid copying a
workstation's ignored `.tools`, cache or preview paths into contributor setup.

## Report an issue

Use this fork's issue tracker for Mana Table UI, host, and API issues. Include the
beta version/commit, OS, window size, deck format, number of players, reproduction,
and expected versus actual behavior. Engine diagnostics are in `engine.log`
inside the active data directory; share the relevant excerpt and a minimal deck.
For the native app, use the [profile and troubleshooting guide](mana-native/README.md#profiles-and-troubleshooting).
Electron locations are in its separate [development guide](forge-desktop/README.md#data-and-troubleshooting).

## Keep upstream changes manageable

`origin` is this fork (or your personal fork). An `upstream` remote may point to
`https://github.com/Card-Forge/forge.git`. Keep shared-engine patches small and
document why the adapter needs them. Do not rewrite shared branch history during
upstream integration. The module map and existing adapter touchpoints are in the
architecture guide. Preserve the [license](LICENSE), Forge attribution, and
third-party notices.
