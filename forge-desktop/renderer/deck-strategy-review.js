/* Both clients consume the engine's analysis; presentation never invents scores or combo claims. */
const deckStrategyReview = (() => {
  let view, pending = false, generation = 0;
  const current = () => view && state && view.report.deckId === state.id && view.report.revision === state.deck.revision;
  const dialog = () => $('strategy-review');
  const names = value => value.split('\n').map(name => name.trim()).filter(Boolean);
  const confidence = value => ({ 'curated-rules-line': 'Rules-based interaction', 'strategic-estimate': 'Strategic estimate', 'text-estimate': 'Estimated from card text' }[value] || 'Needs review');
  function status(message) { $('strategy-status').textContent = message; }
  function busy(value) {
    pending = value;
    dialog().querySelectorAll('button:not([data-close-review]), input, select, textarea').forEach(control => { control.disabled = value; });
  }
  async function refresh() {
    const deckId = state.id, revision = state.deck.revision, token = ++generation;
    busy(true); status('Reviewing your deck…');
    try {
      const next = await api.request('deckAnalysis', { deckId, revision });
      if (token !== generation || state.id !== deckId || state.deck.revision !== revision) { status('The deck changed. Refresh its review.'); return; }
      view = next; render(true); status('Review updated. Ready without setup; preferences are optional.');
    } catch (error) { status(error.message); }
    finally { if (token === generation) busy(false); }
  }
  function options(values, selected) { return values.map(value => `<option value="${esc(value)}" ${value === selected ? 'selected' : ''}>${esc(value === '' ? 'Use deck format (automatic)' : value === 'Constructed' ? 'Casual / any Constructed' : value)}</option>`).join(''); }
  function render(profileChanged = false) {
    if (!view) return;
    const { report, history } = view, p = report.profile, m = report.metrics;
    $('strategy-title').textContent = `${state.deck.name} · Strategy review`;
    if (profileChanged) {
      $('strategy-format').innerHTML = options(report.format.commanderIdentity || report.format.poolRequired ? ['', report.format.name]
        : ['', 'Constructed', 'Standard', 'Pioneer', 'Modern', 'Legacy', 'Vintage', 'Pauper'], p.rulesFormat);
      $('strategy-kind').value = p.strategy; $('strategy-pace').value = p.pace;
      $('strategy-plan').value = p.plan; $('strategy-backup').value = p.backup; $('strategy-turn').value = p.targetTurn;
      $('strategy-protected').value = p.protectedCards.join('\n'); $('strategy-pool').value = p.pool.join('\n');
      $('strategy-pool-label').hidden = !report.format.poolRequired;
    }
    $('strategy-overview').textContent = `${report.findings.filter(f => f.severity === 'review').length} checks to review · ${report.recommendations.length} swap ideas. Read a check, then try one change.`;
    $('strategy-metrics').textContent = `${report.format.name} · ${m.mainCards} main cards · ${m.lands} lands · ${m.earlyRamp} early ramp · ${m.unconditionalDraw + m.conditionalDraw} draw · ${m.interaction} interaction`;
    $('strategy-probability').textContent = m.landProbability === null ? 'Finish the main deck to see a land-drop estimate. Other supported checks are available now.'
      : `Land check: ${Math.round(m.landProbability * 100)}% chance to see ${m.landGoal} lands by turn ${m.landGoal}. Counts lands only; see how this works below.`;
    $('strategy-assumptions').textContent = m.probabilityAssumptions;
    const needsPool = report.findings.some(f => f.id === 'missing-pool');
    const needsFormat = report.format.name === 'Constructed' && !p.rulesFormat;
    $('strategy-question').hidden = !needsPool && !needsFormat;
    $('strategy-question-text').textContent = needsPool ? 'Which cards can you use? Add your draft or sealed pool to get swap ideas. Unused Sideboard cards are included automatically.'
      : 'Which format do you play? Using casual Constructed until you choose one.';
    $('strategy-answer').textContent = needsPool ? 'Add available cards' : 'Choose format';
    $('strategy-answer').dataset.field = needsPool ? 'strategy-pool' : 'strategy-format';
    $('strategy-findings').innerHTML = report.findings.map(f => `<article><strong>${esc(f.dimension)} · ${esc(f.title)}</strong><p>${esc(f.evidence)}</p><small>${esc(confidence(f.confidence))}</small></article>`).join('') || '<p>No specific issues found by the supported checks. Try an opening hand; this review does not cover every interaction.</p>';
    $('strategy-engines').innerHTML = report.engines.map(e => `<article><strong>${esc(e.name)}</strong><p>${e.missing.length ? `Missing: ${esc(e.missing.join(', '))}` : 'All listed pieces are present.'}</p><p>${esc(e.cards.join(' + '))}</p><p>Payoffs present: ${esc(e.payoffs.join(', ') || 'none detected')}</p><ol>${e.steps.map(step => `<li>${esc(step)}</li>`).join('')}</ol><p>Outputs: ${esc(e.outputs.join('; '))}</p><p>Requires: ${esc(e.requirements.join('; '))}</p><small>${esc(confidence(e.confidence))} · No simulator proof</small></article>`).join('') || '<p>No lines from the curated registry were found. Coverage is incomplete; inspect your other synergies.</p>';
    $('strategy-swaps').innerHTML = report.recommendations.map(r => {
      const decision = [...history].reverse().find(h => h.fingerprint === report.fingerprint && h.recommendationId === r.id);
      const decisions = [['consider', 'Add to Considering'], ['keep', 'Keep current'], ['dismiss', 'Dismiss suggestion'], ['tested', 'Mark tested (manual)']];
      return `<article><strong>${esc(r.cutName)} → ${esc(r.add.name)}</strong><p>${esc(r.benefit)}</p><p><b>Tradeoff:</b> ${esc(r.tradeoff)}</p><p>Mana value change: ${r.manaValueDelta > 0 ? '+' : ''}${r.manaValueDelta}. Roles: ${esc(Object.entries(r.roleDelta).map(([role, delta]) => `${role} ${delta > 0 ? '+' : ''}${delta}`).join(', ') || 'unchanged')}.</p><small>${r.eligibility === 'unknown' ? 'Format eligibility unknown' : 'Checked against bundled format rules; freshness is not live'} · Strategic estimate</small>${decision ? `<p>Your decision: ${esc(decision.decision)}${decision.note ? ` · ${esc(decision.note)}` : ''}</p>` : ''}<div class="strategy-actions">${decisions.map(([action, label]) => `<button type="button" data-review-id="${esc(r.id)}" data-review-decision="${action}">${label}</button>`).join('')}</div></article>`;
    }).join('') || '<p>No supported paired swaps match this list. Use the findings and functions to guide your next test.</p>';
    $('strategy-cards').innerHTML = report.cards.map(c => `<details><summary>${c.quantity}× ${esc(c.name)} · ${esc(c.section)}${c.protectedCard ? ' · Preserved' : ''}</summary>${c.functions.map(f => `<p><b>${esc(f.role)} · ${esc(f.event)}:</b> ${esc(f.provides)}${f.requires.length ? ` Requires: ${esc(f.requires.join('; '))}` : ''}</p>`).join('') || '<p>No function confidently detected. Inspect this card’s rules.</p>'}</details>`).join('');
    $('strategy-history').innerHTML = [...history].reverse().map(h => `<article><strong>${esc(h.decision)} · ${h.fingerprint === report.fingerprint ? 'Current deck version' : 'Earlier deck version'}</strong><p>${esc(h.recommendationId)} ${esc(h.note)}</p><small>${esc(new Date(h.created).toLocaleString())}</small></article>`).join('') || '<p>Record decisions or observations to build a history of your tests.</p>';
    $('strategy-limitations').textContent = report.limitations.join(' ');
  }
  async function saveProfile(event) {
    event.preventDefault(); if (pending || !current()) { status('Refresh the review before saving preferences.'); return; }
    const report = view.report;
    const profile = { rulesFormat: $('strategy-format').value, strategy: $('strategy-kind').value, pace: $('strategy-pace').value,
      plan: $('strategy-plan').value, backup: $('strategy-backup').value, targetTurn: Number($('strategy-turn').value),
      protectedCards: names($('strategy-protected').value), pool: names($('strategy-pool').value) };
    busy(true);
    try {
      const next = await api.request('analysisProfile', { deckId: report.deckId, revision: report.revision, metadataRevision: view.metadataRevision,
        fingerprint: report.fingerprint, profile });
      if (state.id !== report.deckId || state.deck.revision !== report.revision) { status('The deck changed. Refresh its review.'); return; }
      view = next; render(true); status('Preferences saved. Checks and swap ideas updated.');
    } catch (error) { status(error.message); }
    finally { busy(false); }
  }
  async function review(id, decision) {
    if (pending || !current()) { status('Refresh the review before recording a decision.'); return; }
    const report = view.report, note = $('strategy-note').value;
    busy(true);
    try {
      const next = await api.request('analysisReview', { deckId: report.deckId, revision: report.revision, metadataRevision: view.metadataRevision,
        fingerprint: report.fingerprint, recommendationId: id, decision, note });
      if (state.id !== report.deckId || state.deck.revision !== report.revision) { status('The deck changed. Refresh its review.'); return; }
      view = next; $('strategy-note').value = ''; render();
      status(decision === 'consider' ? 'Review saved. Suggested card added to Considering.' : 'Review saved with this deck version.');
    } catch (error) { status(error.message); }
    finally { busy(false); }
  }
  function onDeckChanged() { if (dialog()?.open && view && !current()) status('The deck changed. Refresh to review its current version.'); }
  document.addEventListener('DOMContentLoaded', () => {
    $('strategy-review-open').onclick = () => { if (!state) return; $('strategy-goals').open = false; dialog().showModal(); refresh(); };
    $('strategy-answer').onclick = () => { $('strategy-goals').open = true; $($('strategy-answer').dataset.field).focus(); };
    $('strategy-refresh').onclick = refresh;
    $('strategy-profile').onsubmit = saveProfile;
    $('strategy-observation').onclick = () => review('', 'observation');
    dialog().addEventListener('click', event => {
      if (event.target.closest('[data-close-review]')) dialog().close();
      const button = event.target.closest('[data-review-id]');
      if (button) review(button.dataset.reviewId, button.dataset.reviewDecision);
    });
  });
  return { onDeckChanged };
})();
