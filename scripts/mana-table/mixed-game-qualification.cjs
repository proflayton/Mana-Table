// Closed admission of source-bound harness evidence, never a second rules engine.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const DECKS = ['rhys-token-equipment.v1', 'pir-toothy-counter-ramp.v1', 'balmor-spells-multitarget.v1', 'traxos-colorless-artifacts.v1'];
const AUTOMATED_PRIORITY_FIELDS = ['GameId', 'SeatId', 'Epoch', 'AcceptedAcknowledgements', 'AcceptedGrants',
  'AcceptedWithdrawals', 'CertifiedAutomaticPasses', 'LastSequence', 'LastAcknowledgedBoardRevision',
  'LastAcknowledgedPublicActivitySequence', 'Retired'];
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

// This pure prefix certifies only the completed natural campaign. Full release
// admission additionally needs the exact source, fanout, replay and payment data.
function assessNatural({ engineHash, harnessHash, policyHash, manifest, summary, rotations }) {
  requireThat(manifest.formatVersion === 3 && manifest.profile === 'natural' && manifest.mode === 'PolledBackground'
    && manifest.automatedPriorityPolicy === 'NoResponse'
    && manifest.passingMode === 'ServerAuto' && manifest.priorityPolicy === 'PriorityAttention+QuietPriorityPolicy' && manifest.engineRandomnessControlled === false
    && manifest.limits?.Matches === 4 && manifest.limits.Turns === 160 && manifest.limits.MatchTime === '00:03:00'
    && sameHash(manifest.launchJarSha256, engineHash) && sameHash(manifest.harnessAssemblySha256, harnessHash)
    && sameHash(manifest.policyAssemblySha256, policyHash) && exactly(manifest.decks?.map(deck => deck.Id), DECKS),
  'The natural campaign changed its topology, varied decks, original budgets, randomness scope or exact candidate bytes.');
  requireThat(summary.formatVersion === 3 && summary.profile === 'natural' && summary.mode === 'PolledBackground'
    && summary.automatedPriorityPolicy === 'NoResponse'
    && summary.passingMode === 'ServerAuto' && summary.priorityPolicy === 'PriorityAttention+QuietPriorityPolicy' && summary.allRequestedGamesCompleted === true
    && summary.humanReview === 'pending' && summary.allInteractionsClaimed === false && exactly(summary.unrunRotations, [])
    && Array.isArray(summary.results) && summary.results.length === 4 && rotations.length === 4,
  'All four requested natural rotations must complete; a prefix, cutoff or unrun rotation cannot qualify.');
  let observedQuietGrants = 0, observedQuietPasses = 0, observedAutomatedNoResponseGrants = 0, observedAutomatedNoResponsePasses = 0;
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
      && exactly(result.Evidence?.diagnosticErrors, []) && positive(result.PublicComparisons) && positive(result.FourAudienceCohorts),
    'A natural rotation lacks a coherent typed outcome, retains an integrity failure or substitutes missing infrastructure evidence for rare mechanic coverage.');
    requireThat(Array.isArray(participants) && participants.length === 4
      && exactly(participants.map(row => row.SlotIndex), [0, 1, 2, 3]) && new Set(participants.map(row => row.PlayerId)).size === 4
      && exactly(participants.map(row => row.Role), ['Host', 'Guest', 'Ai', 'Ai'])
      && participants.every(row => nonnegative(row.PlayerId) && positive(row.ProviderAcceptances))
      && exactly(result.DevelopedSeats, participants.map(row => row.PlayerId))
      && result.Decks?.every((deck, slot) => deck.CandidateId === DECKS[(slot + index) % 4]
        && sameHash(deck.SourceSha256, manifest.decks[(slot + index) % 4].Hash)) && result.Decks.length === 4,
    'Every rotation needs two ordinary players, two distinct configured AI decks and four progressing physical roles.');
    const aiIds = participants.filter(row => row.Role === 'Ai').map(row => row.PlayerId);
    const automated = result.AutomatedPriorityContinuations, retired = owners.automatedPriorityContinuations;
    requireThat(typeof result.Outcome.GameId === 'string' && result.Outcome.GameId.length > 0
      && Array.isArray(automated) && automated.length === 2 && exactly(automated.map(row => row?.SeatId), aiIds)
      && Array.isArray(retired) && retired.length === 2 && exactly(retired.map(row => row?.SeatId), aiIds),
    'Both actual AI participants need their own source-bound continuation evidence and a matching retired-owner snapshot.');
    for (const value of automated) {
      const final = retired.find(row => row.SeatId === value.SeatId);
      requireThat(exactly(Object.keys(value), AUTOMATED_PRIORITY_FIELDS) && exactly(Object.keys(final), AUTOMATED_PRIORITY_FIELDS)
        && value.GameId === result.Outcome.GameId && positive(value.Epoch) && nonnegative(value.SeatId)
        && positive(value.AcceptedAcknowledgements) && positive(value.AcceptedGrants) && positive(value.CertifiedAutomaticPasses)
        && nonnegative(value.AcceptedWithdrawals) && positive(value.LastSequence)
        && nonnegative(value.LastAcknowledgedBoardRevision) && nonnegative(value.LastAcknowledgedPublicActivitySequence)
        && value.Retired === true && AUTOMATED_PRIORITY_FIELDS.every(field => final[field] === value[field]),
      'AI NoResponse controls need closed typed receipt/certificate counters, an actual game/epoch and exact post-disposal retirement evidence.');
      observedAutomatedNoResponseGrants += value.AcceptedGrants;
      observedAutomatedNoResponsePasses += value.CertifiedAutomaticPasses;
    }
    const gameplay = owners.cumulativeProviderAcceptances;
    requireThat(gameplay && !Array.isArray(gameplay) && exactly(Object.keys(gameplay), aiIds.map(String))
      && aiIds.every(id => positive(gameplay[id]) && gameplay[id] === participants.find(row => row.PlayerId === id).ProviderAcceptances),
    'AI continuation controls and certified passes cannot replace the separate cumulative gameplay provider receipts.');
    requireThat(result.CausalDecks?.length === 4 && result.CausalDecks.every(deck => deck.MechanicalIntegrity === 'no-observed-integrity-failure'
      && exactly(deck.IntegrityFailures, []) && deck.SampledNaturalGame === 'observed-natural-outcome'),
    'Per-deck causal integrity must remain assessed even when rare mechanic witnesses are unobserved.');
    for (const auto of [result.HostAuto, result.GuestAuto]) {
      requireThat(positive(auto?.AcceptedGrants) && positive(auto.CertifiedAutomaticPasses)
        && nonnegative(auto.AcceptedWithdrawals) && nonnegative(auto.AcceptedPolicyPriorityPasses)
        && positive(auto.NoResponseAcceptedGrants) && positive(auto.NoResponseCertifiedAutomaticPasses)
        && nonnegative(auto.QuietAcceptedGrants) && nonnegative(auto.QuietCertifiedAutomaticPasses)
        && nonnegative(auto.QuietConsentWindows) && (auto.QuietAcceptedGrants === 0 || auto.QuietConsentWindows > 0)
        && auto.NoResponseAcceptedGrants + auto.QuietAcceptedGrants === auto.AcceptedGrants
        && auto.NoResponseCertifiedAutomaticPasses + auto.QuietCertifiedAutomaticPasses === auto.CertifiedAutomaticPasses
        && (auto.QuietCertifiedAutomaticPasses === 0 || auto.QuietAcceptedGrants > 0),
      'Both ordinary owners must record actual mode-specific server Auto consent and certified progress; metadata is not a scripted input receipt.');
      observedQuietGrants += auto.QuietAcceptedGrants;
      observedQuietPasses += auto.QuietCertifiedAutomaticPasses;
    }
    requireThat(positive(result.PublicComparison?.ComparedFrames) && result.PublicComparison.AudienceCount === 4
      && positive(result.PublicComparison.MaximumRetainedCohorts) && result.PublicComparison.MaximumRetainedCohorts <= 64
      && nonnegative(result.PublicComparison.RetainedCohorts) && result.PublicComparison.RetainedCohorts <= result.PublicComparison.MaximumRetainedCohorts,
    'The bounded immutable-frame public comparison evidence is missing.');
    const inbox = owners.orderedEvidence;
    requireThat(owners.Executing === 0 && owners.PriorityControlsExecuting === 0
      && inbox?.Pending === 0 && inbox.Retired === true && inbox.MissingOwnedBatch === false
      && inbox.Failure === null && nonnegative(inbox.Enqueued) && nonnegative(inbox.Applied)
      && inbox.Enqueued === inbox.Applied && inbox.DiscardedAfterFault === 0
      && positive(result.HostProviderAcceptances) && positive(result.GuestProviderAcceptances)
      && owners.host?.ProviderAcceptances === result.HostProviderAcceptances && owners.guest?.ProviderAcceptances === result.GuestProviderAcceptances,
    'Every AI/evidence owner must drain and retire with coherent ordinary command receipts.');
  }
  requireThat(positive(observedAutomatedNoResponseGrants) && positive(observedAutomatedNoResponsePasses),
    'Certified AI continuation totals must remain finite typed counts.');
  requireThat(observedQuietGrants > 0 && observedQuietPasses > 0,
    'The four-game campaign must actually observe bounded Quiet consent and certified progress; an unobserved window is not a passing witness.');
  return { naturalRotationsCompleted: 4, ordinaryPassingMode: 'ServerAuto', observedQuietGrants, observedQuietPasses,
    automatedPriorityPolicy: 'NoResponse', observedAutomatedNoResponseGrants, observedAutomatedNoResponsePasses,
    complexDeckQualification: summary.results.every(result => result.Qualification.Status === 'Passed')
      ? 'observed-required-mechanics' : 'required-causal-evidence-incomplete' };
}

