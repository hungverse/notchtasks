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
    claude: 'Duyệt lệnh, sửa file, trả lời câu hỏi', gemini: 'Duyệt lệnh, sửa file, trả lời câu hỏi',
    antigravity: 'Duyệt lệnh, sửa file, trả lời câu hỏi', codex: 'Duyệt lệnh, sửa file',
    cursor: 'Từ chối lệnh. Cho phép chỉ khi Cursor đang tự chạy lệnh', opencode: 'Duyệt lệnh, sửa file, truy cập web',
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
      h('div', { class: 't ob-t' }, L('Chào mừng đến NotchTasks')),
      h('div', { class: 'dim' }, L('Notch hiện những gì đang chạy, và im lặng khi không có gì.')),
      h('div', { class: 'card-block' }, icon('arrow-up', 16, 'acc'),
        h('span', {}, L('Nhìn lên mép trên màn hình: đó là notch của bạn.'))));
  }

  function modulesPage() {
    const s = state.settings;
    return h('div', { class: 'ob-stack' },
      header(L('Hiện gì trên notch'), L('Bấm để bật hoặc tắt. Đổi lại bất cứ lúc nào trong Bảng điều khiển.')),
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
      header(L('Trả lời agent trên notch'), L('Mỗi công tắc thêm một mục vào cấu hình agent và sao lưu bản cũ. Có hiệu lực từ phiên mở sau.')),
      found.length ? h('div', { class: 'list' }, found.map((a) => h('div', { class: 'irow' }, icon('bot', 22, 'dim'),
        h('div', { class: 'itext' }, h('div', { class: 'it' }, a.name), h('div', { class: 'is dim' }, L(AGENT_NOTES[a.kind] || 'Duyệt lệnh, sửa file'))),
        toggle(a.installed, (on) => {
          state = { ...state, agents: state.agents.map((x) => (x.kind === a.kind ? { ...x, installed: on } : x)) };
          nt.act(on ? 'installHook' : 'uninstallHook', a.kind);
          render();
        }, a.name)))) : h('div', { class: 'card-block dim' }, L('Chưa thấy AI agent nào. Thêm sau ở trang Theo dõi.')),
      state.agentError ? h('div', { class: 'err' }, state.agentError) : null);
  }

  function phonePage() {
    return h('div', { class: 'ob-phone' },
      header(L('Điện thoại (tuỳ chọn)'), L('Ghép điện thoại để trả lời agent từ màn hình khoá khi rời máy tính.')),
      h('div', { class: 'row' }, h('button', { class: 'btn', type: 'button', onclick: () => done('phone') }, L('Mở Ghép nối'))));
  }

  function readyPage() {
    const step = (glyphs, title, text) => h('div', { class: 'card-block' },
      h('span', { class: 'row' }, glyphs.map((g) => h('span', { class: 'glyph' }, icon(g, 16)))),
      h('div', { class: 'itext' }, h('div', { class: 'it' }, title), h('div', { class: 'is dim' }, text)));
    return h('div', { class: 'ob-stack' },
      header(L('Sẵn sàng rồi'), L('Ba điều cần nhớ:')),
      h('div', { class: 'ob-steps' },
        step(['mouse-pointer-click'], L('Mở notch'), L('Trỏ chuột vào notch ở mép trên màn hình.')),
        step(['settings', 'power'], L('Bảng điều khiển'), L('Nút Bảng điều khiển và Thoát nằm ở cuối notch khi mở.')),
        step(['panel-top'], L('Biểu tượng trên khay hệ thống'), L('Biểu tượng NotchTasks ở khay hệ thống (cạnh đồng hồ) mở Bảng điều khiển.'))));
  }

  const BUILDERS = [welcome, modulesPage, agentsPage, phonePage, readyPage];

  function render() {
    if (!state) return;
    const pageEl = document.getElementById('page');
    fill(pageEl, [BUILDERS[page]()]);
    const dots = h('div', { class: 'dots' }, PAGES.map((_, i) => h('i', { class: i === page ? 'on' : '' })));
    const last = page === LAST;
    const next = last ? L('Cho tôi xem') : page === PHONE ? L('Để sau') : L('Tiếp');
    fill(document.getElementById('foot'), [
      dots,
      h('span', { class: 'dim small' }, L('Bước {0}/{1}', page + 1, PAGES.length)),
      h('span', { class: 'sp' }),
      h('button', { class: 'btn', type: 'button', onclick: () => done('close') }, L('Bỏ qua')),
      page > 0 ? h('button', { class: 'btn', type: 'button', onclick: () => { page -= 1; render(); } }, L('Quay lại')) : null,
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
