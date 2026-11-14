'use strict';
// Long dev tasks: builds, tests, installs, containers, renders, transfers (Sources/DevTasks.swift).
// Input is the process list from proc.js; nothing here touches the OS except FileProgressWatcher (fs).
const fs = require('fs');
const os = require('os');
const path = require('path');
const { L } = require('./l10n');
const P = require('./proc');

/** "m:ss" or "h:mm:ss", rounded up like the Mac. */
function mmss(sec) {
  const t = Math.max(0, Math.ceil(sec));
  const pad = (n) => String(n).padStart(2, '0');
  return t >= 3600 ? `${Math.floor(t / 3600)}:${pad(Math.floor(t / 60) % 60)}:${pad(t % 60)}` : `${Math.floor(t / 60)}:${pad(t % 60)}`;
}

/** Last path component, either separator, without a script extension. */
function base(s) {
  const b = String(s).replace(/[\\/]+$/, '').split(/[\\/]/).pop();
  for (const ext of ['.js', '.cjs', '.mjs', '.rb', '.py', '.blend']) if (b.endsWith(ext)) return b.slice(0, -ext.length);
  return b;
}

const PLATFORM_DIRS = ['ios', 'macos', 'android', 'app', 'web', 'linux', 'windows'];

const ENDLESS = new Set(['dev', 'start', 'serve', 'watch', 'preview', '--watch', '-w', 'storybook', 'daemon', 'attach', 'run-server']);

/** Process names (normalized: lower case, no .exe) worth reading argv for. */
const NAMES = new Set([
  'xcodebuild', 'swift', 'swift-build', 'swift-test', 'swift-package', 'java', 'dart', 'dartvm', 'node', 'bun', 'deno',
  'cargo', 'go', 'make', 'gmake', 'ninja', 'cmake', 'bazel', 'bazelisk', 'ruby', 'pod', 'fastlane', 'brew', 'carthage',
  'uv', 'docker', 'com.docker.cli', 'podman', 'git', 'rsync', 'scp', 'ditto', 'rclone', 'aria2c', 'yt-dlp', 'wget', 'curl',
  'zip', 'unzip', 'tar', 'bsdtar', '7z', '7zz', 'xz', 'zstd', 'hdiutil', 'ffmpeg', 'handbrakecli', 'blender',
  'magick', 'convert', 'mogrify', 'whisper', 'whisper-cli', 'ollama', 'terraform', 'tofu', 'pulumi', 'helm', 'notarytool',
  'altool', 'unity', 'dotnet', 'rustup', 'xcodes', 'flutter', 'gradle', 'mvn', 'eas', 'expo',
  // Windows
  'msbuild', 'robocopy', 'winget',
]);

const KNOWN_TOOLS = [
  '.NET', 'App Store', 'Bazel', 'Blender', 'Bundler', 'CMake', 'Cargo', 'Carthage', 'CocoaPods', 'Conda', 'Dart', 'Deno',
  'Django', 'Docker', 'EAS', 'Electron Forge', 'Flutter', 'Git', 'GitHub Actions', 'GitLab Runner', 'Go', 'Gradle',
  'HandBrake', 'Helm', 'Homebrew', 'Hugging Face', 'ImageMagick', 'Maven', 'MSBuild', 'Notarize', 'Ollama', 'Playwright', 'Podman',
  'Poetry', 'Prisma', 'Pulumi', 'RSpec', 'SwiftPM', 'Terraform', 'Tải file', 'Unity', 'Whisper', 'Xcode', 'build_runner',
  'bun', 'curl', 'esbuild', 'fastlane', 'ffmpeg', 'hdiutil', 'jest', 'make', 'next', 'ninja', 'npm', 'pip', 'pnpm',
  'pytest', 'python', 'robocopy', 'rsync', 'rustup', 'tsc', 'uv', 'vite', 'vitest', 'webpack', 'wget', 'winget', 'xcodes', 'yarn', 'yt-dlp', 'zip',
];

/** Icon: bundle IDs (the Mac's app icons). Windows has no bundle lookup yet; the UI maps tools to symbols. */
function appIcon(tool) {
  switch (tool) {
    case 'Xcode': case 'SwiftPM': case 'Notarize': case 'App Store': case 'xcodes': return 'com.apple.dt.Xcode';
    case 'Gradle': return 'com.google.android.studio';
    case 'Docker': case 'Podman': return 'com.docker.docker';
    case 'Unity': return 'com.unity3d.unityhub';
    case 'Blender': return 'org.blenderfoundation.blender';
    case 'HandBrake': return 'fr.handbrake.HandBrake';
    case 'Ollama': return 'com.electron.ollama';
    case 'MSBuild': return 'com.microsoft.VisualStudio';
    default: return null;
  }
}

