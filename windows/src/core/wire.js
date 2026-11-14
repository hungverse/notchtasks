'use strict';
// What the Mac sends and receives, field for field Sources/Sync.swift (SyncSnapshot, SyncAnswer, AgentAsk.forPush) and the
// phone's iOS/Shared/Wire.swift and Notch.swift. JS models use Date for every date; these functions convert to and from
// Swift's JSON. Anything the phone could not decode is refused here, before it is sealed.

const crypto = require('node:crypto');
const { toSwiftDate, fromSwiftDate, dropNil, enumNoValue, enumWithValues } = require('./swiftjson');

const AGENT_KINDS = ['claude', 'codex', 'gemini', 'antigravity', 'cursor', 'opencode'];
const NOTCH_KEYS = ['size', 'expandMode', 'accent', 'customAccent', 'background', 'gradientA', 'gradientB', 'toastStyle', 'motion',
  'modules', 'disabled', 'placements', 'haptics', 'sounds', 'volume', 'systemNotifications', 'doneAfter', 'limitWarn', 'lockScreen',
  'lockShows', 'badges', 'lockOwnBadges', 'lockBadges', 'toolIcons', 'seenTools'];

const graphemes = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
/** Swift's String.prefix(n): n characters (graphemes), not bytes. */
function prefixChars(s, n) {
  let out = '';
  let i = 0;
  for (const { segment } of graphemes.segment(String(s))) {
    if (i++ >= n) break;
    out += segment;
  }
  return out;
}

function agentKind(a) {
  if (a === undefined || a === null) return undefined;
  if (!AGENT_KINDS.includes(a)) throw new TypeError(`unknown agent ${a}`);
  return a;
}

function int(v, what) {
  if (!Number.isInteger(v)) throw new TypeError(`${what}: not an integer`);
  return v;
}

function str(v, what) {
  if (typeof v !== 'string') throw new TypeError(`${what}: not a string`);
  return v;
}

/** A MacBadge / Badge as the phone decodes it. Pictures (`image`, `frames`, `fps`) stay the Mac's own. */
function badge(b) {
  return dropNil({
    id: String(b.id || crypto.randomUUID()).toUpperCase(),
    symbol: b.symbol ?? 'star.fill',
    image: b.image,
    text: b.text,
    color: b.color,
    fg: b.fg,
    plain: b.plain,
    shape: b.shape ?? 'circle',
    border: b.border ?? false,
    right: b.right ?? false,
    sprite: b.sprite,
    frames: b.frames,
    fps: b.fps,
  });
}

/** The notch's look (SyncNotch). Every field is required by the phone, so a missing one is an error here. */
function notch(n) {
  const out = {
    size: n.size,
    expandMode: n.expandMode,
    accent: n.accent,
    customAccent: n.customAccent,
    background: n.background,
    gradientA: n.gradientA,
    gradientB: n.gradientB,
    toastStyle: n.toastStyle,
    motion: n.motion,
    modules: [...n.modules],
    disabled: [...n.disabled],
    placements: { ...n.placements },
    haptics: n.haptics,
    sounds: n.sounds,
    volume: n.volume,
    systemNotifications: n.systemNotifications,
    doneAfter: n.doneAfter,
    limitWarn: n.limitWarn,
    lockScreen: n.lockScreen,
    lockShows: [...n.lockShows],
    badges: n.badges.map(badge),
    lockOwnBadges: n.lockOwnBadges,
    lockBadges: n.lockBadges.map(badge),
    toolIcons: Object.fromEntries(Object.entries(n.toolIcons).map(([k, v]) => [k, badge(v)])),
    seenTools: [...n.seenTools],
  };
  for (const k of NOTCH_KEYS) if (out[k] === undefined) throw new TypeError(`notch: missing ${k}`);
  return out;
}

/** SyncRun / AgentRun. */
function run(r) {
  return dropNil({
    id: str(r.id, 'run id'),
    agent: agentKind(r.agent),
    title: str(r.title, 'run title'),
    detail: str(r.detail, 'run detail'),
    since: toSwiftDate(r.since),
    icon: r.icon ? badge(r.icon) : undefined,
  });
}

/** AgentAsk (the phone's and the Mac's). questions[].header is optional. */
function ask(a) {
  return dropNil({
    id: str(a.id, 'ask id'),
    pid: int(a.pid, 'ask pid'),
    session: str(a.session, 'ask session'),
    tool: str(a.tool, 'ask tool'),
    summary: str(a.summary, 'ask summary'),
    questions: (a.questions || []).map((q) => dropNil({
      question: str(q.question, 'question'),
      header: q.header,
      options: q.options.map((o) => str(o, 'option')),
      multiSelect: q.multiSelect === true,
    })),
    at: toSwiftDate(a.at),
    agent: agentKind(a.agent),
    icon: a.icon ? badge(a.icon) : undefined,
  });
}

