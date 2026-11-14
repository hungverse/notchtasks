'use strict';
// Watch list model and presets (Sources/WatchItems.swift). Plain objects, no storage here.
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { L } = require('./l10n');
const { normName, splitArgs } = require('./proc');

/** kind: process | app | codex | claude | dev (dev = the built-in Build & CI row) */
function item(o) {
  return {
    id: crypto.randomUUID(),
    process: '',          // .process: executable name; "" = any name, matched by args
    childTasks: false,    // each command it starts is its own row
    enabled: true,
    ...o,
  };
}

const inBuild = (it) => it.kind === 'dev' || it.build === true;

function defaultThreshold(kind, childTasks) { return kind === 'app' ? 5 : childTasks ? 0.5 : 1.5; }
function cpuThreshold(it) { return it.threshold ?? defaultThreshold(it.kind, it.childTasks); }

function summary(it) {
  const cpu = `CPU > ${Number(cpuThreshold(it).toFixed(1))}%`;
  switch (it.kind) {
    case 'process': {
      const what = !it.process
        ? L('Lệnh chứa “{0}”', it.args || '')
        : L('Tiến trình “{0}”', it.process) + (it.args ? L(' · lệnh chứa “{0}”', it.args) : '');
      return [what, it.childTasks ? L('mỗi lệnh con một dòng') : null, cpu].filter((x) => x != null).join(' · ');
    }
    case 'app': return L('Ứng dụng · {0}', cpu);
    case 'codex': case 'claude': return L('Theo dõi từng phiên');
    case 'dev': return L('Hiện khi đang chạy');
    default: return '';
  }
}

/** Icon name when there is no app to take it from. */
function symbol(it) {
  if (it.symbolName) return it.symbolName;
  return it.kind === 'dev' ? 'hammer.fill' : 'terminal.fill';
}

/** Process names as Windows reports them, for app rows. Unverified names: check on a real PC. */
const WIN_APP_EXE = {
  'com.todesktop.230313mzl4w4u92': 'Cursor',
  'com.exafunction.windsurf': 'Windsurf',
  'com.google.antigravity': 'Antigravity',
  'dev.zed.Zed': 'zed',
  'dev.kiro.desktop': 'Kiro',
  'com.openai.chat': 'ChatGPT',
  'ai.perplexity.mac': 'Perplexity',
  'ai.elementlabs.lmstudio': 'LM Studio',
  'org.blenderfoundation.blender': 'blender',
  'com.docker.docker': 'Docker Desktop',
  'com.unity3d.unityhub': 'Unity Hub',
  'com.google.android.studio': 'studio64',
  'com.blackmagic-design.DaVinciResolveLite': 'Resolve',
  'com.microsoft.VisualStudio': 'devenv',
};

const PATH_DIRS_HOME = ['.local/bin', '.bun/bin', '.cargo/bin', '.npm-global/bin'];

/** Command found on PATH or the usual per-user tool folders (PATHEXT on Windows). */
function findCli(cli, env = process.env) {
  const exts = process.platform === 'win32' ? ['', ...(env.PATHEXT || '.EXE;.CMD;.BAT').split(';')] : [''];
  const home = os.homedir();
  const dirs = [
    ...(env.PATH || '').split(path.delimiter),
    ...PATH_DIRS_HOME.map((d) => path.join(home, d)),
    env.NVM_SYMLINK, path.join(env.APPDATA || '', 'npm'),
  ].filter(Boolean);
  return dirs.some((d) => exts.some((e) => {
    try { return fs.statSync(path.join(d, cli + e)).isFile(); } catch { return false; }
  }));
}

/** Installed: the cli is on PATH, or for apps the item is always shown (no bundle lookup on Windows). */
function isInstalled(it, env) {
  if (it.cli) return findCli(it.cli, env);
  return true;
}

const SHELLS = ['zsh', 'bash', 'sh', 'fish', 'dash', 'grep', 'rg', 'login', 'sudo', 'tail', 'less', 'cmd', 'powershell', 'pwsh'];

/** Process matches the item. p = {pid, name, cmdline}; argvOf(p) -> argv. */
function matches(it, p, argvOf = (q) => splitArgs(q.cmdline), selfPid = process.pid) {
  const needle = (it.args || '').replace(/\\/g, '/');
  const want = normName(it.process);
  if (it.kind !== 'process' || (!want && !needle) || p.pid === selfPid) return false;
  const name = normName(p.name);
  if (want && name !== want) return false;
  // a shell or grep whose command text merely mentions the tool isn't the tool
  if (!want && SHELLS.includes(name)) return false;
  return !needle || argvOf(p).join(' ').replace(/\\/g, '/').includes(needle);
}

/** Outermost matching processes: a matching child belongs to its parent's session. */
function roots(it, procs, argvOf) {
  const found = procs.filter((p) => matches(it, p, argvOf));
  const pids = new Set(found.map((p) => p.pid));
  return found.filter((p) => !pids.has(p.ppid));
}

