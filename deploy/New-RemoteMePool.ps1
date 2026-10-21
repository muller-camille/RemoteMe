#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Crée (ou réinitialise) les comptes Windows du pool d'environnements RemoteMe.

.DESCRIPTION
    Chaque visiteur anonyme reçoit l'un de ces comptes pendant sa visite : c'est ce qui lui donne
    une session, un profil et des fichiers séparés de ceux des autres visiteurs.
    Les comptes sont des utilisateurs standard, membres du seul groupe autorisé à ouvrir une session
    RDS, avec un mot de passe aléatoire long qui n'expire pas.

    Le script écrit un fragment de configuration (section RemoteMe:Environments:Accounts) dans
    -OutFile : copiez-le dans appsettings.Production.json puis supprimez le fichier.

.EXAMPLE
    # Comptes locaux sur l'hôte de session RDS (déploiement à un seul serveur)
    .\New-RemoteMePool.ps1 -Count 20 -OutFile C:\Temp\pool.json

.EXAMPLE
    # Comptes de domaine, pour une collection de plusieurs hôtes de session
    .\New-RemoteMePool.ps1 -Count 50 -ActiveDirectory -OrganizationalUnit 'OU=RemoteMe,DC=contoso,DC=local' `
        -Group 'RDS-RemoteMe-Users' -Domain CONTOSO -OutFile C:\Temp\pool.json
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Prefix = 'kiosk',

    [ValidateRange(1, 999)]
    [int] $Count = 10,

    [int] $Start = 1,

    [switch] $ActiveDirectory,

    [string] $OrganizationalUnit,

    # Groupe AD autorisé sur la collection RDS (comptes de domaine uniquement).
    [string] $Group,

    [string] $Domain,

    [Parameter(Mandatory)]
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'

$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()

# Entier aléatoire uniforme dans [0, max[ (compatible Windows PowerShell 5.1).
function Get-RandomIndex([int] $Max) {
    $bytes = New-Object byte[] 4
    $limit = [uint32]::MaxValue - ([uint32]::MaxValue % [uint32]$Max)
    do {
        $rng.GetBytes($bytes)
        $value = [BitConverter]::ToUInt32($bytes, 0)
    } while ($value -ge $limit)
    [int]($value % [uint32]$Max)
}

function New-RandomPassword([int] $Length = 28) {
    $sets = @('ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '!#%+-=?@_')
    $all = -join $sets
    $chars = New-Object 'System.Collections.Generic.List[char]'
    # Au moins un caractère de chaque famille, pour toute stratégie de complexité.
    foreach ($set in $sets) { $chars.Add($set[(Get-RandomIndex $set.Length)]) }
    while ($chars.Count -lt $Length) { $chars.Add($all[(Get-RandomIndex $all.Length)]) }
    # Mélange de Fisher-Yates
    for ($i = $chars.Count - 1; $i -gt 0; $i--) {
        $j = Get-RandomIndex ($i + 1)
        $tmp = $chars[$i]; $chars[$i] = $chars[$j]; $chars[$j] = $tmp
    }
    -join $chars
}

if ($ActiveDirectory) {
    Import-Module ActiveDirectory
    if (-not $Group) { throw "-Group est obligatoire avec -ActiveDirectory (groupe autorisé sur la collection RDS)." }
}

$digits = [Math]::Max(2, ($Start + $Count - 1).ToString().Length)
$accounts = @()

for ($n = $Start; $n -lt $Start + $Count; $n++) {
    $name = $Prefix + $n.ToString().PadLeft($digits, '0')
    $password = New-RandomPassword
    $secure = ConvertTo-SecureString $password -AsPlainText -Force

    if (-not $PSCmdlet.ShouldProcess($name, 'Créer ou réinitialiser le compte')) { continue }

    if ($ActiveDirectory) {
        $existing = Get-ADUser -Filter "SamAccountName -eq '$name'" -ErrorAction SilentlyContinue
        if ($existing) {
            Set-ADAccountPassword -Identity $existing -NewPassword $secure -Reset
            Enable-ADAccount -Identity $existing
        }
        else {
            $params = @{
                Name                 = $name
                SamAccountName       = $name
                AccountPassword      = $secure
                Enabled              = $true
                PasswordNeverExpires = $true
                CannotChangePassword = $true
                Description          = 'RemoteMe - compte du pool d''environnements anonymes'
            }
            if ($OrganizationalUnit) { $params.Path = $OrganizationalUnit }
            New-ADUser @params
        }
        Add-ADGroupMember -Identity $Group -Members $name
    }
    else {
        $existing = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
        if ($existing) {
            Set-LocalUser -Name $name -Password $secure
            Enable-LocalUser -Name $name
        }
        else {
            New-LocalUser -Name $name -Password $secure -PasswordNeverExpires -UserMayNotChangePassword `
                -AccountNeverExpires -Description 'RemoteMe - pool d''environnements anonymes' | Out-Null
        }
        # S-1-5-32-555 = « Utilisateurs du Bureau à distance », quel que soit la langue du système.
        if (-not (Get-LocalGroupMember -SID 'S-1-5-32-555' -Member $name -ErrorAction SilentlyContinue)) {
            Add-LocalGroupMember -SID 'S-1-5-32-555' -Member $name
        }
    }

    $entry = [ordered]@{ Username = $name; Password = $password }
    if ($Domain) { $entry.Domain = $Domain }
    $accounts += [pscustomobject]$entry
    Write-Host "Compte prêt : $name"
}

$fragment = [ordered]@{ RemoteMe = [ordered]@{ Environments = [ordered]@{ Accounts = $accounts } } }
$fragment | ConvertTo-Json -Depth 5 | Set-Content -Path $OutFile -Encoding UTF8
icacls $OutFile /inheritance:r /grant:r 'BUILTIN\Administrators:F' /Q | Out-Null

Write-Host ''
Write-Host "$($accounts.Count) compte(s). Fragment de configuration : $OutFile"
Write-Host "Copiez-le dans appsettings.Production.json, puis supprimez $OutFile."
