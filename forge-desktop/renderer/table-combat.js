/* Combat gestures act on the real battlefield. Legal destinations and every
   assignment come from the engine; a drag carries one immutable prompt scope. */
function createTableCombat(arena, send) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.classList.add('table-combat-lines'); svg.setAttribute('aria-hidden', 'true'); arena.append(svg);
  const hint = document.createElement('div');
  hint.className = 'table-combat-hint'; hint.id = 'table-combat-instruction'; hint.setAttribute('role', 'status');
  const controls = document.createElement('section'); controls.className = 'table-combat-controls'; controls.hidden = true;
  controls.setAttribute('aria-label', 'Battlefield combat');
  controls.innerHTML = '<div class="table-combat-heading"><strong></strong><span></span></div><div class="table-combat-buttons"><button type="button" class="button secondary" data-table-combat-clear>Cancel selection</button><button type="button" class="button secondary" data-table-combat-undo>Remove assignment</button><button type="button" class="button primary" id="table-combat-confirm"></button></div>';
  controls.insertBefore(hint, controls.lastElementChild); arena.append(controls);
  const confirm = controls.querySelector('#table-combat-confirm'), clear = controls.querySelector('[data-table-combat-clear]'), undo = controls.querySelector('[data-table-combat-undo]');
  let state, cards = new Map(), selected, pressed, frame;
  const cardId = element => element?.dataset.tableCombat;
  const tile = id => [...arena.querySelectorAll('.battlefield-card[data-table-combat]')].find(element => cardId(element) === id);
  const scope = () => ({ sessionId: state?.id, promptId: state?.prompt?.id });
  const mode = () => state?.prompt?.inputType;
  const available = () => state?.phaseKey?.startsWith('COMBAT_') && !['finished', 'error'].includes(state?.status);
  const current = saved => state?.id === saved.sessionId && state?.prompt?.id === saved.promptId;
  const name = id => cards.get(id)?.faceDown ? 'Face-down creature' : cards.get(id)?.name || 'Creature';
  const defenderElement = defender => defender?.kind === 'player'
    ? arena.querySelector(`.match-life[data-match-player="${defender.id}"]`) : tile(defender?.id);
  const canSelect = id => mode() === 'InputAttack'
    ? state.combat?.attackOptions?.some(option => option.cardId === id && option.defenders.length)
    : mode() === 'InputBlock' && state.combat?.attackers.some(attack => attack.eligibleBlockerIds.includes(id));
  function selection(id) {
    return { id, key: cards.get(id)?.key, mode: mode(), scope: scope(),
      defenders: state.combat?.attackOptions?.find(option => option.cardId === id)?.defenders || [],
      keys: new Map([...cards].map(([id, card]) => [id, card.key])) };
  }
  function assign(g, target) {
    selected = null; paint();
    if (g.mode === 'InputBlock') send({ action: 'block', attackerKey: g.keys.get(target.cardId), blockerKey: g.key }, g.scope);
    else send({ action: 'attack', attackerKey: g.key, ...(target.kind === 'player' ? { defenderPlayerId: target.id } : { defenderKey: g.keys.get(target.id) }) }, g.scope);
  }
  function assignedTarget(g) {
    if (!g) return null;
    const attacks = state.combat?.attackers || [];
    return g.mode === 'InputAttack' ? attacks.find(attack => attack.cardId === g.id)?.defender
      : attacks.find(attack => attack.blockerIds.includes(g.id));
  }
  let controlPress;
  controls.addEventListener('pointerdown', event => { controlPress = { target: event.target.closest('button'), scope: scope(), selected }; });
  controls.addEventListener('click', event => {
    const button = event.target.closest('button');
    if (!button || button.disabled) return;
    event.stopPropagation();
    const captured = event.detail ? controlPress : { target: button, scope: scope(), selected };
    controlPress = null;
    if (!captured || captured.target !== button || !current(captured.scope)) return;
    if (button === clear) { selected = null; paint(); }
    else if (button === undo && captured.selected && current(captured.selected.scope)) {
      const target = assignedTarget(captured.selected);
      if (target) assign(captured.selected, target);
    } else if (button === confirm) { selected = null; send({ action: 'ok' }, captured.scope); }
  });
  function targetAt(g, element) {
    if (g.mode === 'InputBlock') {
      const id = cardId(element?.closest('.battlefield-card'));
      return state.combat?.attackers.find(attack => attack.cardId === id && attack.eligibleBlockerIds.includes(g.id));
    }
    const player = element?.closest('[data-match-player]');
    const id = cardId(element?.closest('.battlefield-card'));
    return g.defenders.find(defender => player ? defender.kind === 'player' && defender.id === Number(player.dataset.matchPlayer)
      : defender.kind === 'card' && defender.id === id);
  }
  function destination(g, event) {
    return targetAt(g, document.elementFromPoint(event.clientX, event.clientY));
  }
  const drag = createTableDrag(arena, {
    start(event) {
      const source = event.target.closest('#match-human .battlefield-card');
      if (!source || !['InputAttack', 'InputBlock'].includes(mode())) return;
      const id = cardId(source), card = cards.get(id);
      if (!card) return;
      const defenders = state.combat?.attackOptions?.find(option => option.cardId === id)?.defenders || [];
      if (mode() === 'InputAttack' ? !defenders.length : !state.combat?.attackers.some(attack => attack.eligibleBlockerIds.includes(id))) return;
      return { source, id, mode: mode(), key: card.key, defenders, scope: scope(),
        keys: new Map([...cards].map(([id, card]) => [id, card.key])) };
    },
    valid(g) { return g.source.isConnected && current(g.scope) && mode() === g.mode; },
    returnBounds(g) { return g.source.getBoundingClientRect(); },
    over(g, event) {
      document.querySelectorAll('.combat-drop-ready').forEach(element => element.classList.remove('combat-drop-ready'));
      const target = destination(g, event);
      (g.mode === 'InputBlock' ? tile(target?.cardId) : defenderElement(target))?.classList.add('combat-drop-ready');
      return { valid: Boolean(target), text: target ? g.mode === 'InputBlock' ? `Release to block ${name(target.cardId)}`
        : `Release to attack ${target.name}` : g.mode === 'InputBlock' ? 'Drag onto a highlighted attacker · Esc to cancel' : 'Drag onto an opponent or highlighted defender · Esc to cancel' };
    },
    canDrop(g, event) { return Boolean(destination(g, event)); },
    drop(g, event) {
      const target = destination(g, event);
      if (target) assign(g, target);
    },
    finish() { document.querySelectorAll('.combat-drop-ready').forEach(element => element.classList.remove('combat-drop-ready')); }
  });
  // Both gestures start with your creature. Selection is local until a legal
  // destination is chosen; never fall through to the generic one-click action.
  arena.addEventListener('pointerdown', event => {
    const source = event.target.closest('.battlefield-card, [data-match-player]');
    pressed = source ? { source, scope: scope(), selected } : null;
  });
  arena.addEventListener('pointercancel', () => { pressed = null; });
  arena.addEventListener('click', event => {
    if (!['InputAttack', 'InputBlock'].includes(mode())) return;
    const source = event.target.closest('.battlefield-card, [data-match-player]');
    if (!source) {
      if (!event.target.closest('button, details, #combat-view')) { selected = null; paint(); }
      return;
    }
    event.stopPropagation();
    const id = cardId(source), captured = event.detail ? pressed : { source, scope: scope(), selected };
    pressed = null;
    if (!captured || captured.source !== source || !current(captured.scope)) return;
    if (captured.selected && current(captured.selected.scope)) {
      const target = targetAt(captured.selected, source);
      if (target) { assign(captured.selected, target); return; }
    }
    if (source.closest('#match-human') && canSelect(id)) {
      selected = selected?.id === id ? null : selection(id); paint();
    }
  });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') { selected = null; pressed = null; controlPress = null; paint(); } });
  window.addEventListener('blur', () => { selected = null; pressed = null; controlPress = null; paint(); });
  function paint() {
    const attacks = available() ? state?.combat?.attackers || [] : [];
    arena.querySelectorAll('.combat-target-ready').forEach(element => element.classList.remove('combat-target-ready'));
    for (const element of arena.querySelectorAll('.battlefield-card')) {
      const id = cardId(element), attack = attacks.find(attack => attack.cardId === id);
      const blocks = attacks.filter(attack => attack.blockerIds.includes(id));
      element.classList.toggle('table-attacker', Boolean(attack));
      element.classList.toggle('table-blocker', blocks.length > 0);
      element.classList.toggle('table-combat-selected', id === selected?.id);
      element.classList.toggle('table-combat-ready', Boolean(element.closest('#match-human') && canSelect(id)));
      if (['InputAttack', 'InputBlock'].includes(mode())) element.setAttribute('aria-pressed', String(id === selected?.id));
      else element.removeAttribute('aria-pressed');
      element.querySelector('.table-combat-badge')?.remove();
      if (attack || blocks.length) {
        const badge = document.createElement('span'); badge.className = 'table-combat-badge';
        badge.textContent = attack ? `→ ${attack.defender?.name || 'Defender'}${attack.blocked || attack.blockerIds.length ? ' · Blocked' : ''}`
          : `Blocks ${blocks.map(attack => name(attack.cardId)).join(', ')}`;
        badge.title = badge.textContent; element.append(badge);
      }
    }
    if (selected) {
      if (mode() === 'InputAttack') selected.defenders.forEach(defender => defenderElement(defender)?.classList.add('combat-target-ready'));
      else attacks.filter(attack => attack.eligibleBlockerIds.includes(selected.id)).forEach(attack => tile(attack.cardId)?.classList.add('combat-target-ready'));
    }
    for (const portrait of arena.querySelectorAll('.match-life[data-match-player]')) {
      const count = attacks.filter(attack => attack.defender?.playerId === Number(portrait.dataset.matchPlayer)).length;
      const target = portrait.classList.contains('combat-target-ready');
      portrait.classList.toggle('combat-under-attack', count > 0);
      let label = portrait.querySelector('.combat-player-intent');
      if (!count && !target) { label?.remove(); continue; }
      if (!label) { label = document.createElement('span'); label.className = 'combat-player-intent'; portrait.append(label); }
      label.textContent = target ? 'Attack here' : `${count} incoming`;
    }
    const choosing = ['InputAttack', 'InputBlock'].includes(mode());
    controls.hidden = !available() || !choosing && !attacks.length;
    controls.dataset.mode = mode() === 'InputAttack' ? 'attack' : mode() === 'InputBlock' ? 'block' : 'view';
    const blockCount = attacks.reduce((total, attack) => total + attack.blockerIds.length, 0);
    controls.querySelector('.table-combat-heading strong').textContent = mode() === 'InputAttack' ? 'Choose attackers' : mode() === 'InputBlock' ? 'Choose blockers' : 'Combat';
    controls.querySelector('.table-combat-heading span').textContent = `${attacks.length} attacking · ${blockCount} blocking`;
    const message = selected ? `${name(selected.id)} → ${mode() === 'InputAttack' ? 'Choose a glowing player or defender.' : 'Choose a glowing attacker.'}`
      : mode() === 'InputAttack' ? state.combat?.attackOptions?.length ? 'Click a creature, then the player it should attack.' : 'No creatures can attack this combat.'
      : mode() === 'InputBlock' ? state.combat?.blockProblem || 'Click your creature, then the attacker it should block.'
      : 'Orange arrows show attacks. Blue lines show blocks.';
    if (hint.textContent !== message) hint.textContent = message;
    clear.hidden = !selected;
    undo.hidden = !assignedTarget(selected);
    undo.textContent = mode() === 'InputAttack' ? 'Recall attacker' : 'Remove block';
    confirm.hidden = !choosing;
    confirm.disabled = !state.prompt?.okEnabled || !state.combat || mode() === 'InputBlock' && Boolean(state.combat.blockProblem);
    confirm.textContent = mode() === 'InputAttack' ? attacks.length ? `Attack with ${attacks.length}` : 'No attacks'
      : blockCount ? 'Confirm blocks' : 'No blocks';
    scheduleLines();
  }
  function lines() {
    if (!available()) { svg.replaceChildren(); return; }
    const bounds = arena.getBoundingClientRect();
    svg.setAttribute('viewBox', `0 0 ${bounds.width} ${bounds.height}`);
    const point = element => {
      if (!element || !element.checkVisibility()) return null;
      const r = element.getBoundingClientRect(), row = element.closest('.battlefield-row');
      const clip = arena.classList.contains('scene-active') ? bounds : row?.getBoundingClientRect() || bounds;
      const x = r.left + r.width / 2, y = r.top + r.height / 2;
      return x < clip.left || x > clip.right || y < clip.top || y > clip.bottom ? null : { x: x - bounds.left, y: y - bounds.top };
    };
    const paths = [];
    function connect(from, to, type, edge) {
      const a = point(from), b = point(to);
      if (!a || !b) return;
      const midpoint = (a.y + b.y) / 2;
      paths.push(`<path class="${type}" data-table-edge="${esc(edge)}" d="M${a.x},${a.y} C${a.x},${midpoint} ${b.x},${midpoint} ${b.x},${b.y}" marker-end="url(#table-combat-arrow-${type})"/>`);
    }
    for (const attack of state?.combat?.attackers || []) {
      connect(tile(attack.cardId), defenderElement(attack.defender), 'attack-line', `${attack.cardId}:defender`);
      for (const blocker of attack.blockerIds) connect(tile(blocker), tile(attack.cardId), 'block-line', `${attack.cardId}:${blocker}`);
    }
    svg.innerHTML = '<defs>' + ['attack-line', 'block-line'].map(type => `<marker id="table-combat-arrow-${type}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M0 0 L10 5 L0 10z" class="${type}"/></marker>`).join('') + '</defs>' + paths.join('');
  }
  function scheduleLines() { cancelAnimationFrame(frame); frame = requestAnimationFrame(lines); }
  arena.addEventListener('scroll', scheduleLines, true);
  arena.addEventListener('worldlayout', scheduleLines);
  new ResizeObserver(scheduleLines).observe(arena);
  function render(next) {
    state = next; drag.refresh();
    svg.style.display = available() ? '' : 'none';
    if (selected && (!current(selected.scope) || selected.mode !== mode() || !available() || !canSelect(selected.id))) selected = null;
    cards = new Map((next.players || []).flatMap(player => player.zones.flatMap(zone => zone.cards)).map(card => [card.combatId || card.visualId, card]));
    paint();
  }
  return { render };
}
