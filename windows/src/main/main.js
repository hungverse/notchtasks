'use strict';
// NotchTasks for Windows, main process: Sources/Model.swift and AppDelegate.swift in Electron. Three modes, decided
// before anything else: `--hook <agent>` (an agent asks; no window, no single-instance lock), `--statusline` (Claude's
// statusline; no window), and the app. The pill and the Control Panel both read one state object, built by buildState().

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFile } = require('node:child_process');
const { pathToFileURL, fileURLToPath } = require('node:url');
const { app, BrowserWindow, dialog, Menu, nativeImage, Notification, powerMonitor, safeStorage, shell, Tray } = require('electron');
const l10n = require('../core/l10n');
const settings = require('../core/settings');
const asks = require('../core/asks');
const P = require('../core/providers');
const proc = require('../core/proc');
const watch = require('../core/watchitems');
const { DevScanner, FileProgressWatcher, folderFromArgv } = require('../core/devscan');
const runnerlog = require('../core/runnerlog');
const { GitHubWatcher } = require('../core/github');
const { createStats, netInfo, freeDisk, batteryFrom, readThermalsWmi } = require('../core/stats');
const { createMedia } = require('../core/media');
const { createDownloads } = require('../core/downloads');
const { createTrack } = require('../core/track');
const updater = require('../core/updater');
const { createSync } = require('../core/sync');
const { createPill } = require('./pill');
const prefs = require('./prefs-window');

const HOOK_AT = process.argv.indexOf('--hook');
const STATUSLINE = process.argv.includes('--statusline');

const { L } = l10n;
const SHOT = process.env.NT_SHOT || '';
const WIN = process.platform === 'win32';
const APP_BUILD = 20200; // 2.2.0 as major * 10000 + minor * 100 + patch: an update manifest must name a higher build

const TINT = { green: '#30D158', red: '#FF453A', orange: '#FF9F0A', blue: '#0A84FF', gray: '#8E8E93' };
// Sound names of the Mac packs, as Windows files that every install has in C:\Windows\Media.
const SOUND_FILES = {
  Basso: 'Windows Error.wav', Blow: 'Speech On.wav', Bottle: 'chord.wav', Frog: 'notify.wav', Funk: 'Windows Exclamation.wav',
  Glass: 'Windows Ding.wav', Hero: 'tada.wav', Morse: 'Windows Notify System Generic.wav', Ping: 'ding.wav', Pop: 'Windows Message Nudge.wav',
  Purr: 'Windows Proximity Notification.wav', Sosumi: 'recycle.wav', Submarine: 'Windows Background.wav', Tink: 'chimes.wav',
};
const AGENT_BY_CLI = { agy: 'antigravity', gemini: 'gemini', 'cursor-agent': 'cursor', opencode: 'opencode' };
const LMSTUDIO = 'ai.elementlabs.lmstudio';
const PRELOAD_PILL = path.join(__dirname, 'preload.js');
const PRELOAD_PREFS = path.join(__dirname, 'prefs-preload.js');

// ---- the two small modes: answer, print, exit. Nothing else is loaded or started.

function readStdin(done) {
  if (process.stdin.isTTY) return done('');
  let text = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (d) => { text += d; });
  process.stdin.on('end', () => done(text));
  process.stdin.on('error', () => done(text));
}

// The output is flushed before the exit: a pipe on Windows writes asynchronously.
function finish(out, code) {
  if (!out) return app.exit(code);
  process.stdout.write(out, () => app.exit(code));
}

// Claude's statusline: keep the JSON for the app (rate limits), print "5h 12% · 7d 3%" like notch-statusline.sh.
function runStatusline(input) {
  try {
    const dir = P.claudeDir();
    fs.mkdirSync(dir, { recursive: true });
    P.writeAtomic(path.join(dir, 'notchtasks-status.json'), input);
  } catch { /* the line still prints */ }
  let line = '';
  try {
    const rl = JSON.parse(input).rate_limits || {};
    line = [['five_hour', '5h'], ['seven_day', '7d']]
      .filter(([k]) => typeof rl[k]?.used_percentage === 'number')
      .map(([k, n]) => `${n} ${Math.trunc(rl[k].used_percentage)}%`).join(' · ');
  } catch { /* not JSON: nothing to print */ }
  finish(line ? `${line}\n` : '', 0);
}

// ======================================================================================================================
// The app. Module state is kept here; every window and poller reads it through buildState().

let userDir = '';
let kvFile = '';
let kv = {};                       // small app state the settings file does not hold (shelf, dismissals, sync notes…)
let cfg = null;                    // the settings snapshot (settings.get)
let pill = null, sync = null, tray = null, upd = null, track = null;
let codex = null, antigravity = null, devScanner = null, files = null, sampler = null, media = null, downloads = null;
let github = null;
let syncState = null;              // the last state sync.js emitted
let syncNotice = null;             // a problem found here (group not saved), shown on the phone page
let memoryGroup;                   // the pairing group when it cannot be saved encrypted (undefined = not read yet)
let lockedNow = false;

const M = {
  expanded: false, states: [], limits: [], alerts: [], nowPlaying: null, asks: [], shelf: [], toast: null,
  timerEnd: null, timerTotal: 0, load: null, thermals: [], battery: null, agents: [], agentError: null, running: [],
  matches: null, lastProcs: [], pairUrl: null, pairBusy: false,
  report: { busy: false, code: null, problem: null },
  update: { available: false, busy: false, progress: null, error: null, version: '', notes: '', manifest: null },
};
const toastQueue = [];
let toastTimer = null, toastSince = 0, toastCalmAt = 0;
let hookAsks = [];                 // asks the agents' hooks left in the Asks folder (ms dates)
const answeredCodex = new Set();
let codexLimitsCache = null;
let claudeUsage = null, claudeBusy = false;
let antigravityLimits = null;
let localModels = {};
let hookWatch = null;
const busyAt = new Map();          // process pid -> last time it was busy (6 s stickiness)
const workingSince = new Map();    // "item/job" -> {item, title, since, lastSeen}
const warnedLimits = new Set();
let gitlabPrev = [], gitlabJobs = [];
const ghWorkers = new Map();       // Runner.Worker pid -> {job, log}
let lowBatteryWarned = false, hotWarned = false, diskWarned = false;
let netOnline = null, vpnOn = null, netPending = null, netPendingAt = 0;
let batteryWmi = { at: 0, pct: null, status: 0 };
let procBusy = false, statsBusy = false, batteryBusy = false, pushedJson = '', pushTimer = null, lastPushAt = 0;
let itemMeter = null;
const lastRun = {};
const due = (key, ms, now) => {
  if (now - (lastRun[key] || 0) < ms) return false;
  lastRun[key] = now;
  return true;
};
const on = (mod) => !!cfg && !cfg.disabled.includes(mod);

// ---- small state on disk (kv) ------------------------------------------------------------------------------------

function loadKv() {
  try { kv = JSON.parse(fs.readFileSync(kvFile, 'utf8')) || {}; } catch { kv = {}; }
}
const kvGet = (k) => kv[k];
function kvSet(k, v) {
  if (v === undefined) delete kv[k]; else kv[k] = v;
  try { P.writeAtomic(kvFile, JSON.stringify(kv)); } catch (e) { console.error('state:', e.message); }
}

// ---- the state both windows read ------------------------------------------------------------------------------------

function toast(o) {
  return { id: crypto.randomUUID(), tint: TINT.orange, ...o };
}

function notchOf(c) {
  return {
    size: c.size, expandMode: c.expandMode, accent: c.accent, customAccent: c.customAccent, background: c.background,
    gradientA: c.gradientA, gradientB: c.gradientB, toastStyle: c.toastStyle, motion: c.motion,
    modules: [...c.modules], disabled: [...c.disabled], placements: { ...c.placements }, haptics: false,
    sounds: c.sounds, volume: c.volume, systemNotifications: c.systemNotifications, doneAfter: c.doneAfter,
    limitWarn: c.limitWarn, lockScreen: false, lockShows: [], badges: c.badges, lockOwnBadges: false, lockBadges: [],
    toolIcons: c.toolIcons, seenTools: c.seenTools.slice(0, 12),
  };
}

function syncView() {
  const s = syncState || {};
  const holders = s.proHolders || [];
  return {
    paired: !!s.paired,
    phones: Object.entries(s.phones || {}).map(([uid, name]) => ({ uid, name, pro: holders.includes(uid) })),
    url: M.pairUrl, proUntil: s.proUntil ?? null, problem: s.problem || syncNotice || null, busy: M.pairBusy,
    lan: s.lan !== false, onlyAway: s.onlyAway !== false, direct: s.lanPhones || 0,
  };
}

