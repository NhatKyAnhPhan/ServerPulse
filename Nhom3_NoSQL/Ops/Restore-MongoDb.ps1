[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArchivePath,

    [Parameter(Mandatory = $true)]
    [string] $SourceDatabase,

    [Parameter(Mandatory = $true)]
    [string] $TargetDatabase,

    [string] $MongoUri = "mongodb://localhost:27017"
)

$ErrorActionPreference = "Stop"
$mongorestore = Get-Command mongorestore -ErrorAction SilentlyContinue
if (-not $mongorestore) {
    throw "mongorestore was not found. Install MongoDB Database Tools and add it to PATH."
}
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
if ([string]::IsNullOrWhiteSpace($SourceDatabase) -or [string]::IsNullOrWhiteSpace($TargetDatabase)) {
    throw "SourceDatabase and TargetDatabase are required."
}

$operation = "Replace collections in database '$TargetDatabase' from archive '$archive'"
if ($PSCmdlet.ShouldProcess($TargetDatabase, $operation)) {
    & $mongorestore.Source --uri $MongoUri "--archive=$archive" --gzip --drop `
        "--nsFrom=$SourceDatabase.*" "--nsTo=$TargetDatabase.*"
    if ($LASTEXITCODE -ne 0) {
        throw "mongorestore failed with exit code $LASTEXITCODE."
    }
    Write-Host "Restore completed into database '$TargetDatabase'."
}
