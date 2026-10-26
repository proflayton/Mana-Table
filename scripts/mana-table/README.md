# Mana Table CI tools

The Windows MonoGame app is the only player client. These dependency-free Node
tools preserve release/source verification and documentation checks; they do
not contain an app, rules implementation or game integration-test client.

Run from the repository root with Node 24:

```sh
node scripts/mana-table/check.cjs
node --test scripts/mana-table/tests/*.test.cjs
node scripts/mana-table/check-docs.cjs
```

- `ci-engine-artifact.cjs create|verify [source-sha]` binds the Java JAR's bytes
  to the exact clean checkout. CI supplies `GITHUB_SHA` when the argument is absent.
- `wait-ci-engine.cjs` discovers that exact producer artifact for native packaging.
  An early upload does not make pending Java checks successful.
- `wait-release-ci.cjs` requires the current master source's completed Java and
  shared-tooling checks, plus applicable iOS evidence, before native publication.
  It performs read-only API calls and never merges, reruns or publishes.
- `check-docs.cjs` checks maintained local links/headings without network access.
  It does not validate feature claims or the user experience.

The historical `Mana Table / desktop` required status now runs this small
tool/documentation check only. App builds, native journeys and installer/update
verification belong to the native Windows workflow. Real engine rules and wire
behavior are covered by Java and the production native adapter's checks.

## Optional AI characterization

`MultiplayerAiProbe.java` calls Forge's actual combat-preference and mandatory
damage-target selectors in a disposable, offline process. It records engine
JAR provenance and fixed-state selection counts; it does not simulate matches,
change the shipping policy or establish that an experience is fair. It requires
JDK 17+ and an explicit engine/resource bundle. See the
[AI investigation](../../docs/Development/Mana-Table-AI.md#reproduction-scope-and-provenance)
for compilation, isolation, commands, initial results and limitations. Generated
profiles and evidence belong under `.tools`. This optional diagnostic adds no CI
job or runtime dependency to the game or the Node tools above.