function buildState() {
  const working = M.states.reduce((n, s) => n + s.jobs.filter((j) => j.working).length, 0);
  return {
    lang: cfg.language,
    choice: cfg.language,
    device: l10n.device(),
    version: app.getVersion(),
    expanded: M.expanded,
    settings: cfg,
    items: cfg.items,
    states: M.states,
    limits: M.limits,
    alerts: M.alerts,
    nowPlaying: M.nowPlaying,
    toast: M.toast,
    updatePending: M.update.available,
    asks: M.asks,
    shelf: M.shelf,
    timer: M.timerEnd ? { end: M.timerEnd, total: M.timerTotal } : null,
    load: M.load,
    thermals: M.thermals,
    battery: M.battery,
    live: { working, alerts: M.alerts.length },
    agents: M.agents,
    agentError: M.agentError,
    running: M.running,
    matches: M.matches,
    sync: syncView(),
    update: { ...M.update, manifest: undefined },
    analytics: track.enabled,
    report: M.report,
  };
}

// The sync input: runs, builds, asks, limits, health and the notch, as Sync.swift's snapshot takes them.
function syncInput() {
  const runs = [], builds = [];
  for (const st of M.states) {
    const build = watch.inBuild(st.item);
    for (const j of st.jobs) {
      if (!j.working) continue;
      if (build) builds.push({ id: j.id, title: j.title, detail: j.detail, icon: (j.tool && cfg.toolIcons[j.tool]) || st.item.icon });
      else runs.push({ id: j.id, title: j.title, detail: j.detail, agent: agentOf(st.item), icon: st.item.icon });
    }
  }
  const hot = M.thermals.reduce((a, t) => (!a || t.celsius > a.celsius ? t : a), null);
  return {
    runs, builds, asks: M.asks,
    limits: M.limits.flatMap((p) => p.windows.map((w) => ({ provider: p.name, label: w.label, used: w.used, resets: w.resets }))),
    health: {
      battery: M.battery ? M.battery.percent : null, charging: !!(M.battery && M.battery.charging), thermal: 0,
      temp: hot ? hot.celsius : null, cpu: M.load ? M.load.cpu : null, memory: M.load ? M.load.memory : null,
    },
    notch: notchOf(cfg),
  };
}

const agentOf = (item) => (item.kind === 'claude' || item.kind === 'codex' ? item.kind : AGENT_BY_CLI[item.cli]);

// Pushes the state to the pill and to open Control Panel windows, at most every 250 ms unless `now`. Unchanged = no push.
function schedulePush(now = false) {
  const wait = Date.now() - lastPushAt;
  if (now || wait >= 250) {
    clearTimeout(pushTimer);
    pushTimer = null;
    pushNow();
  } else if (!pushTimer) {
    pushTimer = setTimeout(() => { pushTimer = null; pushNow(); }, 250 - wait);
  }
}

function pushNow() {
  if (!cfg || !pill) return;
  const state = buildState();
  const json = JSON.stringify(state);
  if (json === pushedJson) return;
  pushedJson = json;
  lastPushAt = Date.now();
  pill?.send(state);
  prefs.pushState(state);
  sync?.update(syncInput());
}

// ---- settings reactions ----------------------------------------------------------------------------------------------

function onSettings(s, keys) {
  cfg = s;
  if (keys.includes('language')) {
    l10n.setChoice(s.language);
    buildTray();
  }
  if (keys.includes('modules') || keys.includes('disabled')) syncModules();
  schedulePush();
}

function syncModules() {
  if (on('media')) media?.start(); else media?.stop();
  if (on('downloads')) downloads?.start(); else downloads?.stop();
  if (!on('battery')) M.battery = null;
}

// ---- pill: expansion, sounds -----------------------------------------------------------------------------------------

function soundURL(name) {
  if (!name) return null;
  if (path.isAbsolute(name)) return pathToFileURL(name).href;
  const file = SOUND_FILES[name];
  if (!file) return null;
  return pathToFileURL(path.join(process.env.SystemRoot || 'C:\\Windows', 'Media', file)).href;
}

// The page plays the file (preload.js 'play'); a missing sound is silence, never an error.
function playSound(name, volume) {
  const url = soundURL(name);
  if (url && pill) pill.win.webContents.send('play', url, volume ?? cfg.volume);
}

function playEvent(id) {
  if (cfg.sounds) playSound(cfg.soundMap[id], cfg.volume);
}

function setExpanded(v) {
  if (v === M.expanded) return;
  M.expanded = v;
  playEvent(v ? 'open' : 'close');
  if (v) track.event('notch_open');
  media?.setSlow(!v);
  schedulePush(true);
}

// ---- toasts ----------------------------------------------------------------------------------------------------------

// A toast for the pill and, when it has an event, the phone (Sync.record), the sound and a system notification.
function announce(t, { notify = true } = {}) {
  const item = toast(t);
  sync?.record({ kind: item.event, title: item.title, detail: item.subtitle, toPhone: item.toPhone });
  if (item.event) playEvent(item.event);
  if (notify && cfg.systemNotifications && Notification.isSupported()) {
    new Notification({ title: item.title, body: item.subtitle || '', silent: true }).show();
  }
  toastQueue.push(item);
  if (!M.toast) showNext();
}

function showNext() {
  M.toast = toastQueue.shift() || null;
  clearTimeout(toastTimer);
  if (M.toast) {
    toastSince = toastCalmAt = Date.now();
    toastTimer = setTimeout(toastCheck, 300);
  }
  schedulePush(true);
}

// Shown 30 s if sticky, 3 s if compact, else 4.5 s; kept while the pointer is on it, and 1.5 s after it leaves.
function toastCheck() {
  if (!M.toast) return;
  const now = Date.now();
  if (pill && pill.hovered()) toastCalmAt = now;
  const dur = M.toast.sticky ? 30000 : M.toast.compact ? 3000 : 4500;
  if (now - toastSince >= dur && now - toastCalmAt >= 1500) return showNext();
  toastTimer = setTimeout(toastCheck, 300);
}

function dropAskToasts(ids) {
  for (let i = toastQueue.length - 1; i >= 0; i--) {
    if (toastQueue[i].ask && ids.has(toastQueue[i].ask.id)) toastQueue.splice(i, 1);
  }
  if (M.toast?.ask && ids.has(M.toast.ask.id)) showNext();
}

// ---- agent asks --------------------------------------------------------------------------------------------------------

const normAsk = (a) => ({
  id: a.id, pid: a.pid, session: a.session, tool: a.tool, summary: a.summary, questions: a.questions || [],
  at: +new Date(a.at), agent: a.agent,
});

function askToast(a) {
  const isQ = a.questions.length > 0;
  const name = asks.AGENTS[a.agent]?.name || a.agent;
  return toast({
    symbol: isQ ? 'questionmark.bubble.fill' : 'hand.raised.fill', tint: TINT.orange,
    title: name + (isQ ? L(' đang hỏi') : L(' cần bạn duyệt')),
    subtitle: a.session + ' · ' + (isQ ? a.summary : `${asks.actionLabel(a.tool)}: ${a.summary}`),
    event: 'agentAsk', sticky: true, ask: a,
  });
}

// Questions the Codex app left in its threads (no hook involved): the answer goes back as the next message.
function codexAsks() {
  if (!codex || !cfg.items.some((i) => i.enabled && i.kind === 'codex')) return [];
  return codex.questions(new Date()).flatMap(({ question: q, title }) => {
    const id = `codex|${q.thread}|${q.callID}`;
    if (answeredCodex.has(id)) return [];
    return [{ id, pid: 0, session: title.startsWith('g-p-') ? 'Codex' : title, tool: 'request_user_input',
      summary: q.questions[0]?.question ?? '', questions: q.questions, at: +q.at, agent: 'codex' }];
  });
}

function refreshAsks() {
  if (!on('agents')) {
    for (const a of hookAsks) asks.reply(a.id, { kind: 'pass' });  // the agent asks in its own window, as the Mac does
    hookAsks = [];
  }
  const found = [...(on('agents') ? hookAsks : []), ...codexAsks()];
  const ids = new Set(found.map((a) => a.id));
  const gone = M.asks.filter((a) => !ids.has(a.id)).map((a) => a.id);
  const fresh = found.filter((f) => !M.asks.some((a) => a.id === f.id));
  const changed = gone.length > 0 || fresh.length > 0;
  M.asks = found;
  if (gone.length) dropAskToasts(new Set(gone));
  for (const a of fresh) announce(askToast(a));
  if (changed) schedulePush(true);
}

