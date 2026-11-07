// Closed admission of source-bound harness evidence, never a second rules engine.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const DECKS = ['rhys-token-equipment.v1', 'pir-toothy-counter-ramp.v1', 'balmor-spells-multitarget.v1', 'traxos-colorless-artifacts.v1'];
function requireThat(value, message) { if (!value) throw new Error(message); }
function positive(value) { return Number.isSafeInteger(value) && value > 0; }
function nonnegative(value) { return Number.isSafeInteger(value) && value >= 0; }
function sameHash(left, right) { return /^[a-f0-9]{64}$/i.test(left || '') && left.toLowerCase() === right?.toLowerCase(); }
function exactly(values, expected) {
  if (!Array.isArray(values) || values.length !== expected.length) return false;
  const remaining = new Map();
  for (const value of expected) remaining.set(value, (remaining.get(value) || 0) + 1);
  for (const value of values) {
    const count = remaining.get(value) || 0;
    if (!count) return false;
    remaining.set(value, count - 1);
  }
  return true;
}
function read(file) {
  const stat = fs.lstatSync(file);
  requireThat(stat.isFile() && !stat.isSymbolicLink() && stat.size > 0 && stat.size <= 4 * 1024 * 1024, 'Empty, linked or oversized qualification evidence: ' + file);
  return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
}
function digest(file) {
  const hash = crypto.createHash('sha256'), descriptor = fs.openSync(file, 'r'), block = Buffer.allocUnsafe(1024 * 1024);
  try { let count; while ((count = fs.readSync(descriptor, block, 0, block.length, null)) > 0) hash.update(block.subarray(0, count)); }
  finally { fs.closeSync(descriptor); }
  return hash.digest('hex');
}

