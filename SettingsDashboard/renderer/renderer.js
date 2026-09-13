// Every control here writes straight to settings.json via window.dashboard (see preload.js) —
// there's no explicit Save button, since the widget watches that file and applies changes live.

// Fallback so this page can be previewed in a plain browser (no Electron preload context) —
// the real app always has window.dashboard from preload.js.
if (!window.dashboard) {
  const mock = {
    IdleHideTimeoutSeconds: 10,
    FlyoutDurationSeconds: 4,
    Opacity: 0.85,
    DockSide: 'Tray',
    FlyoutPosition: 'BottomRight',
    TargetMonitorKey: null,
  };
  window.dashboard = {
    getSettings: async () => mock,
    setSettings: async (partial) => Object.assign(mock, partial),
    listDisplays: async () => [
      { key: '0,0', label: '1920×1080 (Primary)', isPrimary: true },
      { key: '1920,0', label: '2560×1440', isPrimary: false },
    ],
    getStartupEnabled: async () => false,
    setStartupEnabled: async (enabled) => ({ ok: true }),
    getAppVersion: async () => '1.0.0 (preview)',
  };
}

function debounce(fn, delay) {
  let timer = null;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), delay);
  };
}

function setActiveByValue(elements, value, attr = 'data-value') {
  elements.forEach((el) => el.classList.toggle('active', el.getAttribute(attr) === String(value)));
}

async function init() {
  const settings = await window.dashboard.getSettings();

  initNav();
  initOpacity(settings);
  initIdlePresets(settings);
  initDockSide(settings);
  initIdleAnimation(settings);
  initCornerGrid(settings);
  initFlyoutDuration(settings);
  await initDisplayList(settings);
  await initStartupToggle();
  await initAbout();
}

// ----- Navigation -----

function initNav() {
  const navItems = document.querySelectorAll('.nav-item[data-page]');
  const pages = document.querySelectorAll('.page');

  function goto(pageId) {
    navItems.forEach((el) => el.classList.toggle('active', el.getAttribute('data-page') === pageId));
    pages.forEach((el) => el.classList.toggle('active', el.id === `page-${pageId}`));
  }

  navItems.forEach((el) => el.addEventListener('click', () => goto(el.getAttribute('data-page'))));
  document.querySelectorAll('.tile[data-goto]').forEach((el) =>
    el.addEventListener('click', () => goto(el.getAttribute('data-goto')))
  );
}

// ----- Taskbar Widget page -----

function initOpacity(settings) {
  const slider = document.getElementById('opacity-slider');
  const label = document.getElementById('opacity-value');

  const percent = Math.round(settings.Opacity * 100);
  slider.value = percent;
  label.textContent = `${percent}%`;

  const commit = debounce((value) => window.dashboard.setSettings({ Opacity: value / 100 }), 150);

  slider.addEventListener('input', () => {
    label.textContent = `${slider.value}%`;
    commit(Number(slider.value));
  });
}

function initIdlePresets(settings) {
  const chips = [...document.querySelectorAll('#idle-presets .chip')];
  setActiveByValue(chips, settings.IdleHideTimeoutSeconds, 'data-seconds');

  chips.forEach((chip) => {
    chip.addEventListener('click', async () => {
      const seconds = Number(chip.getAttribute('data-seconds'));
      setActiveByValue(chips, seconds, 'data-seconds');
      await window.dashboard.setSettings({ IdleHideTimeoutSeconds: seconds });
    });
  });
}

function initDockSide(settings) {
  const segments = [...document.querySelectorAll('#dock-side .segment')];
  setActiveByValue(segments, settings.DockSide);

  segments.forEach((seg) => {
    seg.addEventListener('click', async () => {
      const value = seg.getAttribute('data-value');
      setActiveByValue(segments, value);
      await window.dashboard.setSettings({ DockSide: value });
    });
  });
}

function initIdleAnimation(settings) {
  const toggle = document.getElementById('idle-animation-toggle');
  toggle.checked = settings.IdleAnimationEnabled !== false;

  toggle.addEventListener('change', async () => {
    await window.dashboard.setSettings({ IdleAnimationEnabled: toggle.checked });
  });
}

// ----- Next Track Flyout page -----

function initCornerGrid(settings) {
  const corners = [...document.querySelectorAll('#corner-grid .corner')];
  setActiveByValue(corners, settings.FlyoutPosition);

  corners.forEach((corner) => {
    corner.addEventListener('click', async () => {
      const value = corner.getAttribute('data-value');
      setActiveByValue(corners, value);
      await window.dashboard.setSettings({ FlyoutPosition: value });
    });
  });
}

function initFlyoutDuration(settings) {
  const chips = [...document.querySelectorAll('#flyout-duration-presets .chip')];
  setActiveByValue(chips, settings.FlyoutDurationSeconds, 'data-seconds');

  chips.forEach((chip) => {
    chip.addEventListener('click', async () => {
      const seconds = Number(chip.getAttribute('data-seconds'));
      setActiveByValue(chips, seconds, 'data-seconds');
      await window.dashboard.setSettings({ FlyoutDurationSeconds: seconds });
    });
  });
}

// ----- System page -----

async function initDisplayList(settings) {
  const list = document.getElementById('display-list');
  const displays = await window.dashboard.listDisplays();

  const entries = [{ key: null, label: 'Automatic (primary display)', isPrimary: false }, ...displays];

  list.innerHTML = '';
  entries.forEach((entry) => {
    const item = document.createElement('div');
    item.className = 'list-item';
    if (entry.key === settings.TargetMonitorKey || (entry.key === null && !settings.TargetMonitorKey)) {
      item.classList.add('active');
    }
    item.innerHTML = `<span class="dot"></span><span>${entry.label}</span>`;
    item.addEventListener('click', async () => {
      [...list.children].forEach((el) => el.classList.remove('active'));
      item.classList.add('active');
      await window.dashboard.setSettings({ TargetMonitorKey: entry.key });
    });
    list.appendChild(item);
  });
}

async function initStartupToggle() {
  const toggle = document.getElementById('startup-toggle');
  const status = document.getElementById('startup-status');

  toggle.checked = await window.dashboard.getStartupEnabled();

  toggle.addEventListener('change', async () => {
    const result = await window.dashboard.setStartupEnabled(toggle.checked);
    if (!result.ok) {
      toggle.checked = !toggle.checked;
      status.textContent = result.reason || 'Could not update startup setting.';
    } else {
      status.textContent = '';
    }
  });
}

// ----- About page -----

async function initAbout() {
  const version = await window.dashboard.getAppVersion();
  document.getElementById('about-version').textContent = `Dashboard version ${version}`;
}

document.addEventListener('DOMContentLoaded', init);