function answer(id, reply) {
  if (typeof id !== 'string' || !reply || typeof reply !== 'object') return;
  const a = M.asks.find((x) => x.id === id);
  if (!a) return;
  track.event('ask_reply');
  if (id.startsWith('codex|')) {
    answeredCodex.add(id);
    if (reply.kind === 'answers') {
      const picked = reply.answers || {};
      const text = a.questions.length === 1 ? (Object.values(picked)[0] ?? '')
        : a.questions.map((q) => `${q.question}: ${picked[q.question] ?? ''}`).join('\n');
      asks.codexReply(id.split('|')[1], text).then((r) => { if (!r.ok) console.error('codex reply:', r.out); });
    }
  } else {
    asks.reply(id, reply);  // Asks/<id>.reply: the hook is waiting for it
  }
  M.asks = M.asks.filter((x) => x.id !== id);
  dropAskToasts(new Set([id]));
  if (!M.asks.length && reply.kind !== 'pass') setExpanded(false);
  schedulePush(true);
}

// ---- processes: watch items, dev tasks, runners ------------------------------------------------------------------------

// CPU of a process tree and its busiest child (the detail says what it runs).
function treeCpu(root, kids) {
  let total = itemMeter.percent(root), top = null, topCpu = 1;
  const seen = new Set([root]), stack = [root];
  while (stack.length) {
    for (const c of kids.get(stack.pop()) || []) {
      if (seen.has(c.pid)) continue;
      seen.add(c.pid);
      const v = itemMeter.percent(c.pid);
      total += v;
      if (v > topCpu) { top = c.name; topCpu = v; }
      stack.push(c.pid);
    }
  }
  return { total, top };
}

// One row for a process: working while its tree uses more than the threshold, and for 6 s after.
function procJob(p, o) {
  const key = String(p.pid);
  const { total, top } = treeCpu(p.pid, o.kids);
  if (total > o.threshold) busyAt.set(key, o.now);
  const working = busyAt.has(key) && o.now - busyAt.get(key) < 6000;
  const pct = `${Math.round(total)}% CPU`;
  const detail = working ? (o.busy ? `${o.busy} · ${pct}` : top ? `▶ ${top} · ${pct}` : L('Đang làm việc · {0}', pct)) : o.idle;
  return { id: key, title: o.title, detail, working };
}

function claudeJobs(byPid, now) {
  const out = [];
  for (const s of P.claudeSessions()) {
    const live = byPid.get(s.pid);
    if (!live || proc.normName(live.name) !== 'claude') continue;
    const busy = s.status === 'busy';
    const since = P.ago(Math.max(0, (now - (s.statusUpdatedAt ?? now)) / 1000));
    out.push({
      id: String(s.pid), title: s.name || (s.cwd ? P.lastComponent(s.cwd) : '') || 'Claude',
      detail: busy ? L('Đang làm việc · {0}', since) : L('Đang chờ'), working: busy,
    });
  }
  return out;
}

const localKey = (item) => (item.bundleID === LMSTUDIO ? 'lmstudio' : item.process === 'ollama' ? 'ollama' : null);
function modelTitleOf(item) {
  const names = localModels[localKey(item)] || [];
  return names.length ? names.join(', ') : null;
}
function modelLineOf(item) {
  const key = localKey(item);
  if (!key) return null;
  const names = localModels[key] || [];
  return names.length ? L('Đã nạp ') + names.join(', ') : L('Chưa nạp model');
}

// Each enabled watch item: its running processes as rows. Build items feed the Build & CI row instead (in computeAll).
function itemStates(procs, byPid, kids, now) {
  const out = [];
  const argvOf = (p) => proc.splitArgs(p.cmdline);
  const at = new Date(now);
  for (const item of cfg.items) {
    if (!item.enabled || !on(watch.inBuild(item) ? 'ci' : 'agents')) continue;
    let jobs = [], alive = false;
    const threshold = watch.cpuThreshold(item);
    if (item.kind === 'process' && item.cli === 'agy') {
      jobs = antigravity ? antigravity.jobs(at) : [];
    } else if (item.kind === 'process') {
      for (const p of watch.roots(item, procs, argvOf)) {
        alive = true;
        if (item.childTasks) {
          for (const c of kids.get(p.pid) || []) {
            jobs.push(procJob(c, { title: folderFromArgv(argvOf(c)) || c.name, threshold, idle: L('Đang chờ'), kids, now }));
          }
        } else {
          jobs.push(procJob(p, {
            title: modelTitleOf(item) || folderFromArgv(argvOf(p)) || item.name, threshold, kids, now,
            idle: modelLineOf(item) || L('Đang chờ'), busy: modelTitleOf(item) ? L('Đang chạy') : null,
          }));
        }
      }
    } else if (item.kind === 'app') {
      const want = proc.normName(watch.appExe(item) || '');
      for (const p of procs) {
        if (!want || proc.normName(p.name) !== want || proc.normName(byPid.get(p.ppid)?.name) === want) continue;
        alive = true;
        jobs.push(procJob(p, {
          title: modelTitleOf(item) || item.name, threshold, kids, now,
          idle: modelLineOf(item) || L('Đang mở'), busy: modelTitleOf(item) ? L('Đang chạy') : null,
        }));
      }
    } else if (item.kind === 'codex') {
      jobs = codex ? codex.jobs(at) : [];
    } else if (item.kind === 'claude') {
      jobs = claudeJobs(byPid, now);
    }
    if (alive || jobs.length) out.push({ item, jobs: jobs.sort((a, b) => Number(b.working) - Number(a.working)) });
  }
  return out;
}

// GitHub self-hosted runners: each Runner.Worker is a job; its log says how it ended.
function scanWorkers(procs) {
  const rows = [];
  const seen = new Set();
  for (const p of procs) {
    if (proc.normName(p.name) !== 'runner.worker') continue;
    const exe = proc.splitArgs(p.cmdline)[0];
    const job = runnerlog.workerJob({ pid: p.pid, exe });
    rows.push(job);
    seen.add(p.pid);
    const log = exe ? runnerlog.newestWorkerLog(runnerlog.diagFromWorkerExe(exe)) : null;
    ghWorkers.set(p.pid, { job, log });
  }
  for (const [pid, w] of [...ghWorkers]) {
    if (seen.has(pid)) continue;
    ghWorkers.delete(pid);
    const result = w.log ? runnerlog.workerResult(runnerlog.readTail(w.log, 2000000)) : null;
    if (!result) continue;
    const ok = result === 'Succeeded';
    announce(toast({
      symbol: ok ? 'checkmark.seal.fill' : 'xmark.octagon.fill', tint: ok ? TINT.green : TINT.red,
      title: (ok ? L('CI đạt: ') : L('CI lỗi: ')) + w.job.detail, subtitle: w.job.title, event: ok ? 'ciPassed' : 'ciFailed',
    }));
  }
  return rows;
}

function announceFinished(states, now) {
  if (!on('agents') && !on('ci')) { workingSince.clear(); return; }
  for (const st of states) {
    for (const j of st.jobs) {
      if (!j.working || (st.item.kind === 'dev' && /^(gl:|gh)/.test(j.id))) continue;  // CI results come from the runner
      const key = `${st.item.id}/${j.id}`;
      const prev = workingSince.get(key);
      workingSince.set(key, { item: st.item, title: j.title, since: prev ? prev.since : now, lastSeen: now });
    }
  }
  for (const [key, w] of [...workingSince]) {
    if (now - w.lastSeen < 20000) continue;  // a job that blinks off for one poll is still running
    workingSince.delete(key);
    const took = (w.lastSeen - w.since) / 1000;
    if (took < cfg.doneAfter) continue;
    const dev = w.item.kind === 'dev';
    announce(toast({
      symbol: 'checkmark.circle.fill', tint: TINT.green, title: L('{0} xong', dev ? w.title : w.item.name),
      subtitle: dev ? P.ago(took) : `${w.title} · ${P.ago(took)}`, icon: dev ? undefined : (w.item.icon || undefined),
      event: 'taskDone', toPhone: true,
    }));
  }
}