function assess({ sourceRevision, engineHash, harnessHash, policyHash, manifestHash, buildHash, fixtureHash,
  manifest, summary, rotations, fanout, replays }) {
  requireThat(/^[a-f0-9]{40}$/.test(sourceRevision || ''), 'Qualification needs an exact source revision.');
  requireThat(manifest.profile === 'natural' && manifest.mode === 'PolledBackground' && manifest.engineRandomnessControlled === false
    && manifest.limits?.Matches === 4 && manifest.limits.Turns === 160 && manifest.limits.MatchTime === '00:03:00'
    && sameHash(manifest.launchJarSha256, engineHash) && sameHash(manifest.harnessAssemblySha256, harnessHash)
    && sameHash(manifest.policyAssemblySha256, policyHash) && exactly(manifest.decks?.map(deck => deck.Id), DECKS),
  'The natural campaign changed its topology, varied decks, original budgets, randomness scope or exact candidate bytes.');
  requireThat(summary.profile === 'natural' && summary.mode === 'PolledBackground' && summary.allRequestedGamesCompleted === true
    && summary.humanReview === 'pending' && summary.allInteractionsClaimed === false && exactly(summary.unrunRotations, [])
    && Array.isArray(summary.results) && summary.results.length === 4 && rotations.length === 4,
  'All four requested natural rotations must complete; a prefix, cutoff or unrun rotation cannot qualify.');
  for (let index = 0; index < 4; index++) {
    const { result, owners } = rotations[index], combined = summary.results[index];
    requireThat(JSON.stringify(result) === JSON.stringify(combined), 'The campaign summary and retained rotation disagree.');
    const participants = result.Participants, assessment = result.Qualification;
    requireThat(result.Rotation === index && result.DriveMode === 'PolledBackground' && result.Failure === null
      && result.StopReason === 'natural-outcome' && result.NaturalOutcomeVerified === true && positive(result.Turns) && result.Turns <= 160
      && result.Outcome && ['win', 'draw'].includes(result.Outcome.Kind)
      && assessment?.TypedNaturalGameCompleted === true && ['Passed', 'Inconclusive'].includes(assessment.Status)
      && exactly(assessment.FailureReasons, []) && Array.isArray(assessment.MissingEvidence)
      && (assessment.Status === 'Passed' ? assessment.MissingEvidence.length === 0 : assessment.MissingEvidence.length > 0)
      && assessment.MissingEvidence.every(reason => typeof reason === 'string' && /^slot-[0-3]\/[^/]+\/unobserved$/.test(reason))
      && exactly(result.Evidence?.DiagnosticErrors, []) && positive(result.PublicComparisons) && positive(result.FourAudienceCohorts),
    'A natural rotation lacks a coherent typed outcome, retains an integrity failure or substitutes missing infrastructure evidence for rare mechanic coverage.');
    requireThat(Array.isArray(participants) && participants.length === 4
      && exactly(participants.map(row => row.SlotIndex), [0, 1, 2, 3]) && new Set(participants.map(row => row.PlayerId)).size === 4
      && exactly(participants.map(row => row.Role), ['Host', 'Guest', 'Ai', 'Ai'])
      && participants.every(row => nonnegative(row.PlayerId) && positive(row.ProviderAcceptances))
      && exactly(result.DevelopedSeats, participants.map(row => row.PlayerId))
      && result.Decks?.every((deck, slot) => deck.CandidateId === DECKS[(slot + index) % 4]
        && sameHash(deck.SourceSha256, manifest.decks[(slot + index) % 4].Hash)) && result.Decks.length === 4,
    'Every rotation needs two ordinary players, two distinct configured AI decks and four progressing physical roles.');
    requireThat(result.CausalDecks?.length === 4 && result.CausalDecks.every(deck => deck.MechanicalIntegrity === 'no-observed-integrity-failure'
      && exactly(deck.IntegrityFailures, []) && deck.SampledNaturalGame === 'observed-natural-outcome'),
    'Per-deck causal integrity must remain assessed even when rare mechanic witnesses are unobserved.');
    for (const auto of [result.HostAuto, result.GuestAuto]) requireThat(positive(auto?.AcceptedGrants) && positive(auto.CertifiedAutomaticPasses)
      && Number.isSafeInteger(auto.AcceptedWithdrawals) && auto.AcceptedWithdrawals >= 0
      && Number.isSafeInteger(auto.AcceptedPolicyPriorityPasses) && auto.AcceptedPolicyPriorityPasses >= 0,
    'Both ordinary owners must witness actual server Auto rather than a script fallback.');
    requireThat(positive(result.PublicComparison?.ComparedFrames) && result.PublicComparison.AudienceCount === 4
      && positive(result.PublicComparison.MaximumRetainedCohorts) && result.PublicComparison.MaximumRetainedCohorts <= 64
      && nonnegative(result.PublicComparison.RetainedCohorts) && result.PublicComparison.RetainedCohorts <= result.PublicComparison.MaximumRetainedCohorts,
    'The bounded immutable-frame public comparison evidence is missing.');
    const inbox = owners.orderedEvidence;
    requireThat(owners.Executing === 0 && inbox?.Pending === 0 && inbox.Retired === true && inbox.MissingOwnedBatch === false
      && inbox.Failure === null && nonnegative(inbox.Enqueued) && nonnegative(inbox.Applied)
      && inbox.Enqueued === inbox.Applied && inbox.DiscardedAfterFault === 0
      && positive(result.HostProviderAcceptances) && positive(result.GuestProviderAcceptances)
      && owners.host?.ProviderAcceptances === result.HostProviderAcceptances && owners.guest?.ProviderAcceptances === result.GuestProviderAcceptances,
    'Every AI/evidence owner must drain and retire with coherent ordinary command receipts.');
  }
  const runtime = fanout.runtime;
  requireThat(fanout.passed === true && fanout.failure === null && exactly(fanout.cleanupErrors, []) && fanout.projectionContinuityVerified === true
    && fanout.capturedTriggers === 110 && fanout.optionalPaymentTriggers === 55 && fanout.commonTerminalReceipts === 110
    && fanout.setupBudgetSeconds === 45 && fanout.activeMatchBudgetSeconds === 90 && fanout.totalEnvelopeBudgetSeconds === 135
    && fanout.semanticStallBudgetSeconds === 15 && fanout.hostPacing?.announcementSeconds === 2.4
    && runtime?.SourceRevision === sourceRevision && runtime.Provenance === 'source-receipt-verified'
    && sameHash(runtime.ProductionEngineSha256, engineHash) && sameHash(runtime.FixtureLauncherSha256, fixtureHash)
    && sameHash(runtime.HarnessAssemblySha256, harnessHash) && sameHash(runtime.PolicyAssemblySha256, policyHash)
    && sameHash(runtime.SourceManifestSha256, manifestHash) && sameHash(runtime.CandidateBuildSha256, buildHash),
  'The actual110-trigger rules/receipt/replay workload must complete under unchanged shipping pacing, deadlines and exact source/package provenance.');
  for (const [fact, expected] of Object.entries({ toothyCounterIncrease: 55, capturedOptionalCostDeclines: 55,
    distinctDeclinedProducingTriggers: 55, humanCertifiedAutomaticPasses: 220, acceptedBotQueuePasses: 220,
    committedRevelationDrawActivities: 55, committedManaSpent: 5, manaProductionChildren: 5, hostTappedForests: 5 }))
    requireThat(fanout.facts?.[fact] === expected, 'Actual fanout rules accounting disagrees: ' + fact);
  requireThat(replays.length === 2, 'Both ordinary viewers need production projection continuity evidence.');
  for (let human = 0; human < 2; human++) {
    const replay = replays[human];
    requireThat(replay.human === human && replay.passed === true && replay.audience === 'viewer-private'
      && Array.isArray(replay.reports) && replay.reports.every(report => positive(report.viewport?.width) && positive(report.viewport?.height))
      && exactly(replay.reports?.map(report => `${report.viewport?.width}x${report.viewport?.height}`), ['1000x740', '1540x980']),
    'Production replay must cover both declared viewports for each ordinary viewer.');
    const archive = replay.archive;
    requireThat(positive(archive?.TotalCheckpoints) && archive.TotalCheckpoints <= 512
      && Number.isSafeInteger(archive.IdenticalCallbacks) && archive.IdenticalCallbacks >= 0
      && Number.isSafeInteger(archive.AcceptedCallbacks) && archive.AcceptedCallbacks - archive.TotalCheckpoints === archive.IdenticalCallbacks
      && Array.isArray(archive.parts) && archive.parts.length > 0 && archive.parts.length <= archive.TotalCheckpoints,
    'Each ordinary audience needs one bounded shared checkpoint archive across both review viewports.');
    let next = 0, end = 0;
    archive.parts.forEach((part, index) => {
      requireThat(part.File === `part-${String(index).padStart(3, '0')}.json` && part.FirstOrdinal === next
        && positive(part.EndOrdinal) && part.EndOrdinal > next && part.EndOrdinal <= archive.TotalCheckpoints
        && Number.isSafeInteger(part.StartTicks) && part.StartTicks === end
        && Number.isSafeInteger(part.EndTicks) && part.EndTicks >= part.StartTicks
        && positive(part.Bytes) && part.Bytes <= 64 * 1024 * 1024 && /^[a-f0-9]{64}$/i.test(part.Sha256 || ''),
      'Shared recording parts must preserve bounded files, complete ordinals, exact time seams and byte hashes.');
      next = part.EndOrdinal; end = part.EndTicks;
    });
    requireThat(next === archive.TotalCheckpoints, 'The shared recording archive omitted its final checkpoint.');
    for (const report of replay.reports) requireThat(positive(report.accepted) && positive(report.frames) && report.terminalIds === 110
      && Number.isSafeInteger(report.IdenticalCallbacks) && report.IdenticalCallbacks >= 0
      && report.AcceptedCallbacks === report.accepted + report.IdenticalCallbacks
      && report.accepted === archive.TotalCheckpoints && report.AcceptedCallbacks === archive.AcceptedCallbacks
      && Number.isSafeInteger(report.actualCallbackEndTicks) && report.actualCallbackEndTicks >= 0
      && report.actualCallbackEndTicks + report.syntheticDisplayDrainTicks === end
      && report.incomplete === 0 && nonnegative(report.pendingAtRecordedEnd) && report.pendingAtRecordedEnd <= 4 && report.syntheticDisplayDrainTicks === 120000000
      && report.announcements?.some(item => item.Name === 'Shamanic Revelation')
      && ['Toothy, Imaginary Friend', "Mind's Eye"].every(name => report.announcements.some(item => item.Name === name
        && item.CapturedCount === 55 && item.capturedHeadingPainted === true)),
    'Replay must retain the cast and both real55-trigger groups without missing announcements or110 individual display beats.');
  }
  return { schemaVersion: 1, sourceRevision, naturalRotationsCompleted: 4, actualFanoutTerminalsPerHuman: 110,
    projectionViewportsPerHuman: 2, engineRandomnessControlled: false, humanUxAcceptance: false, allInteractionsClaimed: false,
    complexDeckQualification: summary.results.every(result => result.Qualification.Status === 'Passed') ? 'observed-required-mechanics' : 'required-causal-evidence-incomplete' };
}

