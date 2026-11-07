const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { DECKS, assess, verify } = require('../mixed-game-qualification.cjs');

function fixture() {
  const hashes = { engineHash: '1'.repeat(64), harnessHash: '2'.repeat(64), policyHash: '3'.repeat(64),
    fixtureHash: '4'.repeat(64), manifestHash: '5'.repeat(64), buildHash: '6'.repeat(64) };
  const sourceRevision = 'a'.repeat(40);
  const rotations = [0, 1, 2, 3].map(index => {
    const result = { Rotation: index, DriveMode: 'PolledBackground', Failure: null, StopReason: 'natural-outcome',
      NaturalOutcomeVerified: true, Turns: 42, Outcome: { Kind: 'win' },
      Qualification: { Status: 'Inconclusive', TypedNaturalGameCompleted: true, FailureReasons: [],
        MissingEvidence: ['slot-0/owned-equipment-attachment/unobserved'] },
      Evidence: { DiagnosticErrors: [] }, PublicComparisons: 12, FourAudienceCohorts: 4, DevelopedSeats: [0, 1, 2, 3],
      Participants: [0, 1, 2, 3].map(slot => ({ SlotIndex: slot, PlayerId: slot, Role: ['Host', 'Guest', 'Ai', 'Ai'][slot], ProviderAcceptances: 20 })),
      Decks: DECKS.map((_, slot) => ({ CandidateId: DECKS[(slot + index) % 4], SourceSha256: (7 + (slot + index) % 4).toString(16).repeat(64) })),
      CausalDecks: DECKS.map(() => ({ MechanicalIntegrity: 'no-observed-integrity-failure', IntegrityFailures: [], SampledNaturalGame: 'observed-natural-outcome' })),
      HostProviderAcceptances: 20, GuestProviderAcceptances: 20,
      HostAuto: { AcceptedGrants: 8, CertifiedAutomaticPasses: 100, AcceptedWithdrawals: 7, AcceptedPolicyPriorityPasses: 10 },
      GuestAuto: { AcceptedGrants: 9, CertifiedAutomaticPasses: 101, AcceptedWithdrawals: 8, AcceptedPolicyPriorityPasses: 11 },
      PublicComparison: { ComparedFrames: 40, AudienceCount: 4, MaximumRetainedCohorts: 64, RetainedCohorts: 64 } };
    return { result, owners: { Executing: 0, orderedEvidence: { Pending: 0, Retired: true, MissingOwnedBatch: false, Failure: null,
      Enqueued: 100, Applied: 100, DiscardedAfterFault: 0 }, host: { ProviderAcceptances: 20 }, guest: { ProviderAcceptances: 20 } } };
  });
  return { sourceRevision, ...hashes,
    manifest: { profile: 'natural', mode: 'PolledBackground', engineRandomnessControlled: false,
      limits: { Matches: 4, Turns: 160, MatchTime: '00:03:00' }, launchJarSha256: hashes.engineHash,
      harnessAssemblySha256: hashes.harnessHash, policyAssemblySha256: hashes.policyHash,
      decks: DECKS.map((Id, index) => ({ Id, Hash: (7 + index).toString(16).repeat(64) })) },
    summary: { profile: 'natural', mode: 'PolledBackground', allRequestedGamesCompleted: true, humanReview: 'pending',
      allInteractionsClaimed: false, unrunRotations: [], results: rotations.map(row => row.result) }, rotations,
    fanout: { passed: true, failure: null, cleanupErrors: [], projectionContinuityVerified: true, capturedTriggers: 110,
      optionalPaymentTriggers: 55, commonTerminalReceipts: 110, setupBudgetSeconds: 45, activeMatchBudgetSeconds: 90,
      totalEnvelopeBudgetSeconds: 135, semanticStallBudgetSeconds: 15, hostPacing: { announcementSeconds: 2.4 },
      runtime: { SourceRevision: sourceRevision, Provenance: 'source-receipt-verified', ProductionEngineSha256: hashes.engineHash,
        FixtureLauncherSha256: hashes.fixtureHash, HarnessAssemblySha256: hashes.harnessHash, PolicyAssemblySha256: hashes.policyHash,
        SourceManifestSha256: hashes.manifestHash, CandidateBuildSha256: hashes.buildHash },
      facts: { toothyCounterIncrease: 55, capturedOptionalCostDeclines: 55, distinctDeclinedProducingTriggers: 55,
        humanCertifiedAutomaticPasses: 220, acceptedBotQueuePasses: 220, committedRevelationDrawActivities: 55,
        committedManaSpent: 5, manaProductionChildren: 5, hostTappedForests: 5 } },
    replays: [0, 1].map(human => ({ human, passed: true, audience: 'viewer-private',
      archive: { TotalCheckpoints: 150, AcceptedCallbacks: 450, IdenticalCallbacks: 300,
        parts: [{ File: 'part-000.json', FirstOrdinal: 0, EndOrdinal: 150, StartTicks: 0, EndTicks: 120001000, Bytes: 1024, Sha256: 'a'.repeat(64) }] },
      reports: [
      { width: 1000, height: 740 }, { width: 1540, height: 980 }
    ].map(viewport => ({ viewport, accepted: 150, AcceptedCallbacks: 450, IdenticalCallbacks: 300,
      frames: 700, terminalIds: 110, incomplete: 0, pendingAtRecordedEnd: 2, actualCallbackEndTicks: 1000,
      syntheticDisplayDrainTicks: 120000000, announcements: [
        { Name: 'Shamanic Revelation', CapturedCount: 1 }, { Name: 'Toothy, Imaginary Friend', CapturedCount: 55, capturedHeadingPainted: true },
        { Name: "Mind's Eye", CapturedCount: 55, capturedHeadingPainted: true }
      ] })) })) };
}

