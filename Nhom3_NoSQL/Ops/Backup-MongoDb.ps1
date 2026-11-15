param(
    [Parameter(Mandatory = $true)]
    [string] $Database,

    [string] $MongoUri = "mongodb://localhost:27017",

    [string] $BackupDirectory = (Join-Path $env:LOCALAPPDATA "ServerPulse\backups"),

    [string] $ArchivePath
)

$ErrorActionPreference = "Stop"
$mongodump = Get-Command mongodump -ErrorAction SilentlyContinue
if (-not $mongodump) {
    throw "mongodump was not found. Install MongoDB Database Tools and add it to PATH."
}
if (-not $ArchivePath) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $ArchivePath = Join-Path $BackupDirectory "$Database-$stamp.archive.gz"
}
$ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)
New-Item -ItemType Directory -Path (Split-Path -Parent $ArchivePath) -Force | Out-Null

& $mongodump.Source --uri $MongoUri --db $Database "--archive=$ArchivePath" --gzip
if ($LASTEXITCODE -ne 0) {
    throw "mongodump failed with exit code $LASTEXITCODE."
}
Write-Host "Backup created: $ArchivePath"
