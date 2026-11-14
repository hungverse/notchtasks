'use strict';
// First-run guide (Sources/Onboarding.swift). Five pages: the permission page is Mac-only and dropped. Each change goes
// out as an action; the guide ends with finishOnboarding('close' | 'phone' | 'notch') and main closes the window.
(function () {
  const nt = window.nt;
  const U = window.NTUI;
  const { h, fill, icon, toggle, NotchPreview } = U;
  const L = (k, ...a) => nt.L(k, ...a);
  const CAT = nt.catalog();
  U.setCatalog(CAT);

  const PAGES = ['welcome', 'modules', 'agents', 'phone', 'ready'];
  const LAST = PAGES.length - 1;
  const PHONE = PAGES.indexOf('phone');
  let state = null;
  let page = 0;

  const AGENT_NOTES = {
    claude: 'Review commands, file edits, answer questions', gemini: 'Review commands, file edits, answer questions',
    antigravity: 'Review commands, file edits, answer questions', codex: 'Review commands, file edits',
    cursor: 'Denies commands. Allows only while Cursor runs commands on its own', opencode: 'Review commands, file edits, web access',
  };

  function setSetting(key, value) {
    state = { ...state, settings: { ...state.settings, [key]: value } };
    nt.act('setSetting', key, value);
    U.setSettingsForAccent(state.settings);
  }

  function done(exit) {
    setSetting('onboardingDone', true);
    nt.act('finishOnboarding', exit);
  }

  function header(title, sub) {
    return h('div', { class: 'ob-h' }, h('div', { class: 't' }, title), h('div', { class: 's' }, sub));
  }

  function welcome() {
    return h('div', { class: 'ob-hero' },
      h('div', { class: 'wall' }, NotchPreview(state.settings, state.live, {})),
      h('div', { class: 't ob-t' }, L('Welcome to NotchTasks')),
      h('div', { class: 'dim' }, L('The notch shows what\'s running, and stays quiet when nothing is.')),
      h('div', { class: 'card-block' }, icon('arrow-up', 16, 'acc'),
        h('span', {}, L('Look at the top edge of the screen: that is your notch.'))));
  }

  function modulesPage() {
    const s = state.settings;
    return h('div', { class: 'ob-stack' },
      header(L('What the notch shows'), L('Click to turn on or off. Change it anytime in the Control Panel.')),
      CAT.moduleGroups.map((g) => h('div', { class: 'block' }, h('div', { class: 'grp-t' }, L(g.title)),
        h('div', { class: 'ob-mod' }, g.ids.map((id) => {
          const m = CAT.modules.find((x) => x.id === id);
          const on = !s.disabled.includes(id);
          return h('div', { class: 'mcell ob-tile' }, h('span', { class: `mtile${on ? ' on' : ''}` }, icon(m.icon, 16)),
            h('div', { class: 'ob-tx' }, h('div', { class: `mcell-t${on ? '' : ' off'}` }, L(m.title)),
              h('div', { class: 'ob-tile-blurb dim small' }, L(m.blurb))),
            toggle(on, (v) => {
              const disabled = s.disabled.filter((x) => x !== id);
              if (!v) disabled.push(id);
              setSetting('disabled', disabled);
              render();
            }, L(m.title)));
        })))));
  }

  function agentsPage() {
    const found = state.agents.filter((a) => a.present);
    return h('div', { class: 'ob-stack' },
      header(L('Answer agents on the notch'), L('Each switch adds one entry to the agent config and backs up the old file. It applies to new sessions.')),
      found.length ? h('div', { class: 'list' }, found.map((a) => h('div', { class: 'irow' }, icon('bot', 22, 'dim'),
        h('div', { class: 'itext' }, h('div', { class: 'it' }, a.name), h('div', { class: 'is dim' }, L(AGENT_NOTES[a.kind] || 'Review commands, file edits'))),
        toggle(a.installed, (on) => {
          state = { ...state, agents: state.agents.map((x) => (x.kind === a.kind ? { ...x, installed: on } : x)) };
          nt.act(on ? 'installHook' : 'uninstallHook', a.kind);
          render();
        }, a.name)))) : h('div', { class: 'card-block dim' }, L('No AI agents found yet. Add them later on the Watching page.')),
      state.agentError ? h('div', { class: 'err' }, state.agentError) : null);
  }

  function phonePage() {
    return h('div', { class: 'ob-phone' },
      header(L('Phone (optional)'), L('Pair your phone to answer agents from the lock screen when away from your computer.')),
      h('div', { class: 'row' }, h('button', { class: 'btn', type: 'button', onclick: () => done('phone') }, L('Open Pairing'))));
  }

  function readyPage() {
    const step = (glyphs, title, text) => h('div', { class: 'card-block' },
      h('span', { class: 'row' }, glyphs.map((g) => h('span', { class: 'glyph' }, icon(g, 16)))),
      h('div', { class: 'itext' }, h('div', { class: 'it' }, title), h('div', { class: 'is dim' }, text)));
    return h('div', { class: 'ob-stack' },
      header(L('You\'re all set'), L('Three things to remember:')),
      h('div', { class: 'ob-steps' },
        step(['mouse-pointer-click'], L('Opening the notch'), L('Point at the notch at the top of the screen.')),
        step(['settings', 'power'], L('Control Panel'), L('The Control Panel and Quit buttons are at the end of the notch when it is open.')),
        step(['panel-top'], L('Icon in the system tray'), L('The NotchTasks icon in the system tray (by the clock) opens the Control Panel.'))));
  }

  const BUILDERS = [welcome, modulesPage, agentsPage, phonePage, readyPage];

  function render() {
    if (!state) return;
    const pageEl = document.getElementById('page');
    fill(pageEl, [BUILDERS[page]()]);
    const dots = h('div', { class: 'dots' }, PAGES.map((_, i) => h('i', { class: i === page ? 'on' : '' })));
    const last = page === LAST;
    const next = last ? L('Show me') : page === PHONE ? L('Later') : L('Next');
    fill(document.getElementById('foot'), [
      dots,
      h('span', { class: 'dim small' }, L('Step {0}/{1}', page + 1, PAGES.length)),
      h('span', { class: 'sp' }),
      h('button', { class: 'btn', type: 'button', onclick: () => done('close') }, L('Skip')),
      page > 0 ? h('button', { class: 'btn', type: 'button', onclick: () => { page -= 1; render(); } }, L('Back')) : null,
      h('button', { class: 'btn primary', type: 'button', onclick: () => (last ? done('notch') : (page += 1, render())) }, next)]);
  }

  nt.onState((next) => {
    state = next;
    U.setSettingsForAccent(next.settings);
    render();
  });
  // screenshot checks, as the Mac's debug page switch: "guide-N"
  nt.onPane((p) => {
    const m = /^guide-(\d+)$/.exec(p || '');
    if (m) { page = Math.min(Math.max(Number(m[1]), 0), LAST); render(); }
  });
})();