// Build & CI: local tasks, file transfers, GitLab jobs, GitHub self-hosted workers. Nothing to configure.
function computeAll(procs, now) {
  const byPid = new Map(procs.map((p) => [p.pid, p]));
  const kids = proc.childrenOf(procs);
  itemMeter.sample(procs, now);
  for (const [pid, at] of [...busyAt]) if (now - at > 60000) busyAt.delete(pid);
  const states = itemStates(procs, byPid, kids, now);
  if (on('ci')) {
    const jobs = [...devScanner.scan(procs, now), ...files.jobs(), ...gitlabJobs, ...scanWorkers(procs)];
    if (jobs.length) states.unshift({ item: watch.buildItem, jobs });
    const tools = new Set(jobs.map((j) => j.tool).filter(Boolean));
    const fresh = [...tools].filter((t) => !cfg.seenTools.includes(t));
    if (fresh.length) settings.set('seenTools', [...fresh, ...cfg.seenTools].slice(0, 200));
  } else {
    files.stop();
    gitlabJobs = [];
    M.alerts = [];
  }
  M.states = states;
  M.lastProcs = procs;
  M.running = runningApps(procs);
  announceFinished(states, now);
}

// Apps started from the desktop: the children of explorer.exe, by name.
function runningApps(procs) {
  const shell_ = procs.filter((p) => proc.normName(p.name) === 'explorer').map((p) => p.pid);
  const names = new Set();
  for (const p of procs) {
    if (shell_.includes(p.ppid) && p.name && p.name !== 'notchtasks' && p.name !== 'electron') names.add(p.name);
  }
  return [...names].sort().slice(0, 80).map((n) => ({ name: n.charAt(0).toUpperCase() + n.slice(1), bundleID: `${n}.exe` }));
}

async function pollProcs(now) {
  if (procBusy) return;
  procBusy = true;
  try {
    computeAll(await proc.list(), now);
  } catch (e) {
    console.error('processes:', e.message);
  } finally {
    procBusy = false;
  }
}

// GitLab runner logs: "received" starts a job, "Job succeeded/failed" ends it. Folders are the usual install paths.
function gitlabLogs() {
  const dirs = [path.join(process.env.ProgramData || 'C:\\ProgramData', 'gitlab-runner'), 'C:\\GitLab-Runner'];
  const out = [];
  for (const d of dirs) {
    let names;
    try { names = fs.readdirSync(d); } catch { continue; }
    for (const n of names) {
      if (!n.endsWith('.log')) continue;
      const file = path.join(d, n);
      try { out.push({ file, mtime: fs.statSync(file).mtimeMs }); } catch { /* vanished */ }
    }
  }
  return out.sort((a, b) => b.mtime - a.mtime).slice(0, 3);
}

function pollRunners(now) {
  let running = [];
  const failed = [], succeeded = new Set();
  for (const f of gitlabLogs()) {
    const parsed = runnerlog.parseGitLab(runnerlog.readTail(f.file, 512000));
    failed.push(...parsed.failed);
    for (const id of parsed.succeeded) succeeded.add(id);
    if (now - f.mtime < 5 * 60000) running = running.concat(parsed.running);
  }
  for (const gone of gitlabPrev) {
    if (running.some((r) => r.id === gone.id)) continue;
    if (failed.some((f) => f.id === gone.id)) {
      announce(toast({ symbol: 'xmark.octagon.fill', tint: TINT.red, title: L('CI lỗi: {0}', gone.name), subtitle: gone.project,
        event: 'ciFailed', url: gone.url, action: L('Xem log') }));
    } else if (succeeded.has(gone.id)) {
      announce(toast({ symbol: 'checkmark.seal.fill', tint: TINT.green, title: L('CI đạt: {0}', gone.name), subtitle: gone.project,
        event: 'ciPassed', url: gone.url }));
    }
  }
  gitlabPrev = running;
  gitlabJobs = running.map((r) => ({ id: `gl:${r.id}`, title: r.project, detail: r.name, working: true, url: r.url,
    symbol: 'server.rack', tool: 'GitLab Runner' }));
  // failures already in the log when the app first looks are history, not news
  const dismissed = kvGet('dismissedJobs');
  if (!dismissed) { kvSet('dismissedJobs', failed.map((f) => f.id)); return; }
  const seen = new Set(dismissed);
  const alerts = [];
  for (const f of failed.slice().reverse()) {
    if (seen.has(f.id) || alerts.some((a) => a.id === f.id)) continue;
    alerts.push({ id: f.id, name: f.name, project: f.project, reason: f.reason, url: f.url });
  }
  M.alerts = alerts;
  schedulePush(true);
}

function githubFinished(runs) {
  const dismissed = new Set(kvGet('dismissedJobs') || []);
  for (const r of runs) {
    if (r.conclusion === 'success') {
      announce(toast({ symbol: 'checkmark.seal.fill', tint: TINT.green, title: L('CI đạt: {0}', r.workflow), subtitle: r.repo, event: 'ciPassed', url: r.url }));
    } else if (['failure', 'timed_out', 'startup_failure'].includes(r.conclusion || '')) {
      announce(toast({ symbol: 'xmark.octagon.fill', tint: TINT.red, title: L('CI lỗi: {0}', r.workflow), subtitle: r.repo,
        event: 'ciFailed', url: r.url, action: L('Xem log') }));
      if (!dismissed.has(`gh-${r.id}`)) M.alerts = [{ id: `gh-${r.id}`, name: r.workflow, project: r.repo, reason: r.title || 'failure', url: r.url }, ...M.alerts];
    }
  }
}

async function refreshLocal() {
  const map = {};
  if (cfg.items.some((i) => i.enabled && i.process === 'ollama')) map.ollama = await P.ollamaLoaded();
  if (cfg.items.some((i) => i.enabled && i.bundleID === LMSTUDIO)) map.lmstudio = P.lmStudioLoaded();
  localModels = map;
}

// ---- limits ----------------------------------------------------------------------------------------------------------

function claudeNow(now) {
  const u = claudeUsage;
  if (!u || now - u.at >= 15 * 60000) return P.claudeLimits(new Date(now));
  return {
    kind: 'claude', name: 'Claude', bundleID: 'com.anthropic.claudefordesktop', updated: new Date(u.at),
    windows: u.windows.map((w) => ({ label: w.label, used: w.resets.getTime() > now ? w.used : 0, resets: w.resets })),
  };
}

// The Mac's warnings: each window once per step (80, 85… 99, 100), and once more after its reset.
function warnLimits(list, now) {
  for (const p of list) {
    for (const w of p.windows) {
      const step = P.limitStep(w.used);
      if (step < cfg.limitWarn) continue;
      const key = `${p.name}-${w.label}-${Math.trunc(w.resets.getTime() / 3600000)}-${step}`;
      if (warnedLimits.has(key)) continue;
      warnedLimits.add(key);
      const at = P.resetText(w, { now: new Date(now) });
      const left = Math.trunc(Math.max(0, (w.resets.getTime() - now) / 60000));
      const wait = left >= 60 ? L('{0} giờ {1} phút', Math.trunc(left / 60), left % 60) : L('{0} phút', left);
      announce(toast({
        symbol: step === 100 ? 'hourglass' : 'gauge.with.dots.needle.100percent', tint: step >= 95 ? TINT.red : TINT.orange,
        title: step === 100 ? L('{0} đã hết giới hạn {1}', p.name, w.label)
          : L('{0} đã dùng {1}% giới hạn {2}', p.name, Math.trunc(w.used), w.label),
        subtitle: step === 100 ? L('Chờ đến {0} (còn {1})', at, wait) : L('Đặt lại lúc {0}', at),
        event: 'limitWarn', sticky: true,
      }));
    }
  }
}

function refreshLimits(now) {
  const enabled = new Set(cfg.items.filter((i) => i.enabled).map((i) => i.kind));
  const list = [];
  if (on('limits')) {
    const c = enabled.has('claude') ? claudeNow(now) : null;
    if (c) list.push(c);
    if (enabled.has('codex') && codexLimitsCache) list.push(codexLimitsCache);
    if (antigravityLimits && cfg.items.some((i) => i.enabled && i.cli === 'agy')) list.push(antigravityLimits);
  }
  M.limits = list.map((p) => ({
    kind: p.kind, name: p.name, bundleID: p.bundleID, updated: +p.updated,
    windows: p.windows.map((w) => ({ label: w.label, used: w.used, resets: +w.resets })),
  }));
  warnLimits(list, now);
}

async function refreshClaudeUsage() {
  const cli = P.claudeUsageCli();
  if (claudeBusy || !cli) return;
  claudeBusy = true;
  const now = Date.now();
  try {
    const windows = await P.runClaudeUsage(new Date(now), cli);
    if (windows.length) claudeUsage = { at: now, windows };
  } finally {
    claudeBusy = false;
  }
}