function defaultSymbol(tool) {
  switch (tool) {
    case 'GitLab Runner': case 'GitHub Actions': return 'server.rack';
    case 'Tải file': case 'curl': case 'wget': case 'yt-dlp': return 'arrow.down.circle.fill';
    case 'ffmpeg': case 'HandBrake': return 'film.stack';
    case 'Git': return 'arrow.triangle.branch';
    default: return 'hammer.fill';
  }
}

function symbol(action) {
  const s = action.toLowerCase();
  const any = (words) => words.some((w) => s.includes(w));
  if (any(['test', 'e2e', 'check', 'lint', 'analyze', 'clippy', 'vet', 'typecheck'])) return 'checkmark.diamond.fill';
  if (any(['deploy', 'push', 'publish', 'upload', 'release', 'submit', 'apply', 'export'])) return 'icloud.and.arrow.up.fill';
  if (any(['install', 'ci', 'add', 'update', 'upgrade', 'sync', 'get', 'fetch', 'pull', 'resolve', 'bootstrap', 'download', 'restore', 'clone'])) {
    return 'shippingbox.fill';
  }
  return 'hammer.fill';
}

const isPython = (name) => name === 'python' || name.startsWith('python');

/**
 * Project folder from the command's own paths, for when the cwd is unknown (Windows): the folder that holds
 * node_modules for a package script, else the folder of the first path argument. null when there is none.
 */
function folderFromArgv(argv) {
  for (const x of argv.slice(1)) {
    if (x.startsWith('-') || /^[/-][^\\/]*:/.test(x) || !/[\\/]/.test(x)) continue;  // flags and /t:Target switches
    const n = x.replace(/\\/g, '/');
    const nm = n.lastIndexOf('/node_modules/');
    return project(nm >= 0 ? n.slice(0, nm) : n.replace(/\/[^/]*$/, '')) || null;
  }
  return null;
}

/** Project folder from a path, skipping platform subfolders: ".../mobile-app/ios" -> "mobile-app". */
function project(p) {
  const parts = String(p).split(/[\\/]/).filter(Boolean);
  while (parts.length > 1 && PLATFORM_DIRS.includes(parts[parts.length - 1].toLowerCase())) parts.pop();
  return parts[parts.length - 1] || '';
}

