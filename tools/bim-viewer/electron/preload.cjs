const { contextBridge, ipcRenderer, webUtils } = require('electron');
contextBridge.exposeInMainWorld('desktopApp', {
  getInfo: () => ipcRenderer.invoke('bim:desktop-info'),
  setLanguage: language => ipcRenderer.invoke('bim:desktop-language', language),
  checkUpdates: () => ipcRenderer.invoke('bim:update-check'),
  chooseUpdateSource: () => ipcRenderer.invoke('bim:update-source'),
  useOfficialUpdates: () => ipcRenderer.invoke('bim:update-official'),
  installUpdate: () => ipcRenderer.invoke('bim:update-install'),
  getRevitStatus: () => ipcRenderer.invoke('bim:revit-status'),
  installRevitPlugin: () => ipcRenderer.invoke('bim:revit-install'),
  uninstallRevitPlugin: () => ipcRenderer.invoke('bim:revit-uninstall'),
  onRevitStatus: callback => {
    const listener = (_event, state) => callback(state);
    ipcRenderer.on('bim:revit-state', listener);
    return () => ipcRenderer.removeListener('bim:revit-state', listener);
  },
  onUpdate: callback => {
    const listener = (_event, state) => callback(state);
    ipcRenderer.on('bim:update-state', listener);
    return () => ipcRenderer.removeListener('bim:update-state', listener);
  },
});
contextBridge.exposeInMainWorld('localBim', {
  openArchiveEntry: (file, choice) => ipcRenderer.invoke('bim:open-archive-entry', {
    ...(typeof file.cacheId === 'string' ? { cacheId: file.cacheId } : { input: webUtils.getPathForFile(file) }),
    index: choice.index, kind: choice.kind, name: choice.name,
  }),
  cancelArchiveRead: () => ipcRenderer.send('bim:cancel-archive-read'),
  onProgress: (callback) => { ipcRenderer.on('bim:archive-read-progress', (_event, progress) => callback(progress)); },
});