// ---- stats, battery, network, disk ------------------------------------------------------------------------------------

// The thermal read spawns PowerShell: at most every 30 s, however often the stats are sampled.
let thermalCache = { at: 0, value: [] };
async function readThermalsCached() {
  if (Date.now() - thermalCache.at < 30000) return thermalCache.value;
  thermalCache = { at: Date.now(), value: await readThermalsWmi() };
  return thermalCache.value;
}

async function pollStats() {
  if (statsBusy) return;
  statsBusy = true;
  try {
    const s = await sampler.sample();
    if (!s) return;
    M.load = { cpu: s.cpu, memory: s.memory };
    M.thermals = s.thermalsAvailable ? s.thermals.map((t) => ({ name: t.name, celsius: t.celsius })) : [];
    const hot = s.hottest ? s.hottest.celsius : null;
    if (on('health') && hot !== null && hot >= 90 && !hotWarned) {
      hotWarned = true;
      const top = M.states.flatMap((st) => st.jobs).find((j) => j.working);
      announce(toast({ symbol: 'thermometer.high', tint: hot >= 95 ? TINT.red : TINT.orange, title: L('Máy đang nóng'),
        subtitle: top ? L('{0} đang chạy nặng', String(top.title).split(' · ')[0]) : L('Nên đóng bớt ứng dụng nặng'), event: 'warning' }));
    } else if (hot !== null && hot < 80) {
      hotWarned = false;
    }
    schedulePush();
  } catch (e) {
    console.error('stats:', e.message);
  } finally {
    statsBusy = false;
  }
}

function readBatteryWmi() {
  return new Promise((resolve) => {
    const script = 'Get-CimInstance Win32_Battery | Select-Object EstimatedChargeRemaining,BatteryStatus | ConvertTo-Json -Compress';
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, timeout: 8000 }, (err, out) => {
      if (err) return resolve(null);
      try {
        const j = JSON.parse(String(out).trim() || 'null');
        const b = Array.isArray(j) ? j[0] : j;
        resolve(b && typeof b.EstimatedChargeRemaining === 'number' ? b : null);
      } catch { resolve(null); }
    });
  });
}

// powerMonitor says plugged or on battery; WMI (Windows only, once a minute) says the level and the charging state.
async function pollBattery(now) {
  if (batteryBusy) return;
  batteryBusy = true;
  try {
    if (WIN && now - batteryWmi.at >= 60000) {
      const b = await readBatteryWmi();
      batteryWmi = { at: now, pct: b ? b.EstimatedChargeRemaining : null, status: b ? b.BatteryStatus : 0 };
    }
    if (batteryWmi.pct == null) { M.battery = null; return; }
    const plugged = !powerMonitor.isOnBatteryPower();
    const b = batteryFrom({ level: batteryWmi.pct / 100, charging: [6, 7, 8, 9].includes(batteryWmi.status), plugged });
    const old = M.battery;
    if (old && old.plugged !== b.plugged) {
      announce(toast({ symbol: b.plugged ? 'bolt.fill' : 'powerplug.portrait', tint: b.plugged ? TINT.green : TINT.gray,
        title: b.plugged ? L('Đang sạc') : L('Đã rút sạc'), subtitle: `${b.percent}%`, compact: true,
        event: b.plugged ? 'charging' : 'unplugged', art: { kind: 'charge', percent: b.percent, plugged: b.plugged } }), { notify: false });
    }
    if (b.plugged) lowBatteryWarned = false;
    if (!b.plugged && b.percent <= 20 && !lowBatteryWarned) {
      lowBatteryWarned = true;
      announce(toast({ symbol: 'battery.25percent', tint: TINT.red, title: L('Pin yếu'), subtitle: L('Còn {0}%', b.percent), event: 'lowBattery' }));
    }
    M.battery = b;
  } finally {
    batteryBusy = false;
  }
}

// Network: online changes count only when the next reading agrees (a 2 s blip is not news); VPN switches are quiet.
function watchNetwork(now) {
  const info = netInfo();
  if (netOnline === null) { netOnline = info.online; vpnOn = info.vpn; return; }
  if (!on('network')) { netOnline = info.online; vpnOn = info.vpn; return; }
  if (info.online !== netOnline) {
    if (netPending !== info.online) { netPending = info.online; netPendingAt = now; return; }
    if (now - netPendingAt >= 2000) {
      netOnline = info.online;
      netPending = null;
      if (info.online) {
        const wifi = info.kind === 'wifi';
        announce(toast({ symbol: wifi ? 'wifi' : 'network', tint: TINT.green, title: L('Đã có mạng lại'),
          subtitle: wifi ? L('Wi‑Fi đã kết nối') : L('Đã kết nối'), compact: true, event: 'netUp' }));
      } else {
        announce(toast({ symbol: 'wifi.slash', tint: TINT.red, title: L('Mất kết nối mạng'),
          subtitle: L('Kiểm tra Wi‑Fi hoặc dây mạng'), event: 'netDown' }));
      }
    }
    return;
  }
  netPending = null;
  if (info.vpn !== vpnOn) {
    vpnOn = info.vpn;
    announce(toast({ symbol: info.vpn ? 'lock.shield.fill' : 'lock.open.fill', tint: info.vpn ? TINT.blue : TINT.gray,
      title: info.vpn ? L('VPN đã bật') : L('VPN đã tắt'), subtitle: info.vpn ? L('VPN đã bật') : L('VPN đã tắt'),
      compact: true, event: 'netUp' }), { notify: false });
  }
}

// Free space under 10 GB warns once; back over 15 GB it can warn again.
function checkDisk() {
  const free = freeDisk();
  if (free == null) return;
  const gb = free / 1e9;
  if (gb < 10 && !diskWarned) {
    diskWarned = true;
    announce(toast({ symbol: 'internaldrive.fill', tint: TINT.orange, title: L('Ổ đĩa sắp đầy'),
      subtitle: L('Còn {0} GB trống', gb.toFixed(1)), event: 'warning', url: 'ms-settings:storagesense', action: L('Dọn ổ đĩa') }));
  } else if (gb > 15) {
    diskWarned = false;
  }
}

// ---- timer, shelf ----------------------------------------------------------------------------------------------------

function startTimerMinutes(minutes) {
  track.event('timer_start');
  M.timerTotal = minutes * 60;
  M.timerEnd = Date.now() + M.timerTotal * 1000;
  schedulePush(true);
}

// The pill sends seconds: a start, or (with a timer running) the seconds it should now end after: an extension.
function setTimerSeconds(sec) {
  const s = Math.max(1, Math.round(sec));
  if (M.timerEnd) {
    const extra = s - (M.timerEnd - Date.now()) / 1000;
    M.timerEnd += extra * 1000;
    M.timerTotal = Math.max(1, M.timerTotal + extra);
  } else {
    startTimerMinutes(s / 60);
    return;
  }
  schedulePush(true);
}

function stopTimer() {
  M.timerEnd = null;
  schedulePush(true);
}

function timerDone() {
  M.timerEnd = null;
  announce(toast({ symbol: 'timer', tint: TINT.orange, title: L('Hết giờ'), subtitle: L('{0} phút', Math.trunc(M.timerTotal / 60)), event: 'timerDone' }));
}

function addToShelf(paths) {
  track.event('shelf_add');
  const list = [...M.shelf];
  for (const p of Array.isArray(paths) ? paths : []) {
    if (typeof p === 'string' && path.isAbsolute(p) && fs.existsSync(p) && !list.includes(p)) list.push(p);
  }
  M.shelf = list;
  kvSet('shelf', list);
  schedulePush(true);
}

function removeFromShelf(paths) {
  M.shelf = M.shelf.filter((p) => !(Array.isArray(paths) ? paths : []).includes(p));
  kvSet('shelf', M.shelf);
  schedulePush(true);
}

async function pickShelf() {
  setExpanded(false);
  const r = await dialog.showOpenDialog({ title: L('Thêm vào khay'), properties: ['openFile', 'openDirectory', 'multiSelections'] });
  if (!r.canceled) addToShelf(r.filePaths);
}

// ---- items, pickers, agents, sounds, profile -------------------------------------------------------------------------

function setItems(list) {
  settings.set('items', list);
}

async function pickApp() {
  const r = await dialog.showOpenDialog({ filters: [{ name: 'App', extensions: ['exe'] }], properties: ['openFile'] });
  if (r.canceled || !r.filePaths[0]) return;
  const exe = r.filePaths[0];
  const name = path.basename(exe).replace(/\.exe$/i, '');
  setItems([...cfg.items, watch.item({ name, kind: 'app', exe, enabled: true })]);
}

