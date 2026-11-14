'use strict';
// The Control Panel (Sources/Preferences.swift, SettingsView.swift, BadgesPane.swift). Every page is built from state
// pushed by main; a change goes out as nt.act('setSetting', key, value) and the local copy is updated at once.
(function () {
  const nt = window.nt;
  const U = window.NTUI;
  const { h, fill, icon, toggle, select, segmented, modal, menu, NotchBadge, NotchPreview, BadgeEditor, BadgeBoard, spriteEl, syncSprites, accentHex } = U;
  const L = (k, ...a) => nt.L(k, ...a);
  const CAT = nt.catalog();
  U.setCatalog(CAT);

  // Sidebar, in the Mac's order. Fans, permissions and the lock screen are Mac-only and not listed.
  const PANES = [
    { id: 'overview', title: 'Overview', icon: 'layout-grid' },
    { id: 'modules', title: 'Modules', icon: 'puzzle' },
    { id: 'agents', title: 'Watching', icon: 'eye' },
    { id: 'phone', title: 'Pairing', icon: 'link' },
    { id: 'appearance', title: 'Appearance', icon: 'paintbrush' },
    { id: 'badges', title: 'Symbol', icon: 'star' },
    { id: 'store', title: 'Icon Library', icon: 'sparkles' },
    { id: 'sounds', title: 'Sound', icon: 'volume-2' },
    { id: 'behavior', title: 'Behavior', icon: 'hand' },
  ];
  const AGENT_NOTES = {
    claude: 'Review commands, file edits, answer questions', gemini: 'Review commands, file edits, answer questions',
    antigravity: 'Review commands, file edits, answer questions', codex: 'Review commands, file edits',
    cursor: 'Denies commands. Allows only while Cursor runs commands on its own', opencode: 'Review commands, file edits, web access',
  };
  const PLACE_LABEL = { auto: 'Automatic', left: 'Left side', right: 'Right side' };
  const BG_LABEL = { black: 'Black', glass: 'Frosted glass', gradient: 'Gradient', glow: 'Glowing edge' };
  const TOAST_LABEL = { island: 'Island', compact: 'Compact', card: 'Floating card', banner: 'Banner' };
  const TOAST_BLURB = { island: 'Slides down from the notch.', compact: 'Fits inside the notch.', card: 'A card floating below the notch.', banner: 'Wide, large text.' };
  const MOTION_LABEL = { smooth: 'Smooth', bouncy: 'Bouncy', snappy: 'Nhanh', reduced: 'Minimal' };
  const THEME_ID = (t) => t.id;

  let state = null;
  let lastSig = '';
  let pane = 'overview';
  let dragMod = null;
  let dragItem = null;
  let showPhoneCode = false;
  let storeCat = '';
  let storeQuery = '';
  let justAdded = null;

  const S = () => state.settings;
  const isWin = nt.platform === 'win32';
  let reportDialog = null;
  const sig = () => JSON.stringify(state);

  // ---- state in and actions out ---------------------------------------------------------------------------------------

  /** Local change: the page moves at once, main gets the value and pushes the echo. */
  function set(key, value, { rerender = true } = {}) {
    state = { ...state, settings: { ...state.settings, [key]: value } };
    nt.act('setSetting', key, value);
    U.setSettingsForAccent(state.settings);
    lastSig = sig();
    if (rerender) render();
  }

  function setItems(items) {
    state = { ...state, items };
    nt.act('setItems', items);
    lastSig = sig();
    render();
  }

  // ---- building blocks ------------------------------------------------------------------------------------------------

  function card(title, sub, ...kids) {
    return h('section', { class: 'card' },
      title ? h('div', { class: 'card-h' }, h('div', { class: 'card-t' }, title), sub ? h('div', { class: 'card-s' }, sub) : null) : null,
      ...kids);
  }

  /** One setting row: label (and an optional caption) on the left, the control on the right. */
  function row(label, control, caption) {
    return h('div', { class: 'srow' },
      h('div', { class: 'srow-t' }, h('div', { class: 'srow-l' }, label), caption ? h('div', { class: 'srow-c dim' }, caption) : null),
      h('div', { class: 'srow-c2' }, control));
  }

  function group(...rows) {
    const kids = [];
    rows.forEach((r, i) => { if (i) kids.push(h('div', { class: 'sep' })); kids.push(r); });
    return h('div', { class: 'group' }, kids);
  }

  function button(label, onClick, opts = {}) {
    return h('button', { class: `btn${opts.primary ? ' primary' : ''}${opts.danger ? ' danger' : ''}${opts.small ? ' sm' : ''}`,
      type: 'button', disabled: opts.disabled, onclick: onClick }, opts.icon ? icon(opts.icon, 14) : null, label);
  }

  function slider(value, min, max, step, onInput, onChange) {
    const out = h('span', { class: 'slider-v dim' });
    const input = h('input', { type: 'range', min, max, step, value });
    const show = (v) => { out.textContent = v; };
    input.addEventListener('input', () => { onInput(Number(input.value)); show(input.value); });
    input.addEventListener('change', () => { if (onChange) onChange(Number(input.value)); });
    return { el: h('div', { class: 'slider' }, input, out), show, input };
  }

  function stat(iconName, value, label) {
    return h('div', { class: 'stat' }, icon(iconName, 16, 'stat-i'), h('div', { class: 'stat-v' }, String(value)), h('div', { class: 'stat-l dim' }, label));
  }

  function tileIcon(name, on) {
    return h('span', { class: `mtile${on ? ' on' : ''}` }, icon(name, 16));
  }

  // ---- overview -------------------------------------------------------------------------------------------------------

  /** The notch on a desktop-like tile, so its top edge reads as the screen's edge. */
  function wall(node) { return h('div', { class: 'wall' }, node); }

  function nextSteps() {
    const out = [];
    for (const a of state.agents) if (a.present && !a.installed) out.push({ kind: 'agent', agent: a });
    if (!state.sync.phones.length) out.push({ kind: 'phone' });
    return out;
  }

  function overview() {
    const s = S();
    const live = state.live || { working: 0, alerts: 0 };
    const total = CAT.modules.length;
    const enabled = CAT.modules.filter((m) => !s.disabled.includes(m.id)).length;
    const steps = nextSteps();
    return [
      h('div', { class: 'hero' },
        wall(NotchPreview(s, live, {})),
        h('div', { class: 'hero-text' }, h('div', { class: 'hero-t' }, 'NotchTasks'),
          h('div', { class: 'dim' }, L('The notch shows what\'s running, and stays quiet when nothing is.')))),
      h('div', { class: 'stats' }, stat('zap', live.working, L('Running')), stat('octagon-x', live.alerts, L('CI errors')),
        stat('puzzle', `${enabled}/${total}`, L('Modules'))),
      s.onboardingDone && steps.length ? h('div', { class: 'block' }, h('div', { class: 'sec-t' }, L('Next steps')),
        steps.map((st) => h('div', { class: 'step' },
          icon(st.kind === 'agent' ? 'sparkles' : 'smartphone', 16, 'dim'),
          h('div', { class: 'step-t' }, st.kind === 'agent' ? `${st.agent.name} · ${L('Answer agents on the notch')}` : L('Pairing')),
          button(st.kind === 'agent' ? L('On') : L('Pairing'), () => go(st.kind === 'agent' ? 'agents' : 'phone'), { small: true })))) : null,
      h('div', { class: 'sec-t' }, L('Modules')),
      CAT.moduleGroups.map((g) => h('div', { class: 'block' }, h('div', { class: 'grp-t' }, L(g.title)),
        h('div', { class: 'grid2' }, g.ids.map((id) => moduleCell(id))))),
      h('div', { class: 'row gap wrap' },
        button(L('See the guide'), () => nt.act('openOnboarding')),
        button(L('Report a problem'), openReport)),
    ];
  }

  function moduleCell(id) {
    const m = CAT.modules.find((x) => x.id === id);
    const on = !S().disabled.includes(id);
    return h('div', { class: 'mcell' }, tileIcon(m.icon, on),
      h('div', { class: `mcell-t${on ? '' : ' off'}` }, L(m.title)),
      toggle(on, (v) => setModuleOn(id, v), L(m.title)));
  }

  function setModuleOn(id, on) {
    const disabled = S().disabled.filter((x) => x !== id);
    if (!on) disabled.push(id);
    set('disabled', disabled);
  }

  function go(id) { pane = id; render(); }

  // ---- modules --------------------------------------------------------------------------------------------------------

  function modules() {
    const s = S();
    const reorder = (from, to) => {
      if (!from || from === to) return;
      const list = s.modules.filter((x) => x !== from);
      list.splice(list.indexOf(to), 0, from);
      set('modules', list);
    };
    return [
      h('div', { class: 'hint-row' },
        h('span', { class: 'dim' }, L('Drag to reorder on the notch.')),
        h('span', { class: 'cols' }, h('span', { class: 'col w-pl' }, L('Position when collapsed')), h('span', { class: 'col w-col' }, L('Color')),
          h('span', { class: 'col w-tg' }, L('On')))),
      h('div', { class: 'list' }, s.modules.map((id) => {
        const m = CAT.modules.find((x) => x.id === id);
        if (!m) return null;
        const on = !s.disabled.includes(id);
        const color = s.moduleColors[id] || '#808080';
        const colorInput = h('input', { type: 'color', class: 'color', value: color, title: L('Color'),
          oncontextmenu: (e) => { e.preventDefault(); const map = { ...s.moduleColors }; delete map[id]; set('moduleColors', map); },
          onchange: (e) => set('moduleColors', { ...s.moduleColors, [id]: e.target.value.toUpperCase() }) });
        return h('div', { class: 'mrow', draggable: 'true',
          ondragstart: (e) => { dragMod = id; e.dataTransfer.setData('text/plain', id); e.dataTransfer.effectAllowed = 'move'; },
          ondragover: (e) => e.preventDefault(),
          ondrop: (e) => { e.preventDefault(); reorder(dragMod, id); dragMod = null; } },
        h('span', { class: 'grip' }, icon('grip-vertical', 14)),
        tileIcon(m.icon, on),
        h('span', { class: 'mtext' }, h('span', { class: `mt${on ? '' : ' off'}` }, L(m.title)), h('span', { class: 'ms dim' }, L(m.blurb))),
        select(['auto', 'left', 'right'].map((v) => ({ value: v, label: L(PLACE_LABEL[v]) })), s.placements[id] || 'auto',
          (v) => set('placements', { ...s.placements, [id]: v }), L('Position when collapsed')),
        colorInput,
        toggle(on, (v) => setModuleOn(id, v), L(m.title)));
      })),
    ];
  }

  // ---- watch list, agents, build tools ------------------------------------------------------------------------------

  function itemSymbol(item) {
    return item.symbolName || (item.kind === 'dev' ? 'hammer' : item.kind === 'app' ? 'app-window' : 'terminal');
  }

  function itemIcon(item, size = 26) {
    if (item.icon) return NotchBadge(item.icon, size, accentHex(S()));
    return icon(itemSymbol(item), size * 0.6, 'item-ico');
  }

  function addPreset(p) {
    const item = { ...p, id: crypto.randomUUID() };
    setItems([...state.items, item]);
  }

  function presetAdded(p) {
    return state.items.some((i) => i.kind === p.kind && i.process === p.process && i.args === p.args && i.bundleID === p.bundleID);
  }

  function addMenu(anchor) {
    const sections = CAT.presetGroups.map((g) => ({ title: L(g.title), items: g.items.map((p) => ({
      label: p.name + (presetAdded(p) ? L(' (added)') : ''), disabled: presetAdded(p), onClick: () => addPreset(p) })) }));
    sections.push({ title: L('Build & CI'), items: CAT.buildPresets.map((p) => ({ label: p.name + (presetAdded(p) ? L(' (added)') : ''),
      disabled: presetAdded(p), onClick: () => addPreset(p) })) });
    sections.push({ title: L('Open apps'), empty: L('No apps'), items: (state.running || []).map((r) => ({
      label: r.name, onClick: () => setItems([...state.items, { id: crypto.randomUUID(), name: r.name, kind: 'app', bundleID: r.bundleID, enabled: true }]) })) });
    sections.push({ items: [
      { label: L('Choose another app…'), onClick: () => nt.act('pickApp') },
      { label: L('Enter a process…'), onClick: () => openItemEditor(null) }] });
    menu(anchor, sections);
  }

  function agentNote(a) { return L(AGENT_NOTES[a.kind] || 'Review commands, file edits'); }

  function watch() {
    const s = S();
    const itemRows = state.items.map((item) => h('div', { class: 'irow', draggable: 'true',
      ondragstart: (e) => { dragItem = item.id; e.dataTransfer.setData('text/plain', item.id); },
      ondragover: (e) => e.preventDefault(),
      ondrop: (e) => {
        e.preventDefault();
        if (!dragItem || dragItem === item.id) return;
        const list = state.items.slice();
        const from = list.findIndex((x) => x.id === dragItem);
        const [moved] = list.splice(from, 1);
        list.splice(list.findIndex((x) => x.id === item.id), 0, moved);
        dragItem = null;
        setItems(list);
      } },
    grip(), itemIcon(item), h('div', { class: 'itext' }, h('div', { class: 'it' }, item.name),
      h('div', { class: 'is dim' }, (nt.itemIsBuild(item) ? L('Build & CI') : L('AI agent')) + ' · ' + nt.itemSummary(item))),
    h('span', { class: 'sp' }),
    button(L('Edit'), () => openItemEditor(item), { small: true }),
    toggle(item.enabled, (v) => setItems(state.items.map((x) => (x.id === item.id ? { ...x, enabled: v } : x))), L('On')),
    h('button', { class: 'icon-btn', type: 'button', title: L('Remove'), 'aria-label': L('Remove'),
      onclick: () => setItems(state.items.filter((x) => x.id !== item.id)) }, icon('trash-2', 14))));

    const agents = state.agents.map((a) => {
      const setup = a.setup && a.installed ? h('span', { class: 'warn' }, L(a.setup.missing), button(L(a.setup.button),
        () => nt.act('openSetup', a.kind), { small: true })) : null;
      return h('div', { class: `arow${a.present ? '' : ' absent'}` },
        icon('bot', 22, 'dim'),
        h('div', { class: 'itext' }, h('div', { class: 'it' }, a.name,
          h('span', { class: `chip ${a.tested ? 'ok' : ''}` }, L(a.tested ? 'Tested' : 'Not checked'))),
        h('div', { class: 'is dim' }, a.present ? agentNote(a) : L('Not found on this computer'))),
        h('span', { class: 'sp' }),
        setup,
        a.present && a.installed && !a.setup ? h('span', { class: 'ok-t' }, icon('circle-check', 14), L('Ready')) : null,
        toggle(a.installed, (on) => {
          state = { ...state, agents: state.agents.map((x) => (x.kind === a.kind ? { ...x, installed: on } : x)) };
          nt.act(on ? 'installHook' : 'uninstallHook', a.kind);
          render();
        }, a.name));
    });

    return [
      h('p', { class: 'dim lead' }, L('What the notch watches. Only what\'s working shows on the notch.')),
      h('div', { class: 'list' }, itemRows.length ? itemRows : h('div', { class: 'empty dim' }, L('Nothing here yet.'))),
      h('div', { class: 'row gap' }, button(L('Add item to watch'), (e) => addMenu(e.currentTarget), { icon: 'circle-plus' }),
        h('span', { class: 'dim small' }, L('Choose an agent, an open app, or type a process name.'))),
      card(L('Answer agents on the notch'), L('When an agent asks, the notch shows answer buttons.'),
        h('div', { class: 'list' }, agents),
        state.agentError ? h('div', { class: 'err' }, state.agentError) : null,
        h('div', { class: 'dim small pt' }, L('Takes effect the next time the agent starts.'))),
      buildTools(),
    ];
  }

  function grip() { return h('span', { class: 'grip' }, icon('grip-vertical', 14)); }

  let showAllTools = false;
  function buildTools() {
    const s = S();
    const tools = showAllTools ? [...new Set([...s.seenTools, ...CAT.knownTools])] : s.seenTools;
    return card('Build & CI', null,
      h('div', { class: 'row between' }, h('span', { class: 'dim small' },
        s.seenTools.length || showAllTools ? L('Nothing to add: any running tool shows up. Click a tool to change its icon.')
          : L('No builds seen yet. Turn on "Show all" to set icons ahead of time.'))),
      h('div', { class: 'row between' }, h('span', { class: 'dim small' }, ''),
        h('label', { class: 'check' }, h('input', { type: 'checkbox', checked: showAllTools,
          onchange: (e) => { showAllTools = e.target.checked; render(); } }), L('Show all tools'))),
      h('div', { class: 'tools' }, tools.map((t) => {
        const custom = s.toolIcons[t];
        return h('button', { class: 'tool', type: 'button', onclick: () => openToolEditor(t) },
          custom ? NotchBadge(custom, 20, accentHex(s)) : icon('hammer', 16, 'dim'),
          h('span', { class: 'tool-t' }, t),
          custom ? h('span', { class: 'dot' }) : null);
      })));
  }

  function openToolEditor(tool) {
    const cur = S().toolIcons[tool] || { id: crypto.randomUUID(), symbol: 'hammer.fill', shape: 'rounded', border: false, right: false };
    BadgeEditor({ badge: cur, title: L('Symbol'), subtitle: tool,
      onChange: (b) => set('toolIcons', { ...S().toolIcons, [tool]: b }),
      onReset: () => { const map = { ...S().toolIcons }; delete map[tool]; set('toolIcons', map); } });
  }

  // The item editor: name, kind, the process, CPU threshold, and what it matches right now.
  function openItemEditor(item) {
    const isNew = !item;
    let it = item ? { ...item } : { id: crypto.randomUUID(), name: '', kind: 'process', process: '', args: null, childTasks: false, enabled: true };
    const configurable = it.kind === 'process' || it.kind === 'app';
    const threshold = () => (it.threshold ?? (it.kind === 'app' ? 5 : it.childTasks ? 0.5 : 1.5));
    const valid = () => it.name.trim() && (it.kind !== 'process' || it.process.trim() || (it.args || '').trim());
    const body = h('div', { class: 'stack' });
    const save = h('button', { class: 'btn primary', type: 'button', onclick: () => {
      const clean = { ...it, name: it.name.trim(), process: (it.process || '').trim() };
      setItems(isNew ? [...state.items, clean] : state.items.map((x) => (x.id === clean.id ? clean : x)));
      dlg.close();
    } }, isNew ? L('Add') : L('Save'));
    const refresh = () => { save.disabled = !valid(); };

    function draw() {
      const iconBtn = h('button', { class: 'icon-pick', type: 'button', title: L('Change icon'), onclick: () => {
        BadgeEditor({ badge: it.icon || { id: crypto.randomUUID(), symbol: itemSymbol(it), shape: 'rounded', border: false, right: false },
          title: L('Symbol'), subtitle: it.name || null,
          onChange: (b) => { it = { ...it, icon: b }; draw(); },
          onReset: () => { it = { ...it, icon: null }; draw(); } });
      } }, itemIcon(it, 30));
      const matches = state.matches && state.matches.id === it.id ? state.matches.rows : null;
      fill(body, [
        h('div', { class: 'ie-head' }, iconBtn, h('div', { class: 'itext' }, h('div', { class: 'it' }, it.name || L(isNew ? 'Add item to watch' : 'Watched item')),
          h('div', { class: 'is dim' }, it.inBuild ? 'Build & CI' : 'AI agent'))),
        h('label', { class: 'field' }, h('span', { class: 'field-l' }, L('Display name')),
          h('input', { class: 'inp', value: it.name, placeholder: 'vd: My Agent', oninput: (e) => { it.name = e.target.value; refresh(); } })),
        configurable ? h('div', { class: 'field' }, h('span', { class: 'field-l' }, L('Show in')),
          segmented([{ value: false, label: 'AI agent' }, { value: true, label: 'Build & CI' }], !!it.build,
            (v) => { it = { ...it, build: v }; draw(); })) : null,
        it.kind === 'process' ? h('div', { class: 'block' },
          h('label', { class: 'field' }, h('span', { class: 'field-l' }, L('Process name')),
            h('span', { class: 'row gap' }, h('input', { class: 'inp', value: it.process, placeholder: 'vd: node',
              oninput: (e) => { it.process = e.target.value; refresh(); } }))),
          h('label', { class: 'field' }, h('span', { class: 'field-l' }, L('Command line contains')),
            h('input', { class: 'inp', value: it.args || '', placeholder: L('optional, e.g. gemini-cli'),
              oninput: (e) => { it.args = e.target.value || null; refresh(); } })),
          h('div', { class: 'field' }, h('span', { class: 'field-l' }, L('Each line on the notch is')),
            segmented([{ value: false, label: L('Each run') }, { value: true, label: L('Each subprocess (runner, worker)') }],
              !!it.childTasks, (v) => { it = { ...it, childTasks: v }; draw(); }))) : null,
        configurable ? h('div', { class: 'field' }, h('span', { class: 'field-l' }, L('Working when CPU is above')),
          slider(threshold(), 0.5, 50, 0.5, (v) => { it = { ...it, threshold: v }; }, null).el) : null,
        it.kind === 'process' ? h('div', { class: 'field' }, h('span', { class: 'field-l' }, L('Matching: {0}', matches ? matches.length : 0)),
          h('div', { class: 'matches' }, matches && matches.length ? matches.map((m) => h('div', { class: 'mt-row' },
            h('div', { class: 'mono' }, `${m.name} · pid ${m.pid}`), h('div', { class: 'mono dim small' }, m.command))) : h('div', { class: 'dim small' },
            L('Not found. Open the tool, then click Check again.'))),
          h('div', { class: 'row gap' }, button(L('Check again'), () => nt.act('checkItem', it), { small: true }))) : null]);
      refresh();
    }
    draw();
    const dlg = modal({ width: 520, title: null, body: h('div', { class: 'ie' }, body),
      footer: [button(L('Cancel'), () => dlg.close()), save] });
    if (it.kind === 'process') nt.act('checkItem', it);
  }

  // ---- phone ----------------------------------------------------------------------------------------------------------

  function proStatus(until) {
    if (!until || until <= Date.now()) return L('No Pro: one phone, basic notifications');
    if (until >= 30_000_000_000_000) return L('Pro lifetime');
    return L('Pro until {0}', new Date(until).toLocaleDateString());
  }

  function qrBlock(url) {
    const img = h('img', { class: 'qr', alt: 'QR', src: '' });
    nt.qr(url).then((src) => { img.src = src; });
    return h('div', { class: 'qr-box' }, img);
  }

  function copy(text) { navigator.clipboard.writeText(text).catch(() => {}); }

  function phone() {
    const sy = state.sync;
    const hasPhones = sy.phones.length > 0;
    const pairing = (sy.url && !hasPhones) || (hasPhones && showPhoneCode);
    return [
      card(null, null,
        h('div', { class: 'pro-t' }, proStatus(sy.proUntil)),
        h('div', { class: 'dim' }, L('Answer agents and see what\'s running right on your phone. Data is encrypted between your devices.'))),
      !hasPhones ? card(null, null,
        h('ol', { class: 'steps' }, h('li', {}, L('Install NotchTasks on your phone')), h('li', {}, L('Tap Pair device')),
          h('li', {}, L('Scan the QR code with the app or Camera')))) : null,
      pairing ? card(null, null,
        h('div', { class: 'pair' }, qrBlock(sy.url), h('div', { class: 'pair-text' },
          h('div', {}, L('Open NotchTasks on your phone → Pair with computer, then scan this code.')),
          h('div', { class: 'dim small' }, L('This code only works for your devices.')),
          h('div', { class: 'row gap wrap' }, button(L('Copy code'), () => copy(sy.url), { small: true }),
            hasPhones ? button(L('Hide code'), () => { showPhoneCode = false; render(); }, { small: true }) : null)))) : null,
      !pairing ? h('div', { class: 'row gap' }, button(sy.busy ? L('Creating code…') : (hasPhones ? L('Pair another device…') : L('Pair device…')),
        () => { if (hasPhones) { showPhoneCode = true; render(); } if (!sy.url) nt.act('pair'); }, { primary: true, disabled: !!sy.busy })) : null,
      sy.problem ? h('div', { class: 'warn' }, sy.problem) : null,
      h('div', { class: 'row gap' }, button(L('Report a problem'), openReport, { small: true })),
      hasPhones ? group(
        row(L('Connect directly on the same Wi‑Fi'), toggle(sy.lan, (v) => { state = { ...state, sync: { ...sy, lan: v } }; nt.act('setSyncOption', 'lan', v); render(); })),
        row(L('Only alert my phone when I\'m away'), toggle(sy.onlyAway, (v) => { state = { ...state, sync: { ...sy, onlyAway: v } }; nt.act('setSyncOption', 'onlyAway', v); render(); })),
        row(L('Connecting…'), h('span', { class: 'dim' }, sy.direct > 0 ? L('{0} devices over Wi‑Fi', sy.direct) : L('Over the Internet')))) : null,
      hasPhones ? h('p', { class: 'dim small' }, L('On the same Wi‑Fi your phone gets updates instantly; elsewhere, over the Internet. While you\'re using this computer, your phone stays quiet.')) : null,
      hasPhones ? card(L('Paired'), L('Every device will disconnect from this computer. Pairing again uses a new code.'),
        h('div', { class: 'list' }, sy.phones.map((p) => h('div', { class: 'prow' }, icon('smartphone', 16, 'dim'), h('span', { class: 'it' }, p.name),
          h('span', { class: 'sp' }), p.pro ? h('span', { class: 'pro' }, 'Pro') : null,
          h('button', { class: 'icon-btn', type: 'button', title: L('Unpair'), 'aria-label': L('Unpair'),
            onclick: () => nt.act('unpair', p.uid) }, icon('x', 14))))),
        h('div', { class: 'row gap' }, button(L('Unpair all'), () => nt.act('unpairAll'), { danger: true, small: true }))) : null,
    ];
  }

  // ---- appearance -----------------------------------------------------------------------------------------------------

  function themeTile(t) {
    const s = S();
    const selected = s.background === t.background && s.toastStyle === t.toastStyle && s.accent === t.accent &&
      s.motion === t.motion && (t.accent !== 'custom' || s.customAccent === t.customAccent);
    const accent = t.accent === 'custom' ? t.customAccent : (CAT.accents.find((a) => a.name === t.accent) || {}).hex || '#0A84FF';
    const bg = t.background === 'gradient' ? `linear-gradient(135deg, ${t.gradientA}, ${t.gradientB})`
      : t.background === 'glass' ? 'rgba(90,90,90,.85)' : '#1f1f1f';
    return h('button', { class: `theme${selected ? ' on' : ''}`, type: 'button', onclick: () => {
      nt.act('applyTheme', THEME_ID(t));
      const patch = { background: t.background, gradientA: t.gradientA, gradientB: t.gradientB, accent: t.accent,
        customAccent: t.customAccent, toastStyle: t.toastStyle, motion: t.motion };
      state = { ...state, settings: { ...state.settings, ...patch } };
      lastSig = sig();
      render();
    } },
    h('span', { class: 'theme-art', style: { background: bg } },
      h('span', { class: `theme-notch${t.background === 'glow' ? ' glow' : ''}`, style: { '--acc': accent } },
        h('i', { style: { background: accent } }), h('b', { style: { background: accent } }))),
    h('span', { class: 'theme-t' }, h('span', {}, L(t.title)), selected ? icon('circle-check', 14, 'acc') : null));
  }

  function appearance() {
    const s = S();
    return [
      h('div', { class: 'hero' }, wall(NotchPreview(s, state.live, {})), h('span', { class: 'sp' }),
        button(L('Test alert'), () => nt.act('testToast'), { primary: true, icon: 'bell-ring' })),
      h('div', { class: 'themes' }, CAT.themes.map(themeTile)),
      card('Notch', isWin ? L('On this computer, the notch is a pill floating at the top centre of the screen.') : null,
        group(
          row(L('Background'), select(CAT.backgrounds.map((v) => ({ value: v, label: L(BG_LABEL[v]) })), s.background, (v) => set('background', v))),
          s.background === 'gradient' ? row(L('Top color'), colorInput(s.gradientA, (v) => set('gradientA', v))) : null,
          s.background === 'gradient' ? row(L('Bottom color'), colorInput(s.gradientB, (v) => set('gradientB', v))) : null,
          row(L('Size'), segmented([{ value: 'compact', label: L('Compact') }, { value: 'regular', label: L('Large') }], s.size, (v) => set('size', v))),
          row(L('Motion'), segmented(CAT.motions.map((v) => ({ value: v, label: L(MOTION_LABEL[v]) })), s.motion, (v) => set('motion', v))))),
      card(L('Notifications'), L(TOAST_BLURB[s.toastStyle]),
        group(row(L('Style'), segmented(CAT.toastStyles.map((v) => ({ value: v, label: L(TOAST_LABEL[v]) })), s.toastStyle, (v) => set('toastStyle', v))))),
      card(L('Color'), null,
        group(
          row(L('Accent color'), select(CAT.accents.map((a) => ({ value: a.name, label: L(a.title) })), s.accent, (v) => set('accent', v))),
          s.accent === 'custom' ? row(L('Custom color'), colorInput(s.customAccent, (v) => set('customAccent', v))) : null)),
      card(L('Back up settings'), L('All your customization in one .json file.'),
        h('div', { class: 'row gap' }, button(L('Export…'), () => nt.act('exportProfile'), { small: true }),
          button(L('Import…'), () => nt.act('importProfile'), { small: true }))),
    ];
  }

  function colorInput(value, onChange) {
    return h('input', { type: 'color', class: 'color', value, onchange: (e) => onChange(e.target.value.toUpperCase()) });
  }

  // ---- badges and icon store ------------------------------------------------------------------------------------------

  function screenStrip(badges) {
    const s = S();
    const left = badges.filter((b) => !b.right);
    const right = badges.filter((b) => b.right);
    const size = s.size === 'regular' ? 20 : 16;
    return h('div', { class: 'desk' }, h('div', { class: 'notch-strip' },
      h('div', { class: 'ns-side' }, left.map((b) => NotchBadge(b, size, accentHex(s)))),
      h('span', { class: 'ns-cam' }),
      h('div', { class: 'ns-side' }, right.map((b) => NotchBadge(b, size, accentHex(s))))));
  }

  function openBadge(id) {
    const b = S().badges.find((x) => x.id === id);
    if (!b) return;
    const save = (nb) => set('badges', S().badges.map((x) => (x.id === nb.id ? nb : x)), { rerender: true });
    BadgeEditor({ badge: b, title: L('Pinned icon'), subtitle: b.right ? L('Right of the notch') : L('Left of the notch'),
      onChange: save, onDelete: () => set('badges', S().badges.filter((x) => x.id !== id)) });
  }

  function addBadge(right) {
    const s = S();
    const count = s.badges.filter((b) => b.right === right).length;
    if (count >= CAT.perSide) return;
    const b = { id: crypto.randomUUID(), symbol: 'star', shape: 'circle', border: false, right };
    set('badges', [...s.badges, b]);
    openBadge(b.id);
  }

  function badges() {
    const s = S();
    const board = BadgeBoard(s.badges, {
      perSide: CAT.perSide,
      onChange: (list) => set('badges', list),
      onEdit: ({ id, right }) => { if (id) openBadge(id); else addBadge(right); },
      onStore: () => go('store'),
    });
    return [card(L('Main screen'), L('Icons always shown on the collapsed notch.'),
      screenStrip(s.badges), board)];
  }

  function store() {
    const s = S();
    const cats = [...new Set(CAT.sprites.map((x) => x.category).filter((c) => c !== 'pets'))];
    const q = storeQuery.trim().toLowerCase();
    const shown = CAT.sprites.filter((x) => x.category !== 'pets' && (!storeCat || x.category === storeCat)
      && (!q || L(x.title).toLowerCase().includes(q) || x.category.toLowerCase().includes(q)));
    const used = (id) => s.badges.some((b) => b.sprite === id) || state.items.some((i) => i.icon && i.icon.sprite === id)
      || Object.values(s.toolIcons).some((b) => b.sprite === id);
    const search = h('input', { class: 'inp narrow', value: storeQuery, placeholder: L('Search'),
      oninput: (e) => { storeQuery = e.target.value; render(); } });
    return [
      h('div', { class: 'store-head' }, h('div', { class: 'store-t' }, L('Available')),
        h('span', { class: 'dim small' }, L('{0} animated icons · Free', CAT.sprites.filter((x) => x.category !== 'pets').length)),
        h('span', { class: 'sp' }), search,
        h('button', { class: 'icon-btn', type: 'button', title: L('Restart every animated icon at once'),
          onclick: () => syncSprites(document) }, icon('refresh-cw', 14))),
      h('div', { class: 'chips' }, h('button', { class: `chip-b${storeCat === '' ? ' on' : ''}`, type: 'button', onclick: () => { storeCat = ''; render(); } }, L('All')),
        cats.map((c) => h('button', { class: `chip-b${storeCat === c ? ' on' : ''}`, type: 'button', onclick: () => { storeCat = c; render(); } }, L(c)))),
      shown.length ? h('div', { class: 'sprite-tiles' }, shown.map((x) => h('button', {
        class: `stile${justAdded === x.id ? ' added' : ''}`, type: 'button', title: L(x.title),
        onclick: (e) => storeMenu(e.currentTarget, x) },
      h('span', { class: 'stile-art' }, spriteEl(nt.spriteURL(x.id), x.frames, x.fps, 40, x.pixel), used(x.id) ? h('span', { class: 'dot' }) : null),
      h('span', { class: 'stile-t' }, L(x.title))))) : h('div', { class: 'empty dim' }, L('No icons match “{0}”.', storeQuery)),
      h('div', { class: 'dim small' }, icon('users', 14), ' ', L('Community packs: coming soon.')),
    ];
  }

  function storeMenu(anchor, sprite) {
    const s = S();
    const icon2 = { id: crypto.randomUUID(), symbol: 'star', sprite: sprite.id, plain: true, shape: 'circle', border: false, right: false };
    const flash = () => { justAdded = sprite.id; render(); setTimeout(() => { justAdded = null; render(); }, 1600); };
    const full = (right) => s.badges.filter((b) => b.right === right).length >= CAT.perSide;
    menu(anchor, [
      { title: L('Pinned icon on the notch'), items: [
        { label: L('Add on the left'), disabled: full(false), onClick: () => { set('badges', [...s.badges, { ...icon2, right: false }]); flash(); } },
        { label: L('Add on the right'), disabled: full(true), onClick: () => { set('badges', [...s.badges, { ...icon2, right: true }]); flash(); } }] },
      { title: L('Set for a watched app'), items: state.items.map((it) => ({ label: it.name,
        onClick: () => { setItems(state.items.map((x) => (x.id === it.id ? { ...x, icon: { ...icon2, right: false } } : x))); flash(); } })) },
      { title: L('Set for a build tool'), items: s.seenTools.map((t) => ({ label: t,
        onClick: () => { set('toolIcons', { ...s.toolIcons, [t]: { ...icon2, right: false } }); flash(); } })) },
    ]);
  }

  // ---- sounds ---------------------------------------------------------------------------------------------------------

  function currentPack() {
    const m = S().soundMap;
    const pack = CAT.soundPacks.find((p) => CAT.soundEvents.every((e) => (m[e.id] ?? '') === (p.map[e.id] ?? '')));
    return pack ? pack.name : '';
  }

  function soundName(v) {
    if (!v) return L('No');
    return v.includes('/') || v.includes('\\') ? `♪ ${v.split(/[\\/]/).pop()}` : v;
  }

  function sounds() {
    const s = S();
    const packOptions = [...(currentPack() ? [] : [{ value: '', label: L('Yours') }]),
      ...CAT.soundPacks.map((p) => ({ value: p.name, label: L(p.name) }))];
    const fileInput = h('input', { type: 'file', accept: '.wav,.mp3,.m4a,.ogg,.aiff', class: 'hidden' });
    let pendingEvent = null;
    fileInput.addEventListener('change', () => {
      const f = fileInput.files[0];
      if (f && pendingEvent) {
        const path = nt.importSound(f.path);
        if (path) set('soundMap', { ...S().soundMap, [pendingEvent]: path });
      }
      pendingEvent = null;
    });
    const vol = slider(s.volume, 0, 1, 0.05, (v) => { state = { ...state, settings: { ...state.settings, volume: v } }; },
      (v) => set('volume', v, { rerender: false }));
    vol.show(`${Math.round(s.volume * 100)}%`);

    const soundGroup = group(
      row(L('Sound'), toggle(s.sounds, (v) => set('sounds', v))),
      row(L('Volume'), vol.el),
      row(L('Sound pack'), select(packOptions, currentPack(), (v) => { if (v) nt.act('applyPack', v); }, L('Sound pack'))),
      row(L('Back up, share'), h('div', { class: 'row gap' },
        button(L('Export pack…'), () => nt.act('exportSounds'), { small: true }),
        button(L('Import pack…'), () => nt.act('importSounds'), { small: true }))));

    const eventRows = CAT.soundEvents.map((e) => {
      const cur = s.soundMap[e.id] ?? '';
      const options = [{ value: '', label: L('No') }, ...CAT.soundNames.map((n) => ({ value: n, label: n }))];
      if (cur && !CAT.soundNames.includes(cur)) options.push({ value: cur, label: soundName(cur) });
      options.push({ value: '__file__', label: L('Choose your file…') });
      const pick = select(options, cur, (v) => {
        if (v === '__file__') { pendingEvent = e.id; fileInput.click(); return; }
        set('soundMap', { ...S().soundMap, [e.id]: v });
        if (v) nt.act('playSound', v, S().volume);
      }, L(e.title));
      const play = h('button', { class: 'icon-btn', type: 'button', title: L('Preview'), disabled: !cur,
        onclick: () => nt.act('playSound', cur, S().volume) }, icon('play', 14));
      return h('div', { class: 'srow2' }, icon(e.icon, 16, 'dim'), h('span', { class: 'it' }, L(e.title)), h('span', { class: 'sp' }), pick, play);
    });

    return [
      soundGroup,
      h('div', { class: 'dim small pt' }, L('Sounds are kept separately, changing the look won\'t lose them. Export a pack to take it to another computer.')),
      card(L('Events'), null, fileInput, h('div', { class: `list${s.sounds ? '' : ' muted'}` }, eventRows)),
    ];
  }

  // ---- behavior -------------------------------------------------------------------------------------------------------

  function behavior() {
    const s = S();
    const langs = [{ value: '', label: `${L('Device')} · ${(CAT.languages.find((x) => x.code === state.device) || {}).name || 'English'}` },
      ...CAT.languages.map((x) => ({ value: x.code, label: x.name }))];
    const delay = slider(s.hoverDelay, 0, 1, 0.05, (v) => { state = { ...state, settings: { ...state.settings, hoverDelay: v } }; },
      (v) => set('hoverDelay', v, { rerender: false }));
    delay.show(`${Math.round(s.hoverDelay * 1000)}`);
    const limit = slider(s.limitWarn, 50, 100, 5, (v) => { state = { ...state, settings: { ...state.settings, limitWarn: v } }; },
      (v) => set('limitWarn', v, { rerender: false }));
    limit.show(`${Math.round(s.limitWarn)}%`);
    return [
      card(L('Opening the notch'), null, group(
        row(L('How to open'), segmented([{ value: 'hover', label: L('Hover') }, { value: 'click', label: L('Click') }], s.expandMode, (v) => set('expandMode', v))),
        s.expandMode === 'hover' ? row(L('Delay {0} ms', Math.round(s.hoverDelay * 1000)), delay.el) : null,
        row(L('CPU and RAM when expanded'), toggle(s.systemLoad, (v) => set('systemLoad', v)))),
      ),
      card(L('Notifications'), null, group(
        row(L('System notifications'), toggle(s.systemNotifications, (v) => set('systemNotifications', v))),
        row(L('Say done when it ran {0} sec or more', Math.round(s.doneAfter)), stepper(s.doneAfter, 5, 600, 5, (v) => set('doneAfter', v))),
        row(L('Warn about limits at {0}%', Math.round(s.limitWarn)), limit.el))),
      card(L('Other'), null, group(
        row(L('Language'), select(langs, state.choice || '', (v) => nt.act('setLanguage', v))),
        row(L('Send anonymous usage statistics'), toggle(state.analytics, (v) => { state = { ...state, analytics: v }; nt.act('setAnalytics', v); render(); })))),
    ];
  }

  function stepper(value, min, max, step, onChange) {
    const out = h('span', { class: 'stepper-v' }, String(value));
    const bump = (d) => {
      const v = Math.min(max, Math.max(min, value + d));
      value = v;
      out.textContent = String(v);
      onChange(v);
    };
    return h('span', { class: 'stepper' }, h('button', { class: 'icon-btn', type: 'button', onclick: () => bump(-step), 'aria-label': '−' }, icon('minus', 14)),
      out, h('button', { class: 'icon-btn', type: 'button', onclick: () => bump(step), 'aria-label': '+' }, icon('plus', 14)));
  }

  // ---- report ---------------------------------------------------------------------------------------------------------

  function openReport() {
    const topics = ['Ghép nối', 'Cập nhật', 'Khác'];
    let topic = topics[2];
    let text = '';
    let contact = '';
    const area = h('textarea', { class: 'inp area', rows: 4, placeholder: L('What happened?'), oninput: (e) => { text = e.target.value; send.disabled = !text.trim() || !!state.report?.code || !!state.report?.busy; } });
    const mail = h('input', { class: 'inp', placeholder: L('Email or other contact (so we can reply)'), oninput: (e) => { contact = e.target.value; } });
    const status = h('div', { class: 'status' });
    const send = h('button', { class: 'btn primary', type: 'button', disabled: true, onclick: () => {
      nt.act('sendReport', { topic: L(topic), text, contact });
      send.disabled = true;
    } }, L('Send'));
    const body = h('div', { class: 'stack' },
      h('label', { class: 'field' }, h('span', { class: 'field-l' }, L('Issue type')),
        select(topics.map((t) => ({ value: t, label: L(t) })), topic, (v) => { topic = v; })),
      area, mail,
      h('div', { class: 'dim small' }, L('Your description and contact; anonymous device ID, group ID, pairing status, number of phones, app version, OS, device model, language, Pro status and transaction if available. Commands, source code, file paths, task titles and project names are not attached automatically.')),
      status);
    const refresh = () => {
      const r = state.report || {};
      status.replaceChildren();
      if (r.code) status.append(h('div', {}, L('Sent. Report code: {0}', String(r.code).slice(-6))));
      if (r.problem) status.append(h('div', { class: 'warn' }, r.problem));
    };
    refresh();
    const dlg = modal({ width: 500, title: L('Report a problem'), body, footer: [button(L('Close'), () => dlg.close()), send],
      onClose: () => { reportDialog = null; } });
    reportDialog = { refresh, send };
  }

  // ---- update footer --------------------------------------------------------------------------------------------------

  function updateFooter() {
    const u = state.update || {};
    if (u.busy && u.progress != null) {
      return h('div', { class: 'upd' }, h('div', { class: 'dim' }, L('Downloading… {0}%', Math.round(u.progress * 100))),
        h('div', { class: 'bar' }, h('i', { style: { width: `${Math.round(u.progress * 100)}%` } })));
    }
    if (u.busy) return h('div', { class: 'upd dim' }, L('Checking…'));
    if (u.available) return h('div', { class: 'upd' },
      button(L('Update {0}', u.version || ''), () => nt.act('installUpdate'), { primary: true, small: true, icon: 'download' }),
      u.notes ? h('div', { class: 'dim small clamp', title: u.notes }, u.notes) : null);
    if (u.error) return h('div', { class: 'upd' }, h('div', { class: 'warn small' }, u.error),
      button(L('Try again'), () => nt.act('checkUpdate'), { small: true }));
    return h('button', { class: 'upd-ver dim', type: 'button', title: L('Check now'), onclick: () => nt.act('checkUpdate') }, `v${state.version}`);
  }

  // ---- frame ----------------------------------------------------------------------------------------------------------

  const PAGES = { overview, modules, agents: watch, phone, appearance, badges, store, sounds, behavior };

  function render() {
    if (!state) return;
    const titleOf = PANES.find((p) => p.id === pane) || PANES[0];
    document.getElementById('nav').replaceChildren(...PANES.map((p) => h('button', {
      class: `nav-i${p.id === pane ? ' on' : ''}`, type: 'button', onclick: () => go(p.id) }, icon(p.icon, 16), L(p.title))));
    fill(document.getElementById('update'), [updateFooter()]);
    const page = PAGES[pane] ? PAGES[pane]() : [];
    fill(document.getElementById('pane'), [h('h1', { class: 'ptitle' }, L(titleOf.title)), page]);
    if (reportDialog) reportDialog.refresh();
  }

  nt.onState((next) => {
    const first = !state;
    state = next;
    U.setSettingsForAccent(next.settings);
    const s = sig();
    if (first || s !== lastSig) { lastSig = s; render(); }
    if (reportDialog && next.report && next.report.busy === false) reportDialog.send.disabled = !!next.report.code;
  });
  nt.onPane((p) => { if (PANES.some((x) => x.id === p)) { pane = p; render(); } });
})();
