#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installe ou met à jour RemoteMe sur IIS.

.DESCRIPTION
    - active IIS et le protocole WebSocket ;
    - vérifie la présence du module ASP.NET Core (ASP.NET Core Hosting Bundle 8) ;
    - copie l'application publiée (dotnet publish) en conservant la configuration locale ;
    - crée un pool d'applications « sans code managé » à processus unique, sans recyclage
      chevauchant : le pool d'environnements vit en mémoire et ne doit exister qu'en un exemplaire ;
    - crée le site (HTTPS si une empreinte de certificat est fournie).

.EXAMPLE
    dotnet publish src/RemoteMe.Web -c Release -o .\publish
    .\deploy\Install-RemoteMe.ps1 -SourcePath .\publish -HostName apps.contoso.com -CertificateThumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SourcePath,

    [string] $InstallPath = 'C:\inetpub\RemoteMe',

    [string] $SiteName = 'RemoteMe',

    [string] $AppPoolName = 'RemoteMe',

    [string] $HostName = '',

    [int] $Port = 0,

    [string] $CertificateThumbprint
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $SourcePath 'RemoteMe.Web.dll'))) {
    throw "RemoteMe.Web.dll introuvable dans '$SourcePath'. Lancez d'abord : dotnet publish src/RemoteMe.Web -c Release -o <dossier>"
}

Write-Host '1/5 Fonctionnalités IIS (serveur web + WebSocket)...'
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
    Install-WindowsFeature Web-Server, Web-WebSockets, Web-Mgmt-Console | Out-Null
}
else {
    # Windows client (tests)
    Enable-WindowsOptionalFeature -Online -NoRestart -FeatureName IIS-WebServerRole, IIS-WebSockets, IIS-ManagementConsole | Out-Null
}

Write-Host '2/5 Module ASP.NET Core...'
$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path $ancm)) {
    throw "Module ASP.NET Core absent. Installez l'« ASP.NET Core 8 Hosting Bundle » (https://dotnet.microsoft.com/download/dotnet/8.0) puis relancez ce script."
}

Import-Module WebAdministration

Write-Host "3/5 Copie des fichiers vers $InstallPath..."
if (Test-Path "IIS:\AppPools\$AppPoolName") {
    Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path $InstallPath, (Join-Path $InstallPath 'logs') | Out-Null
# La configuration locale (secrets, catalogue) n'est jamais écrasée par une mise à jour.
$keep = @('appsettings.Production.json')
if (Test-Path (Join-Path $InstallPath 'apps.json')) { $keep += 'apps.json' }
robocopy $SourcePath $InstallPath /E /NFL /NDL /NJH /NJS /NP /XF $keep /XD logs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy a échoué (code $LASTEXITCODE)." }
Get-ChildItem -Path $InstallPath -Recurse -Filter *.ps1 | Unblock-File

Write-Host "4/5 Pool d'applications $AppPoolName..."
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
$pool = "IIS:\AppPools\$AppPoolName"
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''
Set-ItemProperty $pool -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name processModel.maxProcesses -Value 1
Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name recycling.disallowOverlappingRotation -Value $true

$identity = "IIS AppPool\$AppPoolName"
icacls $InstallPath /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null
icacls (Join-Path $InstallPath 'logs') /grant "${identity}:(OI)(CI)M" /Q | Out-Null
$secrets = Join-Path $InstallPath 'appsettings.Production.json'
if (Test-Path $secrets) {
    # Mots de passe du pool : lisibles uniquement par les administrateurs et le pool IIS.
    icacls $secrets /inheritance:r /grant:r 'BUILTIN\Administrators:F' "SYSTEM:F" "${identity}:R" /Q | Out-Null
}

Write-Host "5/5 Site $SiteName..."
$useHttps = [bool]$CertificateThumbprint
if ($Port -eq 0) { $Port = if ($useHttps) { 443 } else { 80 } }
if (-not (Get-Website -Name $SiteName)) {
    New-Website -Name $SiteName -PhysicalPath $InstallPath -ApplicationPool $AppPoolName `
        -Port $Port -HostHeader $HostName -Ssl:$useHttps | Out-Null
    if ($useHttps) {
        $binding = Get-WebBinding -Name $SiteName -Protocol https
        $binding.AddSslCertificate($CertificateThumbprint, 'my')
    }
}
else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $InstallPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}

Start-WebAppPool -Name $AppPoolName
Start-Website -Name $SiteName -ErrorAction SilentlyContinue

$scheme = if ($useHttps) { 'https' } else { 'http' }
$hostPart = if ($HostName) { $HostName } else { 'localhost' }
Write-Host ''
Write-Host "RemoteMe est installé : ${scheme}://${hostPart}:$Port/"
Write-Host "Configuration : $secrets (voir README, section Configuration)."
if (-not (Test-Path $secrets)) {
    Write-Warning "appsettings.Production.json n'existe pas encore : créez-le (comptes du pool, serveur RDS, guacd)."
}
