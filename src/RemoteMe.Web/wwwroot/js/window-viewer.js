// Visionneuse « par fenêtre » : affiche les images PNG envoyées par l'agent et renvoie
// le clavier et la souris. Pas de dépendance externe.

const stage = document.getElementById('stage');
const canvas = document.getElementById('screen');
const ctx = canvas.getContext('2d', { alpha: false });
const toolbar = document.getElementById('toolbar');
const handle = document.getElementById('toolbar-handle');
const stateDot = document.getElementById('state-dot');
const appNameEl = document.getElementById('app-name');
const overlay = document.getElementById('overlay');
const overlayTitle = document.getElementById('overlay-title');
const overlayText = document.getElementById('overlay-text');
const overlayActions = document.getElementById('overlay-actions');
const statusLine = document.getElementById('status-line');

const appId = new URLSearchParams(location.search).get('app');

let socket = null;
let connected = false;
let failed = false;
let decoding = false;
let pending = null;
let toolbarTimer = 0;
let lastMove = 0;

// ---------- Superposition ----------

function showOverlay(kind, title, text) {
  overlay.hidden = false;
  overlay.className = `overlay ${kind}`;
  overlayTitle.textContent = title;
  overlayText.textContent = text;
  overlayActions.hidden = kind === 'loading';
}

function hideOverlay() {
  overlay.hidden = true;
}

function setState(kind) {
  stateDot.className = `dot ${kind}`;
}

function fail(message) {
  if (failed) return;
  failed = true;
  setState('danger');
  showOverlay('error', 'Connexion interrompue', message);
}

function showEnded() {
  if (failed) return;
  setState('');
  showOverlay('ended', 'Application fermée', 'Vous pouvez la rouvrir ou revenir au catalogue.');
}

// ---------- Barre d'outils ----------

function openToolbar(autoHide = true) {
  toolbar.classList.add('open');
  clearTimeout(toolbarTimer);
  if (autoHide) toolbarTimer = setTimeout(() => toolbar.classList.remove('open'), 3500);
}

handle.addEventListener('mouseenter', () => openToolbar());
handle.addEventListener('click', () => openToolbar());
toolbar.addEventListener('mouseenter', () => openToolbar(false));
toolbar.addEventListener('mouseleave', () => openToolbar());

document.getElementById('btn-fullscreen').addEventListener('click', () => {
  if (document.fullscreenElement) document.exitFullscreen();
  else document.documentElement.requestFullscreen?.().catch(() => {});
});
document.getElementById('btn-close').addEventListener('click', () => socket?.close());
document.getElementById('btn-retry').addEventListener('click', () => location.reload());

// ---------- Affichage ----------

function fit() {
  const scale = Math.min(stage.clientWidth / canvas.width, stage.clientHeight / canvas.height, 1);
  canvas.style.width = `${Math.round(canvas.width * scale)}px`;
  canvas.style.height = `${Math.round(canvas.height * scale)}px`;
}

window.addEventListener('resize', fit);

function queueFrame(blob) {
  // Seule la dernière image compte : si le décodage prend du retard, on saute les intermédiaires.
  pending = blob;
  if (decoding) return;
  decoding = true;
  (async () => {
    try {
      while (pending) {
        const blobToDraw = pending;
        pending = null;
        const bitmap = await createImageBitmap(blobToDraw);
        if (canvas.width !== bitmap.width || canvas.height !== bitmap.height) {
          canvas.width = bitmap.width;
          canvas.height = bitmap.height;
          fit();
        }
        ctx.drawImage(bitmap, 0, 0);
        bitmap.close?.();
        if (!connected) {
          connected = true;
          hideOverlay();
          setState('ok');
          openToolbar();
          stage.focus();
        }
      }
    } catch {
      // Image illisible : ignorée.
    } finally {
      decoding = false;
    }
  })();
}