/** What a process is doing, read from its name + argv. name is normalized (lower case, no .exe). */
function classify(name, argv) {
  const a = argv.slice(1);
  const words = a.filter((x) => !x.startsWith('-'));
  const opt = (...flags) => {
    for (const f of flags) { const i = a.indexOf(f); if (i >= 0 && i + 1 < a.length) return a[i + 1]; }
    return null;
  };
  const verb = (allowed, tool, { icon = null, w = words } = {}) => {
    const v = w[0];
    if (v === undefined || !allowed.includes(v)) return null;
    const action = w.slice(0, 2).join(' ');
    return { tool, action, symbol: symbol(action), icon };
  };
  if (isPython(name)) return python(a);
  switch (name) {
    case 'xcodebuild': {
      if (a.some((x) => ['-list', '-version', '-showsdks', '-showbuildsettings', '-showdestinations', '-help'].includes(x.toLowerCase()))) return null;
      const acts = ['archive', 'test', 'build', 'build-for-testing', 'test-without-building', 'analyze', 'docbuild', 'clean'];
      const act = a.find((x) => acts.includes(x)) || (a.includes('-exportArchive') ? 'export'
        : a.includes('-resolvePackageDependencies') ? 'resolve packages' : 'build');
      const ws = opt('-workspace', '-project');
      const scheme = opt('-scheme');
      return { tool: 'Xcode', action: [act, scheme].filter(Boolean).join(' · '), symbol: symbol(act), icon: 'com.apple.dt.Xcode',
        subject: ws ? project(ws.replace(/[\\/][^\\/]*$/, '')) : undefined };
    }
    case 'msbuild': {
      // MSBuild /t:Restore /p:Configuration=Release App.sln
      const targets = a.filter((x) => /^[/-]t:/i.test(x)).map((x) => x.slice(3));
      const act = targets.length ? targets.join(' ') : 'build';
      const proj = a.find((x) => /\.(sln|csproj|vcxproj)$/i.test(x));
      return { tool: 'MSBuild', action: act, symbol: symbol(act), icon: 'com.microsoft.VisualStudio',
        subject: proj ? base(proj).replace(/\.\w+$/, '') : undefined };
    }
    case 'swift-build': case 'swift-test': case 'swift-package': {
      const act = name === 'swift-test' ? 'test' : name === 'swift-package' ? (words[0] || 'package') : 'build';
      if (name === 'swift-package' && !['resolve', 'update', 'archive-source'].includes(act)) return null;
      return { tool: 'SwiftPM', action: act, symbol: symbol(act), icon: 'com.apple.dt.Xcode' };
    }
    case 'swift': return verb(['build', 'test'], 'SwiftPM', { icon: 'com.apple.dt.Xcode' });
    case 'java': {
      const i = a.findIndex((x) => x.endsWith('GradleWrapperMain') || x === 'org.gradle.launcher.GradleMain');
      if (i >= 0) {
        const tasks = a.slice(i + 1).filter((x) => !x.startsWith('-'));
        if (!tasks.every((t) => !ENDLESS.has(t) && t !== '--continuous')) return null;
        const act = tasks.slice(0, 2).join(' ');
        return { tool: 'Gradle', action: act || 'build', symbol: symbol(act), icon: 'com.google.android.studio' };
      }
      // Android Studio builds through the daemon (no client process): busy daemon = build
      if (a.includes('org.gradle.launcher.daemon.bootstrap.GradleDaemon')) {
        return { tool: 'Gradle', action: 'build (Android Studio)', symbol: 'hammer.fill', icon: 'com.google.android.studio', busyOnly: true };
      }
      const j = a.indexOf('org.codehaus.plexus.classworlds.launcher.Launcher');
      if (j >= 0) {
        const goals = a.slice(j + 1).filter((x) => !x.startsWith('-')).slice(0, 2).join(' ');
        return { tool: 'Maven', action: goals || 'build', symbol: symbol(goals) };
      }
      return null;
    }
    case 'dart': case 'dartvm': {
      const i = a.findIndex((x) => x.endsWith('flutter_tools.snapshot'));
      if (i >= 0) {
        const rest = a.slice(i + 1).filter((x) => !x.startsWith('-'));
        return verb(['build', 'test', 'pub', 'precache', 'analyze', 'drive', 'install', 'clean', 'gen-l10n', 'packages', 'create', 'upgrade'],
          'Flutter', { w: rest });
      }
      if (a.some((x) => x.includes('build_runner')) && a.includes('build')) return { tool: 'build_runner', action: 'build', symbol: 'hammer.fill' };
      return verb(['compile', 'test', 'pub', 'analyze'], 'Dart');
    }
    case 'node': return node(a);
    case 'bun': {
      const v = words[0];
      if (v === undefined) return null;
      if (['install', 'i', 'add', 'update', 'build', 'test'].includes(v)) return { tool: 'bun', action: v, symbol: symbol(v) };
      return v === 'run' ? script('bun', words[1]) : null;
    }
    case 'deno': return verb(['compile', 'test', 'install', 'bundle'], 'Deno');
    case 'cargo': return verb(['build', 'b', 'test', 't', 'install', 'check', 'c', 'clippy', 'bench', 'doc', 'publish', 'fetch', 'update'], 'Cargo');
    case 'rustup': return verb(['update', 'install', 'toolchain'], 'rustup');
    case 'go': return verb(['build', 'test', 'install', 'generate', 'vet', 'mod', 'get'], 'Go');
    case 'make': case 'gmake': {
      if (a.includes('-n') || a.includes('-q')) return null;
      const t = words.slice(0, 2).join(' ');
      return { tool: 'make', action: t || 'build', symbol: symbol(t) };
    }
    case 'ninja': return { tool: 'ninja', action: words[0] || 'build', symbol: 'hammer.fill' };
    case 'cmake': {
      if (!a.includes('--build') && !a.includes('--install')) return null;
      return { tool: 'CMake', action: a.includes('--install') ? 'install' : 'build', symbol: 'hammer.fill' };
    }
    case 'bazel': case 'bazelisk': return verb(['build', 'test', 'fetch', 'sync'], 'Bazel');
    case 'dotnet': return verb(['build', 'test', 'publish', 'restore', 'pack'], '.NET');
    case 'ruby': return ruby(a);
    case 'pod': return verb(['install', 'update', 'repo', 'deintegrate', 'outdated'], 'CocoaPods');
    case 'fastlane': return words.length ? { tool: 'fastlane', action: words.slice(0, 2).join(' '), symbol: 'airplane' } : null;
    case 'brew': return verb(['install', 'upgrade', 'reinstall', 'update', 'bundle', 'fetch'], 'Homebrew');
    case 'carthage': return verb(['update', 'bootstrap', 'build'], 'Carthage');
    case 'winget': return verb(['install', 'upgrade', 'update', 'uninstall'], 'winget');
    case 'uv': {
      // `uv tool uvx …` / `uv run` start servers (MCP…); only installs count
      if (words[0] === 'tool') {
        return ['install', 'upgrade'].includes(words[1] || '') ? { tool: 'uv', action: 'tool install', symbol: symbol('install') } : null;
      }
      return verb(['sync', 'pip', 'add', 'lock', 'build', 'python'], 'uv');
    }
    case 'docker': case 'com.docker.cli': case 'podman': {
      const tool = name === 'podman' ? 'Podman' : 'Docker';
      const v = words[0];
      if (v !== undefined && ['compose', 'buildx', 'image'].includes(v)) {
        const m = verb(['build', 'pull', 'push', 'bake'], tool, { icon: 'com.docker.docker', w: words.slice(1) });
        return m && { ...m, action: `${v} ${m.action}` };
      }
      return verb(['build', 'pull', 'push', 'save', 'load'], tool, { icon: 'com.docker.docker' });
    }
    case 'git': {
      const v = words[0];
      if (!['clone', 'push', 'pull', 'fetch', 'lfs', 'submodule', 'gc', 'repack'].includes(v)) return null;
      const subject = v === 'clone' && words[1] ? base(words[1]).replace(/\.git$/, '') : undefined;
      return { tool: 'Git', action: v, symbol: symbol(v), subject };
    }
    case 'rsync': case 'scp': case 'rclone': case 'ditto': case 'robocopy':
      return { tool: name, action: L('sao chép'), symbol: 'arrow.left.arrow.right', subject: words.length ? base(words[words.length - 1]) : undefined };
    case 'aria2c': case 'yt-dlp': case 'wget':
      return { tool: name, action: L('tải xuống'), symbol: 'arrow.down.circle.fill', subject: words.length ? base(words[words.length - 1]) : undefined };
    case 'curl': {
      // only real downloads to a file; API calls from scripts are filtered by the minimum duration too
      const out = opt('-o', '--output') || ((a.includes('-O') || a.includes('-LO')) && words.length ? base(words[words.length - 1]) : null);
      if (!out) return null;
      return { tool: 'curl', action: L('tải xuống'), symbol: 'arrow.down.circle.fill', subject: base(out) };
    }
    case 'zip': case 'unzip': case 'tar': case 'bsdtar': case '7z': case '7zz': case 'xz': case 'zstd':
      return { tool: name, action: L('nén / giải nén'), symbol: 'archivebox.fill', subject: words.length ? base(words[words.length - 1]) : undefined };
    case 'hdiutil': return verb(['create', 'convert', 'makehybrid', 'compact'], 'hdiutil');
    case 'ffmpeg': {
      const inp = opt('-i');
      const input = inp && !inp.includes('=') ? base(inp) : null;  // lavfi/stream inputs aren't file names
      const last = a.length ? base(a[a.length - 1]) : null;
      return { tool: 'ffmpeg', action: L('xử lý video'), symbol: 'film.stack', subject: [input, last].filter(Boolean).join(' → ') };
    }
    case 'handbrakecli':
      return { tool: 'HandBrake', action: 'encode', symbol: 'film.stack', icon: 'fr.handbrake.HandBrake', subject: opt('-i', '--input') ? base(opt('-i', '--input')) : undefined };
    case 'blender': {
      if (!a.includes('-b') && !a.includes('--background')) return null;
      const frames = a.includes('-a') || a.includes('--render-anim') ? 'render animation' : 'render';
      const blend = a.find((x) => x.endsWith('.blend'));
      return { tool: 'Blender', action: frames, symbol: 'cube.transparent.fill', icon: 'org.blenderfoundation.blender', subject: blend ? base(blend) : undefined };
    }
    case 'magick': case 'convert': case 'mogrify':
      return { tool: 'ImageMagick', action: L('xử lý ảnh'), symbol: 'photo.stack.fill', subject: words.length ? base(words[words.length - 1]) : undefined };
    case 'whisper': case 'whisper-cli':
      return { tool: 'Whisper', action: L('chép lời'), symbol: 'waveform', subject: words.length ? base(words[words.length - 1]) : undefined };
    case 'ollama': return verb(['pull', 'create', 'push'], 'Ollama', { icon: 'com.electron.ollama' });
    case 'terraform': case 'tofu': return verb(['apply', 'plan', 'init', 'destroy'], 'Terraform');
    case 'pulumi': return verb(['up', 'preview', 'destroy', 'refresh'], 'Pulumi');
    case 'helm': return verb(['install', 'upgrade'], 'Helm');
    case 'notarytool': return verb(['submit'], 'Notarize', { icon: 'com.apple.dt.Xcode' });
    case 'altool': return { tool: 'App Store', action: 'upload', symbol: 'icloud.and.arrow.up.fill', icon: 'com.apple.dt.Xcode' };
    case 'xcodes': return verb(['install', 'download'], 'xcodes', { icon: 'com.apple.dt.Xcode' });
    case 'unity': {
      if (!a.includes('-batchmode')) return null;
      const bt = opt('-buildTarget');
      return { tool: 'Unity', action: bt ? `build ${bt}` : 'batch', symbol: 'hammer.fill', icon: 'com.unity3d.unityhub' };
    }
    case 'eas': return verb(['build', 'submit', 'update'], 'EAS');
    default: return null;
  }
}

