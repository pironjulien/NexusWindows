const {contextBridge,ipcRenderer}=require('electron');
contextBridge.exposeInMainWorld('fixture',{emit:record=>ipcRenderer.send('nexus-fixture-oracle',record)});