test('four real natural rotations and the actual source-bound110-trigger UI workload admit scoped release evidence', () => {
  const result = assess(fixture());
  assert.equal(result.naturalRotationsCompleted, 4);
  assert.equal(result.actualFanoutTerminalsPerHuman, 110);
  assert.equal(result.complexDeckQualification, 'required-causal-evidence-incomplete');
  assert.equal(result.engineRandomnessControlled, false);
  assert.equal(result.humanUxAcceptance, false);
  assert.equal(result.allInteractionsClaimed, false);
});

test('a natural finish never upgrades unobserved rare mechanics to full deck qualification', () => {
  const f = fixture();
  for (const row of f.rotations) { row.result.Qualification.Status = 'Passed'; row.result.Qualification.MissingEvidence = []; }
  assert.equal(assess(f).complexDeckQualification, 'observed-required-mechanics');
  f.rotations[0].result.CausalDecks[0].IntegrityFailures = [{ Consequence: 'bad payment' }];
  assert.throws(() => assess(f), /causal integrity/);
});

test('cutoffs, missing rotations, fake topologies and removed server Auto cannot enter natural release qualification', () => {
  for (const mutate of [
    f => { f.summary.allRequestedGamesCompleted = false; }, f => { f.summary.unrunRotations = [3]; },
    f => { f.rotations.pop(); }, f => { f.rotations[0].result.StopReason = 'time-budget'; },
    f => { f.rotations[0].result.NaturalOutcomeVerified = false; }, f => { f.rotations[0].result.Outcome = null; },
    f => { f.rotations[0].result.Participants[1].Role = 'Ai'; }, f => { f.rotations[1].result.Decks[3].CandidateId = DECKS[1]; },
    f => { f.rotations[0].result.HostAuto.CertifiedAutomaticPasses = 0; }, f => { f.rotations[0].result.GuestAuto = null; },
    f => { f.manifest.limits.MatchTime = '00:04:00'; }, f => { f.manifest.limits.Turns = 200; },
    f => { f.manifest.engineRandomnessControlled = true; }, f => { f.summary.humanReview = 'approved'; }
  ]) {
    const f = fixture(); mutate(f); assert.throws(() => assess(f));
  }
});

test('strict causal integrity, public coherence and drained receipt owners remain mandatory', () => {
  for (const mutate of [
    r => { r.result.Failure = 'rules crash'; }, r => { r.result.Qualification.Status = 'Failed'; },
    r => { r.result.Qualification.MissingEvidence = ['slot-0/causal-qualification-missing']; },
    r => { r.result.Qualification.Status = 'Passed'; }, r => { r.result.Decks[0].SourceSha256 = 'f'.repeat(64); },
    r => { r.result.Evidence.DiagnosticErrors = ['lost record']; }, r => { r.result.FourAudienceCohorts = 0; },
    r => { r.result.PublicComparison.AudienceCount = 3; }, r => { r.result.PublicComparison.RetainedCohorts = 65; },
    r => { r.owners.Executing = 1; }, r => { r.owners.orderedEvidence.Pending = 1; },
    r => { r.owners.orderedEvidence.MissingOwnedBatch = true; }, r => { r.owners.orderedEvidence.Enqueued++; },
    r => { r.owners.orderedEvidence.Retired = false; }, r => { r.owners.guest.ProviderAcceptances--; }
  ]) { const f = fixture(); mutate(f.rotations[0]); assert.throws(() => assess(f)); }
});