function assess({ sourceRevision, engineHash, harnessHash, policyHash, manifestHash, buildHash, fixtureHash,
  manifest, summary, rotations, fanout, replays, manaOutput }) {
  requireThat(/^[a-f0-9]{40}$/.test(sourceRevision || ''), 'Qualification needs an exact source revision.');
  const natural = assessNatural({ engineHash, harnessHash, policyHash, manifest, summary, rotations });
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
  const paymentRuntime = manaOutput?.runtime;
  requireThat(manaOutput?.scenario === 'ordinary-polled-consuming-output-payment' && manaOutput.passed === true
    && manaOutput.failure === null && exactly(manaOutput.cleanupErrors, []) && manaOutput.passing === 'FullControl'
    && manaOutput.totalBudgetSeconds === 90 && manaOutput.activeBudgetSeconds === 45
    && typeof manaOutput.creditedTotalSeconds === 'number' && manaOutput.creditedTotalSeconds > 0 && manaOutput.creditedTotalSeconds <= 90
    && typeof manaOutput.creditedActiveSeconds === 'number' && manaOutput.creditedActiveSeconds > 0 && manaOutput.creditedActiveSeconds <= 45
    && positive(manaOutput.coherentPublicComparisons) && Array.isArray(manaOutput.players) && manaOutput.players.length === 2
    && exactly(manaOutput.players.map(player => player.index), [0, 1]) && new Set(manaOutput.players.map(player => player.viewerId)).size === 2
    && manaOutput.players.every(player => nonnegative(player.viewerId) && positive(player.ClientAdmissions) && player.ProviderAcceptances === player.ClientAdmissions
      && player.AutoEvidence?.AcceptedGrants === 0 && player.AutoEvidence.CertifiedAutomaticPasses === 0)
    && manaOutput.publicComparison?.AudienceCount === 2 && positive(manaOutput.publicComparison.ComparedFrames)
    && positive(manaOutput.publicComparison.MaximumRetainedCohorts) && manaOutput.publicComparison.MaximumRetainedCohorts <= 64
    && nonnegative(manaOutput.publicComparison.RetainedCohorts)
    && manaOutput.publicComparison.RetainedCohorts <= manaOutput.publicComparison.MaximumRetainedCohorts
    && paymentRuntime?.SourceRevision === sourceRevision && paymentRuntime.Provenance === 'source-receipt-verified'
    && sameHash(paymentRuntime.ProductionEngineSha256, engineHash) && sameHash(paymentRuntime.FixtureLauncherSha256, fixtureHash)
    && sameHash(paymentRuntime.HarnessAssemblySha256, harnessHash) && sameHash(paymentRuntime.PolicyAssemblySha256, policyHash)
    && sameHash(paymentRuntime.SourceManifestSha256, manifestHash) && sameHash(paymentRuntime.CandidateBuildSha256, buildHash),
  'The current ordinary consuming-source payment must complete with exact source/package provenance, conserved receipts and its declared FullControl bounds.');
  for (const [fact, expected] of Object.entries({ consumingActivationReceipts: 1, explicitConsumingOutputs: 0,
    resolvedInspectors: 1, sacrificedCrystalVeins: 1, tappedWastes: 1, remainingPool: 0,
    committedCrystalVeinColorless: 2, committedWastesColorless: 1, committedManaSpent: 3 }))
    requireThat(manaOutput.facts?.[fact] === expected, 'Actual consuming-source rules/payment accounting disagrees: ' + fact);
  return { schemaVersion: 4, sourceRevision, ...natural, actualFanoutTerminalsPerHuman: 110,
    ordinaryConsumingSourcePayment: true,
    projectionViewportsPerHuman: 2, engineRandomnessControlled: false, humanUxAcceptance: false, allInteractionsClaimed: false };
}

