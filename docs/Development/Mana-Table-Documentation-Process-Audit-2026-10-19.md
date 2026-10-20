# Documentation and process audit

October 19, 2026

The main documentation was still routing players and contributors through
Electron even though the Windows download is the native MonoGame app. The
largest consequences were incorrect setup instructions, incorrect test-profile
isolation, and confusion between installed releases, current source and pending
work. The documentation changes accompanying this audit correct those routes
and add a small automated check for broken local links.

## Scope

The baseline is master revision `c11a3094be6c95ba1bddbafbe54aa7ad8fe3a7db`,
the published `mana-native-v0.2.37` release, and pending
[PR 7](https://github.com/proflayton/Mana-Table/pull/7). PR 7's new recovery,
public-state, legality and deck-review behavior is pending rather than part of
this master baseline. Local equipment work is separate.

The review covers the root and client guides, API contract, architecture,
testing, encounters, packaging and updates, contribution and issue templates,
workflow triggers and the active master ruleset.

## Findings and corrections

P1 findings can send a reader to the wrong app, profile or release procedure.
P2 findings undermine repeatability or allow documentation drift.

| Priority | Finding at the baseline | Correction |
| --- | --- | --- |
| P1 | The [README](https://github.com/proflayton/Mana-Table/blob/c11a3094be6c95ba1bddbafbe54aa7ad8fe3a7db/README.md#L3) calls the product a web UI and routes source setup through Electron; contribution layers also start there. | Native-first README, a dedicated [native development guide](../../mana-native/README.md), corrected contribution routing and separate Electron scope. |
| P1 | [Contribution guidance](https://github.com/proflayton/Mana-Table/blob/c11a3094be6c95ba1bddbafbe54aa7ad8fe3a7db/CONTRIBUTING.md#L43) recommends `FORGE_USER_DATA` for tests, but native runs use `--profile`. Electron package log locations were also inaccurate. | Separate native/Electron profile instructions in contributor, test, client and issue guides. Native examples use a disposable profile and avoid overwriting PowerShell's automatic `$PROFILE` variable. |
| P1 | The [release guide](https://github.com/proflayton/Mana-Table/blob/c11a3094be6c95ba1bddbafbe54aa7ad8fe3a7db/docs/Development/Mana-Table-Releases.md#L50) says in-app updating is absent. | Document existing discovery, verified download, explicit install/restart and the native stable channel, separately from legacy Electron releases. |
| P1 | Build instructions do not adequately distinguish compiling the client from assembling its engine/runtime/resources, or rebuilding a JAR from refreshing a package. | The native guide supplies complete preview and isolated launch steps, explains the production/test CLI distinction, and requires a fresh matching JAR after engine changes. |
| P1 | Source controls and battlefield behavior are mixed with descriptions of the older published installer. | Player and architecture guides identify the source baseline, explain changes after installers through 0.2.37, and direct readers to package revision metadata. PR 7 additions remain explicitly pending. |
| P2 | Test guidance mixes native, Electron and shared-engine proof; some mode names, viewport advice and battlefield-paging claims no longer match the code. Unquoted path placeholders also fail PowerShell parsing. | Correct the [testing guide](Mana-Table-Testing.md), quote path placeholders, distinguish controlled UI fixtures from rules coverage, and label the encounter runner as Electron-only. |
| P2 | API wording omits current hand-placement/allocation behavior and conflates ordinary anonymous cards with richer allocation snapshots. Some test/data claims are too broad. | Correct the [API contract](../../forge-api/README.md) against master, preserve the current protocol/synchronization versions, and identify pending PR 7 fields separately. |
| P2 | The documented draft and assistance-disclosure rules were not followed consistently: PR 7 was opened ready while checks were pending and omitted its AI disclosure. | Return PR 7 to draft, add its disclosure and make exact-head checks, artifact identity, documentation changes and AI assistance explicit in the PR template. |
| P2 | Maintained local links and heading fragments had no dedicated automated validation; guide maintenance had no clear routing. | Add a dependency-free [documentation checker](../../forge-desktop/scripts/check-docs.cjs), unit fixtures and an early desktop CI step. [Contributing](../../CONTRIBUTING.md#keep-documentation-current) maps behavior changes to the guide that must change with them. |

## Merge and publication rules

The active [master ruleset](https://api.github.com/repos/proflayton/Mana-Table/rulesets/25431097)
requires a pull request, an up-to-date branch and four GitHub Actions checks:
`desktop`, `Verify and package Windows x64`, `Test with Java 17`, and
`Test with Java 21`. It has no bypass actors. Applicable iOS compatibility checks
are also part of contributor validation and release prerequisites. The configured
approving-review count is zero and code-owner review is disabled.

Keep work in draft until its final head passes the applicable checks. Record
the tested revision and completed runs; a new head needs its own evidence.
New regression checks must be included in CI. The final merged master revision
is tested again before native publication. The publication helper checks its
exact source, required workflows and whether master advanced; an uploaded draft
does not establish that a release was published.

These rules already exist. This audit does not change merge permissions,
review ownership or release authorization.

## Remaining process improvements

1. **Strengthen manual package provenance.** `package.ps1` accepts an arbitrary
   `-EngineJar` and records the checkout revision plus a file hash. Those fields
   do not independently prove the JAR was built from that revision. CI builds
   and tests its JAR immediately before packaging; local maintainers must retain
   matching-source evidence. A future code change can bind the JAR's source to
   package metadata and reject mismatches. See the
   [packaging input](https://github.com/proflayton/Mana-Table/blob/c11a3094be6c95ba1bddbafbe54aa7ad8fe3a7db/mana-native/package.ps1#L31).
2. **Reduce duplicate feature-branch CI deliberately.** Java and desktop
   workflows run on pushes and pull requests. Their event/ref handling can
   schedule separate runs for the same feature head. Evaluate keeping PR checks
   plus master publication prerequisites while removing redundant work; preserve
   every required PR check and exact-master release check. This audit does not
   change those triggers or claim a measured time saving.
3. **Keep semantic review with the change.** The link checker cannot establish
   that prose, a command, a privacy claim or a release claim is correct. The
   contributor updates the mapped guide and the reviewer compares it with the
   source. If independent review or CODEOWNERS becomes required, choose the
   responsible maintainers and permissions explicitly.

## Validation

Run `node forge-desktop/scripts/check-docs.cjs` from the root, or
`npm run check:docs` in `forge-desktop`. Its unit fixtures run in the existing
Node unit suite. The checker covers the maintained entry points and Mana Table
development guides, resolves local files and Markdown heading fragments, and
does not contact external sites or certify arbitrary Markdown syntax.

Review PowerShell examples for syntax and profile isolation, and verify command
names, result files, npm scripts, workflow labels and release flags against
their implementation. A docs-only local check does not substitute for the
required PR builds. Historical audits and design prototypes remain reference
material rather than a competing current setup guide.

Local verification passed 54 Node unit tests, JavaScript syntax checks for 103
files, and local-link/heading checks across 15 maintained documents. All 15
PowerShell example blocks parsed successfully after correcting placeholders;
the syntax check did not execute the examples. Required PR build evidence is
separate from these documentation checks.
