'use strict';
/* global nt, NTSprites, NTClock */
// The pill page: renders the state main pushes (closed wings, inline toasts, the expanded notch) into #inner.
// Parts are cached by a key of their inputs, so a push rebuilds only what changed. Clock text ticks once a second.
(function () {
  const L = (k, ...a) => nt.L(k, ...a);
  const pill = document.getElementById('pill');
  const inner = document.getElementById('inner');
  const stage = document.getElementById('stage');

  // Modules Windows has (calendar, devices, calls, privacy, levels and mirror are not shown at all).
  const WIN_MODULES = new Set(['agents', 'ci', 'media', 'timer', 'shelf', 'limits', 'health', 'battery', 'network', 'downloads']);
  const DEFAULT_MODULES = ['agents', 'ci', 'media', 'timer', 'shelf', 'limits', 'health', 'battery', 'network', 'downloads'];
  const SYS = { blue: '#0A84FF', teal: '#64D2FF', green: '#30D158', pink: '#FF2D55', purple: '#BF5AF2', yellow: '#FFD60A', orange: '#FF9F0A', red: '#FF453A', white: '#FFFFFF' };
  const PALETTE = [SYS.blue, SYS.teal, SYS.green, SYS.pink, SYS.purple, SYS.yellow, SYS.orange];
  const KIND_NAMES = { claude: 'Claude', codex: 'Codex', gemini: 'Gemini', antigravity: 'Antigravity', cursor: 'Cursor', opencode: 'OpenCode' };
  const RISK = [
    [/\brm\s+-[a-zA-Z]*[rf]/, 'Deletes files or folders'],
    [/\bsudo\b/, 'Runs as administrator'],
    [/git\s+push\b.*(--force|-f\b)/, 'Overwrites history on the git server'],
    [/git\s+reset\s+--hard/, 'Discards all unsaved changes'],
    [/git\s+clean\s+-[a-zA-Z]*f/, 'Deletes untracked files'],
    [/\|\s*(sh|bash|zsh)\b/, 'Runs a script downloaded from the web'],
    [/\b(mkfs|diskutil\s+erase|dd\s+if=)/, 'Writes straight to a disk'],
    [/chmod\s+-R\s+777/, 'Gives everyone full access to files'],
    [/\bdrop\s+(table|database)\b/i, 'Deletes data in a database'],
    [/--no-verify\b/, 'Skips safety checks'],
  ];

  let S = null;              // the last state from main
  let STATE_AT = Date.now(); // when it arrived: the media clock runs from here
  let lastMode = '';
  let cardEl = null;
  let dropOver = false;
  const PICKS = new Map();   // ask id -> Map(question index -> Set(option))
  const SHELF_SEL = new Set();
  const parts = new Map();   // part name -> { key, el }
  const tickers = new Set();

  // ---------- DOM helpers ----------
  function h(tag, props, ...kids) {
    const e = document.createElement(tag);
    if (props) {
      for (const k in props) {
        const v = props[k];
        if (v == null || v === false) continue;
        if (k === 'class') e.className = v;
        else if (k === 'style') e.style.cssText = v;
        else if (k === 'text') e.textContent = v;
        else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
        else e.setAttribute(k, v === true ? '' : String(v));
      }
    }
    for (const c of kids.flat(Infinity)) {
      if (c == null || c === false || c === true) continue;
      e.appendChild(c instanceof Node ? c : document.createTextNode(String(c)));
    }
    return e;
  }
  // an SF Symbol name drawn as its Lucide icon
  function ic(name, size, color) {
    const e = h('span', { class: 'ic', style: `width:${size}px;height:${size}px;${color ? 'color:' + color : ''}` });
    e.innerHTML = nt.icon(name);
    return e;
  }
  // text that changes every second: fn(el) runs from the one clock
  function tick(el, fn) { el._t = fn; tickers.add(el); }
  setInterval(() => {
    for (const el of tickers) {
      if (!el.isConnected) tickers.delete(el);
      else el._t(el);
    }
  }, 1000);
  // a part of the page is rebuilt only when its key changes; a reused element keeps its listeners
  // (a kept element that left the page is rebuilt: NTClock stopped stepping it)
  function part(name, key, build) {
    const old = parts.get(name);
    if (old && old.key === key && old.el.isConnected) return old.el;
    const el = build();
    parts.set(name, { key, el });
    return el;
  }

  // ---------- animated pieces: NTClock steps them (no CSS animations) ----------
  const BAR_STEPS = [0.27, 0.64, 1, 0.64];                     // scaleY over one bar cycle
  const RIPPLE = [0, 1, 2, 3, 4, 5].map((k) => [Math.round((9 * k) / 5), Math.round(80 * (1 - k / 5))]); // [spread px, alpha %]
  function animBars(el) {
    [0, 2, 1].forEach((off, k) => NTClock.add(el.children[k], 4 / 0.9, 4,
      (x, i) => { x.style.transform = `scaleY(${BAR_STEPS[(i + off) % 4]})`; }));
  }
  function animSpin(el) { NTClock.add(el, 8, 8, (x, i) => { x.style.transform = `rotate(${i * 45}deg)`; }); }
  function animRipple(el) {
    NTClock.add(el, 5, 6, (x, i) => { x.style.boxShadow = `0 0 0 ${RIPPLE[i][0]}px color-mix(in srgb, var(--tint) ${RIPPLE[i][1]}%, transparent)`; });
  }

  const clamp = (v) => Math.min(1, Math.max(0, Number(v) || 0));
  const toMs = (x) => (typeof x === 'number' ? x : new Date(x).getTime());
  function mmss(sec) {
    const t = Math.max(0, Math.ceil(sec));
    const hh = Math.floor(t / 3600), mm = Math.floor(t / 60) % 60, ss = t % 60;
    const p = (n) => String(n).padStart(2, '0');
    return hh > 0 ? `${hh}:${p(mm)}:${p(ss)}` : `${mm}:${p(ss)}`;
  }
  function resetText(resets, now = Date.now()) {
    const d = new Date(toMs(resets)), n = new Date(now);
    if (d.toDateString() === n.toDateString()) return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    if (d.getTime() - now < 6 * 86400000) return d.toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' });
    return d.toLocaleDateString([], { day: 'numeric', month: 'short' });
  }

  // ---------- settings and state readers ----------
  const st = () => S.settings || {};
  const activeModules = () => {
    const mods = st().modules && st().modules.length ? st().modules : DEFAULT_MODULES;
    const dis = new Set(st().disabled || []);
    return mods.filter((m) => WIN_MODULES.has(m) && !dis.has(m));
  };
  const isActive = (m) => activeModules().includes(m);
  const placementOf = (m) => (st().placements && st().placements[m]) || 'auto';
  const modColor = (m) => (st().moduleColors && st().moduleColors[m]) || null;
  const accentColor = () => {
    const a = st().accent;
    if (a === 'custom') return st().customAccent || SYS.orange;
    if (!a || a === 'auto') return null;
    return SYS[a] || SYS.orange;
  };
  const inBuild = (item) => item.kind === 'dev' || item.build === true;
  function tintOf(item) {
    if (item.icon && item.icon.color) return item.icon.color;
    const a = accentColor();
    if (a) return a;
    if (item.kind === 'codex') return '#6E8BFF';
    let sum = 0;
    for (const ch of item.name || '') sum += ch.codePointAt(0);
    return PALETTE[sum % PALETTE.length];
  }
  const runningOf = () => (S.states || [])
    .map((x) => ({ item: x.item, jobs: (x.jobs || []).filter((j) => j.working) }))
    .filter((x) => x.jobs.length);
  const playing = () => (S.nowPlaying && S.nowPlaying.playing ? S.nowPlaying : null);
  const limitsOf = (item) => {
    const p = (S.limits || []).find((x) => x.kind === item.kind && (x.kind !== 'process' || x.bundleID === item.bundleID));
    return p ? p.windows || [] : [];
  };
  const agentItem = (kind) => (S.items || []).find((i) => i.kind === kind) || null;
  const kindOf = (a) => a.agent || 'claude';
  const stepOf = (used) => (used >= 100 ? 100 : used >= 99 ? 99 : Math.floor(used / 5) * 5);
  const limitColor = (used) => (used >= 90 ? SYS.red : used >= 70 ? SYS.orange : SYS.green);
  const timerTint = () => modColor('timer') || accentColor() || SYS.orange;
  const iconSize = () => (st().size === 'compact' ? 16 : 20);
  const animated = (b) => !!(b.sprite || b.frames || (b.image && /\.gif$/i.test(b.image)));
  const extOf = (p) => (String(p).split('.').pop() || '').toLowerCase();
  const baseOf = (p) => String(p).split(/[\\/]/).pop();

  function chipsOf() {
    const out = [];
    const run = runningOf();
    for (const m of activeModules()) {
      if (m === 'ci') {
        if (S.alerts && S.alerts.length) out.push({ k: 'alerts', n: S.alerts.length, module: 'ci' });
        for (const x of run) if (inBuild(x.item)) out.push({ k: 'agent', x, module: 'ci' });
      } else if (m === 'media') {
        const np = playing();
        if (np) out.push({ k: 'media', np, module: 'media' });
      } else if (m === 'timer') {
        if (S.timer && S.timer.end) out.push({ k: 'timer', end: toMs(S.timer.end), module: 'timer' });
      } else if (m === 'agents') {
        for (const x of run.filter((y) => !inBuild(y.item)).slice(0, 4)) out.push({ k: 'agent', x, module: 'agents' });
      } else if (m === 'limits') {
        for (const p of S.limits || []) {
          const w = (p.windows || []).filter((y) => stepOf(y.used) >= (st().limitWarn ?? 80)).sort((a, b) => b.used - a.used)[0];
          if (w) out.push({ k: 'limit', name: p.name, w, module: 'limits' });
        }
      }
    }
    // an agent waiting on you comes right after a call (none here) and is stuck until you answer
    if (isActive('agents') && S.asks && S.asks.length) out.unshift({ k: 'ask', asks: S.asks, module: 'agents' });
    return out;
  }
  function sidesOf(chips) {
    if (chips.length === 1 && placementOf(chips[0].module) === 'auto') return { left: [], right: [], single: chips[0] };
    const left = [], right = [];
    for (const c of chips) {
      const p = placementOf(c.module);
      if (p === 'left') left.push(c);
      else if (p === 'right') right.push(c);
    }
    for (const c of chips) if (placementOf(c.module) === 'auto') (left.length <= right.length ? left : right).push(c);
    const order = (c) => chips.indexOf(c);
    left.sort((a, b) => order(a) - order(b));
    right.sort((a, b) => order(a) - order(b));
    return { left, right, single: null };
  }

  // ---------- icons, badges, sprites ----------
  function badgeEl(b, size) {
    const tint = b.color || accentColor() || '#4D4D4D';
    const isAnim = animated(b);
    const plain = b.plain === true && (!b.image || isAnim);
    const ink = b.fg || (plain ? tint : '#fff');
    const radius = b.shape === 'rounded' ? `${size * 0.28}px` : '999px';
    const box = h('span', {
      class: 'badge',
      style: `position:relative;display:inline-flex;align-items:center;justify-content:center;box-sizing:border-box;flex:none;` +
        `width:${size}px;height:${size}px;overflow:hidden;` +
        `background:${plain && isAnim ? 'transparent' : plain ? 'transparent' : tint};` +
        `border-radius:${plain && isAnim ? '0' : radius};` +
        (b.border ? 'border:1px solid rgba(255,255,255,0.6);' : ''),
    });
    const inner = plain ? size : size * 0.78;
    let content = null;
    if (isAnim) {
      if (b.sprite) content = NTSprites.builtIn(b.sprite, inner);
      else if (b.frames) content = NTSprites.userStrip(b.image, b.frames, b.fps, inner);
      else content = h('img', { src: NTSprites.fileURL(b.image), style: `width:${inner}px;height:${inner}px;object-fit:cover` });
    } else if (b.image) {
      content = h('img', { src: NTSprites.fileURL(b.image), style: `width:${size}px;height:${size}px;object-fit:cover` });
    } else if (b.text) {
      content = h('span', { class: 'num', style: `font-size:${size * 0.5}px;font-weight:700;color:${ink};white-space:nowrap;padding:0 ${b.text.length > 2 ? size * 0.25 : 0}px` }, b.text);
    } else {
      content = ic(b.symbol || 'star.fill', size * (plain ? 0.75 : 0.55), ink);
    }
    box.appendChild(content || ic('star.fill', size * 0.55, ink));
    return box;
  }
  function itemIcon(item, size) {
    if (item.icon) return badgeEl(item.icon, size);
    const tint = tintOf(item);
    const sym = item.symbolName || (item.kind === 'dev' ? 'hammer.fill' : item.kind === 'claude' ? 'sparkles' : 'terminal.fill');
    return h('span', { style: `display:inline-flex;align-items:center;justify-content:center;flex:none;width:${size}px;height:${size}px` },
      h('span', { style: `display:flex;align-items:center;justify-content:center;width:${size * 0.86}px;height:${size * 0.86}px;border-radius:${size * 0.28}px;background:${tint};color:#fff` },
        ic(sym, size * 0.5)));
  }
  function artEl(np, size) {
    const r = Math.round(size * 0.22) + 'px';
    if (np.artwork) return h('img', { src: np.artwork, style: `width:${size}px;height:${size}px;border-radius:${r};object-fit:cover;flex:none` });
    const item = Object.assign({ name: np.appName || '', kind: 'app' }, np.item || {}, { symbolName: 'music.note' });
    return h('span', { style: `display:inline-flex;border-radius:${r};overflow:hidden;flex:none` }, itemIcon(item, size));
  }
  function ringEl(size, width, color, frac) {
    const r = (size - width) / 2, c = 2 * Math.PI * r;
    const wrap = h('span', { class: 'ring', style: `width:${size}px;height:${size}px` });
    wrap.innerHTML = `<svg viewBox="0 0 ${size} ${size}"><circle class="trk" cx="${size / 2}" cy="${size / 2}" r="${r}" stroke-width="${width}"/>` +
      `<circle class="val" cx="${size / 2}" cy="${size / 2}" r="${r}" stroke="${color}" stroke-width="${width}" stroke-dasharray="${c}" stroke-dashoffset="${c * (1 - clamp(frac))}"/></svg>`;
    wrap.valEl = wrap.querySelector('.val');
    wrap.circ = c;
    return wrap;
  }
  function setRing(ring, frac) { ring.valEl.setAttribute('stroke-dashoffset', String(ring.circ * (1 - clamp(frac)))); }

  // ---------- chips: the collapsed activities ----------
  function leadEl(c, size) {
    switch (c.k) {
      case 'alerts':
        return ic('exclamationmark.triangle.fill', size * 0.8, modColor('ci') || SYS.red);
      case 'media':
        return artEl(c.np, size);
      case 'timer':
        return ic('timer', size * 0.8, timerTint());
      case 'agent': {
        const ci = inBuild(c.x.item);
        const tint = modColor(ci ? 'ci' : 'agents') || tintOf(c.x.item);
        if (c.x.item.kind === 'dev' && c.x.item.icon == null && c.x.jobs[0] && c.x.jobs[0].symbol) return ic(c.x.jobs[0].symbol, size, tint);
        return h('span', { style: `display:inline-flex;width:${size}px;height:${size}px` }, itemIcon(c.x.item, size));
      }
      case 'ask': {
        const a = c.asks[0];
        const item = agentItem(kindOf(a));
        const base = item ? itemIcon(item, size) : ic('hand.raised.fill', size * 0.72, SYS.orange);
        const badge = h('span', {
          style: `position:absolute;right:-${size * 0.2}px;bottom:-${size * 0.14}px;display:flex;align-items:center;justify-content:center;` +
            `width:${size * 0.55}px;height:${size * 0.55}px;border-radius:50%;background:${SYS.orange};border:1.5px solid #000;box-sizing:border-box;color:#fff`,
        }, ic('hand.raised.fill', size * 0.3, '#fff'));
        return h('span', { style: `position:relative;display:inline-flex;width:${size}px;height:${size}px` }, base, badge);
      }
      case 'limit': {
        const u = c.w.used;
        return ic(u >= 100 ? 'hourglass' : 'gauge.with.dots.needle.67percent', size * 0.75, u >= 95 ? SYS.red : SYS.orange);
      }
    }
    return null;
  }
  function trailEl(c, size) {
    const base = 'font-size:11px;font-weight:700;font-variant-numeric:tabular-nums;font-family:var(--font-num);white-space:nowrap';
    switch (c.k) {
      case 'alerts': {
        const col = modColor('ci') || SYS.red;
        return h('span', { style: base + ';color:' + col }, c.n === 1 ? L('1 error') : L('{0} errors', c.n));
      }
      case 'media': {
        const col = modColor('media') || tintOf(c.np.item || { name: c.np.appName || '', kind: 'app' });
        const bars = h('span', { class: 'bars', style: `--tint:${col}` }, h('i'), h('i'), h('i'));
        animBars(bars);
        return bars;
      }
      case 'timer': {
        const el = h('span', { style: base + ';color:' + timerTint() }, mmss((c.end - Date.now()) / 1000));
        tick(el, (e) => { e.textContent = mmss((c.end - Date.now()) / 1000); });
        return el;
      }
      case 'agent': {
        const ci = inBuild(c.x.item);
        const tint = modColor(ci ? 'ci' : 'agents') || tintOf(c.x.item);
        const jobs = c.x.jobs;
        if (jobs.length === 1 && jobs[0].progress != null) return ringEl(11, 2, tint, jobs[0].progress);
        return h('span', { style: base + ';color:' + tint }, String(jobs.length));
      }
      case 'ask':
        return h('span', { style: base + ';color:' + SYS.orange }, c.asks.length === 1 ? L('Waiting for you') : L('{0} waiting', c.asks.length));
      case 'limit': {
        const w = c.w;
        const col = w.used >= 95 ? SYS.red : SYS.orange;
        return h('span', { style: base + ';color:' + col, title: L('{0} used {1}% of its {2} limit', c.name, Math.round(w.used), w.label) },
          w.used >= 100 ? L('Waiting ') + resetText(w.resets) : Math.round(w.used) + '%');
      }
    }
    return null;
  }
  const chipEl = (c, size, part) => h('span', { class: 'chip' }, part !== 'trail' ? leadEl(c, size) : null, part !== 'lead' ? trailEl(c, size) : null);

  function closedView() {
    const size = iconSize();
    const chips = chipsOf();
    const sd = sidesOf(chips);
    const bs = st().badges || [];
    const lefts = bs.filter((b) => !b.right).map((b) => badgeEl(b, size));
    const rights = bs.filter((b) => b.right).map((b) => badgeEl(b, size));
    let wl, wr;
    if (sd.single) {
      wl = [...lefts, leadEl(sd.single, size)];
      wr = [trailEl(sd.single, size), ...rights];
    } else {
      wl = [...lefts, ...sd.left.map((c) => chipEl(c, size, 'both'))];
      wr = [...sd.right.map((c) => chipEl(c, size, 'both')), ...rights];
    }
    return h('div', { class: 'wings', onclick: () => nt.act('expand', true) },
      h('div', { class: 'wl' }, wl), h('span'), h('div', { class: 'wr' }, wr));
  }

  // ---------- toasts ----------
  const toastInline = () => (S.toast && st().toastStyle !== 'card' ? S.toast : null);
  const choice = (label, primary, onclick, big) => h('button', { class: 'choice' + (primary ? ' primary' : '') + (big ? ' big' : ''), onclick: (e) => { e.stopPropagation(); onclick(); } }, label);
  function openToast(t) {
    if (t.ask) { nt.act('dismissToast'); nt.act('expand', true); return; }
    if (t.pane) nt.act('openPrefs', t.pane);
    else if (t.url) nt.act('openURL', t.url);
    nt.act('dismissToast');
  }
  function toastIcon(t, size) {
    if (t.art) {
      if (t.art.kind === 'charge') {
        const pct = Math.max(0, Math.min(100, t.art.percent || 0));
        const col = t.art.plugged ? SYS.green : pct <= 20 ? SYS.red : '#fff';
        const body = h('span', { class: 'body' }, h('span', { class: 'lvl', style: `width:${Math.max(2, (22 * pct) / 100)}px;background:${col}` }),
          t.art.plugged ? h('span', { class: 'bolt' }, ic('bolt.fill', 11)) : null);
        return h('span', { class: 'batt' }, body, h('span', { class: 'nub' }));
      }
      if (t.art.kind === 'loading') {
        const spin = h('span', { class: 'spin', style: `width:${size}px;height:${size}px` });
        animSpin(spin);
        return spin;
      }
      if (t.art.kind === 'device') return ic(t.art.symbol || 'bell', size * 0.75, t.tint);
    }
    if (t.icon) return itemIcon(t.icon, size);
    return ic(t.symbol || 'bell', size * 0.75, t.tint);
  }
  function toastButtons(t) {
    const kids = [];
    if (t.ask) {
      if (t.ask.questions && t.ask.questions.length) kids.push(choice(L('Reply'), true, () => openToast(t)));
      else {
        kids.push(choice(L('Deny'), false, () => nt.act('answer', t.ask.id, { kind: 'deny' })));
        kids.push(choice(L('Allow'), true, () => nt.act('answer', t.ask.id, { kind: 'allow' })));
      }
    } else if (t.action || t.url || t.pane) {
      kids.push(choice(t.action || L('Open'), true, () => openToast(t)));
    }
    kids.push(h('button', { class: 'xbtn', title: L('Hide alert'), onclick: (e) => { e.stopPropagation(); nt.act('dismissToast'); } }, ic('xmark', 9)));
    return h('div', { class: 'btns' }, kids);
  }
  function toastEl(t) {
    const style = st().toastStyle;
    const tint = t.tint || SYS.orange;
    if (t.compact || style === 'compact') {
      const side = t.compact ? 52 : 170;
      return h('div', { class: 'toast-compact', style: `--tint:${tint};grid-template-columns:${side}px 12px ${side}px;justify-content:space-between`,
        title: t.title + (t.subtitle ? ' · ' + t.subtitle : ''), onclick: () => openToast(t) },
        h('span', { style: 'display:flex;align-items:center' }, toastIcon(t, 18)),
        h('span'),
        h('span', { class: 'tc-r', style: `color:${t.compact ? tint : '#fff'}` }, t.compact ? t.subtitle : t.title));
    }
    const banner = style === 'banner';
    const wide = banner || t.ask ? 520 : 340;
    const hasDevice = !!(t.art && t.art.kind === 'device' && t.art.art);
    return h('div', { class: 'toast-wide', style: `--tw:${wide}px;--tint:${tint}`, onclick: () => openToast(t) },
      h('div', { class: 'toast-top', style: 'grid-template-columns:40px 1fr 40px' },
        h('span', { style: 'display:flex;align-items:center' }, hasDevice ? null : toastIcon(t, 20)),
        h('span'),
        h('span', { class: 'tr' }, t.icon ? ic(t.symbol || 'bell', 14, tint) : null)),
      h('div', { class: 'toast-body' + (banner ? ' banner' : '') },
        banner ? h('span', { class: 'toast-bar' }) : null,
        hasDevice ? toastIcon(t, 54) : null,
        h('div', { class: 'tb-text' },
          h('div', { class: 'toast-title' }, t.title),
          t.subtitle ? h('div', { class: 'toast-sub' }, t.subtitle) : null),
        toastButtons(t)));
  }
  function cardToastEl(t) {
    const tint = t.tint || SYS.orange;
    return h('div', { class: 'cardtoast' + (st().background === 'gradient' ? ' bg-gradient' : ''), style: `--tint:${tint}`, onclick: () => openToast(t) },
      toastIcon(t, 26),
      h('div', { class: 'ct-text' },
        h('div', { class: 'toast-title' }, t.title),
        t.subtitle ? h('div', { class: 'toast-sub' }, t.subtitle) : null),
      t.icon ? ic(t.symbol || 'bell', 15, tint) : null,
      toastButtons(t));
  }

  // ---------- asks ----------
  function pickSet(askId, qi) {
    let m = PICKS.get(askId);
    if (!m) { m = new Map(); PICKS.set(askId, m); }
    let set = m.get(qi);
    if (!set) { set = new Set(); m.set(qi, set); }
    return set;
  }
  function askAction(tool) {
    switch (tool) {
      case 'Bash': case 'run_shell_command': case 'run_command': case 'shell': case 'exec_command': case 'local_shell': case 'Execute': case 'bash':
        return L('Run command');
      case 'Edit': case 'Write': case 'MultiEdit': case 'NotebookEdit': case 'write_file': case 'replace': case 'write_to_file': case 'replace_file_content':
      case 'multi_replace_file_content': case 'apply_patch': case 'Create': case 'ApplyPatch': case 'edit': case 'write':
        return L('Edit file');
      case 'Read': case 'read_file': case 'view_file': return L('Read file');
      case 'WebFetch': case 'WebSearch': case 'web_fetch': case 'FetchUrl': case 'webfetch': return L('Web access');
      default:
        if (String(tool).startsWith('mcp__')) return L('Use tool ') + (String(tool).split('_').pop() || '');
        return tool;
    }
  }
  function riskReason(cmd) {
    const r = RISK.find(([re]) => re.test(cmd));
    return r ? L(r[1]) : null;
  }
  function askCard(a) {
    const kind = kindOf(a);
    const item = agentItem(kind);
    const isQ = !!(a.questions && a.questions.length);
    const risky = !isQ && askAction(a.tool) === L('Run command') ? riskReason(a.summary || '') : null;
    const ready = isQ && a.questions.every((_, i) => pickSet(a.id, i).size > 0);
    const single = isQ && a.questions.length === 1 && !a.questions[0].multiSelect;
    const wait = h('span', { class: 'ask-wait num' });
    tick(wait, (e) => { e.textContent = L('Waiting ') + mmss((Date.now() - toMs(a.at)) / 1000); });
    wait.textContent = L('Waiting ') + mmss((Date.now() - toMs(a.at)) / 1000);
    const head = h('div', { class: 'ask-head' },
      item ? itemIcon(item, 28) : h('span', { class: 'ic', style: 'width:28px;height:28px;color:#ff9f0a' }, ic('sparkles', 14, SYS.orange)),
      h('div', { style: 'min-width:0;flex:1' },
        h('div', { class: 'ask-title txt' }, (KIND_NAMES[kind] || kind) + (isQ ? L(' is asking') : L(' needs your approval'))),
        h('div', { class: 'ask-sub txt' }, a.session || '')),
      wait);
    let body;
    if (isQ) {
      body = a.questions.map((q, qi) => {
        const set = pickSet(a.id, qi);
        return h('div', { class: 'ask-q' },
          h('div', { class: 'ask-q-text' }, q.question),
          h('div', { class: 'opts' }, q.options.map((o) => {
            const on = set.has(o);
            return h('button', {
              class: 'opt' + (on ? ' on' : ''),
              onclick: () => {
                if (q.multiSelect) { on ? set.delete(o) : set.add(o); }
                else {
                  set.clear(); set.add(o);
                  if (a.questions.length === 1) { nt.act('answer', a.id, { kind: 'answers', answers: { [q.question]: o } }); PICKS.delete(a.id); return; }
                }
                paint();
              },
            }, q.multiSelect ? ic(on ? 'checkmark.square.fill' : 'square', 13) : null, h('span', null, o));
          })));
      });
    } else {
      body = h('div', { class: 'ask-action' },
        h('div', { class: 'ask-verb' + (risky ? ' red' : '') }, askAction(a.tool)),
        h('div', { class: 'ask-cmd' }, a.summary || ''),
        risky ? h('div', { class: 'ask-warn' }, ic('exclamationmark.triangle.fill', 12, SYS.red), risky) : null);
    }
    const name = KIND_NAMES[kind] || kind;
    const foot = h('div', { class: 'ask-foot' },
      h('button', { class: 'underline-link', title: L('Skip here, {0} will ask as usual', name), onclick: () => { PICKS.delete(a.id); nt.act('answer', a.id, { kind: 'pass' }); } },
        L('Answer in {0}', name)),
      h('span', { class: 'spacer' }),
      isQ
        ? (single ? null : choice(L('Send'), true, () => {
          const out = {};
          a.questions.forEach((q, qi) => { const set = pickSet(a.id, qi); out[q.question] = q.options.filter((o) => set.has(o)).join(', '); });
          PICKS.delete(a.id);
          nt.act('answer', a.id, { kind: 'answers', answers: out });
        }, true))
        : [choice(L('Deny'), false, () => nt.act('answer', a.id, { kind: 'deny' }), true), choice(L('Allow'), true, () => nt.act('answer', a.id, { kind: 'allow' }), true)]);
    if (isQ && !single) foot.lastChild.disabled = !ready;
    if (isQ && !single) foot.lastChild.style.opacity = ready ? '1' : '0.4';
    return h('div', { class: 'card ask' + (risky ? ' risky' : '') }, head, body, foot);
  }

  // ---------- open cards ----------
  function alertCard(a) {
    return h('div', { class: 'card alert', onclick: () => a.url && nt.act('openURL', a.url), title: a.url || '' },
      ic('xmark.octagon.fill', 16, SYS.red),
      h('div', { style: 'flex:1;min-width:0' },
        h('div', { style: 'font-size:12px;font-weight:600', class: 'txt' }, L('CI failed: {0}', a.name)),
        h('div', { class: 'txt', style: 'font-size:11px;color:var(--gray)' }, `${a.project} · ${a.reason}`)),
      a.url ? h('span', { class: 'choice', style: 'color:' + SYS.red + ';background:rgba(255,69,58,0.18)' }, L('View log')) : null,
      h('button', { class: 'xbtn', title: L('Hide alert'), onclick: (e) => { e.stopPropagation(); nt.act('dismissAlert', a.id); } }, ic('xmark', 9)));
  }
  function limitBadges(windows, now = Date.now()) {
    let pressing = null;
    windows.forEach((w, i) => {
      if (w.used < 90) return;
      if (pressing == null) { pressing = i; return; }
      const b = windows[pressing];
      if (w.used > b.used || (w.used === b.used && toMs(w.resets) > toMs(b.resets))) pressing = i;
    });
    const candidates = windows.map((_, i) => i);
    const selected = candidates.slice(0, 3);
    if (pressing != null && selected.length === 3 && !selected.includes(pressing)) selected[2] = pressing;
    const shown = selected.slice().sort((a, b) => a - b).map((i) => {
      const w = windows[i];
      const urgent = i === pressing;
      const reset = resetText(w.resets, now);
      const detail = w.used >= 100 && urgent ? '→ ' + reset
        : w.used >= 90 && urgent ? `${Math.round(w.used)}% · ${reset}`
          : w.used >= 70 ? `${Math.round(w.used)}%` : null;
      return { w, label: w.label, detail };
    });
    const hidden = windows.filter((_, i) => !selected.includes(i));
    return { shown, hidden };
  }
  function limitBadgeEl(b) {
    const color = limitColor(b.w.used);
    const children = [ringEl(12, 2, color, b.w.used / 100)];
    if (b.label) children.push(h('span', { class: 'lbl' }, b.label));
    if (b.detail) children.push(h('span', { class: 'det', style: 'color:' + color }, b.detail));
    return h('span', { class: 'lbadge', title: `${b.w.label} · ${Math.round(b.w.used)}% · ` + resetText(b.w.resets) }, children);
  }
  function jobRow(j, tint) {
    let icon = null;
    if (j.symbol) icon = ic(j.symbol, 11, tint);
    else if (j.tool && st().toolIcons && st().toolIcons[j.tool]) icon = badgeEl(st().toolIcons[j.tool], 15);
    else {
      icon = h('span', { class: 'dot' + (j.working ? ' on' : '') });
      if (j.working) animRipple(icon);
    }
    return h('div', { class: 'job' },
      h('div', { class: 'job-row', title: j.title + ' — ' + j.detail, onclick: j.url ? () => nt.act('openURL', j.url) : null },
        h('span', { class: 'jicon' }, icon),
        h('span', { class: 'jt' }, j.title),
        h('span', { class: 'jd num' + (j.working ? ' on' : '') }, j.detail),
        j.url ? ic('text.alignleft', 10, 'var(--gray)') : null),
      j.progress != null ? h('div', { class: 'pline' }, h('i', { class: j.estimated ? 'est' : '', style: `width:${clamp(j.progress) * 100}%` })) : null);
  }
  function itemCard(x) {
    const item = x.item;
    const ci = inBuild(item);
    const tint = modColor(ci ? 'ci' : 'agents') || tintOf(item);
    const lim = limitBadges(limitsOf(item));
    const head = h('div', { class: 'item-head', onclick: () => nt.act('openItem', item.id) },
      itemIcon(item, 20),
      h('span', { class: 'item-name txt' }, item.name),
      h('span', { class: 'lims' },
        lim.shown.map(limitBadgeEl),
        lim.hidden.length ? h('span', { class: 'more-lim', title: lim.hidden.map((w) => `${w.label} · ${Math.round(w.used)}%`).join('\n') }, '+' + lim.hidden.length) : null),
      h('span', { class: 'count num', style: 'background:' + tint, title: L('{0} tasks running', x.jobs.length) }, String(x.jobs.length)));
    return h('div', { class: 'card', style: `--tint:${tint}` },
      head,
      x.jobs.slice(0, 4).map((j) => jobRow(j, tint)),
      x.jobs.length > 4 ? h('div', { class: 'more' }, L('+{0} more tasks', x.jobs.length - 4)) : null);
  }
  function mediaCard(np) {
    const tint = modColor('media') || tintOf(np.item || { name: np.appName || '', kind: 'app' });
    const playingNow = !!np.playing;
    const ctl = (name, cmd, big) => h('button', { class: 'mbtn' + (big ? ' big' : ''), onclick: () => nt.act('media', cmd) }, ic(name, big ? 16 : 12, '#fff'));
    const seek = np.duration > 0 && np.position != null ? seekBar(np, tint) : null;
    return h('div', { class: 'card media', style: `--tint:${tint}` },
      artEl(np, 46),
      h('div', { class: 'mtext' },
        h('div', { class: 'mtitle' }, np.title || L('Playing audio')),
        h('div', { class: 'msub' }, h('span', { class: 'txt' }, [np.artist, np.appName].filter(Boolean).join(' · '))),
        seek),
      h('div', { class: 'mctl' },
        ctl('backward.fill', 'previous'),
        ctl(playingNow ? 'pause.fill' : 'play.fill', 'toggle', true),
        ctl('forward.fill', 'next')));
  }
  function seekBar(np, tint) {
    const dur = np.duration;
    const frac = () => clamp((np.position + (np.playing ? (Date.now() - STATE_AT) / 1000 : 0)) / dur);
    const fill = h('div', { class: 'fill', style: `width:${frac() * 100}%;background:${tint}` });
    const knob = h('div', { class: 'knob', style: `left:${frac() * 100}%` });
    const times = h('div', { class: 'seek-times num' }, h('span', null, mmss(frac() * dur)), h('span', null, '-' + mmss(dur - frac() * dur)));
    const bar = h('div', { class: 'seek' }, h('div', { class: 'track' }, fill, knob));
    const set = (f) => {
      fill.style.width = f * 100 + '%';
      knob.style.left = f * 100 + '%';
      times.firstChild.textContent = mmss(f * dur);
      times.lastChild.textContent = '-' + mmss(dur - f * dur);
    };
    const fracAt = (e) => { const r = bar.getBoundingClientRect(); return clamp((e.clientX - r.left) / r.width); };
    tick(bar, () => { if (!bar.classList.contains('drag')) set(frac()); });
    bar.addEventListener('pointerdown', (e) => {
      bar.setPointerCapture(e.pointerId);
      bar.classList.add('drag');
      fill.style.transition = 'none';
      set(fracAt(e));
    });
    bar.addEventListener('pointermove', (e) => { if (bar.classList.contains('drag')) set(fracAt(e)); });
    bar.addEventListener('pointerup', (e) => {
      if (!bar.classList.contains('drag')) return;
      bar.classList.remove('drag');
      fill.style.transition = '';
      nt.act('media', 'seek', fracAt(e) * dur);
    });
    return h('div', { style: 'display:flex;flex-direction:column;gap:2px' }, bar, times);
  }
  function timerCard() {
    const t = S.timer;
    const end = toMs(t.end), total = Math.max(Number(t.total) || 1, 1);
    const tint = timerTint();
    const left = () => Math.max(0, (end - Date.now()) / 1000);
    const ring = ringEl(34, 3, tint, left() / total);
    const big = h('div', { class: 'tbig num' }, mmss(left()));
    tick(big, (e) => { e.textContent = mmss(left()); });
    tick(ring, (e) => setRing(e, left() / total));
    const extend = (sec) => () => nt.act('timer', Math.ceil(left()) + sec);
    return h('div', { class: 'card timer' },
      h('div', { style: 'position:relative;width:34px;height:34px;flex:none' }, ring, h('span', { style: 'position:absolute;inset:0;display:flex;align-items:center;justify-content:center' }, ic('timer', 12, tint))),
      h('div', { class: 'tt' }, big, h('div', { class: 'tlab' }, L('{0}-minute timer', Math.round(total / 60)))),
      h('button', { class: 'pillbtn', onclick: extend(60) }, '+1′'),
      h('button', { class: 'pillbtn', onclick: extend(300) }, '+5′'),
      h('button', { class: 'stopbtn', title: L('Stop timer'), onclick: () => nt.act('timer', null) }, ic('stop.fill', 11, '#fff')));
  }
  function shelfCard() {
    const files = S.shelf || [];
    for (const p of [...SHELF_SEL]) if (!files.includes(p)) SHELF_SEL.delete(p);
    const picked = files.filter((p) => SHELF_SEL.has(p));
    const targets = picked.length ? picked : files;
    const all = picked.length === files.length;
    const iconBtn = (name, title, fn) => h('button', { class: 'iconbtn', title, onclick: fn }, ic(name, 11));
    const head = h('div', { class: 'shelf-head' },
      picked.length === 0
        ? [h('span', { class: 'sh-t' }, ic('tray.full.fill', 12, '#fff'), L('File shelf')), h('span', { class: 'sh-n num' }, String(files.length))]
        : h('span', { class: 'sh-t' }, L('{0}/{1} selected', picked.length, files.length)),
      h('span', { class: 'spacer' }),
      files.length ? h('button', { class: 'spill', onclick: () => { SHELF_SEL.clear(); if (!all) files.forEach((p) => SHELF_SEL.add(p)); paint(); } }, all ? L('Deselect') : L('Select all')) : null,
      files.length && picked.length ? iconBtn('arrow.up.forward.app', L('Open'), () => picked.forEach((p) => nt.act('shelfOpen', p))) : null,
      files.length ? iconBtn('folder', L('Show in File Explorer'), () => nt.act('shelfReveal', targets)) : null,
      files.length ? iconBtn(picked.length ? 'minus.circle' : 'trash', picked.length ? L('Remove from the shelf (original stays)') : L('Remove all from the shelf (originals stay)'), () => {
        const gone = new Set(targets);
        for (const p of gone) SHELF_SEL.delete(p);
        nt.act('shelfRemove', targets);
      }) : null,
      iconBtn('plus', L('Add files…'), () => nt.act('shelfPick')));
    let body;
    if (!files.length) {
      body = h('div', { class: 'dropzone' + (dropOver ? ' target' : '') }, dropOver ? L('Drop here') : L('Drag files onto the notch, or click + to choose'));
    } else {
      body = [
        h('div', { class: 'tiles' }, files.map((p) => tileEl(p, picked))),
        h('div', { class: 'shelf-hint' }, picked.length ? L('Drag one selected file to bring all {0} files', picked.length)
          : L('Click to select · double-click to open · drag out to drop into another app')),
      ];
    }
    return h('div', { class: 'card shelf' + (dropOver ? ' target' : '') }, head, body);
  }
  function tileEl(p, picked) {
    const on = SHELF_SEL.has(p);
    const img = ['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'svg'].includes(extOf(p));
    const thumb = img ? h('img', { src: NTSprites.fileURL(p), draggable: 'false' }) : [ic('doc.fill', 26, 'var(--gray)'), h('span', { class: 'ext' }, extOf(p) || '')];
    const t = h('div', {
      class: 'tile' + (on ? ' on' : ''), title: p, draggable: 'true',
      onclick: () => { on ? SHELF_SEL.delete(p) : SHELF_SEL.add(p); paint(); },
      ondblclick: () => nt.act('shelfOpen', p),
      // the pill stays open for the whole drag (the Mac's `dragging`)
      ondragstart: (e) => { e.preventDefault(); nt.hold(true); nt.startDrag(picked.includes(p) ? picked : [p]); },
      ondragend: () => nt.hold(false),
    }, h('div', { class: 'thumb' }, thumb), h('div', { class: 'fname' }, baseOf(p)),
      on ? h('span', { class: 'tick' }, ic('checkmark.circle.fill', 13)) : null);
    return t;
  }
  function healthCard() {
    const load = S.load || {};
    const v = (x) => clamp(x > 1 ? x / 100 : x || 0);
    const meter = (label, frac) => {
      const tint = frac >= 0.9 ? SYS.red : frac >= 0.7 ? SYS.orange : SYS.green;
      const r = ringEl(20, 2.5, tint, Math.max(frac, 0.02));
      return h('span', { class: 'meter' },
        h('span', { style: 'position:relative;width:20px;height:20px;flex:none' }, r,
          h('span', { class: 'rv num', style: 'color:#fff' }, String(Math.round(frac * 100)))),
        h('span', { class: 'ml' }, label));
    };
    const temps = (S.thermals || []).map((t) => h('span', null, `${t.name} ${Math.round(t.celsius)}°`));
    return h('div', { class: 'card health' },
      h('div', { class: 'hrow' }, meter('CPU', v(load.cpu)), meter('RAM', v(load.memory))),
      temps.length ? h('div', { class: 'temps' }, temps) : null);
  }
  function sectionNodes(m) {
    switch (m) {
      case 'ci':
        return [...(S.alerts || []).map(alertCard), ...runningOf().filter((x) => inBuild(x.item)).map(itemCard)];
      case 'media':
        return S.nowPlaying ? [mediaCard(S.nowPlaying)] : [];
      case 'timer':
        return S.timer && S.timer.end ? [timerCard()] : [];
      case 'agents':
        return [...(S.asks || []).map(askCard), ...runningOf().filter((x) => !inBuild(x.item)).map(itemCard)];
      case 'shelf':
        return (S.shelf && S.shelf.length) || dropOver ? [shelfCard()] : [];
      case 'health':
        return [healthCard()];
      default:
        return [];
    }
  }
  function quickBar() {
    const kids = [];
    if (isActive('timer') && !S.timer) {
      kids.push(ic('timer', 11, 'var(--gray)'));
      for (const m of [5, 15, 25, 45]) kids.push(h('button', { class: 'pillbtn', onclick: () => nt.act('timer', m * 60) }, `${m}′`));
    }
    kids.push(h('span', { class: 'spacer' }));
    if (isActive('shelf') && !(S.shelf && S.shelf.length) && !dropOver) {
      kids.push(h('button', { class: 'qhint', title: L('Drag files onto the notch, or click to choose files for the shelf'), onclick: () => nt.act('shelfPick') },
        ic('tray.and.arrow.down', 11), L('Drag and drop or choose files')));
    }
    kids.push(h('span', { class: 'qhint', style: 'gap:6px' },
      h('button', { class: 'qbtn', title: L('Control Panel'), onclick: () => nt.act('openPrefs') },
        ic('gearshape', 11), S.updatePending ? h('span', { class: 'upd' }) : null),
      h('button', { class: 'qbtn', title: L('Quit NotchTasks'), onclick: () => nt.act('quit') }, ic('power', 11))));
    return h('div', { class: 'quick' }, kids);
  }

  // ---------- the open notch ----------
  function summaryText() {
    let working = 0, total = 0;
    for (const x of S.states || []) {
      const jobs = x.jobs || [];
      working += jobs.filter((j) => j.working).length;
      total += jobs.length;
    }
    const parts = [];
    if (working > 0) parts.push(L('{0} running', working));
    if (total > working) parts.push(L('{0} waiting', total - working));
    const np = playing();
    if (np) parts.push('♪ ' + (np.appName || ''));
    return parts.length ? parts.join(' · ') : L('Nothing running');
  }
  function hottest() {
    const list = S.thermals || [];
    return list.length ? list.reduce((a, b) => (b.celsius > a.celsius ? b : a)) : null;
  }
  function topBar() {
    const t = hottest();
    const right = [];
    if (t && st().systemLoad) {
      const col = t.celsius >= 85 ? SYS.red : t.celsius >= 60 ? SYS.orange : SYS.green;
      right.push(h('span', { class: 'temp', style: 'color:' + col, title: L('Temperature') + `: ${t.name} ${Math.round(t.celsius)}°C` },
        ic('thermometer.medium', 14), `${Math.round(t.celsius)}°`));
    }
    const w = st().size === 'compact' ? 480 : 520;
    return h('div', { class: 'topbar', style: `width:${w}px;padding:0 16px` },
      h('span', { class: 'summary txt', style: 'flex:1' }, summaryText()),
      h('span', { style: 'display:flex;align-items:center;flex:none' }, right));
  }
  function openBody() {
    const kids = [];
    for (const m of activeModules()) kids.push(...sectionNodes(m));
    const hasCollapsed = !!((st().badges || []).length || chipsOf().length);
    const shelfOn = isActive('shelf') && ((S.shelf && S.shelf.length) || dropOver);
    if (!hasCollapsed && !shelfOn) kids.push(h('div', { class: 'empty' }, L('Nothing running')));
    kids.push(quickBar());
    const w = st().size === 'compact' ? 480 : 520;
    return h('div', { class: 'open-body', style: `width:${w}px` }, kids);
  }

  // ---------- keys: what makes a part change ----------
  const pickKey = () => JSON.stringify([...PICKS].map(([id, m]) => [id, [...m].map(([q, set]) => [q, [...set]])]));
  const openKey = () => JSON.stringify([S.settings, S.items, S.states, S.limits, S.alerts, S.nowPlaying, S.asks, S.shelf,
    S.timer, S.load, S.thermals, S.updatePending, pickKey(), [...SHELF_SEL], dropOver]);
  const topKey = () => JSON.stringify([summaryText(), hottest(), st().systemLoad, st().size]);
  const closedKey = () => JSON.stringify([st().badges, st().placements, st().size, st().accent, st().customAccent, S.states, S.alerts,
    S.nowPlaying, S.timer, S.limits, S.asks, st().modules, st().disabled, st().limitWarn, st().moduleColors, st().toolIcons]);

  // ---------- painting ----------
  let reportT = 0;
  function reportNow() {
    reportT = 0;
    const rs = [pill.getBoundingClientRect()];
    if (cardEl && cardEl.isConnected) rs.push(cardEl.getBoundingClientRect());
    const x = Math.min(...rs.map((r) => r.left)), y = Math.min(...rs.map((r) => r.top));
    const r2 = Math.max(...rs.map((r) => r.right)), b = Math.max(...rs.map((r) => r.bottom));
    nt.reportRect({ x: Math.floor(x), y: Math.floor(y), width: Math.ceil(r2 - x), height: Math.ceil(b - y) });
  }
  function scheduleReport() { if (!reportT) reportT = setTimeout(reportNow, 50); }
  new ResizeObserver(scheduleReport).observe(pill);
  pill.addEventListener('transitionend', scheduleReport);

  function paint() {
    if (!S) return;
    const expanded = !!S.expanded;
    const inl = toastInline();
    const mode = expanded ? 'open' : inl ? 'toast' : 'closed';
    // drop picks of asks that are gone
    const ids = new Set((S.asks || []).map((a) => a.id));
    for (const id of [...PICKS.keys()]) if (!ids.has(id)) PICKS.delete(id);

    let kids;
    if (mode === 'open') kids = [part('top', topKey(), topBar), part('open', openKey(), openBody)];
    else if (mode === 'toast') kids = [part('toast', JSON.stringify([inl, st().toastStyle, st().size]), () => toastEl(inl))];
    else kids = [part('closed', closedKey() + JSON.stringify(S.toast ? 1 : 0), closedView)];
    if (mode !== lastMode) {
      inner.classList.remove('enter');
      void inner.offsetWidth;
      inner.classList.add('enter');
      lastMode = mode;
    }
    inner.replaceChildren(...kids);

    const bg = st().background || 'black';
    const opened = mode !== 'closed';
    const hasCollapsed = !!((st().badges || []).length || chipsOf().length);
    const cls = ['pill'];
    if (mode === 'open') cls.push('open');
    if (mode === 'toast') cls.push('toast', inl.compact || st().toastStyle === 'compact' ? 'compact' : '');
    if (opened && bg === 'gradient') cls.push('bg-gradient');
    if (opened && bg === 'glass') cls.push('bg-glass');
    if (bg === 'glow' && (opened || hasCollapsed)) cls.push('glow');
    pill.className = cls.filter(Boolean).join(' ');
    pill.style.width = inner.offsetWidth + 'px';
    // a long open notch scrolls inside the pill: the window is never taller than its size
    pill.style.height = Math.min(inner.offsetHeight, window.innerHeight - 12) + 'px';

    // the card toast floats under the pill (never while the notch is open)
    const cardT = S.toast && st().toastStyle === 'card' && !expanded ? S.toast : null;
    if (cardT) {
      const el = part('card', JSON.stringify([cardT, st().background]), () => cardToastEl(cardT));
      if (el !== cardEl) { if (cardEl) cardEl.remove(); cardEl = el; }
      if (!el.isConnected) stage.appendChild(el);
    } else if (cardEl) {
      cardEl.remove();
      cardEl = null;
    }
    scheduleReport();
  }

  function applyGlobals() {
    const motion = st().motion || 'bouncy';
    document.body.dataset.motion = motion;
    document.body.classList.toggle('reduced', motion === 'reduced');
    const root = document.documentElement.style;
    root.setProperty('--grad-a', st().gradientA || '#1E1B4B');
    root.setProperty('--grad-b', st().gradientB || '#000000');
    const acc = accentColor() || SYS.orange;
    root.setProperty('--accent', acc);
    root.setProperty('--glow', acc);
    root.setProperty('--gray', '#8e8e93');
    pill.style.setProperty('--tint', acc);
    document.body.dataset.open = S.expanded ? '1' : '';
    document.body.style.setProperty('--open-w', (st().size === 'compact' ? 480 : 520) + 'px');
  }

  // the window forwards mouse moves while it ignores the mouse: wake main's cursor poll (throttled)
  let pokeAt = 0;
  pill.addEventListener('mousemove', () => {
    const now = Date.now();
    if (now - pokeAt >= 100) { pokeAt = now; nt.poke(); }
  });

  // dropping files from Explorer onto the pill goes to the shelf
  pill.addEventListener('dragover', (e) => {
    if (!isActive('shelf') || !e.dataTransfer || ![...e.dataTransfer.types].includes('Files')) return;
    e.preventDefault();
    if (!dropOver) { dropOver = true; paint(); }
  });
  pill.addEventListener('dragleave', (e) => {
    if (!e.relatedTarget && dropOver) { dropOver = false; paint(); }
  });
  pill.addEventListener('drop', (e) => {
    e.preventDefault();
    dropOver = false;
    const paths = [...(e.dataTransfer.files || [])].map((f) => nt.pathForFile(f)).filter(Boolean);
    if (paths.length) nt.act('shelfAdd', paths);
    paint();
  });
  document.addEventListener('visibilitychange', () => document.body.classList.toggle('hidden', document.hidden));

  nt.onState((s) => {
    S = s;
    STATE_AT = Date.now();
    applyGlobals();
    paint();
  });
})();
