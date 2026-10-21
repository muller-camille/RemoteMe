// Visionneuse : affiche une application distante (RemoteApp via guacd) dans la page.

import Guacamole from '../lib/guacamole-common-js/guacamole-common.min.js';

const stage = document.getElementById('stage');
const toolbar = document.getElementById('toolbar');
const handle = document.getElementById('toolbar-handle');
const stateDot = document.getElementById('state-dot');
const appNameEl = document.getElementById('app-name');
const overlay = document.getElementById('overlay');
const overlayTitle = document.getElementById('overlay-title');
const overlayText = document.getElementById('overlay-text');
const overlayActions = document.getElementById('overlay-actions');
const clipboardBtn = document.getElementById('btn-clipboard');
const clipboardPanel = document.getElementById('clipboard-panel');
const clipboardText = document.getElementById('clipboard-text');

const appId = new URLSearchParams(location.search).get('app');

// Messages par code de statut Guacamole (renvoyé par le serveur ou par guacd).
const MESSAGES = {
  0x0100: ['Opération non prise en charge', "Le serveur d'applications a refusé une fonctionnalité demandée."],
  0x0200: ['Erreur du serveur', 'Une erreur interne est survenue. Réessayez dans un instant.'],
  0x0201: ['Tous les environnements sont occupés', 'Le nombre maximal de visiteurs simultanés est atteint. Réessayez dans quelques minutes.'],
  0x0202: ["Le serveur d'applications ne répond pas", 'Il est peut-être surchargé ou en maintenance.'],
  0x0203: ["Erreur du serveur d'applications", "La connexion au serveur d'applications a échoué."],
  0x0204: ['Application introuvable', "Cette application n'est pas (ou plus) publiée."],
  0x0207: ["Serveur d'applications injoignable", 'Le service de diffusion est indisponible pour le moment.'],
  0x0208: ["Serveur d'applications indisponible", 'Il est saturé ou refuse les connexions pour le moment.'],
  0x0209: ['Session reprise ailleurs', 'Votre environnement a été ouvert dans un autre onglet.'],
  0x020A: ['Environnement expiré', 'Votre environnement a été effacé après une période d\'inactivité ou sa durée maximale.'],
  0x020B: ['Session fermée', "L'application a été fermée."],
  0x0300: ['Requête invalide', "Le lien vers l'application est incorrect."],
  0x0301: ['Accès refusé par le serveur', "Le compte d'environnement a été refusé. Prévenez l'administrateur."],
  0x0303: ['Accès refusé', "Le serveur d'applications refuse l'ouverture de cette application."],
  0x0308: ['Délai dépassé', 'Le navigateur n\'a pas répondu à temps.'],
  0x030D: ['Données trop volumineuses', 'Le navigateur a envoyé trop de données d\'un coup.'],
  0x031D: ['Trop de sessions', 'Trop d\'applications ou d\'environnements sont ouverts depuis votre poste. Fermez-en un.'],
};

let client = null;
let keyboard = null;
let features = {};
let failed = false;
let toolbarTimer = 0;
let lastRemoteClipboard = '';

// ---------- Superposition (chargement, erreur, fin) ----------

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

function fail(code, detail) {
  if (failed) return;
  failed = true;
  const [title, text] = MESSAGES[code] ?? ['Connexion interrompue', detail || 'La connexion a été perdue.'];
  setState('danger');
  showOverlay('error', title, text);
}