// The matches of a process item, from the last process list: what the editor shows under "Đang khớp".
function checkItem(it) {
  if (!it || typeof it !== 'object') return;
  const rows = M.lastProcs.filter((p) => watch.matches(it, p)).slice(0, 20)
    .map((p) => ({ name: p.name, pid: p.pid, command: String(p.cmdline).slice(0, 200) }));
  M.matches = { id: it.id, rows };
}

function openItem(id) {
  const it = cfg.items.find((i) => i.id === id);
  if (it?.exe) shell.openPath(it.exe);
}

function refreshAgents() {
  M.agents = asks.AGENT_KINDS.map((kind) => ({
    kind, name: asks.AGENTS[kind].name, present: asks.present(kind), installed: asks.isInstalled(kind),
    tested: asks.AGENTS[kind].tested, setup: asks.setupStep(kind),
  }));
}

// The command the agents run: this app when packaged; in a dev run, electron plus the app folder. The splice is on
// purpose: asks.command() wraps the path in quotes, so `"electron" "app"` comes out as two arguments.
function hookExe() {
  return app.isPackaged ? process.execPath : `${process.execPath}" "${app.getAppPath()}`;
}

function installHook(kind, on_) {
  try {
    if (on_) asks.install(kind, hookExe());
    else asks.uninstall(kind);
    M.agentError = null;
  } catch (e) {
    M.agentError = e.message;
  }
  refreshAgents();
  schedulePush(true);
}

function openSetup(kind) {
  const step = asks.setupStep(kind);
  if (step) shell.openPath(asks.openInTerminal(step.command));
}

const SOUND_KEYS = ['size', 'expandMode', 'accent', 'customAccent', 'background', 'gradientA', 'gradientB', 'toastStyle', 'motion',
  'sounds', 'volume', 'systemNotifications', 'doneAfter', 'limitWarn', 'modules', 'disabled', 'placements'];

// The phone's edit of the notch: each value the settings take is set, the rest stays. Pictures stay this PC's own.
function applyNotch(n) {
  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  const values = { ...n, volume: clamp(n.volume, 0, 1), doneAfter: clamp(n.doneAfter, 5, 600), limitWarn: clamp(n.limitWarn, 50, 100),
    modules: (n.modules || []).filter((m) => settings.MODULE_IDS.includes(m)),
    disabled: (n.disabled || []).filter((m) => settings.MODULE_IDS.includes(m)) };
  for (const k of SOUND_KEYS) {
    if (values[k] === undefined) continue;
    try { settings.set(k, values[k]); } catch { /* a value this PC does not take stays as it is */ }
  }
  const keepPicture = (b, kept) => !(b.image || b.frames) || kept.some((x) => x.image === b.image);
  try { settings.set('badges', (n.badges || []).filter((b) => keepPicture(b, cfg.badges))); } catch { /* as before */ }
  const tools = {};
  for (const [t, b] of Object.entries(n.toolIcons || {})) {
    if (cfg.seenTools.includes(t) && keepPicture(b, Object.values(cfg.toolIcons))) tools[t] = b;
  }
  try { settings.set('toolIcons', tools); } catch { /* as before */ }
}

function applyPack(name) {
  const pack = settings.SOUND_PACKS.find((p) => p.name === name);
  if (pack) settings.set('soundMap', { ...pack.map });
}

async function exportProfile() {
  const r = await dialog.showSaveDialog({ defaultPath: 'NotchTasks.json', filters: [{ name: 'JSON', extensions: ['json'] }] });
  if (r.canceled || !r.filePath) return;
  fs.writeFileSync(r.filePath, JSON.stringify(settings.exportProfile(), null, 2));
}

async function importProfile() {
  const r = await dialog.showOpenDialog({ filters: [{ name: 'JSON', extensions: ['json'] }], properties: ['openFile'] });
  if (r.canceled || !r.filePaths[0]) return;
  try {
    settings.importProfile(JSON.parse(fs.readFileSync(r.filePaths[0], 'utf8')));
  } catch (e) {
    announce(toast({ symbol: 'speaker.slash.fill', tint: TINT.red, title: L('Không nhập được cấu hình'), subtitle: e.message }), { notify: false });
  }
}

// A sound pack: the map, and the picked files inside it (base64), so it works on another PC.
async function exportSounds() {
  const r = await dialog.showSaveDialog({ defaultPath: 'NotchTasks.notchsounds', filters: [{ name: 'NotchTasks', extensions: ['notchsounds'] }] });
  if (r.canceled || !r.filePath) return;
  const files = {}, soundMap = {};
  for (const [ev, v] of Object.entries(cfg.soundMap)) {
    if (v && path.isAbsolute(v) && fs.existsSync(v) && fs.statSync(v).size <= 8e6) {
      const key = `${ev}${path.extname(v)}`;
      files[key] = fs.readFileSync(v).toString('base64');
      soundMap[ev] = `file:${key}`;
    } else {
      soundMap[ev] = v;
    }
  }
  fs.writeFileSync(r.filePath, JSON.stringify({ format: 'notchsounds', soundMap, files }));
}

async function importSounds() {
  const r = await dialog.showOpenDialog({ filters: [{ name: 'NotchTasks', extensions: ['notchsounds', 'json'] }], properties: ['openFile'] });
  if (r.canceled || !r.filePaths[0]) return;
  let pack;
  try { pack = JSON.parse(fs.readFileSync(r.filePaths[0], 'utf8')); } catch { pack = null; }
  if (!pack || pack.format !== 'notchsounds' || !pack.soundMap || typeof pack.soundMap !== 'object') {
    announce(toast({ symbol: 'speaker.slash.fill', tint: TINT.red, title: L('Không nhập được gói âm thanh'), subtitle: '' }), { notify: false });
    return;
  }
  const dir = path.join(userDir, 'Sounds');
  fs.mkdirSync(dir, { recursive: true });
  const map = {};
  for (const [ev, v] of Object.entries(pack.soundMap)) {
    if (typeof v === 'string' && v.startsWith('file:')) {
      const key = path.basename(v.slice(5));
      const data = pack.files && pack.files[key];
      if (typeof data !== 'string' || data.length > 12e6) continue;
      const file = path.join(dir, `${crypto.randomUUID()}${path.extname(key)}`);
      fs.writeFileSync(file, Buffer.from(data, 'base64'));
      map[ev] = file;
    } else {
      map[ev] = v;
    }
  }
  try {
    settings.set('soundMap', map);
    announce(toast({ symbol: 'speaker.wave.2.fill', tint: TINT.green, title: L('Đã dùng gói âm thanh'), event: 'taskDone' }), { notify: false });
  } catch (e) {
    announce(toast({ symbol: 'speaker.slash.fill', tint: TINT.red, title: L('Không nhập được gói âm thanh'), subtitle: e.message }), { notify: false });
  }
}

// ---- links, sync, updates ---------------------------------------------------------------------------------------------