/** node <script> …: the package owning the script is the tool (".../node_modules/next/dist/bin/next" -> next). */
function node(a) {
  const script = a.find((x) => !x.startsWith('-'));
  if (script === undefined) return null;
  let tool = base(script);
  const norm = script.replace(/\\/g, '/');
  const at = norm.lastIndexOf('node_modules/');
  if (at >= 0) {
    const parts = norm.slice(at + 'node_modules/'.length).split('/').filter(Boolean);
    if (parts.length) tool = parts[0].startsWith('@') && parts.length > 1 ? `${parts[0]}/${parts[1]}` : parts[0];
  }
  if (tool === 'npm-cli') tool = 'npm';
  const i = a.indexOf(script);
  const rest = a.slice(i + 1).filter((x) => !x.startsWith('-'));
  const flags = new Set(a);
  if (!rest.every((x) => !ENDLESS.has(x)) || flags.has('--watch') || flags.has('-w')) return null;
  switch (tool) {
    case 'npm': case 'pnpm': case 'yarn': case 'corepack': {
      const v = rest[0] ?? (tool === 'yarn' ? 'install' : '');
      if (['install', 'i', 'ci', 'add', 'update', 'upgrade', 'remove', 'rebuild', 'dedupe', 'publish', 'pack'].includes(v)) {
        return { tool, action: v, symbol: symbol(v) };
      }
      if (['test', 't'].includes(v)) return { tool, action: 'test', symbol: symbol('test') };
      return script_(tool, ['run', 'run-script'].includes(v) ? rest[1] : v);
    }
    case 'next': case 'vite': case 'astro': case 'nuxt': case 'nuxi': case 'svelte-kit': case 'gatsby': case 'remix': case 'parcel':
    case 'ng': case 'react-native': case 'expo': case 'turbo': case 'nx': case 'lerna': case '@expo/cli': case '@angular/cli':
    case '@react-native-community/cli': {
      const v = rest[0];
      if (!['build', 'export', 'generate', 'test', 'lint', 'prebuild', 'run-ios', 'run-android', 'run:ios', 'run:android',
        'bundle', 'run', 'run-many', 'affected'].includes(v)) return null;
      return { tool, action: rest.slice(0, 2).join(' '), symbol: symbol(v) };
    }
    case 'typescript': case 'tsc': case 'webpack': case 'webpack-cli': case 'esbuild': case 'rollup': case 'tsup': case 'jest':
    case 'eslint': case 'prettier': case 'electron-builder': case 'mocha': {
      const testy = ['jest', 'mocha', 'eslint'].includes(tool);
      return { tool: tool === 'typescript' ? 'tsc' : tool, action: rest[0] ?? (['jest', 'mocha'].includes(tool) ? 'test' : 'build'),
        symbol: symbol(testy ? 'test' : 'build') };
    }
    case 'vitest': return flags.has('run') || flags.has('--run') ? { tool: 'vitest', action: 'test', symbol: symbol('test') } : null;
    case '@playwright/test': case 'playwright':
      return rest[0] === 'test' ? { tool: 'Playwright', action: 'test', symbol: symbol('test') } : null;
    case '@electron-forge/cli': return rest[0] ? { tool: 'Electron Forge', action: rest[0], symbol: symbol(rest[0]) } : null;
    case 'eas-cli': return ['build', 'submit', 'update'].includes(rest[0]) ? { tool: 'EAS', action: rest[0], symbol: symbol(rest[0]) } : null;
    case 'firebase-tools': case 'vercel': case 'netlify-cli': case 'wrangler': case 'aws-cdk': case 'serverless': {
      const v = rest[0] ?? (tool === 'vercel' ? 'deploy' : null);
      if (!['deploy', 'publish', 'synth', 'build'].includes(v)) return null;
      return { tool, action: v, symbol: symbol(v) };
    }
    case 'prisma': return rest[0] ? { tool: 'Prisma', action: rest.slice(0, 2).join(' '), symbol: symbol(rest[0]) } : null;
    default: return null;
  }
}

