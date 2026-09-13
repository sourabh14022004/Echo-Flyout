const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('dashboard', {
  getSettings: () => ipcRenderer.invoke('settings:get'),
  setSettings: (partial) => ipcRenderer.invoke('settings:set', partial),
  listDisplays: () => ipcRenderer.invoke('displays:list'),
  getStartupEnabled: () => ipcRenderer.invoke('startup:get'),
  setStartupEnabled: (enabled) => ipcRenderer.invoke('startup:set', enabled),
  getAppVersion: () => ipcRenderer.invoke('app:version'),
});