// http and https only, plus the Windows storage page and this PC's own files (a download's folder).
function openURL(u) {
  if (typeof u !== 'string') return;
  if (/^https?:\/\//i.test(u) || u === 'ms-settings:storagesense') return shell.openExternal(u);
  if (u.startsWith('file:')) {
    const p = fileURLToPath(u);
    if (fs.existsSync(p)) shell.showItemInFolder(p);
  }
}

function dismissAlert(id) {
  M.alerts = M.alerts.filter((a) => a.id !== id);
  kvSet('dismissedJobs', [...(kvGet('dismissedJobs') || []), id].slice(-1000));
  schedulePush(true);
}

async function pair() {
  if (M.pairBusy) return;
  M.pairBusy = true;
  schedulePush(true);
  try {
    const r = await sync.pair();
    M.pairUrl = r.url;
  } catch {
    // the sync problem text says why
  } finally {
    M.pairBusy = false;
    schedulePush(true);
  }
}

async function sendReport(r) {
  M.report = { busy: true, code: null, problem: null };
  schedulePush(true);
  try {
    const code = await sync.sendReport({
      topic: String(r.topic || ''), text: String(r.text || ''), contact: String(r.contact || ''),
      info: { version: app.getVersion(), build: APP_BUILD, os: `${os.type()} ${os.release()}`, lang: l10n.code() },
    });
    M.report = { busy: false, code, problem: null };
  } catch {
    M.report = { busy: false, code: null, problem: L('Không gửi được báo lỗi. Thử lại khi có mạng.') };
  }
  schedulePush(true);
}

// The phone's answers (Sync.swift apply): a refresh is handled in sync.js; here a timer, the notch, or an ask's reply.
function onAnswer(a) {
  if (a.timer !== undefined) {
    if (a.timer > 0) startTimerMinutes(Math.min(a.timer, 180));
    else stopTimer();
    return;
  }
  if (a.notch) return applyNotch(a.notch);
  if (a.ask) answer(a.ask, a.reply);
}

// The pairing group: encrypted with the OS keychain (safeStorage). Without it, the group lives in memory only.
function groupFile() { return path.join(userDir, 'group.bin'); }

function loadGroup() {
  if (memoryGroup !== undefined) return memoryGroup;
  try {
    memoryGroup = JSON.parse(safeStorage.decryptString(fs.readFileSync(groupFile())));
  } catch {
    memoryGroup = null;
  }
  return memoryGroup;
}

function saveGroup(g) {
  memoryGroup = g || null;
  try {
    if (!g) {
      fs.rmSync(groupFile(), { force: true });
    } else if (!safeStorage.isEncryptionAvailable()) {
      syncNotice = L('Không lưu được mã ghép trên máy này: sẽ mất khi thoát');
    } else {
      P.writeAtomic(groupFile(), safeStorage.encryptString(JSON.stringify(g)));
      syncNotice = null;
    }
  } catch (e) {
    syncNotice = L('Không lưu được mã ghép: {0}', e.message);
  }
}

// Updates: the manifest on the feed; the verified installer is started and the app closes.
async function checkUpdate(manual = false) {
  if (M.update.busy) return;
  M.update = { ...M.update, busy: true, error: null };
  schedulePush(true);
  try {
    const m = await upd.check();
    if (m) {
      const fresh = !M.update.available || M.update.version !== m.version;
      M.update = { available: true, busy: false, progress: null, error: null, version: m.version,
        notes: updater.noteFor(m, l10n.code()), manifest: m };
      if (fresh) announce(toast({ symbol: 'arrow.down.circle.fill', tint: TINT.blue, title: L('Có bản mới {0}', m.version),
        action: L('Bảng điều khiển'), pane: 'overview' }), { notify: false });
    } else {
      M.update = { ...M.update, available: false, busy: false, manifest: null };
      if (manual) M.update.error = null;
    }
  } catch (e) {
    M.update = { ...M.update, busy: false, error: e.message };
  }
  schedulePush(true);
}

async function installUpdate() {
  const m = M.update.manifest;
  if (!m || M.update.busy) return;
  M.update = { ...M.update, busy: true, progress: 0, error: null };
  schedulePush(true);
  try {
    const file = await upd.install(m, (p) => { M.update.progress = p; schedulePush(); });
    M.update = { ...M.update, busy: false, progress: null };
    const why = await shell.openPath(file);
    if (why) throw new Error(why);
    app.quit();
  } catch (e) {
    M.update = { ...M.update, busy: false, progress: null, error: e.message };
    schedulePush(true);
  }
}

// ---- windows, tray ---------------------------------------------------------------------------------------------------

function actFromPill(name, ...args) { act(name, args); }
function actFromPrefs(name, args) { act(name, args); }

function openPrefs(pane) {
  prefs.openPrefs({ pane: pane || 'overview', preload: PRELOAD_PREFS, onAction: actFromPrefs });
}

function openOnboarding() {
  prefs.openOnboarding({ preload: PRELOAD_PREFS, onAction: actFromPrefs });
}

function finishOnboarding(exit) {
  prefs.close('onboarding');
  if (exit === 'phone') openPrefs('phone');
  else if (exit === 'notch') setExpanded(true);
}

function buildTray() {
  if (!tray) {
    tray = new Tray(nativeImage.createFromPath(path.join(__dirname, '..', '..', 'assets', 'tray.png')));
    tray.on('click', () => openPrefs());
  }
  tray.setToolTip('NotchTasks');
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: L('Bảng điều khiển…'), click: () => openPrefs() },
    { label: L('Ghép thiết bị…'), click: () => openPrefs('phone') },
    { label: L('Kiểm tra bản cập nhật'), click: () => checkUpdate(true) },
    { type: 'separator' },
    { label: L('Thoát NotchTasks'), click: () => app.quit() },
  ]));
}

// ---- every action from the pill and the Control Panel ------------------------------------------------------------------

function act(name, args = []) {
  const [a0, a1] = args;
  try {
    switch (name) {
      case 'expand':
        if (SHOT && a0 === false) break;  // screenshots keep the notch open with no pointer over it
        setExpanded(!!a0);
        break;
      case 'answer': answer(a0, a1); break;
      case 'media':
        if (a0 === 'seek') media.command({ seek: Number(a1) });
        else media.command(a0);
        break;
      case 'timer':
        if (a0 == null) stopTimer();
        else setTimerSeconds(Number(a0));
        break;
      case 'shelfAdd': addToShelf(a0); break;
      case 'shelfRemove': removeFromShelf(a0); break;
      case 'shelfOpen': if (typeof a0 === 'string') shell.openPath(a0); break;
      case 'shelfReveal': if (Array.isArray(a0) && a0[0]) shell.showItemInFolder(a0[0]); break;
      case 'shelfPick': pickShelf(); break;
      case 'openItem': openItem(a0); break;
      case 'openURL': openURL(a0); break;
      case 'openPrefs': openPrefs(a0); break;
      case 'dismissAlert': dismissAlert(a0); break;
      case 'dismissToast': showNext(); break;
      case 'quit': app.quit(); break;
      case 'setSetting': settings.set(a0, a1); break;
      case 'setItems': setItems(a0); break;
      case 'applyTheme': settings.applyTheme(a0); break;
      case 'applyPack': applyPack(a0); break;
      case 'exportProfile': exportProfile(); break;
      case 'importProfile': importProfile(); break;
      case 'exportSounds': exportSounds(); break;
      case 'importSounds': importSounds(); break;
      case 'playSound': playSound(a0, a1); break;
      case 'testToast':
        announce(toast({ symbol: 'bell.badge.fill', tint: TINT.blue, title: L('Thử thông báo'),
          subtitle: L('Thông báo sẽ hiện như thế này'), event: 'taskDone' }));
        break;
      case 'pickApp': pickApp(); break;
      case 'checkItem': checkItem(a0); break;
      case 'openSetup': openSetup(a0); break;
      case 'installHook': installHook(a0, true); break;
      case 'uninstallHook': installHook(a0, false); break;
      case 'pair': pair(); break;
      case 'unpair': sync.unpair(a0).catch(() => {}); break;
      case 'unpairAll': sync.unpairAll().then(() => { M.pairUrl = null; schedulePush(true); }, () => {}); break;
      case 'setSyncOption':
        if (a0 === 'lan') sync.setLan(a1);
        else if (a0 === 'onlyAway') sync.setOnlyAway(a1);
        break;
      case 'setLanguage': settings.set('language', a0 || ''); break;
      case 'setAnalytics': track.enabled = !!a0; break;
      case 'checkUpdate': checkUpdate(true); break;
      case 'installUpdate': installUpdate(); break;
      case 'sendReport': sendReport(a0 || {}); break;
      case 'openOnboarding': openOnboarding(); break;
      case 'finishOnboarding': finishOnboarding(a0); break;
      default: console.error('unknown action:', name);
    }
  } catch (e) {
    console.error(`action ${name}:`, e.message);
  }
  schedulePush();
}

// ---- first run, the Asks folder, the sound and pill windows -------------------------------------------------------------

// The agents found on this PC join the watch list once, as the Mac adds them at first start.
function addFoundAgents() {
  const add = [];
  for (const p of watch.presetGroups[0].items) {
    if (!watch.isInstalled(p)) continue;
    const same = (i) => i.kind === p.kind && i.process === p.process && (i.args ?? null) === (p.args ?? null);
    if (![...cfg.items, ...add].some(same)) add.push({ ...p });
  }
  if (add.length) setItems([...cfg.items, ...add]);
}

function shoot(file, pad = 24) {
  return pill.win.webContents.executeJavaScript(
    '(() => { const rs = [...document.querySelectorAll("#pill, .cardtoast")].map((e) => e.getBoundingClientRect());' +
    ' const x = Math.min(...rs.map((r) => r.left)), y = Math.min(...rs.map((r) => r.top));' +
    ' const r2 = Math.max(...rs.map((r) => r.right)), b = Math.max(...rs.map((r) => r.bottom));' +
    ' return { x, y, width: r2 - x, height: b - y }; })()')
    .then(async (r) => {
      const img = await pill.win.webContents.capturePage({
        x: Math.max(0, Math.floor(r.x - pad)), y: Math.max(0, Math.floor(r.y - pad)),
        width: Math.ceil(r.width + pad * 2), height: Math.ceil(r.height + pad * 2),
      });
      fs.writeFileSync(file, img.toPNG());
      console.log('wrote', file);
    });
}