function verify({ inputs, packageDirectory, campaign, fanout, sourceRevision }) {
  const engineFile = path.join(inputs, 'ci-engine-manifest.json'), engine = read(engineFile), harness = read(path.join(inputs, 'ci-harness-manifest.json'));
  const buildFile = path.join(packageDirectory, 'build.json'), build = read(buildFile);
  requireThat(engine.sourceRevision === sourceRevision && harness.sourceRevision === sourceRevision && build.sourceRevision === sourceRevision
    && build.release === true, 'All transferred artifacts must belong to the requested release source.');
  const engineHash = digest(path.join(packageDirectory, 'engine/forge-engine.jar'));
  const harnessHash = digest(path.join(inputs, 'harness/Mana.Table.Tests.dll'));
  const policyHash = digest(path.join(packageDirectory, 'Mana.AI.dll'));
  const fixtureHash = digest(path.join(inputs, 'forge-engine-fixtures.jar'));
  requireThat(sameHash(engine.engine?.sha256, engineHash) && sameHash(engine.fixtureLauncher?.sha256, fixtureHash)
    && sameHash(harness.files?.find(file => file.path === 'Mana.Table.Tests.dll')?.sha256, harnessHash),
  'Qualification inputs differ from the byte-bound candidate manifest.');
  return assess({ sourceRevision, engineHash, harnessHash, policyHash, fixtureHash, manifestHash: digest(engineFile), buildHash: digest(buildFile),
    manifest: read(path.join(campaign, 'manifest.json')), summary: read(path.join(campaign, 'summary.json')),
    rotations: DECKS.map((_, index) => ({ result: read(path.join(campaign, `match-${index}/result.json`)),
      owners: read(path.join(campaign, `match-${index}/private-owner-teardown.json`)) })),
    fanout: read(path.join(fanout, 'result.json')),
    replays: [0, 1].map(index => read(path.join(fanout, `projection-human-${index}/result.json`))) });
}
if (require.main === module) {
  try {
    const allowed = ['inputs', 'package', 'campaign', 'fanout', 'source-revision', 'output'], values = {};
    for (let index = 2; index < process.argv.length; index += 2) {
      const key = process.argv[index].slice(2), value = process.argv[index + 1];
      requireThat(process.argv[index].startsWith('--') && allowed.includes(key) && !Object.hasOwn(values, key) && value && !value.startsWith('--'), 'Unknown or incomplete qualification argument.');
      values[key] = value;
    }
    requireThat(allowed.every(key => values[key]), 'Missing qualification argument.');
    const result = verify({ inputs: values.inputs, packageDirectory: values.package, campaign: values.campaign, fanout: values.fanout, sourceRevision: values['source-revision'] });
    fs.writeFileSync(values.output, JSON.stringify(result, null, 2) + '\n', { flag: 'wx' });
    console.log('Four natural mixed games and the actual110-trigger rules/UI workload qualified; rare mechanic coverage and human UX remain separate.');
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { DECKS, assess, verify };
