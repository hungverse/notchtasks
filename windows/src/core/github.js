'use strict';
// GitHub Actions: runs triggered by a git push from this PC (Sources/GitHub.swift).
// Token: the settings token, else `gh auth token` when gh is installed (as the Mac uses gh's login).
const fs = require('fs');
const https = require('https');
const path = require('path');
const { execFile } = require('child_process');
const { L } = require('./l10n');
const { mmss } = require('./devscan');
const { job } = require('./watchitems');

/** "git@github.com:o/r.git" / "https://github.com/o/r" -> "o/r" */
function repoFromRemote(url) {
  const m = /github\.com[:/]/.exec(String(url));
  if (!m) return null;
  let p = url.slice(m.index + m[0].length);
  if (p.endsWith('.git')) p = p.slice(0, -4);
  const parts = p.split('/').filter(Boolean);
  return parts.length >= 2 ? `${parts[0]}/${parts[1]}` : null;
}

/** .git/config + .git/HEAD text -> {repo, branch} for origin on a branch, else null. */
function parse(config, head) {
  let inOrigin = false, remote = null;
  for (const raw of String(config).split('\n')) {
    const line = raw.trim();
    if (line.startsWith('[')) inOrigin = line === '[remote "origin"]';
    else if (inOrigin && line.startsWith('url')) {
      const eq = line.indexOf('=');
      if (eq >= 0) remote = line.slice(eq + 1).trim();
    }
  }
  const repo = remote && repoFromRemote(remote);
  const h = String(head).trim();
  if (!repo || !h.startsWith('ref: refs/heads/')) return null;
  return { repo, branch: h.slice('ref: refs/heads/'.length) };
}

/** Repo (from origin) and current branch of the checkout containing dir. Worktrees (.git file) are skipped. */
function checkout(dir) {
  let cur = path.resolve(dir);
  for (;;) {
    const git = path.join(cur, '.git');
    let st = null;
    try { st = fs.statSync(git); } catch { /* not here, go up */ }
    if (st) {
      if (!st.isDirectory()) return null;
      try {
        return parse(fs.readFileSync(path.join(git, 'config'), 'utf8'), fs.readFileSync(path.join(git, 'HEAD'), 'utf8'));
      } catch {
        return null;
      }
    }
    const up = path.dirname(cur);
    if (up === cur) return null;
    cur = up;
  }
}

/** `gh auth token`, or null when gh is missing or not logged in. */
function ghToken() {
  return new Promise((resolve) => {
    execFile('gh', ['auth', 'token'], { windowsHide: true, timeout: 5000 }, (err, out) => {
      resolve(!err && String(out).trim() ? String(out).trim() : null);
    });
  });
}

/** GET api.github.com/<route>. Resolves parsed JSON, or null on any failure (no throw: polls must not crash). */
function api(route, token) {
  return new Promise((resolve) => {
    const headers = { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'NotchTasks' };
    if (token) headers.Authorization = `Bearer ${token}`;
    const req = https.get({ hostname: 'api.github.com', path: `/${route}`, headers, timeout: 10000 }, (res) => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (c) => { data += c; });
      res.on('end', () => {
        if (res.statusCode !== 200) return resolve(null);
        try { resolve(JSON.parse(data)); } catch { resolve(null); }
      });
    });
    req.on('timeout', () => req.destroy());
    req.on('error', () => resolve(null));
  });
}

/** workflow_runs JSON -> [GHRun]. started is epoch ms (0 when unknown). */
function runs(json) {
  const list = json && Array.isArray(json.workflow_runs) ? json.workflow_runs : null;
  if (!list) return [];
  return list.flatMap((r) => {
    const repo = r.repository && r.repository.full_name;
    if (typeof r.id !== 'number' || typeof repo !== 'string') return [];
    const started = Date.parse(r.run_started_at || r.created_at || '');
    return [{
      id: r.id,
      repo,
      workflow: r.name || 'workflow',
      title: r.display_title || '',
      status: r.status || '',
      conclusion: r.conclusion ?? null,
      url: r.html_url || null,
      started: isNaN(started) ? 0 : started,
      step: null,
      progress: null,
    }];
  });
}