/** package.json scripts: only ones that end (build, test…), never dev servers. */
function script_(tool, name) {
  if (!name || ENDLESS.has(name)) return null;
  const s = name.toLowerCase();
  const finite = ['build', 'test', 'lint', 'check', 'type', 'e2e', 'deploy', 'release', 'export', 'generate', 'compile', 'bundle', 'package',
    'dist', 'ci', 'format', 'migrate', 'seed', 'codegen', 'prebuild', 'android', 'ios'];
  if (!finite.some((w) => s.includes(w)) || ['dev', 'start', 'serve', 'watch'].some((w) => s.includes(w))) return null;
  return { tool, action: `run ${name}`, symbol: symbol(name) };
}

function ruby(a) {
  const script = a.find((x) => !x.startsWith('-'));
  if (script === undefined) return null;
  const rest = a.slice(a.indexOf(script) + 1).filter((x) => !x.startsWith('-'));
  switch (base(script)) {
    case 'pod': return classify('pod', ['pod', ...rest]);
    case 'fastlane': return classify('fastlane', ['fastlane', ...rest]);
    case 'brew': return classify('brew', ['brew', ...rest]);
    case 'bundle': return rest[0] === 'install' || rest.length === 0 ? { tool: 'Bundler', action: 'install', symbol: symbol('install') } : null;
    case 'rspec': return { tool: 'RSpec', action: 'test', symbol: symbol('test') };
    default: return null;
  }
}

