// Catalogue : liste des applications et état de l'environnement du visiteur.

const groupsEl = document.getElementById('groups');
const emptyEl = document.getElementById('empty');
const searchEl = document.getElementById('search');
const envDot = document.getElementById('env-dot');
const envText = document.getElementById('env-text');
const envDetail = document.getElementById('env-detail');
const resetBtn = document.getElementById('reset');

let apps = [];

const timeFormat = new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' });

function hueOf(text) {
  let hash = 0;
  for (const ch of text) hash = (hash * 31 + ch.codePointAt(0)) >>> 0;
  return hash % 360;
}

function initials(name) {
  const words = name.trim().split(/[\s\-_]+/).filter(Boolean);
  const letters = words.length > 1 ? words[0][0] + words[1][0] : name.slice(0, 2);
  return letters.toUpperCase();
}

function renderIcon(app) {
  const icon = document.createElement('div');
  icon.className = 'app-icon';
  icon.style.setProperty('--hue', String(hueOf(app.id)));
  if (app.icon) {
    const img = document.createElement('img');
    img.src = app.icon;
    img.alt = '';
    img.loading = 'lazy';
    img.addEventListener('error', () => {
      img.remove();
      icon.textContent = initials(app.name);
    });
    icon.append(img);
  } else {
    icon.textContent = initials(app.name);
  }
  return icon;
}

function renderCard(app) {
  const card = document.createElement('a');
  card.className = 'app-card';
  // Deux visionneuses : RDP via guacd (app.html) ou diffusion par fenêtre via l'agent (win.html).
  const page = app.backend === 'window' ? 'win.html' : 'app.html';
  card.href = `${page}?app=${encodeURIComponent(app.id)}`;
  // Chaque application dans son onglet : on peut en ouvrir plusieurs dans le même environnement.
  card.target = '_blank';
  card.rel = 'noopener';

  const meta = document.createElement('div');
  meta.className = 'app-meta';
  const name = document.createElement('p');
  name.className = 'app-name';
  name.textContent = app.name;
  meta.append(name);
  if (app.description) {
    const desc = document.createElement('p');
    desc.className = 'app-desc';
    desc.textContent = app.description;
    meta.append(desc);
  }

  card.append(renderIcon(app), meta);
  return card;
}

function render() {
  const query = searchEl.value.trim().toLocaleLowerCase('fr');
  const visible = apps.filter((app) =>
    !query || `${app.name} ${app.description ?? ''} ${app.category ?? ''}`.toLocaleLowerCase('fr').includes(query));

  const groups = new Map();
  for (const app of visible) {
    const key = app.category || 'Applications';
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(app);
  }

  groupsEl.replaceChildren(...[...groups.entries()]
    .sort(([a], [b]) => a.localeCompare(b, 'fr'))
    .map(([category, list]) => {
      const section = document.createElement('section');
      section.className = 'group';
      const title = document.createElement('h2');
      title.textContent = category;
      const grid = document.createElement('div');
      grid.className = 'grid';
      grid.append(...list.sort((a, b) => a.name.localeCompare(b.name, 'fr')).map(renderCard));
      section.append(title, grid);
      return section;
    }));

  emptyEl.textContent = apps.length === 0
    ? "Aucune application n'est publiée pour le moment."
    : 'Aucune application ne correspond.';
  emptyEl.hidden = visible.length > 0;
}

function renderEnvironment(state) {
  const env = state.environment;
  const pool = state.pool;
  resetBtn.hidden = !env;

  if (env) {
    const count = env.activeConnections;
    envDot.className = 'dot ok';
    envText.textContent = `Environnement ${env.shortId} · ${count === 0 ? 'en veille' : `${count} application${count > 1 ? 's' : ''} ouverte${count > 1 ? 's' : ''}`}`;
    envDetail.textContent = count === 0
      ? `Votre environnement ${env.shortId} sera effacé à ${timeFormat.format(new Date(env.expiresAt))} si vous n'ouvrez plus d'application.`
      : `Votre environnement ${env.shortId} est actif jusqu'à ${timeFormat.format(new Date(env.expiresAt))} au plus tard.`;
  } else if (pool.total === 0) {
    envDot.className = 'dot danger';
    envText.textContent = 'Service non configuré';
    envDetail.textContent = "Aucun environnement n'est configuré sur ce serveur.";
  } else if (pool.available === 0) {
    envDot.className = 'dot warn';
    envText.textContent = 'Tous les environnements sont occupés';
    envDetail.textContent = 'Revenez dans quelques minutes : un environnement se libère dès qu\'un visiteur part.';
  } else {
    envDot.className = 'dot';
    envText.textContent = `${pool.available} environnement${pool.available > 1 ? 's' : ''} disponible${pool.available > 1 ? 's' : ''}`;
    envDetail.textContent = 'Un environnement vous sera attribué à l\'ouverture de votre première application.';
  }
}

async function refreshEnvironment() {
  try {
    const response = await fetch('api/environment', { credentials: 'same-origin' });
    if (response.ok) renderEnvironment(await response.json());
  } catch {
    envDot.className = 'dot danger';
    envText.textContent = 'Serveur injoignable';
  }
}

async function loadApps() {
  try {
    const response = await fetch('api/apps');
    apps = (await response.json()).apps;
  } catch {
    apps = [];
  }
  render();
}

resetBtn.addEventListener('click', async () => {
  if (!confirm('Fermer toutes vos applications et effacer votre environnement ?')) return;
  resetBtn.disabled = true;
  try {
    await fetch('api/environment/reset', { method: 'POST', credentials: 'same-origin' });
  } finally {
    resetBtn.disabled = false;
    refreshEnvironment();
  }
});

searchEl.addEventListener('input', render);
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) refreshEnvironment();
});

loadApps();
refreshEnvironment();
setInterval(refreshEnvironment, 20000);