test('stale source/package bytes, workload cutoffs, relaxed deadlines and incomplete replay all fail closed', () => {
  for (const mutate of [
    f => { f.manifest.launchJarSha256 = 'f'.repeat(64); }, f => { f.fanout.runtime.SourceRevision = 'b'.repeat(40); },
    f => { f.fanout.runtime.CandidateBuildSha256 = null; }, f => { f.fanout.runtime.Provenance = 'locally-asserted-source'; },
    f => { f.fanout.commonTerminalReceipts = 109; }, f => { f.fanout.activeMatchBudgetSeconds = 120; },
    f => { f.fanout.hostPacing.announcementSeconds = 0; }, f => { f.fanout.facts.humanCertifiedAutomaticPasses = 219; },
    f => { f.fanout.projectionContinuityVerified = false; }, f => { f.replays[0].reports.pop(); },
    f => { f.replays[1].reports[0].terminalIds = 109; }, f => { f.replays[0].reports[0].pendingAtRecordedEnd = 110; },
    f => { f.replays[1].reports[1].announcements.pop(); }, f => { f.replays[0].reports[0].incomplete = 1; },
    f => { f.replays[0].reports[0].announcements[1].capturedHeadingPainted = false; },
    f => { f.replays[0].reports[0].IdenticalCallbacks++; },
    f => { f.replays[0].archive.TotalCheckpoints = 513; },
    f => { f.replays[1].archive.parts[0].Bytes = 64 * 1024 * 1024 + 1; },
    f => { f.replays[0].archive.parts[0].FirstOrdinal = 1; },
    f => { f.replays[1].archive.parts[0].EndTicks++; },
    f => { f.replays[0].archive.parts[0].File = '../foreign.json'; }
  ]) { const f = fixture(); mutate(f); assert.throws(() => assess(f)); }
});

test('typed identity sets and bounded evidence counters reject JSON coercion and omitted matching fields', () => {
  for (const [name, mutate] of [
    ['string slot index', f => { f.rotations[0].result.Participants[0].SlotIndex = '0'; }],
    ['nested deck identity', f => { f.manifest.decks[0].Id = [DECKS[0]]; }],
    ['null player identity', f => { f.rotations[0].result.Participants[0].PlayerId = null; f.rotations[0].result.DevelopedSeats[0] = null; }],
    ['string player identity', f => { f.rotations[0].result.Participants[0].PlayerId = '0'; f.rotations[0].result.DevelopedSeats[0] = '0'; }],
    ['extra developed identity', f => { f.rotations[0].result.DevelopedSeats.push(99); }],
    ['null cohort maximum', f => { f.rotations[0].result.PublicComparison.MaximumRetainedCohorts = null; }],
    ['negative cohort maximum', f => { f.rotations[0].result.PublicComparison.MaximumRetainedCohorts = -1; }],
    ['string cohort maximum', f => { f.rotations[0].result.PublicComparison.MaximumRetainedCohorts = '64'; }],
    ['null cohort count', f => { f.rotations[0].result.PublicComparison.RetainedCohorts = null; }],
    ['negative cohort count', f => { f.rotations[0].result.PublicComparison.RetainedCohorts = -1; }],
    ['string cohort count', f => { f.rotations[0].result.PublicComparison.RetainedCohorts = '64'; }],
    ['omitted equal inbox counters', f => { delete f.rotations[0].owners.orderedEvidence.Enqueued; delete f.rotations[0].owners.orderedEvidence.Applied; }],
    ['string equal inbox counters', f => { f.rotations[0].owners.orderedEvidence.Enqueued = '100'; f.rotations[0].owners.orderedEvidence.Applied = '100'; }],
    ['omitted matching host receipts', f => { delete f.rotations[0].result.HostProviderAcceptances; delete f.rotations[0].owners.host.ProviderAcceptances; }],
    ['string viewport width', f => { f.replays[0].reports[0].viewport.width = '1000'; }],
    ['string viewport height', f => { f.replays[0].reports[0].viewport.height = '740'; }],
    ['null pending display count', f => { f.replays[0].reports[0].pendingAtRecordedEnd = null; }],
    ['negative pending display count', f => { f.replays[0].reports[0].pendingAtRecordedEnd = -1; }],
    ['string pending display count', f => { f.replays[0].reports[0].pendingAtRecordedEnd = '2'; }],
    ['nested rare-mechanic caveat', f => { f.rotations[0].result.Qualification.MissingEvidence = [['slot-0/owned-equipment-attachment/unobserved']]; }]
  ]) {
    const f = fixture(); mutate(f);
    assert.throws(() => assess(f), undefined, name);
  }
  const reordered = fixture();
  reordered.rotations[0].result.DevelopedSeats.reverse();
  reordered.rotations[0].result.Participants.reverse();
  assert.equal(assess(reordered).naturalRotationsCompleted, 4, 'The check preserves legitimate order-independent typed identity sets.');
});