function python(a) {
  let tool = null, rest = [];
  const m = a.indexOf('-m');
  if (m >= 0 && m + 1 < a.length) {
    tool = a[m + 1];
    rest = a.slice(m + 2).filter((x) => !x.startsWith('-'));
  } else {
    const script = a.find((x) => !x.startsWith('-'));
    if (script !== undefined) {
      tool = base(script);
      rest = a.slice(a.indexOf(script) + 1).filter((x) => !x.startsWith('-'));
    }
  }
  const v = rest[0] || '';
  switch (tool || '') {
    case 'pip': case 'pip3': return ['install', 'download', 'wheel'].includes(v) ? { tool: 'pip', action: v, symbol: symbol(v) } : null;
    case 'poetry': return ['install', 'update', 'add', 'lock', 'build'].includes(v) ? { tool: 'Poetry', action: v, symbol: symbol(v) } : null;
    case 'conda': case 'mamba': return ['install', 'create', 'update', 'env'].includes(v) ? { tool: 'Conda', action: v, symbol: symbol(v) } : null;
    case 'pytest': case 'py.test': return { tool: 'pytest', action: 'test', symbol: symbol('test') };
    case 'manage': return v === 'test' ? { tool: 'Django', action: 'test', symbol: symbol('test') } : null;
    case 'build': return { tool: 'python', action: 'build', symbol: 'hammer.fill' };
    case 'hf': case 'huggingface-cli': return ['download', 'upload'].includes(v) ? { tool: 'Hugging Face', action: v, symbol: symbol(v) } : null;
    case 'tox': case 'nox': return { tool, action: 'test', symbol: symbol('test') };
    default: return null;
  }
}

/** Past run times per task, for "≈ 2 phút nữa" and a progress estimate. store = {load(), save(obj)} (injected). */
class TaskHistory {
  constructor(store = {}) {
    this.store = store;
    const loaded = store.load ? store.load() : null;
    this.runs = {};
    if (loaded && typeof loaded === 'object') {
      for (const [k, v] of Object.entries(loaded)) {
        if (Array.isArray(v)) this.runs[k] = v.filter((x) => typeof x === 'number' && x >= 0);
      }
    }
  }

  expected(key) {
    const r = this.runs[key];
    if (!r || !r.length) return null;
    const s = [...r].sort((a, b) => a - b);
    return s[Math.floor(s.length / 2)];  // median: one odd run doesn't skew it
  }

  record(key, seconds) {
    if (!(seconds >= 5)) return;
    this.runs[key] = [...(this.runs[key] || []), seconds].slice(-5);
    const keys = Object.keys(this.runs);
    if (keys.length > 300) {
      const keep = keys.sort(() => Math.random() - 0.5).slice(0, 250);
      this.runs = Object.fromEntries(keep.map((k) => [k, this.runs[k]]));
    }
    if (this.store.save) this.store.save(this.runs);
  }

  /** [progress 0…0.99 or null, label] from elapsed vs the usual duration (seconds). */
  static estimate(elapsed, expected) {
    const time = mmss(elapsed);
    if (expected == null || expected < 10) return [null, time];
    const left = expected - elapsed;
    if (left <= 0) return [0.99, time + L(' · lâu hơn thường lệ')];
    const rest = left < 60 ? `${Math.trunc(left)}s` : L('{0} phút', Math.trunc(left / 60) + 1);
    return [Math.min(elapsed / expected, 0.99), `${time} · ≈ ${rest}${L(' nữa')}`];
  }
}