// NT_SHOT=<folder>: the real state of this PC, closed then open, as PNGs; then quit.
function runShots() {
  fs.mkdirSync(SHOT, { recursive: true });
  setTimeout(async () => {
    await shoot(path.join(SHOT, 'closed.png'));
    M.expanded = true;
    schedulePush(true);
    setTimeout(async () => {
      await shoot(path.join(SHOT, 'open.png'), 16);
      await shootPrefs();
      app.quit();
    }, 1500);
  }, 6000);
  setTimeout(() => app.exit(1), 60000);  // never hang the check
}

// The Control Panel, pane by pane, and the icon editor from the badges pane (NT_SHOT only).
async function shootPrefs() {
  const PANES = ['overview', 'modules', 'agents', 'phone', 'appearance', 'badges', 'store', 'sounds', 'behavior'];
  openPrefs('overview');
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  await wait(1500);
  const win = BrowserWindow.getAllWindows().find((w) => w !== pill.win && !w.isDestroyed());
  if (!win) return;
  win.webContents.on('console-message', (e, level, msg) => { if (level >= 2) console.error('[prefs]', msg); });
  for (const p of PANES) {
    win.webContents.send('prefs:pane', p);
    await wait(600);
    fs.writeFileSync(path.join(SHOT, 'pane-' + p + '.png'), (await win.webContents.capturePage()).toPNG());
  }
  console.log('prefs panes written');
}

function hookPoll() {
  hookWatch = asks.watchAsks(asks.askDir(), (list) => {
    hookAsks = list.map(normAsk);
    refreshAsks();
  });
}

function onTick() {
  const now = Date.now();
  const open = M.expanded || !!M.toast;
  if (!procBusy && due('procs', open ? 5000 : 15000, now)) pollProcs(now);
  if (on('ci') && due('runners', open ? 5000 : 15000, now)) pollRunners(now);
  if (!statsBusy && due('stats', open ? 2000 : 10000, now)) pollStats();
  if (due('net', 2000, now)) watchNetwork(now);
  if (!batteryBusy && on('battery') && due('battery', 10000, now)) pollBattery(now);
  if (on('health') && due('disk', 600000, now)) checkDisk();
  if (due('agentsList', 10000, now)) refreshAgents();
  if (on('limits') && cfg.items.some((i) => i.enabled && i.kind === 'codex') && due('codexLimits', 30000, now)) {
    codexLimitsCache = codex.limits(new Date(now));
  }
  if (due('limits', 10000, now)) refreshLimits(now);
  if (on('limits') && cfg.items.some((i) => i.enabled && i.kind === 'claude') && due('claudeUsage', 180000, now)) {
    refreshClaudeUsage();
  }
  if (due('antigravity', 60000, now)) {
    if (on('limits') && cfg.items.some((i) => i.enabled && i.cli === 'agy')) {
      P.fetchAntigravityLimits({ now: new Date(now) }).then((l) => { antigravityLimits = l; });
    } else {
      antigravityLimits = null;
    }
  }
  if (due('local', 30000, now)) refreshLocal();
  if (on('ci') && due('github', 10000, now)) github.poll(undefined, githubFinished).catch(() => {});
  if (M.timerEnd && now >= M.timerEnd) timerDone();
  refreshAsks();
  schedulePush();
}

function startApp() {
  if (!app.requestSingleInstanceLock()) return app.quit();  // the first instance opens its Control Panel
  app.on('second-instance', () => openPrefs());
  app.on('window-all-closed', () => {});  // the pill stays up; the Control Panel may close
  app.on('before-quit', () => {
    media?.stop();
    downloads?.stop();
    flushKvSafe();
    sync?.stop();
  });
  app.whenReady().then(boot, (e) => { console.error(e); app.exit(1); });
}

function flushKvSafe() {
  try { P.writeAtomic(kvFile, JSON.stringify(kv)); } catch { /* best effort on quit */ }
}

function boot() {
  app.setAppUserModelId('com.notchtasks.windows');
  userDir = app.getPath('userData');
  kvFile = path.join(userDir, 'state.json');
  loadKv();
  settings.load(userDir);
  cfg = settings.get();
  settings.on(onSettings);
  l10n.setChoice(cfg.language);
  if (!kvGet('addedAgents')) {
    addFoundAgents();
    kvSet('addedAgents', true);
  }
  cfg = settings.get();
  M.shelf = (kvGet('shelf') || []).filter((p) => typeof p === 'string' && fs.existsSync(p));
  itemMeter = new proc.CpuMeter();
  codex = new P.CodexScanner();
  antigravity = new P.AntigravityScanner();
  devScanner = new DevScanner({
    history: { load: () => kvGet('taskHistory'), save: (runs) => kvSet('taskHistory', runs) },
  });
  files = new FileProgressWatcher();
  sampler = createStats({ readThermals: readThermalsCached });
  github = new GitHubWatcher();
  media = createMedia({
    log: (m) => console.error(m),
    onChange: (np) => {
      M.nowPlaying = np ? { ...np, appName: appLabel(np.source), item: { name: appLabel(np.source), kind: 'app' } } : null;
      schedulePush();
    },
  });
  downloads = createDownloads({
    onChange: ({ added }) => {
      for (const name of added) {
        const full = path.join(os.homedir(), 'Downloads', name);
        announce(toast({ symbol: 'arrow.down.circle.fill', tint: TINT.blue, title: L('Đã tải xong'), subtitle: name,
          event: 'downloadDone', url: pathToFileURL(full).href, action: L('Mở nơi lưu') }));
      }
    },
  });
  track = createTrack({ load: (k) => kvGet(k), save: (k, v) => kvSet(k, v) });
  upd = updater.createUpdater({ appBuild: APP_BUILD, tmpDir: app.getPath('temp') });
  sync = createSync({
    loadGroup, saveGroup, macName: os.hostname(), lan: true, onlyAway: true,
    isAway: () => lockedNow || powerMonitor.getSystemIdleTime() >= 120,
    onAnswer,
    onState: (s) => { syncState = s; schedulePush(); },
    onProNote: (text) => announce(toast({ symbol: 'sparkles', tint: TINT.orange, title: text }), { notify: false }),
    getNoteAt: (g) => kvGet(`lastNotchProNoteAt.${g}`) || 0,
    setNoteAt: (g, at) => kvSet(`lastNotchProNoteAt.${g}`, at),
    track: (name) => track.event(name),
    log: (m) => console.error('sync:', m),
  });

  pill = createPill({ preload: PRELOAD_PILL, onAction: actFromPill });
  pill.win.webContents.on('console-message', (e, level, msg, line, src) => {
    if (level >= 2) console.error('[pill]', msg, src ? `${path.basename(src)}:${line}` : '');
  });
  buildTray();
  asks.markAppRunning(asks.askDir());
  hookPoll();
  syncModules();
  refreshAgents();
  pushNow();
  setInterval(onTick, 1000);
  onTick();

  if (SHOT) return runShots();  // the screenshot run: no phone, no updates, no guide
  if (WIN && !kvGet('loginItemSet') && app.isPackaged) {
    app.setLoginItemSettings({ openAtLogin: true });
    kvSet('loginItemSet', true);
  }
  powerMonitor.on('lock-screen', () => { lockedNow = true; });
  powerMonitor.on('unlock-screen', () => { lockedNow = false; });
  sync.start().catch((e) => console.error('sync:', e.message));
  if (!cfg.onboardingDone) openOnboarding();
  setTimeout(() => checkUpdate(false), 10000);
  setInterval(() => checkUpdate(false), 24 * 3600 * 1000);
}

// The name of a running app's process, for the now-playing card: "Spotify.exe" -> "Spotify".
function appLabel(source) {
  return String(source || '').split('!').pop().replace(/\.exe$/i, '');
}

if (HOOK_AT >= 0 || STATUSLINE) {
  app.disableHardwareAcceleration();
  app.dock?.hide();
  readStdin((input) => {
    if (STATUSLINE) return runStatusline(input);
    const agent = process.argv[HOOK_AT + 1] || 'claude';
    asks.runHook(agent, input, {}).then((r) => finish(r.stdout, r.exitCode), () => finish('', 0));
  });
} else {
  startApp();
}
