const { app, BrowserWindow, screen, ipcMain } = require('electron');
const path = require('path');
const fs = require('fs');
const { execFile } = require('child_process');
const { loadSettings, saveSettings } = require('./settings');

const RUN_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run';
const RUN_VALUE = 'TaskbarMediaWidget';

let mainWindow = null;

// Only one dashboard window at a time — a second launch (e.g. from the widget's tray icon while
// the dashboard is already open) just focuses the existing one instead of opening a duplicate.
const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  app.whenReady().then(createWindow);

  app.on('window-all-closed', () => app.quit());
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 960,
    height: 640,
    minWidth: 760,
    minHeight: 520,
    backgroundColor: '#1a1a1a',
    title: 'Echo Flyout — Settings',
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });

  mainWindow.loadFile(path.join(__dirname, 'renderer', 'index.html'));
}

// ----- Widget exe resolution (for the "Start with Windows" registry entry) -----

function findWidgetExePath() {
  const repoRoot = path.join(__dirname, '..');
  const candidates = [
    path.join(repoRoot, 'TaskbarMediaWidget', 'bin', 'Release', 'net8.0-windows10.0.19041.0', 'TaskbarMediaWidget.exe'),
    path.join(repoRoot, 'TaskbarMediaWidget', 'bin', 'Debug', 'net8.0-windows10.0.19041.0', 'TaskbarMediaWidget.exe'),
  ];
  return candidates.find((p) => fs.existsSync(p)) || null;
}

// ----- IPC -----

ipcMain.handle('settings:get', () => loadSettings());

ipcMain.handle('settings:set', (_event, partial) => saveSettings(partial));

ipcMain.handle('displays:list', () => {
  const primary = screen.getPrimaryDisplay();
  return screen.getAllDisplays().map((d) => ({
    key: `${d.bounds.x},${d.bounds.y}`,
    label: `${d.bounds.width}×${d.bounds.height}${d.id === primary.id ? ' (Primary)' : ''}`,
    isPrimary: d.id === primary.id,
  }));
});

ipcMain.handle('startup:get', () => {
  return new Promise((resolve) => {
    execFile('reg', ['query', RUN_KEY, '/v', RUN_VALUE], (error) => resolve(!error));
  });
});

ipcMain.handle('startup:set', (_event, enabled) => {
  return new Promise((resolve) => {
    if (enabled) {
      const exePath = findWidgetExePath();
      if (!exePath) {
        resolve({ ok: false, reason: 'Widget executable not found — build TaskbarMediaWidget first.' });
        return;
      }
      execFile('reg', ['add', RUN_KEY, '/v', RUN_VALUE, '/t', 'REG_SZ', '/d', `"${exePath}"`, '/f'], (error) => {
        resolve({ ok: !error, reason: error ? String(error) : null });
      });
    } else {
      execFile('reg', ['delete', RUN_KEY, '/v', RUN_VALUE, '/f'], (error) => {
        resolve({ ok: !error, reason: error ? String(error) : null });
      });
    }
  });
});

ipcMain.handle('app:version', () => app.getVersion());
