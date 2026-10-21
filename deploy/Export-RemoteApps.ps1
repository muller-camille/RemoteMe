<#
.SYNOPSIS
    Génère le catalogue RemoteMe (apps.json) à partir des RemoteApp publiées dans une collection RDS.

.DESCRIPTION
    Lit les programmes RemoteApp de la collection (module RemoteDesktop, à lancer sur le
    Connection Broker ou avec -ConnectionBroker), et écrit pour chacun une entrée :
      Program = "||<alias>", Hostname = broker, LoadBalanceInfo = collection.
    Avec -IconDirectory, l'icône de chaque programme est extraite en PNG (si son fichier est
    accessible depuis cette machine).

    Le fichier existant est sauvegardé (apps.json.bak) ; les entrées ajoutées à la main pour
    d'autres collections sont conservées avec -Merge.

.EXAMPLE
    .\Export-RemoteApps.ps1 -CollectionName Bureautique -ConnectionBroker broker.contoso.local `
        -OutFile C:\inetpub\RemoteMe\apps.json -IconDirectory C:\inetpub\RemoteMe\wwwroot\icons -Merge
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CollectionName,

    [string] $ConnectionBroker = ([System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName),

    [string] $OutFile = '.\apps.json',

    [string] $IconDirectory,

    # Catégorie affichée dans le catalogue (par défaut : nom de la collection).
    [string] $Category,

    # Conserve les applications existantes qui ne viennent pas de cette collection.
    [switch] $Merge,

    # N'exporte que les RemoteApp visibles dans RD Web Access.
    [switch] $OnlyVisibleInWebAccess
)

$ErrorActionPreference = 'Stop'
Import-Module RemoteDesktop

if (-not $Category) { $Category = $CollectionName }

$collection = Get-RDSessionCollection -CollectionName $CollectionName -ConnectionBroker $ConnectionBroker
$loadBalanceInfo = "tsv://MS Terminal Services Plugin.1.$($collection.CollectionAlias)"
$remoteApps = Get-RDRemoteApp -CollectionName $CollectionName -ConnectionBroker $ConnectionBroker
if ($OnlyVisibleInWebAccess) { $remoteApps = $remoteApps | Where-Object ShowInWebAccess }

if ($IconDirectory) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force -Path $IconDirectory | Out-Null
}

function ConvertTo-AppId([string] $alias) {
    $id = ($alias.ToLowerInvariant() -replace '[^a-z0-9_-]', '-').Trim('-')
    if ($id.Length -gt 64) { $id = $id.Substring(0, 64) }
    if (-not $id) { $id = 'app' }
    $id
}

$exported = foreach ($app in $remoteApps | Sort-Object DisplayName) {
    $id = ConvertTo-AppId $app.Alias
    $entry = [ordered]@{
        Id              = $id
        Name            = $app.DisplayName
        Description     = ''
        Category        = $Category
        Program         = "||$($app.Alias)"
        Hostname        = $ConnectionBroker
        LoadBalanceInfo = $loadBalanceInfo
        Collection      = $CollectionName
    }
    if ($app.CommandLineSetting -eq 'Require' -and $app.RequiredCommandLine) {
        $entry.Arguments = $app.RequiredCommandLine
    }

    if ($IconDirectory) {
        $source = if ($app.IconPath) { $app.IconPath } else { $app.FilePath }
        $source = [Environment]::ExpandEnvironmentVariables($source)
        if (Test-Path $source) {
            try {
                $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($source)
                $png = Join-Path $IconDirectory "$id.png"
                $icon.ToBitmap().Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
                $entry.Icon = "icons/$id.png"
            }
            catch {
                Write-Warning "Icône de $($app.DisplayName) non extraite : $_"
            }
        }
        else {
            Write-Warning "Icône de $($app.DisplayName) : '$source' inaccessible depuis cette machine."
        }
    }

    [pscustomobject]$entry
    Write-Host "  $($app.DisplayName)  →  $id"
}

$apps = @($exported)
if ($Merge -and (Test-Path $OutFile)) {
    $existing = (Get-Content $OutFile -Raw | ConvertFrom-Json).Apps
    $ids = $apps.Id
    $kept = @($existing | Where-Object { $_.Collection -ne $CollectionName -and $ids -notcontains $_.Id })
    $apps = $kept + $apps
}

if (Test-Path $OutFile) { Copy-Item $OutFile "$OutFile.bak" -Force }
[ordered]@{ Apps = $apps } | ConvertTo-Json -Depth 5 | Set-Content -Path $OutFile -Encoding UTF8

Write-Host ''
Write-Host "$(@($exported).Count) application(s) exportée(s) depuis « $CollectionName » vers $OutFile."
Write-Host 'RemoteMe recharge le catalogue automatiquement, sans redémarrage.'