test('the actual file verifier binds transferred engine/harness/package bytes, including legitimate UTF8 BOM manifests', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mana-mixed-qualification-'));
  try {
    const f = fixture(), inputs = path.join(root, 'inputs'), packageDirectory = path.join(root, 'package');
    const campaign = path.join(root, 'campaign'), fanout = path.join(root, 'fanout');
    const put = (file, content) => { fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, content); };
    const json = (file, value, bom = false) => put(file, (bom ? '\uFEFF' : '') + JSON.stringify(value));
    const hash = content => crypto.createHash('sha256').update(content).digest('hex');
    const engine = 'candidate engine bytes', harness = 'candidate harness bytes', policy = 'candidate policy bytes', launcher = 'candidate fixture bytes';
    const engineHash = hash(engine), harnessHash = hash(harness), policyHash = hash(policy), fixtureHash = hash(launcher);
    put(path.join(packageDirectory, 'engine/forge-engine.jar'), engine); put(path.join(packageDirectory, 'Mana.AI.dll'), policy);
    put(path.join(inputs, 'harness/Mana.Table.Tests.dll'), harness); put(path.join(inputs, 'forge-engine-fixtures.jar'), launcher);
    const engineFile = path.join(inputs, 'ci-engine-manifest.json'), buildFile = path.join(packageDirectory, 'build.json');
    json(engineFile, { sourceRevision: f.sourceRevision, engine: { sha256: engineHash }, fixtureLauncher: { sha256: fixtureHash } }, true);
    json(buildFile, { sourceRevision: f.sourceRevision, release: true }, true);
    json(path.join(inputs, 'ci-harness-manifest.json'), { sourceRevision: f.sourceRevision, files: [{ path: 'Mana.Table.Tests.dll', sha256: harnessHash }] });
    Object.assign(f.manifest, { launchJarSha256: engineHash, harnessAssemblySha256: harnessHash, policyAssemblySha256: policyHash });
    Object.assign(f.fanout.runtime, { ProductionEngineSha256: engineHash, HarnessAssemblySha256: harnessHash, PolicyAssemblySha256: policyHash,
      FixtureLauncherSha256: fixtureHash, SourceManifestSha256: hash(fs.readFileSync(engineFile)), CandidateBuildSha256: hash(fs.readFileSync(buildFile)) });
    json(path.join(campaign, 'manifest.json'), f.manifest); json(path.join(campaign, 'summary.json'), f.summary);
    for (let index = 0; index < 4; index++) {
      json(path.join(campaign, `match-${index}/result.json`), f.rotations[index].result);
      json(path.join(campaign, `match-${index}/private-owner-teardown.json`), f.rotations[index].owners);
    }
    json(path.join(fanout, 'result.json'), f.fanout);
    for (let index = 0; index < 2; index++) json(path.join(fanout, `projection-human-${index}/result.json`), f.replays[index]);
    const options = { inputs, packageDirectory, campaign, fanout, sourceRevision: f.sourceRevision };
    assert.equal(verify(options).naturalRotationsCompleted, 4);
    put(path.join(packageDirectory, 'Mana.AI.dll'), 'later different policy bytes');
    assert.throws(() => verify(options), /candidate bytes|provenance/);
    put(path.join(packageDirectory, 'Mana.AI.dll'), policy);
    put(path.join(campaign, 'summary.json'), '{"profile":"natural",');
    assert.throws(() => verify(options), SyntaxError);
  } finally {
    const resolved = path.resolve(root), parent = path.resolve(os.tmpdir()) + path.sep;
    assert.ok(resolved.startsWith(parent) && path.basename(resolved).startsWith('mana-mixed-qualification-'));
    fs.rmSync(resolved, { recursive: true });
  }
});