function handleMeta(text) {
  let meta;
  try {
    meta = JSON.parse(text);
  } catch {
    return;
  }

  if (meta.type === 'error') {
    fail(meta.message || 'Erreur du serveur.');
    return;
  }
  if (meta.exit) {
    showEnded();
    socket?.close();
    return;
  }
  if (meta.title) {
    appNameEl.textContent = meta.title;
    document.title = `${meta.title} · RemoteMe`;
  }
  if (meta.echo !== undefined) {
    statusLine.hidden = false;
    statusLine.textContent = `Dernière entrée : ${meta.echo}${meta.keys !== undefined ? ` · touches : ${meta.keys}` : ''}`;
  }
}

// ---------- Entrées ----------

function toImageCoords(event) {
  const rect = canvas.getBoundingClientRect();
  const x = Math.round((event.clientX - rect.left) * (canvas.width / rect.width));
  const y = Math.round((event.clientY - rect.top) * (canvas.height / rect.height));
  return {
    x: Math.max(0, Math.min(canvas.width - 1, x)),
    y: Math.max(0, Math.min(canvas.height - 1, y)),
  };
}

function send(message) {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify(message));
  }
}

function uiHasFocus() {
  const el = document.activeElement;
  return el instanceof HTMLButtonElement || el instanceof HTMLAnchorElement;
}

canvas.addEventListener('mousemove', (event) => {
  const now = performance.now();
  if (now - lastMove < 16) return; // ~60 évènements/s au plus
  lastMove = now;
  const { x, y } = toImageCoords(event);
  send({ t: 'm', x, y, b: event.buttons });
});

canvas.addEventListener('mousedown', (event) => {
  stage.focus();
  const { x, y } = toImageCoords(event);
  send({ t: 'm', x, y, b: event.buttons });
  event.preventDefault();
});

window.addEventListener('mouseup', (event) => {
  const { x, y } = toImageCoords(event);
  send({ t: 'm', x, y, b: event.buttons });
});

canvas.addEventListener('contextmenu', (event) => event.preventDefault());

canvas.addEventListener('wheel', (event) => {
  const { x, y } = toImageCoords(event);
  send({ t: 'w', x, y, d: event.deltaY < 0 ? 120 : -120 });
  event.preventDefault();
}, { passive: false });

window.addEventListener('keydown', (event) => {
  if (uiHasFocus()) return;
  send({ t: 'k', d: true, c: event.code, k: event.key.length === 1 ? event.key : '' });
  event.preventDefault();
});

window.addEventListener('keyup', (event) => {
  if (uiHasFocus()) return;
  send({ t: 'k', d: false, c: event.code, k: '' });
  event.preventDefault();
});

// ---------- Connexion ----------

function connect() {
  const dpr = window.devicePixelRatio || 1;
  const width = Math.round(stage.clientWidth * dpr);
  const height = Math.round(stage.clientHeight * dpr);
  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const url = `${scheme}//${location.host}/winstream?app=${encodeURIComponent(appId)}&width=${width}&height=${height}`;

  socket = new WebSocket(url);
  socket.binaryType = 'blob';
  socket.onopen = () => setState('warn');
  socket.onmessage = (event) => {
    if (typeof event.data === 'string') handleMeta(event.data);
    else queueFrame(event.data);
  };
  socket.onclose = () => {
    if (!connected && !failed) fail("La connexion au serveur a échoué.");
    else showEnded();
  };
}

window.addEventListener('pagehide', () => socket?.close());

async function start() {
  if (!appId) {
    fail("Le lien vers l'application est incorrect.");
    return;
  }

  showOverlay('loading', "Démarrage de l'application…", 'Lancement de votre instance, cela peut prendre quelques secondes.');
  try {
    const response = await fetch(`api/apps/${encodeURIComponent(appId)}`);
    if (response.ok) {
      const app = await response.json();
      appNameEl.textContent = app.name;
      document.title = `${app.name} · RemoteMe`;
    }
  } catch {
    // Le nom sera complété par les métadonnées de l'agent.
  }

  connect();
}

start();