// GUI editors/renderers: a task while they work hard. Matched by process name (no .exe).
const RENDER_APPS = [
  { exe: 'resolve', name: 'DaVinci Resolve', icon: 'com.blackmagic-design.DaVinciResolve' },
  { exe: 'adobe premiere pro', name: 'Premiere Pro', icon: 'com.adobe.PremierePro' },
  { exe: 'afterfx', name: 'After Effects', icon: 'com.adobe.AfterEffects' },
  { exe: 'adobe media encoder', name: 'Media Encoder', icon: 'com.adobe.ame' },
  { exe: 'blender', name: 'Blender', icon: 'org.blenderfoundation.blender' },
  { exe: 'handbrake', name: 'HandBrake', icon: 'fr.handbrake.HandBrake' },
  { exe: 'capcut', name: 'CapCut', icon: 'com.lemon.lvoverseas' },
  { exe: 'topaz video ai', name: 'Topaz', icon: 'com.topazlabs' },
  { exe: 'lightroom', name: 'Lightroom', icon: 'com.adobe.LightroomClassic' },
  { exe: 'unity', name: 'Unity', icon: 'com.unity3d.UnityEditor' },
  { exe: 'unrealeditor', name: 'Unreal', icon: 'com.epicgames.UnrealEditor' },
];

/**
 * Finds running dev tasks in the process list on every tick. Keeps its own per-pid CPU meter.
 * opts: history {load,save}, cwd(pid) -> folder or null (Windows cannot read it cheaply, so null by default),
 *       onPush(dir) for `git push` (GitHub Actions tracking).
 */
class DevScanner {
  constructor({ history = {}, cwd = () => null, onPush = () => {} } = {}) {
    this.history = new TaskHistory(history);
    this.cwd = cwd;
    this.onPush = onPush;
    this.meter = new P.CpuMeter();
    this.argvCache = new Map();  // pid -> {name, match}
    this.running = new Map();    // job id -> {match, title, since, key, lastBusy}
    this.seenGit = new Set();
  }

  /** procs from proc.list(); now in ms. Returns the Job rows. */
  scan(procs, now = Date.now()) {
    this.meter.sample(procs, now);
    const byPid = new Map(procs.map((p) => [p.pid, p]));
    const kids = P.childrenOf(procs);
    for (const pid of [...this.argvCache.keys()]) {
      if (!byPid.has(pid) || byPid.get(pid).name !== this.argvCache.get(pid).name) this.argvCache.delete(pid);
    }
    for (const pid of [...this.seenGit]) if (!byPid.has(pid)) this.seenGit.delete(pid);

    const argvOf = (p) => P.splitArgs(p.cmdline);
    const match = (p) => {
      const c = this.argvCache.get(p.pid);
      if (c) return c.match;
      const m = NAMES.has(p.name) || isPython(p.name) ? classify(p.name, argvOf(p)) : null;
      this.argvCache.set(p.pid, { name: p.name, match: m });
      return m;
    };
    // CI jobs are shown by their runner; nested tools belong to the outermost one (npm -> node next build)
    const ancestors = (p) => {
      const out = [];
      let cur = byPid.get(p.ppid);
      while (cur && cur.pid > 1 && out.length < 40) { out.push(cur); cur = byPid.get(cur.ppid); }
      return out;
    };
    const treeCPU = (root) => {
      let total = this.meter.percent(root);
      const seen = new Set([root]), stack = [root];
      while (stack.length) {
        for (const c of kids.get(stack.pop()) || []) {
          if (seen.has(c.pid)) continue;
          seen.add(c.pid);
          total += this.meter.percent(c.pid);
          stack.push(c.pid);
        }
      }
      return total;
    };
    const startMs = (p) => (p.start ? p.start.getTime() : now);

    const found = new Map();
    const keep = (id, m, { title, since, key, busy }) => {
      const r = this.running.get(id) || { match: m, title, since, key, lastBusy: null };
      r.match = m;
      if (busy) r.lastBusy = now;
      found.set(id, r);
    };

    for (const p of procs) {
      if (p.name === 'git' && !this.seenGit.has(p.pid)) {
        this.seenGit.add(p.pid);
        if (argvOf(p)[1] === 'push') {
          const dir = this.cwd(p.pid);
          if (dir) this.onPush(dir);
        }
      }
      const m = match(p);
      if (!m) continue;
      const up = ancestors(p);
      if (up.some((a) => a.name === 'gitlab-runner' || a.name === 'runner.worker' || match(a))) continue;
      const id = `dev:${p.pid}`;
      const cwd = this.cwd(p.pid);
      const folder = cwd ? project(cwd) : folderFromArgv(argvOf(p)) || m.tool;
      const title = m.subject || folder;
      const key = [m.tool, m.action, folder].join('|');
      if (m.busyOnly) {
        const busy = treeCPU(p.pid) > 40;
        // the Gradle daemon only counts when no gradle client is already shown
        const prev = this.running.get(id);
        if (busy || (prev && prev.lastBusy != null && now - prev.lastBusy < 8000)) {
          keep(id, m, { title, since: prev ? prev.since : now, key, busy });
        }
      } else if (now - startMs(p) >= 3000) {  // quick commands are not tasks
        keep(id, m, { title, since: startMs(p), key, busy: true });
      }
    }
    if ([...found.values()].some((r) => r.match.tool === 'Gradle' && !r.match.busyOnly)) {
      for (const [id, r] of found) if (r.match.tool === 'Gradle' && r.match.busyOnly) found.delete(id);
    }

    // heavy work in GUI render/edit apps: sustained 10 s before it counts, so scrubbing a timeline doesn't
    for (const p of procs) {
      const app = RENDER_APPS.find((a) => a.exe === p.name);
      if (!app) continue;
      const id = `app:${p.pid}`;
      const load = treeCPU(p.pid);
      const prev = this.running.get(id);
      const busy = load > 150;
      const since = busy ? (prev ? prev.since : now) : prev && prev.since;
      if (since == null || !(busy || (prev && prev.lastBusy != null && now - prev.lastBusy < 8000))) continue;
      const m = { tool: app.name, action: L('đang xử lý · {0}% CPU', Math.trunc(load)), symbol: 'film.stack', icon: app.icon, busyOnly: true };
      keep(id, m, { title: app.name, since, key: `app|${app.icon}`, busy });
    }

    for (const [id, r] of this.running) {
      if (!found.has(id)) this.history.record(r.key, (now - r.since) / 1000);
    }
    this.running = found;

    return [...found].map(([id, r]) => {
      const elapsed = (now - r.since) / 1000;
      const shown = !r.match.busyOnly || id.startsWith('dev:') || elapsed >= 10;
      const [progress, time] = TaskHistory.estimate(elapsed, this.history.expected(r.key));
      return {
        id, title: `${r.match.tool} · ${r.title}`, detail: `${r.match.action} · ${time}`, working: shown,
        progress, estimated: true, icon: r.match.icon, symbol: r.match.symbol, tool: r.match.tool,
      };
    }).filter((j) => j.working).sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  }
}