function setState(kind) {
  stateDot.className = `dot ${kind}`;
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

document.getElementById('btn-close').addEventListener('click', () => {
  client?.disconnect();
});

document.getElementById('btn-retry').addEventListener('click', () => location.reload());

// ---------- Presse-papiers ----------

function uiHasFocus() {
  const el = document.activeElement;
  return el instanceof HTMLTextAreaElement || el instanceof HTMLInputElement;
}

function sendClipboard(text) {
  if (!client || !features.clipboardPaste) return;
  const stream = client.createClipboardStream('text/plain');
  const writer = new Guacamole.StringWriter(stream);
  writer.sendText(text);
  writer.sendEnd();
}

clipboardBtn.addEventListener('click', () => {
  clipboardPanel.hidden = !clipboardPanel.hidden;
  if (!clipboardPanel.hidden) clipboardText.focus();
});

document.getElementById('clipboard-close').addEventListener('click', () => {
  clipboardPanel.hidden = true;
  stage.focus();
});

document.getElementById('clipboard-send').addEventListener('click', () => {
  sendClipboard(clipboardText.value);
  clipboardPanel.hidden = true;
  stage.focus();
});

// Si le navigateur l'autorise déjà, le presse-papiers local est transmis à chaque retour sur la page.
async function syncLocalClipboard() {
  if (!features.clipboardPaste || !navigator.clipboard?.readText) return;
  try {
    const permission = await navigator.permissions?.query({ name: 'clipboard-read' });
    if (permission && permission.state !== 'granted') return;
    const text = await navigator.clipboard.readText();
    if (text && text !== lastRemoteClipboard) sendClipboard(text);
  } catch {
    // Permission refusée ou API absente : le panneau reste disponible.
  }
}

window.addEventListener('focus', syncLocalClipboard);

// ---------- Connexion ----------

function supportsWebp() {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 1;
  return canvas.toDataURL('image/webp').startsWith('data:image/webp');
}

function stageSize() {
  const density = window.devicePixelRatio || 1;
  return {
    density,
    width: Math.max(320, Math.round(stage.clientWidth * density)),
    height: Math.max(240, Math.round(stage.clientHeight * density)),
  };
}

function connect() {
  const tunnel = new Guacamole.WebSocketTunnel('tunnel');
  client = new Guacamole.Client(tunnel);
  const display = client.getDisplay();
  const element = display.getElement();
  stage.replaceChildren(element);

  // Échelle : 1 pixel distant = 1 pixel physique ; réduit si l'écran distant est plus grand que la page.
  const fit = () => {
    const { density } = stageSize();
    const width = display.getWidth();
    const height = display.getHeight();
    if (!width || !height) return;
    const scale = Math.min(stage.clientWidth / width, stage.clientHeight / height, 1 / density);
    display.scale(scale);
  };
  display.onresize = fit;

  // Curseur distant appliqué comme curseur local : plus réactif qu'un curseur dessiné.
  display.oncursor = (canvas, x, y) => {
    element.style.cursor = `url(${canvas.toDataURL('image/png')}) ${x} ${y}, default`;
  };
  display.showCursor(false);

  client.onstatechange = (state) => {
    switch (state) {
      case 1: // CONNECTING
      case 2: // WAITING
        setState('warn');
        showOverlay('loading', 'Préparation de votre environnement…', "Ouverture de l'application, cela peut prendre quelques secondes.");
        break;
      case 3: // CONNECTED
        setState('ok');
        hideOverlay();
        stage.focus();
        openToolbar();
        break;
      case 5: // DISCONNECTED
        if (!failed) {
          setState('');
          showOverlay('ended', 'Application fermée', 'Vous pouvez la rouvrir ou revenir au catalogue. Votre environnement est conservé quelques minutes.');
        }
        teardownInput();
        break;
      default:
        break;
    }
  };

  client.onerror = (status) => fail(status.code, status.message);
  tunnel.onerror = (status) => fail(status.code, status.message);

  client.onclipboard = (stream, mimetype) => {
    if (!/^text\//.test(mimetype)) {
      stream.sendAck('Format non pris en charge', Guacamole.Status.Code.UNSUPPORTED);
      return;
    }
    const reader = new Guacamole.StringReader(stream);
    let data = '';
    reader.ontext = (text) => { data += text; };
    reader.onend = () => {
      lastRemoteClipboard = data;
      clipboardText.value = data;
      navigator.clipboard?.writeText(data).catch(() => {});
    };
  };

  // Fichiers envoyés par le serveur (impression en PDF, téléchargement depuis le lecteur virtuel).
  client.onfile = (stream, mimetype, filename) => {
    const reader = new Guacamole.BlobReader(stream, mimetype);
    reader.onprogress = () => stream.sendAck('OK', Guacamole.Status.Code.SUCCESS);
    reader.onend = () => {
      const url = URL.createObjectURL(reader.getBlob());
      const link = document.createElement('a');
      link.href = url;
      link.download = filename;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    };
    stream.sendAck('OK', Guacamole.Status.Code.SUCCESS);
  };

  // Souris, écran tactile et clavier.
  const sendMouse = (event) => client.sendMouseState(event.state, true);
  const mouse = new Guacamole.Mouse(element);
  mouse.onEach(['mousedown', 'mousemove', 'mouseup'], sendMouse);
  const touch = new Guacamole.Mouse.Touchscreen(element);
  touch.onEach(['mousedown', 'mousemove', 'mouseup'], sendMouse);

  keyboard = new Guacamole.Keyboard(document);
  keyboard.onkeydown = (keysym) => {
    if (uiHasFocus()) return true;
    client.sendKeyEvent(1, keysym);
    return false;
  };
  keyboard.onkeyup = (keysym) => {
    if (!uiHasFocus()) client.sendKeyEvent(0, keysym);
  };

  // Le contexte audio ne démarre qu'après une interaction de l'utilisateur.
  const resumeAudio = () => Guacamole.AudioContextFactory.getAudioContext()?.resume?.();
  document.addEventListener('pointerdown', resumeAudio, { once: true });
  document.addEventListener('keydown', resumeAudio, { once: true });

  const { width, height, density } = stageSize();
  const query = new URLSearchParams({
    app: appId,
    width: String(width),
    height: String(height),
    dpi: String(Math.round(96 * density)),
  });
  try {
    query.set('tz', Intl.DateTimeFormat().resolvedOptions().timeZone);
  } catch {
    // Fuseau horaire inconnu : celui du serveur sera utilisé.
  }
  if (features.audio) {
    for (const type of Guacamole.AudioPlayer.getSupportedTypes()) query.append('audio', type);
  }
  for (const type of ['image/png', 'image/jpeg']) query.append('image', type);
  if (supportsWebp()) query.append('image', 'image/webp');

  client.connect(query.toString());
}

function teardownInput() {
  if (keyboard) {
    keyboard.onkeydown = null;
    keyboard.onkeyup = null;
    keyboard.reset();
  }
}

// Redimensionnement de la page → nouvelle taille d'écran distante (après une courte pause).
let resizeTimer = 0;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    if (!client) return;
    const { width, height } = stageSize();
    client.sendSize(width, height);
    client.getDisplay().onresize?.();
  }, 250);
});

// Touches relâchées si la page perd le focus (évite une touche « collée » côté serveur).
window.addEventListener('blur', () => keyboard?.reset());
window.addEventListener('pagehide', () => client?.disconnect());

async function start() {
  if (!appId) {
    fail(0x0300);
    return;
  }

  showOverlay('loading', 'Préparation de votre environnement…', "Ouverture de l'application, cela peut prendre quelques secondes.");
  try {
    // L'appel à api/environment crée au besoin le cookie visiteur, indispensable au tunnel.
    const [appResponse, envResponse] = await Promise.all([
      fetch(`api/apps/${encodeURIComponent(appId)}`),
      fetch('api/environment', { credentials: 'same-origin' }),
    ]);
    if (appResponse.status === 404) {
      fail(0x0204);
      return;
    }
    if (!appResponse.ok || !envResponse.ok) throw new Error(`HTTP ${appResponse.status}/${envResponse.status}`);

    const app = await appResponse.json();
    features = (await envResponse.json()).features ?? {};
    appNameEl.textContent = app.name;
    document.title = `${app.name} · RemoteMe`;
    clipboardBtn.hidden = !(features.clipboardCopy || features.clipboardPaste);
  } catch {
    fail(0x0207);
    return;
  }

  connect();
}

start();
