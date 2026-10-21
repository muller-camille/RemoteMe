# RemoteMe

**RemoteApp dans une page web, sans login.** Le visiteur ouvre le catalogue, clique sur une
application : elle s'affiche directement dans son navigateur (aucun client RDP, aucun plugin,
aucun identifiant). Chaque visiteur dispose de **son propre environnement** (session Windows,
profil, fichiers), séparé de celui des autres et effacé après son départ.

- Hébergement : **IIS** (ASP.NET Core 8, hébergement *in-process*).
- Diffusion : protocole **Apache Guacamole** — `guacd` traduit RDP/RemoteApp en un flux
  affiché dans un `<canvas>` par `guacamole-common-js`.
- Applications : les **RemoteApp** d'une collection RDS existante (ou un bureau complet).

## Sommaire

1. [Fonctionnement](#fonctionnement)
2. [Prérequis](#prérequis)
3. [Installation pas à pas](#installation-pas-à-pas)
4. [Le catalogue d'applications](#le-catalogue-dapplications)
5. [Un visiteur = un environnement](#un-visiteur--un-environnement)
6. [Sécurité](#sécurité)
7. [Référence de configuration](#référence-de-configuration)
8. [Développement et tests](#développement-et-tests)
9. [Limites connues](#limites-connues)

## Fonctionnement

```
 Navigateur                     Serveur IIS                        Linux / Docker        Ferme RDS
┌────────────────────┐ HTTPS ┌───────────────────────────┐  TCP  ┌─────────────┐  RDP  ┌────────────────┐
│ index.html         │──────►│ RemoteMe (ASP.NET Core 8) │──────►│   guacd     │──────►│ Hôte de session│
│  catalogue         │       │  /api/apps   catalogue    │ 4822  │ (Guacamole) │ 3389  │  RemoteApp     │
│ app.html           │  WSS  │  /tunnel     WebSocket    │       └─────────────┘       │  kiosk01..N    │
│  visionneuse       │◄─────►│  pool d'environnements    │                             └────────────────┘
│  guacamole-common  │       │  (comptes kiosk01..N)     │
└────────────────────┘       └───────────────────────────┘
```

1. Le navigateur charge le catalogue (`/api/apps`) et reçoit un **cookie visiteur** aléatoire
   (256 bits, `HttpOnly`) : c'est sa seule « identité ».
2. À l'ouverture d'une application, la visionneuse ouvre le tunnel WebSocket `/tunnel?app=<id>`.
3. RemoteMe attribue au visiteur un **compte du pool** (ex. `kiosk07`), libre et réinitialisé,
   puis ouvre via guacd une connexion RDP **RemoteApp** avec ce compte. Le mot de passe ne
   quitte jamais le serveur : le navigateur ne fournit que l'identifiant de l'application et la
   taille de son écran.
4. Le flux Guacamole est relayé dans les deux sens (affichage, son, souris, clavier, écran
   tactile, presse-papiers).
5. Les applications suivantes ouvertes par le même visiteur réutilisent **le même compte** :
   elles partagent ses fichiers et son profil, comme sur un vrai poste.
6. Après le départ du visiteur (inactivité, durée maximale, ou bouton « Réinitialiser mon
   environnement »), le compte est **mis en quarantaine**, ses sessions sont fermées et son
   profil supprimé (script optionnel), avant d'être confié à un autre visiteur.

## Prérequis

| Élément | Détail |
|---|---|
| Serveur web | Windows Server 2016+ avec IIS 10, fonctionnalité **Protocole WebSocket**, [ASP.NET Core 8 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/8.0) |
| guacd | Apache Guacamole **1.3 ou plus récent**, installé en paquet Linux (`apt install guacd`, **sans Docker**) sur une petite VM Linux ou dans WSL2 sur le serveur IIS — ou en conteneur Docker si vous préférez |
| RDS | Un ou plusieurs hôtes de session avec des programmes **RemoteApp** publiés dans une collection |
| Comptes | Un compte Windows standard par visiteur simultané (local à l'hôte de session, ou de domaine) |
| Réseau | IIS → guacd (TCP 4822) ; guacd → hôtes RDS / broker (TCP 3389) ; visiteurs → IIS (HTTPS 443) |
| Certificat | HTTPS obligatoire en production (le tunnel passe en `wss://`) |

## Installation pas à pas

### 1. Démarrer guacd

guacd est le seul composant non Windows : il parle RDP aux hôtes de session et le traduit en
un flux affichable par le navigateur (un navigateur ne sait pas ouvrir une connexion RDP).
**Docker n'est pas nécessaire** : guacd est un simple paquet Linux. Trois façons de l'installer,
au choix :

**a) Paquet natif sur une petite VM Linux** (Debian / Ubuntu) — recommandé en production :

```bash
sudo apt install guacd
sudo tee /etc/guacamole/guacd.conf >/dev/null <<'CONF'
[server]
# Adresse sur laquelle IIS joindra guacd
bind_host = 10.0.0.20
bind_port = 4822
CONF
sudo systemctl enable --now guacd
```

**b) Sur le serveur IIS lui-même, via WSL2** (Windows Server 2022 ou plus récent) — aucune
machine supplémentaire :

```powershell
wsl --install -d Ubuntu            # puis redémarrer
wsl -d Ubuntu -u root -- apt-get install -y guacd
# Tâche planifiée « au démarrage » (compte SYSTEM) pour lancer guacd et garder WSL actif :
$action = New-ScheduledTaskAction -Execute 'wsl.exe' -Argument '-d Ubuntu -u root -- /usr/sbin/guacd -f -b 127.0.0.1 -l 4822'
Register-ScheduledTask -TaskName 'guacd' -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -User 'SYSTEM' -RunLevel Highest
```

IIS joint alors guacd sur `127.0.0.1:4822` (redirection localhost de WSL2), ce qui est la
valeur par défaut de `RemoteMe:Guacd`.

**c) Conteneur Docker**, si Docker est déjà utilisé chez vous :

```bash
cd deploy/guacd
GUACD_BIND=10.0.0.20 docker compose up -d
```

Versions testées : guacd **1.3.0** (paquet Ubuntu 24.04, sans Docker) et **1.5.5** (image
Docker). guacd n'a **aucune authentification** : n'autorisez le port 4822 qu'**en provenance
du serveur IIS** (pare-feu), ou faites-le écouter sur `127.0.0.1` (cas WSL2).

### 2. Créer les comptes du pool

Sur l'hôte de session (comptes locaux, déploiement à un seul serveur) :

```powershell
.\deploy\New-RemoteMePool.ps1 -Count 20 -OutFile C:\Temp\pool.json
```

Pour une collection de plusieurs hôtes, utilisez des comptes de domaine :

```powershell
.\deploy\New-RemoteMePool.ps1 -Count 50 -ActiveDirectory -OrganizationalUnit 'OU=RemoteMe,DC=contoso,DC=local' `
    -Group 'RDS-RemoteMe-Users' -Domain CONTOSO -OutFile C:\Temp\pool.json
```

Le script crée des utilisateurs standard (mot de passe aléatoire de 28 caractères, sans
expiration) et écrit un fragment `RemoteMe:Environments:Accounts` à reporter dans
`appsettings.Production.json`. Le nombre de comptes = **nombre maximal de visiteurs simultanés**.

### 3. Régler la collection RDS pour l'isolation

Voir [Un visiteur = un environnement](#un-visiteur--un-environnement). Au minimum : fermer les
sessions déconnectées après 1 minute, et repartir d'un profil vierge (profil obligatoire, ou
script de réinitialisation avec `-RemoveProfile`).

### 4. Publier et installer sur IIS

```powershell
dotnet publish src/RemoteMe.Web -c Release -o .\publish
.\deploy\Install-RemoteMe.ps1 -SourcePath .\publish -HostName apps.contoso.com -CertificateThumbprint <empreinte>
```

Le script active IIS + WebSocket, vérifie le module ASP.NET Core, copie les fichiers vers
`C:\inetpub\RemoteMe` (sans écraser `appsettings.Production.json` ni `apps.json`), et crée un
pool d'applications **à processus unique, sans recyclage chevauchant ni arrêt sur inactivité**
(le pool d'environnements vit en mémoire et ne doit exister qu'en un seul exemplaire).

### 5. Configurer

Créez `C:\inetpub\RemoteMe\appsettings.Production.json` (le script d'installation en restreint
l'accès aux administrateurs et au pool IIS) :

```json
{
  "RemoteMe": {
    "Guacd": { "Host": "10.0.0.20", "Port": 4822 },
    "Rdp": {
      "Hostname": "broker.contoso.local",
      "Security": "nla",
      "Domain": "CONTOSO",
      "LoadBalanceInfo": "tsv://MS Terminal Services Plugin.1.Bureautique",
      "ServerLayout": "fr-fr-azerty"
    },
    "Environments": {
      "Accounts": [
        { "Username": "kiosk01", "Password": "…" },
        { "Username": "kiosk02", "Password": "…" }
      ],
      "IdleTimeoutMinutes": 15,
      "MaxLifetimeMinutes": 240,
      "ResetCommand": "powershell.exe",
      "ResetArguments": [
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned",
        "-File", "C:\\inetpub\\RemoteMe\\scripts\\Reset-RemoteMeAccount.ps1",
        "-UserName", "{username}", "-ConnectionBroker", "{hostname}", "-RemoveProfile"
      ]
    }
  }
}
```

Alternative aux comptes listés un par un : `UsernamePattern` (`"kiosk{0:00}"`), `PatternCount`
et `SharedPassword` (même mot de passe pour tous, à passer de préférence par la variable
d'environnement `RemoteMe__Environments__SharedPassword` du pool IIS).

### 6. Publier les applications

```powershell
.\deploy\Export-RemoteApps.ps1 -CollectionName Bureautique -ConnectionBroker broker.contoso.local `
    -OutFile C:\inetpub\RemoteMe\apps.json -IconDirectory C:\inetpub\RemoteMe\wwwroot\icons -Merge
```

Détails dans la section suivante.

### 7. Vérifier

- `https://apps.contoso.com/healthz` → `{"status":"ok","guacd":"ok","pool":{…}}`
- `https://apps.contoso.com/` → catalogue ; cliquer sur une application.
- Journaux : observateur d'événements, ou `stdoutLogEnabled="true"` dans `web.config`
  (dossier `logs\`). Chaque attribution, ouverture, fermeture et réinitialisation y est tracée
  avec l'identifiant court de l'environnement et le compte utilisé.

## Le catalogue d'applications

La liste des applications disponibles est le fichier **`apps.json`** à la racine du site.
Il est **relu à chaud** : une modification est prise en compte sans redémarrage.

```json
{
  "Apps": [
    {
      "Id": "excel",
      "Name": "Excel",
      "Description": "Tableur Microsoft Excel.",
      "Category": "Bureautique",
      "Icon": "icons/excel.png",
      "Program": "||EXCEL"
    },
    {
      "Id": "erp",
      "Name": "ERP Comptabilité",
      "Category": "Métier",
      "Program": "||ERP",
      "Arguments": "/societe:01",
      "Hostname": "rds-erp.contoso.local",
      "LoadBalanceInfo": "tsv://MS Terminal Services Plugin.1.Metier"
    },
    {
      "Id": "bureau",
      "Name": "Bureau complet",
      "Program": "",
      "Enabled": false
    }
  ]
}
```

| Champ | Rôle |
|---|---|
| `Id` | Identifiant d'URL (`a-z`, `0-9`, `-`, `_`). Lien direct : `https://…/app.html?app=<Id>` |
| `Name`, `Description`, `Category` | Affichage dans le catalogue (regroupement par catégorie, recherche) |
| `Icon` | Image sous `wwwroot` (ex. `icons/excel.png`). Absente : pastille avec les initiales |
| `Program` | `\|\|alias` = RemoteApp publiée (recommandé). Un chemin complet n'est accepté que si la collection autorise les programmes non publiés. **Vide = bureau complet** |
| `Arguments`, `WorkingDirectory` | Ligne de commande et dossier de travail du programme |
| `Hostname`, `LoadBalanceInfo` | Surchargent `RemoteMe:Rdp` pour cette application (autre collection / autre serveur) |
| `Parameters` | Paramètres guacd supplémentaires pour cette application (ex. `"color-depth": "24"`) |
| `Enabled` | `false` = masquée et inaccessible, sans la supprimer du fichier |

**Comment obtenir la liste des RemoteApp publiées ?** Sur le Connection Broker :

```powershell
Get-RDRemoteApp -CollectionName Bureautique | Select-Object Alias, DisplayName, FilePath
```

L'`Alias` donne `Program = "||<Alias>"`. Le script `deploy/Export-RemoteApps.ps1` fait
cette conversion pour toute la collection, extrait les icônes en PNG et calcule
`LoadBalanceInfo` à partir de l'alias de la collection. Avec `-Merge`, les applications
d'autres collections déjà présentes dans `apps.json` sont conservées (le champ `Collection`
écrit par le script ne sert qu'à ce rapprochement).

## Un visiteur = un environnement

| Moment | Ce qui se passe |
|---|---|
| Première visite | Cookie aléatoire `RemoteMe.Visitor` (aucune donnée personnelle) |
| Première application ouverte | Un compte libre du pool est attribué au visiteur |
| Applications suivantes | Même compte → mêmes fichiers, même profil, même session |
| Visiteur inactif (aucune application ouverte depuis `IdleTimeoutMinutes`) | Environnement libéré |
| Durée maximale (`MaxLifetimeMinutes`) atteinte | Applications fermées, environnement libéré |
| Bouton « Réinitialiser mon environnement » | Applications fermées, environnement libéré, nouveau cookie |
| Après libération | Quarantaine (`CooldownSeconds`) + `ResetCommand` réussie, puis compte réattribuable |
| Pool épuisé | Le visiteur voit « Tous les environnements sont occupés » |

Un compte dont la réinitialisation échoue **reste hors rotation** (nouvelles tentatives
espacées) : mieux vaut un pool réduit qu'un visiteur qui retrouve la session du précédent.
Au démarrage de l'application (recyclage IIS), tous les comptes commencent en quarantaine
pour la même raison.

**Réglages RDS recommandés** (stratégie de groupe appliquée aux hôtes de session, ou
propriétés de la collection) :

- *Définir une limite de temps pour les sessions déconnectées* : **1 minute** — et *Mettre fin
  à la session lorsque les limites de temps sont atteintes* : activé.
- Profil vierge pour chaque visiteur : **profil obligatoire** (`NTUSER.MAN`) pour les comptes du
  pool, ou `Reset-RemoteMeAccount.ps1 -RemoveProfile` (suppression du profil à chaque libération).
- *Ne pas supprimer les dossiers temporaires en quittant* : désactivé (les dossiers temporaires sont supprimés à la fermeture de session).
- *Restreindre les utilisateurs des services Bureau à distance à une seule session* :
  **désactivé** si les visiteurs doivent pouvoir ouvrir plusieurs applications côte à côte
  (chaque onglet est une connexion RDP ; avec la restriction, l'onglet le plus récent reprend
  la session et l'autre affiche « Session reprise ailleurs »). Sinon, réglez
  `MaxConnectionsPerEnvironment` à 1.
- Limiter les programmes exécutables (AppLocker / WDAC) aux applications publiées.

**Droits de la réinitialisation** : `Reset-RemoteMeAccount.ps1` s'exécute sous l'identité du
pool IIS, qui doit pouvoir fermer des sessions et supprimer des profils sur les hôtes de
session. Utilisez un compte de service dédié (idéalement un gMSA) comme identité du pool,
administrateur des seuls hôtes de session.

## Sécurité

L'absence de login est **voulue** : toute personne qui atteint l'URL peut utiliser les
applications publiées, dans un environnement jetable. Les protections portent donc sur ce
qu'un visiteur peut faire, et sur l'isolation entre visiteurs.

- **Identifiants côté serveur uniquement** : le navigateur choisit une application du catalogue
  et la taille de son écran ; hôte, compte, mot de passe et programme sont fixés par la
  configuration. Les valeurs reçues sont bornées et filtrées (taille, DPI, fuseau horaire,
  formats d'image et de son).
- **Comptes à privilèges minimaux** : utilisateurs standard, membres du seul groupe Bureau à
  distance ; ajoutez AppLocker/WDAC pour n'autoriser que les programmes publiés.
- **Redirections désactivées par défaut** : lecteurs, impression, micro. Le presse-papiers
  (activé par défaut) se coupe par `EnableClipboardCopy` / `EnableClipboardPaste`.
- **Limites anti-abus** : environnements simultanés par IP (`MaxEnvironmentsPerIp`), créations
  par IP et par heure (`MaxNewEnvironmentsPerIpPerHour`), applications par environnement
  (`MaxConnectionsPerEnvironment`). Derrière un reverse proxy, déclarez-le dans `KnownProxies`
  pour que l'IP réelle du visiteur soit prise en compte. Derrière un NAT d'entreprise, de
  nombreux visiteurs partagent une IP : augmentez `MaxEnvironmentsPerIp` en conséquence.
- **Navigateur** : cookie `HttpOnly` / `SameSite=Lax` / `Secure` en HTTPS ; vérification de
  l'en-tête `Origin` sur le tunnel et la réinitialisation (une page tierce ne peut pas ouvrir
  le tunnel avec le cookie d'un visiteur) ; `Content-Security-Policy` stricte, sans script
  ni style en ligne ; `X-Frame-Options: DENY`.
- **Réseau** : HTTPS obligatoire en production ; guacd joignable uniquement depuis IIS.
- **Secrets** : `appsettings.Production.json` lisible seulement par les administrateurs et le
  pool IIS (ou variables d'environnement du pool). Il est exclu du dépôt (`.gitignore`).
- Pour restreindre l'accès à certains réseaux, utilisez *Restrictions d'adresses IP et de
  domaines* d'IIS.

## Référence de configuration

Section `RemoteMe` de `appsettings.json` / `appsettings.Production.json` (ou variables
d'environnement `RemoteMe__Section__Clé`). Les modifications de cette section nécessitent un
redémarrage du pool IIS ; `apps.json` est relu à chaud.

| Clé | Défaut | Rôle |
|---|---|---|
| `Guacd:Host` / `Guacd:Port` | `127.0.0.1` / `4822` | Adresse de guacd |
| `Guacd:ConnectTimeoutSeconds` | `15` | Délai de connexion et de poignée de main |
| `Rdp:Hostname` / `Rdp:Port` | — / `3389` | Hôte de session ou Connection Broker |
| `Rdp:Security` | `nla` | `any`, `nla`, `nla-ext`, `tls`, `rdp`, `vmconnect` |
| `Rdp:IgnoreCertificate` | `false` | Accepter un certificat RDP non approuvé par guacd |
| `Rdp:Domain` | — | Domaine des comptes (si non précisé par compte) |
| `Rdp:LoadBalanceInfo` | — | `tsv://MS Terminal Services Plugin.1.<AliasCollection>` derrière un broker |
| `Rdp:ServerLayout` | `fr-fr-azerty` | Disposition clavier du serveur (`en-us-qwerty`, `de-de-qwertz`, …) |
| `Rdp:ResizeMethod` | `display-update` | Adaptation de la résolution à la fenêtre du navigateur |
| `Rdp:EnableAudio` / `EnableAudioInput` | `true` / `false` | Son / micro |
| `Rdp:EnablePrinting` | `false` | Impression vers un PDF téléchargé |
| `Rdp:EnableClipboardCopy` / `EnableClipboardPaste` | `true` / `true` | Presse-papiers serveur → navigateur / navigateur → serveur |
| `Rdp:EnableDrive` / `DriveRootPath` | `false` / `/var/lib/guacamole/drives` | Lecteur virtuel (un dossier par environnement, côté guacd) |
| `Rdp:ExtraParameters` | `{}` | Tout [paramètre RDP de guacd](https://guacamole.apache.org/doc/gug/configuring-guacamole.html#rdp) |
| `Environments:Accounts` | `[]` | Comptes `{ Username, Password, Domain? }` |
| `Environments:UsernamePattern` / `PatternStart` / `PatternCount` / `SharedPassword` | — / `1` / `0` / — | Comptes générés par motif |
| `Environments:IdleTimeoutMinutes` | `15` | Libération sans application ouverte |
| `Environments:MaxLifetimeMinutes` | `240` | Durée de vie maximale |
| `Environments:CooldownSeconds` | `90` | Quarantaine avant réattribution |
| `Environments:MaxConnectionsPerEnvironment` | `3` | Applications simultanées par visiteur |
| `Environments:MaxEnvironmentsPerIp` | `3` | Environnements simultanés par IP (`0` = illimité) |
| `Environments:MaxNewEnvironmentsPerIpPerHour` | `20` | Créations par IP et par heure (`0` = illimité) |
| `Environments:ResetCommand` / `ResetArguments` | — | Commande de réinitialisation ; jetons `{username}`, `{domain}`, `{hostname}` |
| `Environments:ResetTimeoutSeconds` | `120` | Délai maximal de la réinitialisation |
| `KnownProxies` | `[]` | IP des reverse proxies de confiance (`X-Forwarded-For`) |

## Développement et tests

```bash
dotnet test                      # protocole, pool, paramètres RDP, tunnel complet avec un faux guacd
dotnet run --project src/RemoteMe.Web
```

Essai de bout en bout sans infrastructure Windows, avec un serveur RDP Linux (xrdp) :

```bash
docker network create rm-e2e
docker run -d --name rm-xrdp --network rm-e2e --shm-size=1g scottyhardy/docker-remote-desktop
docker exec rm-xrdp sh -c 'for u in kiosk01 kiosk02; do useradd -m -s /bin/bash $u && echo "$u:Kiosk-Pass-1" | chpasswd; done'
docker run -d --name rm-guacd --network rm-e2e -p 127.0.0.1:4822:4822 guacamole/guacd:1.5.5

dotnet run --project src/RemoteMe.Web -- \
  --RemoteMe:Rdp:Hostname=rm-xrdp --RemoteMe:Rdp:Security=any --RemoteMe:Rdp:IgnoreCertificate=true \
  --RemoteMe:Rdp:ServerLayout=en-us-qwerty \
  --RemoteMe:Environments:UsernamePattern="kiosk{0:00}" --RemoteMe:Environments:PatternCount=2 \
  --RemoteMe:Environments:SharedPassword=Kiosk-Pass-1 --RemoteMe:Environments:CooldownSeconds=0 \
  --Apps:4:Id=desktop --Apps:4:Name="Bureau Linux (test)" --Apps:4:Program= --Apps:4:Enabled=true
```

Puis `http://localhost:5051/app.html?app=desktop` : deux navigateurs différents obtiennent
`kiosk01` et `kiosk02` ; un troisième voit « Tous les environnements sont occupés ».
(xrdp ne gère pas RemoteApp : ce banc d'essai affiche un bureau complet.)

Structure :

```
src/RemoteMe.Web/
  Program.cs                 points d'entrée HTTP, en-têtes de sécurité
  Catalog/                   catalogue apps.json (rechargé à chaud)
  Environments/              pool de comptes, expiration, réinitialisation
  Guacamole/                 protocole, poignée de main guacd, tunnel WebSocket
  Http/                      cookie visiteur, contrôle d'origine
  wwwroot/                   catalogue (index.html) et visionneuse (app.html)
deploy/                      IIS, comptes du pool, réinitialisation, export RemoteApp, guacd
tests/RemoteMe.Tests/        tests unitaires et d'intégration
```

## Limites connues

- **Un onglet = une connexion RDP.** guacd ne regroupe pas plusieurs RemoteApp dans une même
  connexion comme le client Windows : chaque application ouverte est un onglet distinct (voir
  la restriction « une seule session par utilisateur » plus haut).
- Les fenêtres RemoteApp sont dessinées dans la zone de la page, sur fond noir ; elles se
  déplacent et se redimensionnent à l'intérieur de cette zone.
- **État en mémoire** : un seul processus IIS (configuré par le script d'installation). Une
  ferme de plusieurs serveurs IIS nécessiterait un pool partagé (non fourni) ou une affinité de
  session et des pools de comptes distincts par serveur.
- Presse-papiers navigateur → application : par le panneau « Presse-papiers », ou
  automatiquement si le navigateur a déjà accordé l'autorisation de lecture du presse-papiers.
- Le son nécessite une première interaction (clic ou touche) avec la page.

---

`wwwroot/lib/guacamole-common-js` : client Apache Guacamole 1.5.0, licence Apache 2.0.