const TRANSFER_EXT = /\.(crdownload|part|download|partial)$/i;
const MIN_TRANSFER = 5_000_000;  // small files finish before anyone looks
const STALL_MS = 8000;

function sizeText(bytes) {
  if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`;
  if (bytes >= 1e6) return `${(bytes / 1e6).toFixed(1)} MB`;
  if (bytes >= 1e3) return `${(bytes / 1e3).toFixed(0)} KB`;
  return `${bytes} B`;
}

/**
 * Downloads and copies. The Mac reads NSProgress; Windows has no such feed, so this polls the top level of the
 * usual folders: partial downloads (.crdownload/.part) and big files that keep growing. No total is known, so no %.
 * shortcut: subfolders are not watched, and a copy that never grows is invisible; add folders here if needed.
 */
class FileProgressWatcher {
  constructor({ folders, now = () => Date.now() } = {}) {
    const home = os.homedir();
    this.folders = folders || ['Downloads', 'Desktop', 'Documents', 'Videos', 'Pictures', 'Music'].map((f) => path.join(home, f));
    this.now = now;
    this.seen = new Map();  // file path -> {size, at, lastChange}
  }

  /** Active transfers now (synchronous readdir/stat of the watched folders; call once per tick). */
  jobs() {
    const t = this.now();
    const out = [], live = new Set();
    for (const dir of this.folders) {
      let names;
      try { names = fs.readdirSync(dir); } catch { continue; }
      for (const name of names) {
        const full = path.join(dir, name);
        let st;
        try { st = fs.statSync(full); } catch { continue; }
        if (!st.isFile()) continue;
        live.add(full);
        const partial = TRANSFER_EXT.test(name);
        const prev = this.seen.get(full);
        const changed = prev && prev.size !== st.size;
        const lastChange = prev ? (changed ? t : prev.lastChange) : (partial ? st.mtimeMs : null);
        const speed = changed ? (st.size - prev.size) / Math.max(0.001, (t - prev.at) / 1000) : prev && prev.speed;
        this.seen.set(full, { size: st.size, at: t, lastChange, speed });
        if (st.size < MIN_TRANSFER || lastChange == null || t - lastChange >= STALL_MS) continue;
        out.push({
          id: `file:${full}`, title: `${partial ? L('Tải xuống') : L('Sao chép')} · ${name}`,
          detail: `${sizeText(st.size)}${speed > 0 ? ` · ${sizeText(speed)}/s` : ''}`,
          working: true, progress: null, estimated: false,
          symbol: partial ? 'arrow.down.circle.fill' : 'doc.on.doc.fill', tool: 'Tải file',
        });
      }
    }
    for (const k of [...this.seen.keys()]) if (!live.has(k)) this.seen.delete(k);
    return out;
  }

  stop() { this.seen.clear(); }
}

module.exports = {
  mmss, base, project, folderFromArgv, classify, node, python, ruby, script_, symbol, appIcon, defaultSymbol,
  NAMES, KNOWN_TOOLS, RENDER_APPS, ENDLESS, TaskHistory, DevScanner, FileProgressWatcher, sizeText,
};
