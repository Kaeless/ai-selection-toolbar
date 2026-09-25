param(
    [switch]$ProbeSqlite,
    [string]$Root
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Check-Package([string]$Path) {
    foreach ($relative in @('AiSelectionToolbar.Desktop.exe', 'System.Data.SQLite.dll', 'x64\SQLite.Interop.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $Path $relative) -PathType Leaf)) {
            throw "Package is missing $relative in $Path"
        }
    }
}

if ($ProbeSqlite) {
    Check-Package $Root
    # Run under Windows PowerShell 5.1 (full .NET Framework), matching the WPF process.
    Add-Type -Path (Join-Path $Root 'System.Data.SQLite.dll')
    $db = Join-Path $env:TEMP ('ai-toolbar-sqlite-' + [guid]::NewGuid().ToString('N') + '.db')
    $connection = New-Object System.Data.SQLite.SQLiteConnection("Data Source=$db;Version=3;")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = 'CREATE TABLE probe (value TEXT)'
            [void]$command.ExecuteNonQuery()
            $command.CommandText = 'INSERT INTO probe (value) VALUES (''ok'')'
            [void]$command.ExecuteNonQuery()
            $command.CommandText = 'SELECT value FROM probe LIMIT 1'
            if ($command.ExecuteScalar() -ne 'ok') { throw 'SQLite read/write probe failed' }
        } finally {
            $command.Dispose()
        }
        Write-Host "SQLite read/write passed: $Root"
    } finally {
        $connection.Dispose()
        Remove-Item -LiteralPath $db -ErrorAction SilentlyContinue
    }
    exit 0
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $repo 'dist'
$portableZip = Join-Path $dist 'AISelectionToolbar-0.1.0-win-x64-portable.zip'
$setup = Join-Path $dist 'AISelectionToolbar-0.1.0-win-x64-setup.exe'
foreach ($file in @($portableZip, $setup)) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package: $file" }
}

$scratch = Join-Path $env:RUNNER_TEMP ('ai-toolbar-smoke-' + [guid]::NewGuid().ToString('N'))
$portable = Join-Path $scratch 'portable'
$installed = Join-Path $scratch 'installed'
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$probe = $PSCommandPath
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

function Probe-Sqlite([string]$Path) {
    Check-Package $Path
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe -ProbeSqlite -Root $Path
    if ($LASTEXITCODE -ne 0) { throw "SQLite probe failed for $Path" }
}

try {
    Expand-Archive -LiteralPath $portableZip -DestinationPath $portable
    Probe-Sqlite $portable

    $installProcess = Start-Process -FilePath $setup -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installed"
    ) -Wait -PassThru
    if ($installProcess.ExitCode -ne 0) { throw "Installer failed: $($installProcess.ExitCode)" }
    Probe-Sqlite $installed

    $uninstaller = Join-Path $installed 'unins000.exe'
    if (!(Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Missing uninstaller' }
    $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    ) -Wait -PassThru
    if ($uninstallProcess.ExitCode -ne 0) { throw "Uninstaller failed: $($uninstallProcess.ExitCode)" }
    if (Test-Path -LiteralPath (Join-Path $installed 'AiSelectionToolbar.Desktop.exe')) {
        throw 'Application executable remains after uninstall'
    }
    Write-Host 'Portable extraction, installer, SQLite and uninstall smoke checks passed.'
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
