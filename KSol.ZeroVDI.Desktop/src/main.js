const { app, BrowserWindow, Menu, ipcMain, session, shell } = require('electron');
const path = require('path');
const Store = require('electron-store');

const store = new Store({
  schema: {
    serverUrl: { type: 'string' }
  }
});

let mainWindow = null;
let settingsWindow = null;

// The desktop app is a single-user native client, not a shared browser — the auth cookie should
// always survive a full quit. ASP.NET Identity only sets a real expiry when "Remember me" is
// checked; without it the cookie has none, and Chromium (by design, like closing all browser
// windows) drops such session cookies on process exit even from a persist: partition. Intercept
// the Identity cookie as it's set and give it a far-future expiry so it always persists to disk.
const AUTH_COOKIE_NAME = '.AspNetCore.Identity.Application';

function makeCookiesPersistent(ses) {
  ses.cookies.on('changed', (_event, cookie, cause, removed) => {
    if (removed || cookie.name !== AUTH_COOKIE_NAME || cookie.session === false) return;
    const url = `http${cookie.secure ? 's' : ''}://${cookie.domain.replace(/^\./, '')}${cookie.path}`;
    ses.cookies.set({
      url,
      name: cookie.name,
      value: cookie.value,
      domain: cookie.domain,
      path: cookie.path,
      secure: cookie.secure,
      httpOnly: cookie.httpOnly,
      sameSite: cookie.sameSite,
      expirationDate: Math.floor(Date.now() / 1000) + 60 * 60 * 24 * 365
    }).catch(() => {});
  });
}

function normalizeServerUrl(raw) {
  let url = raw.trim();
  if (!/^https?:\/\//i.test(url)) url = `https://${url}`;
  return url.replace(/\/+$/, '');
}

function createSettingsWindow() {
  if (settingsWindow) {
    settingsWindow.focus();
    return;
  }
  settingsWindow = new BrowserWindow({
    width: 480,
    height: 280,
    resizable: false,
    title: 'ZeroVDI — Server Settings',
    webPreferences: {
      preload: path.join(__dirname, 'settings-preload.js'),
      contextIsolation: true
    }
  });
  settingsWindow.setMenuBarVisibility(false);
  settingsWindow.loadFile(path.join(__dirname, 'settings.html'));
  settingsWindow.on('closed', () => { settingsWindow = null; });
}

function createMainWindow(serverUrl) {
  mainWindow = new BrowserWindow({
    width: 1400,
    height: 900,
    title: 'ZeroVDI',
    webPreferences: {
      contextIsolation: true,
      sandbox: true,
      // Persistent partition so ASP.NET Identity auth cookies survive across app restarts.
      partition: 'persist:zerovdi'
    }
  });

  makeCookiesPersistent(mainWindow.webContents.session);

  Menu.setApplicationMenu(buildAppMenu());

  mainWindow.loadURL(serverUrl);

  // Keep external links (docs, "forgot password" mailtos, etc.) out of the app window.
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (!url.startsWith(serverUrl)) {
      shell.openExternal(url);
      return { action: 'deny' };
    }
    return { action: 'allow' };
  });

  mainWindow.on('closed', () => { mainWindow = null; });
}

function buildAppMenu() {
  const template = [
    {
      label: 'ZeroVDI',
      submenu: [
        { role: 'about' },
        { type: 'separator' },
        {
          label: 'Change Server…',
          click: () => {
            if (mainWindow) { mainWindow.close(); }
            createSettingsWindow();
          }
        },
        { type: 'separator' },
        { role: 'quit' }
      ]
    },
    {
      label: 'Edit',
      submenu: [
        { role: 'undo' }, { role: 'redo' }, { type: 'separator' },
        { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }
      ]
    },
    {
      label: 'View',
      submenu: [
        { role: 'reload' },
        { role: 'forceReload' },
        { role: 'toggleDevTools' },
        { type: 'separator' },
        { role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' },
        { type: 'separator' },
        { role: 'togglefullscreen' }
      ]
    },
    {
      label: 'Window',
      submenu: [{ role: 'minimize' }, { role: 'close' }]
    }
  ];
  return Menu.buildFromTemplate(template);
}

function launch() {
  const saved = store.get('serverUrl');
  if (saved) {
    createMainWindow(saved);
  } else {
    createSettingsWindow();
  }
}

ipcMain.handle('settings:save-server-url', (_event, rawUrl) => {
  const url = normalizeServerUrl(rawUrl);
  store.set('serverUrl', url);
  if (settingsWindow) { settingsWindow.close(); }
  createMainWindow(url);
  return url;
});

ipcMain.handle('settings:get-server-url', () => store.get('serverUrl') || '');

app.whenReady().then(launch);

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) launch();
});
