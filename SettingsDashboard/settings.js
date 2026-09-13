// Shared settings.json read/write — this file, at the exact same path the WPF widget uses
// (%LocalAppData%\TaskbarMediaWidget\settings.json), is the entire sync mechanism between the
// two apps. No IPC: the widget watches this file for changes and reloads live.

const fs = require('fs');
const path = require('path');

const DEFAULTS = {
  IdleHideTimeoutSeconds: 10,
  FlyoutDurationSeconds: 4,
  Opacity: 0.85,
  DockSide: 'Tray',
  FlyoutPosition: 'BottomRight',
  IdleAnimationEnabled: true,
  TargetMonitorKey: null,
};

function getSettingsPath() {
  const base = process.env.LOCALAPPDATA || path.join(require('os').homedir(), 'AppData', 'Local');
  return path.join(base, 'TaskbarMediaWidget', 'settings.json');
}

function loadSettings() {
  try {
    const raw = fs.readFileSync(getSettingsPath(), 'utf-8');
    const parsed = JSON.parse(raw);
    return { ...DEFAULTS, ...parsed };
  } catch {
    return { ...DEFAULTS };
  }
}

function saveSettings(partial) {
  const current = loadSettings();
  const merged = { ...current, ...partial };

  const settingsPath = getSettingsPath();
  fs.mkdirSync(path.dirname(settingsPath), { recursive: true });
  fs.writeFileSync(settingsPath, JSON.stringify(merged, null, 2), 'utf-8');

  return merged;
}

module.exports = { DEFAULTS, getSettingsPath, loadSettings, saveSettings };