/** jobs JSON -> {step, progress}: current step label and finished steps / all steps. */
function steps(json) {
  const jobs = json && Array.isArray(json.jobs) ? json.jobs : null;
  if (!jobs) return { step: null, progress: null };
  const all = jobs.flatMap((j) => j.steps || []);
  const current = jobs.find((j) => j.status === 'in_progress');
  const jobName = current ? current.name : null;
  if (!all.length) return { step: jobName, progress: null };
  const done = all.filter((s) => s.status === 'completed').length;
  const now = all.find((s) => s.status === 'in_progress');
  const label = [jobName, now && now.name].filter((x) => x != null).join(' › ');
  return { step: label || null, progress: done / all.length };
}

/** Watches each pushed branch for up to 1 h: runs, live step, pass/fail. */
class GitHubWatcher {
  constructor({ now = () => Date.now() } = {}) {
    this.now = now;
    this.watched = new Map();  // repo -> {branch, since}
    this.runs = new Map();     // run id -> GHRun
    this.busy = false;
    this.gh = { token: null, at: 0 };
  }

  pushed(dir, at = this.now()) {
    const c = checkout(dir);
    if (!c) return false;
    this.watched.set(c.repo, { branch: c.branch, since: at });
    return true;
  }

  async token(settingsToken) {
    if (settingsToken && String(settingsToken).trim()) return String(settingsToken).trim();
    if (this.now() - this.gh.at > 600000) {  // gh is a process: ask at most every 10 min
      this.gh = { token: await ghToken(), at: this.now() };
    }
    return this.gh.token;
  }

  /** Polls once (one poll at a time). done(finishedRuns) gets runs that just finished. */
  async poll(settingsToken, done = () => {}) {
    if (this.busy || this.watched.size === 0) return;
    this.busy = true;
    try {
      const targets = new Map(this.watched);
      const previous = new Map(this.runs);
      const tok = await this.token(settingsToken);
      const next = new Map();
      for (const [repo, w] of targets) {
        const branch = encodeURIComponent(w.branch);
        for (const r of runs(await api(`repos/${repo}/actions/runs?branch=${branch}&per_page=10`, tok))) {
          if (r.started < w.since - 90000) continue;
          if (r.status !== 'completed') Object.assign(r, steps(await api(`repos/${repo}/actions/runs/${r.id}/jobs`, tok)));
          next.set(r.id, r);
        }
      }
      const finished = [...next.values()].filter((r) => r.status === 'completed' && previous.get(r.id) && previous.get(r.id).status !== 'completed');
      this.runs = next;
      const now = this.now();
      // stop watching a repo once its runs are all done (after 2 min), or after an hour
      for (const [repo, w] of targets) {
        const mine = [...next.values()].filter((r) => r.repo.toLowerCase() === repo.toLowerCase());
        const settled = mine.length > 0 && mine.every((r) => r.status === 'completed') && now - w.since > 120000;
        if (settled || now - w.since > 3600000) {
          if (this.watched.get(repo) === w) this.watched.delete(repo);
        }
      }
      if (finished.length) done(finished);
    } finally {
      this.busy = false;
    }
  }

  get jobs() {
    return [...this.runs.values()].filter((r) => r.status !== 'completed').sort((a, b) => a.id - b.id).map((r) => {
      const waiting = ['queued', 'waiting', 'pending', 'requested'].includes(r.status);
      return job({
        id: `gh:${r.id}`,
        title: `${r.repo} · ${r.workflow}`,
        detail: waiting ? L('Đang chờ runner') : `${r.step || L('đang chạy')} · ${mmss((this.now() - r.started) / 1000)}`,
        working: true, url: r.url, progress: r.progress, tool: 'GitHub Actions',
      });
    });
  }
}

module.exports = { repoFromRemote, parse, checkout, ghToken, api, runs, steps, GitHubWatcher };
