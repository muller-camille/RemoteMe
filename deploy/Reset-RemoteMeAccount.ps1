<#
.SYNOPSIS
    Remet à zéro un compte du pool RemoteMe après le départ d'un visiteur.

.DESCRIPTION
    Appelé par RemoteMe (RemoteMe:Environments:ResetCommand) à chaque libération d'environnement :
      1. ferme toutes les sessions RDS du compte (active ou déconnectée) ;
      2. avec -RemoveProfile, supprime son profil Windows, pour que le visiteur suivant reparte
         d'un profil vierge.
    Code de sortie 0 = compte réutilisable ; toute autre valeur = compte tenu à l'écart et
    nouvelle tentative plus tard (le visiteur suivant ne reçoit jamais un compte mal nettoyé).

    Droits nécessaires pour l'identité du pool IIS (compte de service dédié recommandé, par exemple
    un gMSA) : administrateur des hôtes de session (fermeture de session et suppression de profil).

.EXAMPLE
    .\Reset-RemoteMeAccount.ps1 -UserName kiosk07 -Server rds01,rds02 -RemoveProfile

.EXAMPLE
    # Collection avec Connection Broker : les sessions sont trouvées via le broker.
    .\Reset-RemoteMeAccount.ps1 -UserName kiosk07 -ConnectionBroker broker.contoso.local -RemoveProfile
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')]
    [string] $UserName,

    # Hôtes de session à nettoyer (ignoré si -ConnectionBroker est fourni, sauf pour les profils).
    [string[]] $Server = @('localhost'),

    [string] $ConnectionBroker,

    [switch] $RemoveProfile,

    [int] $TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

function Get-QuserSessions([string] $Computer) {
    # quser renvoie un code non nul quand il n'y a aucune session : ce n'est pas une erreur.
    $lines = & quser.exe "/server:$Computer" 2>$null
    foreach ($line in $lines | Select-Object -Skip 1) {
        $parts = ($line.TrimStart('>', ' ')) -split '\s+'
        if ($parts.Count -lt 3) { continue }
        # Colonnes : UTILISATEUR [SESSION] ID ÉTAT ... (SESSION est vide pour une session déconnectée)
        $id = $parts | Select-Object -Skip 1 | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1
        if ($id) { [pscustomobject]@{ User = $parts[0]; Id = [int]$id; Server = $Computer } }
    }
}

function Wait-Until([scriptblock] $Condition, [string] $What) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw "Délai dépassé : $What" }
        Start-Sleep -Seconds 2
    }
}

try {
    $hosts = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($s in $Server) { [void]$hosts.Add($s) }

    # 1. Fermeture des sessions
    if ($ConnectionBroker) {
        Import-Module RemoteDesktop
        $sessions = Get-RDUserSession -ConnectionBroker $ConnectionBroker |
            Where-Object { $_.UserName -ieq $UserName }
        foreach ($session in $sessions) {
            Write-Output "Fermeture de la session $($session.UnifiedSessionId) sur $($session.HostServer)"
            Invoke-RDUserLogoff -HostServer $session.HostServer -UnifiedSessionID $session.UnifiedSessionId -Force
            [void]$hosts.Add($session.HostServer)
        }
        Wait-Until { -not (Get-RDUserSession -ConnectionBroker $ConnectionBroker | Where-Object { $_.UserName -ieq $UserName }) } `
            "fermeture des sessions de $UserName"
    }
    else {
        foreach ($computer in $hosts) {
            foreach ($session in Get-QuserSessions $computer | Where-Object { $_.User -ieq $UserName }) {
                Write-Output "Fermeture de la session $($session.Id) sur $computer"
                & logoff.exe $session.Id "/server:$computer"
            }
            Wait-Until { -not (Get-QuserSessions $computer | Where-Object { $_.User -ieq $UserName }) } `
                "fermeture des sessions de $UserName sur $computer"
        }
    }

    # 2. Suppression du profil (une fois déchargé)
    if ($RemoveProfile) {
        $pattern = '\\' + [regex]::Escape($UserName) + '(\.[^\\]+)?$'
        foreach ($computer in $hosts) {
            $cim = if ($computer -ieq 'localhost') { @{} } else { @{ ComputerName = $computer } }
            $profiles = { Get-CimInstance @cim -ClassName Win32_UserProfile -Filter 'Special = FALSE' |
                Where-Object { $_.LocalPath -match $pattern } }
            Wait-Until { -not (& $profiles | Where-Object Loaded) } "déchargement du profil de $UserName sur $computer"
            foreach ($userProfile in & $profiles) {
                Write-Output "Suppression du profil $($userProfile.LocalPath) sur $computer"
                Remove-CimInstance -InputObject $userProfile
            }
        }
    }

    Write-Output "Compte $UserName réinitialisé."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
