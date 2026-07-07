const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('zerovdiSettings', {
  saveServerUrl: (url) => ipcRenderer.invoke('settings:save-server-url', url),
  getServerUrl: () => ipcRenderer.invoke('settings:get-server-url')
});