function verify({ inputs, packageDirectory, campaign, fanout, manaOutput, sourceRevision }) {
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
    fanout: read(path.join(fanout, 'result.json')), manaOutput: read(path.join(manaOutput, 'result.json')),
    replays: [0, 1].map(index => read(path.join(fanout, `projection-human-${index}/result.json`))) });
}
if (require.main === module) {
  try {
    const allowed = ['inputs', 'package', 'campaign', 'fanout', 'mana-output', 'source-revision', 'output'], values = {};
    for (let index = 2; index < process.argv.length; index += 2) {
      const key = process.argv[index].slice(2), value = process.argv[index + 1];
      requireThat(process.argv[index].startsWith('--') && allowed.includes(key) && !Object.hasOwn(values, key) && value && !value.startsWith('--'), 'Unknown or incomplete qualification argument.');
      values[key] = value;
    }
    requireThat(allowed.every(key => values[key]), 'Missing qualification argument.');
    const result = verify({ inputs: values.inputs, packageDirectory: values.package, campaign: values.campaign,
      fanout: values.fanout, manaOutput: values['mana-output'], sourceRevision: values['source-revision'] });
    fs.writeFileSync(values.output, JSON.stringify(result, null, 2) + '\n', { flag: 'wx' });
    console.log('Four natural mixed games, actual110-trigger rules/UI and ordinary consuming-source payment qualified; rare mechanic coverage and human UX remain separate.');
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { DECKS, assessNatural, assess, verify };