/** CLI agent: often a node/python script, so matched by its command line; working = its process tree uses CPU. */
function agent(name, cli, { process: proc = '', args, icon, threshold = 3 } = {}) {
  return item({ name, kind: 'process', process: proc, bundleID: icon, args: args ?? (proc ? undefined : cli),
    threshold, symbolName: 'sparkles', cli });
}

/** GUI apps also use CPU for plain editing, so their bar is higher. */
function aiApp(name, bundleID, threshold) { return item({ name, kind: 'app', bundleID, threshold }); }

const presetGroups = [
  ['Agent trong terminal', [
    item({ name: 'Claude', kind: 'claude', process: 'claude', bundleID: 'com.anthropic.claudefordesktop' }),
    item({ name: 'Codex', kind: 'codex', bundleID: 'com.openai.codex' }),
    agent('Gemini CLI', 'gemini', { args: 'gemini-cli' }),
    agent('Antigravity agent', 'agy', { process: 'agy', icon: 'com.google.antigravity' }),
    agent('Cursor CLI', 'cursor-agent', { args: 'cursor-agent', icon: 'com.todesktop.230313mzl4w4u92' }),
    agent('GitHub Copilot CLI', 'copilot', { args: '@github/copilot' }),
    agent('Qwen Code', 'qwen', { args: 'qwen-code' }),
    agent('OpenCode', 'opencode', { process: 'opencode' }),
    agent('Amp', 'amp', { args: '@sourcegraph/amp' }),
    agent('Aider', 'aider', { args: 'bin/aider' }),
    agent('Goose', 'goose', { process: 'goose' }),
    agent('Crush', 'crush', { process: 'crush' }),
    agent('Muse Code', 'muse', { process: 'muse' }),
    agent('Hermes Agent', 'hermes', { args: 'bin/hermes' }),
    agent('Augment (auggie)', 'auggie', { args: '@augmentcode/auggie' }),
    agent('Kimi CLI', 'kimi', { args: 'bin/kimi' }),
    agent('Kiro CLI', 'kiro-cli', { process: 'kiro-cli' }),
    agent('OpenClaw', 'openclaw', { args: 'openclaw/dist', threshold: 15 }), // its gateway idles at 1–4% CPU
  ]],
  [L('Ứng dụng AI'), [
    aiApp('ChatGPT', 'com.openai.chat', 15),
    aiApp('Cursor', 'com.todesktop.230313mzl4w4u92', 30),
    aiApp('Windsurf', 'com.exafunction.windsurf', 30),
    aiApp('Antigravity', 'com.google.antigravity', 25),
    aiApp('Kiro', 'dev.kiro.desktop', 30),
    aiApp('Zed', 'dev.zed.Zed', 30),
    aiApp('Perplexity', 'ai.perplexity.mac', 15),
  ]],
  // local model runners: on the pill while they generate, with the model they have loaded
  [L('Model chạy trên máy'), [
    aiApp('LM Studio', 'ai.elementlabs.lmstudio', 8),
    item({ name: 'Ollama', kind: 'process', process: 'ollama', bundleID: 'com.electron.ollama', threshold: 8, cli: 'ollama' }),
  ]],
].map(([title, items]) => ({ title, items }));

const presets = presetGroups.flatMap((g) => g.items);

/** Build tools and apps to watch on top of what Build & CI detects by itself. */
function buildApp(name, bundleID, threshold, extra = {}) {
  return item({ name, kind: 'app', bundleID, threshold, build: true, ...extra });
}

const buildPresets = [
  buildApp('Visual Studio', 'com.microsoft.VisualStudio', 30),
  buildApp('Android Studio', 'com.google.android.studio', 40),
  buildApp('Docker', 'com.docker.docker', 20),
  buildApp('Unity Hub', 'com.unity3d.unityhub', 30),
  buildApp('Blender', 'org.blenderfoundation.blender', 60),
  buildApp('DaVinci Resolve', 'com.blackmagic-design.DaVinciResolveLite', 60),
  item({ name: 'MSBuild', kind: 'process', process: 'msbuild', threshold: 20, symbolName: 'hammer.fill', build: true }),
  item({ name: 'Gradle daemon', kind: 'process', process: 'java', args: 'GradleDaemon', threshold: 20, symbolName: 'hammer.fill', build: true }),
];

/** The Build & CI module's row: not in the watch list, everything in it is detected. */
const buildItem = item({ id: 'B0B0B0B0-0000-4000-8000-000000000001', name: 'Build & CI', kind: 'dev' });

/** A row in the pill: {id, title, detail, working, url?, progress?, estimated?, icon?, symbol?, tool?} */
function job(o) {
  return { working: true, estimated: false, ...o };
}

/** The app's executable name on Windows, for an app item. */
// An app picked from the running list carries its process name as bundleID ("Code.exe").
function appExe(it) { return it.exe || WIN_APP_EXE[it.bundleID] || (/\.exe$/i.test(it.bundleID || "") ? it.bundleID : null); }

module.exports = {
  item, inBuild, defaultThreshold, cpuThreshold, summary, symbol, WIN_APP_EXE, appExe, findCli, isInstalled,
  matches, roots, presetGroups, presets, buildPresets, buildItem, job,
};