/** AgentAsk.forPush (Sources/Sync.swift): summary 300 characters, 2 questions of 200, 4 options of 60. */
function askForPush(a) {
  return {
    ...a,
    summary: prefixChars(a.summary ?? '', 300),
    questions: (a.questions || []).slice(0, 2).map((q) => ({
      question: prefixChars(q.question, 200),
      header: q.header,
      options: q.options.slice(0, 4).map((o) => prefixChars(o, 60)),
      multiSelect: q.multiSelect,
    })),
  };
}

/** SyncSnapshot: what the phone decodes as Snapshot. Optional fields are left out when absent, as Swift does. */
function snapshot(s) {
  return dropNil({
    mac: str(s.mac, 'mac'),
    runs: (s.runs || []).map(run),
    asks: (s.asks || []).map(ask),
    limits: (s.limits || []).map((l) => ({
      provider: str(l.provider, 'provider'),
      label: str(l.label, 'label'),
      used: l.used,
      resets: toSwiftDate(l.resets),
    })),
    at: toSwiftDate(s.at),
    builds: (s.builds || []).map(run),
    health: s.health ? dropNil({
      battery: s.health.battery,
      charging: s.health.charging === true,
      thermal: s.health.thermal ?? 0,
      temp: s.health.temp,
      fan: s.health.fan,
      cpu: s.health.cpu,
      memory: s.health.memory,
    }) : undefined,
    events: (s.events || []).map((e) => ({
      id: str(e.id, 'event id'),
      kind: str(e.kind, 'event kind'),
      title: str(e.title, 'event title'),
      detail: str(e.detail, 'event detail'),
      at: toSwiftDate(e.at),
    })),
    job: s.job ? dropNil({
      title: str(s.job.title, 'job title'),
      detail: str(s.job.detail, 'job detail'),
      since: toSwiftDate(s.job.since),
      state: str(s.job.state, 'job state'),
      ended: s.job.ended ? toSwiftDate(s.job.ended) : undefined,
      icon: s.job.icon ? badge(s.job.icon) : undefined,
    }) : undefined,
    notch: s.notch ? notch(s.notch) : undefined,
    crew: s.crew ? dropNil({
      count: int(s.crew.count, 'crew count'),
      title: str(s.crew.title, 'crew title'),
      detail: str(s.crew.detail, 'crew detail'),
      since: toSwiftDate(s.crew.since),
      agent: agentKind(s.crew.agent),
      icon: s.crew.icon ? badge(s.crew.icon) : undefined,
      members: (s.crew.members || []).map((m) => dropNil({ agent: agentKind(m.agent), icon: m.icon ? badge(m.icon) : undefined })),
    }) : undefined,
  });
}

/** AskReply as Swift encodes it: {"allow":{}}, {"deny":{}}, {"pass":{}}, {"answers":{"_0":{question: label}}}. */
function encodeReply(r) {
  switch (r && r.kind) {
    case 'allow':
    case 'deny':
    case 'pass':
      return enumNoValue(r.kind);
    case 'answers':
      return enumWithValues('answers', [answersObject(r.answers)]);
    default:
      throw new TypeError('reply: unknown kind');
  }
}

function answersObject(a) {
  if (!a || typeof a !== 'object' || Array.isArray(a)) throw new TypeError('answers: not an object');
  const out = {};
  for (const [q, v] of Object.entries(a)) out[str(q, 'question')] = str(v, 'answer');
  return out;
}

/** The inverse of encodeReply: {kind, answers?}. Throws on any other shape. */
function decodeReply(j) {
  if (j && typeof j === 'object' && !Array.isArray(j)) {
    const keys = Object.keys(j);
    const v = keys.length === 1 ? j[keys[0]] : undefined;
    if (keys.length === 1 && v && typeof v === 'object') {
      if (keys[0] === 'allow' || keys[0] === 'deny' || keys[0] === 'pass') return { kind: keys[0] };
      if (keys[0] === 'answers' && v._0 !== undefined) return { kind: 'answers', answers: answersObject(v._0) };
    }
  }
  throw new TypeError('reply: unknown shape');
}

/** A phone's sealed Answer, as the Mac reads it. at is a Date; the optional keys are set only when present. */
function readAnswer(o) {
  if (!o || typeof o !== 'object') throw new TypeError('answer: not an object');
  const out = {
    ask: str(o.ask, 'answer ask'),
    reply: decodeReply(o.reply),
    at: fromSwiftDate(o.at),
  };
  if (o.phone !== undefined && o.phone !== null) out.phone = str(o.phone, 'phone');
  if (o.notch !== undefined && o.notch !== null) out.notch = notch(o.notch);
  if (o.timer !== undefined && o.timer !== null) out.timer = int(o.timer, 'timer');
  if (o.refresh !== undefined && o.refresh !== null) {
    if (typeof o.refresh !== 'boolean') throw new TypeError('refresh: not a boolean');
    out.refresh = o.refresh;
  }
  return out;
}

module.exports = { snapshot, askForPush, readAnswer, encodeReply, decodeReply, badge, notch, ask, prefixChars, AGENT_KINDS };
