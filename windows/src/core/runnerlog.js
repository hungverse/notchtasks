'use strict';
// CI runner logs (Sources/Runner.swift for GitLab Runner, GHSelfHosted in Sources/GitHub.swift for the GitHub
// self-hosted runner). Pure parsing plus small fs helpers. Nothing here reads launchd or the Mac's process tree.
const fs = require('fs');
const path = require('path');
const { job } = require('./watchitems');

/** Default install folder of the GitHub Actions runner on Windows. */
const DEFAULT_RUNNER_DIR = 'C:\\actions-runner';

const FIELD = /([\w-]+)=("([^"]*)"|\S+)/g;
const ANSI = /\x1b\[[0-9;]*m/g;

function readTail(file, bytes = 2_000_000) {
  let fd;
  try {
    fd = fs.openSync(file, 'r');
    const size = fs.fstatSync(fd).size;
    const len = Math.min(size, bytes);
    const buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, size - len);
    return buf.toString('utf8');
  } catch {
    return '';
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
  }
}

/** GitLab: "repo_url" https://gitlab.com/group/project.git -> "group/project". */
function projectFromRepoUrl(u) {
  try {
    return new URL(u).pathname.slice(1).replace(/\.git/g, '');
  } catch {
    return null;
  }
}

/**
 * GitLab Runner log lines: "received" = job started, "Job succeeded" / "Job failed" = end.
 * server: the GitLab base URL (config.toml on the Mac; the caller passes it).
 */
function parseGitLab(text, { server = 'https://gitlab.com' } = {}) {
  const jobURL = (project, id) => (project ? `${server}/${project}/-/jobs/${id}` : null);
  let running = [];
  const failed = [];
  const succeeded = new Set();
  for (const raw of String(text).split('\n')) {
    const received = raw.includes('received');
    const ok = raw.includes('Job succeeded');
    const fail = raw.includes('Job failed');
    if (!received && !ok && !fail) continue;
    const line = raw.replace(ANSI, '');
    const f = {};
    for (const m of line.matchAll(FIELD)) f[m[1]] = m[3] ?? m[2];
    const id = f.job;
    if (!id) continue;
    if (received) {
      const project = (f.repo_url && projectFromRepoUrl(f.repo_url)) || f.project_full_path || null;
      running.push({ id, name: f.job_name ?? `job ${id}`, project: project ?? '?', url: jobURL(project, id) });
      continue;
    }
    running = running.filter((r) => r.id !== id);
    if (ok) succeeded.add(id);
    if (fail) {
      const project = f.project_full_path || null;
      failed.push({
        id, name: f.job_name ?? `job ${id}`, project: project ?? '?',
        reason: [f.failure_reason, f.exit_code ? `exit ${f.exit_code}` : null].filter(Boolean).join(' · '),
        url: jobURL(project, id),
      });
    }
  }
  return { running, failed, succeeded };
}

/** Worker log: "Job result after all job steps finish: Succeeded" (last one). '' or null when not finished. */
function workerResult(log) {
  const marker = 'Job result after all job steps finish: ';
  const i = String(log || '').lastIndexOf(marker);
  if (i < 0) return null;
  return /^[A-Za-z]*/.exec(log.slice(i + marker.length))[0] || null;
}

/** Worker log: the last "jobDisplayName" (the running or finished job). */
function workerJobName(log) {
  const marker = '"jobDisplayName": "';
  const i = String(log || '').lastIndexOf(marker);
  if (i < 0) return null;
  return log.slice(i + marker.length).split('"')[0];
}

/**
 * Repo folder name from the job's workspace path (…/_work/<repo>/<repo>). Windows has no cwd of the worker
 * process, so this reads the log instead.
 * shortcut: the first _work path in the log is taken as the workspace; upgrade if a runner version writes it differently.
 */
function repoFromWorkerLog(log) {
  const m = /[\\/]_work[\\/]([^\\/\s"]+)/.exec(String(log || ''));
  return m ? m[1] : null;
}

/** …\bin\Runner.Worker.exe -> …\_diag (the runner's log folder). Keeps the separator the path uses. */
function diagFromWorkerExe(exe) {
  const sep = String(exe).includes('\\') ? '\\' : '/';
  const parts = String(exe).split(/[\\/]/);
  parts.pop();
  parts.pop();
  return [...parts, '_diag'].join(sep);
}

/** Newest Worker_* log in a _diag folder, or null. */
function newestWorkerLog(diag) {
  let names;
  try { names = fs.readdirSync(diag); } catch { return null; }
  let best = null, bestTime = -Infinity;
  for (const name of names) {
    if (!name.startsWith('Worker_')) continue;
    const full = path.join(diag, name);
    let t;
    try { t = fs.statSync(full).mtimeMs; } catch { continue; }
    if (t > bestTime) { best = full; bestTime = t; }
  }
  return best;
}

/**
 * Row for one running self-hosted job: pid of Runner.Worker, its exe path (from argv[0]) or the default runner folder.
 */
function workerJob({ pid, exe, runnerDir = DEFAULT_RUNNER_DIR }) {
  const diag = exe ? diagFromWorkerExe(exe) : path.join(runnerDir, '_diag');
  const file = newestWorkerLog(diag);
  const text = file ? readTail(file) : '';
  return job({
    id: `ghw:${pid}`,
    title: repoFromWorkerLog(text) || 'GitHub runner',
    detail: workerJobName(text) || 'job',
    working: true,
    symbol: 'server.rack',
    tool: 'GitHub Actions',
  });
}

module.exports = {
  DEFAULT_RUNNER_DIR, readTail, parseGitLab, workerResult, workerJobName, repoFromWorkerLog,
  diagFromWorkerExe, newestWorkerLog, workerJob,
};
