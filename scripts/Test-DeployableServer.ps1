param(
    [Parameter(Mandatory = $true)]
    [string] $ServerRoot,
    [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $ServerRoot).Path
$required = @(
    'Vega.Server.exe',
    'Vega.Server.dll',
    'libs/Vega.Contracts.dll',
    'libs/Vega.Core.dll',
    'libs/Vega.Persistence.dll',
    'libs/Vega.Regions.dll',
    'ServerPlugins/Vega.Modules.Pvp.dll',
    'config/server.json',
    'config/database.json',
    'config/soft-restart.json'
)
$missing = $required | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $source $_) -PathType Leaf)
}
if ($missing.Count -ne 0) {
    throw "Deployable server is incomplete: $($missing -join ', ')."
}

Get-ChildItem -LiteralPath (Join-Path $source 'config') -Filter '*.json' -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json | Out-Null }

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("vega-deployment-smoke-" + [Guid]::NewGuid().ToString('N'))
$deploy = Join-Path $smokeRoot 'server'
$process = $null
try {
New-Item -ItemType Directory -Path $deploy -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $deploy -Recurse -Force
foreach ($relative in @('data', 'logs', 'worlds')) {
    $generated = Join-Path $deploy $relative
    if ([IO.Directory]::Exists($generated)) { [IO.Directory]::Delete($generated, $true) }
}
$world = Join-Path $deploy 'worlds/smoke.wld'
New-Item -ItemType Directory -Path (Split-Path -Parent $world) -Force | Out-Null

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $deploy 'Vega.Server.exe'
$start.WorkingDirectory = $deploy
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$quotedWorld = '"' + $world.Replace('"', '\"') + '"'
$start.Arguments = "--vega-ui=plain -autocreate 1 -world $quotedWorld -port 17889 -maxplayers 4 -password deployment-smoke"

$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
    if (-not $process.Start()) { throw 'Vega.Server.exe did not start.' }
    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $operationsLog = Join-Path $deploy 'logs/vega-operations.jsonl'
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $operationsLog) -and
            (Get-Content -LiteralPath $operationsLog -Raw -ErrorAction SilentlyContinue) -match 'NetworkReady') { break }
        Start-Sleep -Milliseconds 200
    }
    if ($process.HasExited) {
        throw "Server exited before NetworkReady with code $($process.ExitCode):`n$($standardOutput.Result)`n$($standardError.Result)"
    }
    if (-not (Test-Path -LiteralPath $operationsLog) -or
        (Get-Content -LiteralPath $operationsLog -Raw) -notmatch 'NetworkReady') {
        throw "Server did not reach NetworkReady within $TimeoutSeconds seconds."
    }

    if (-not (Test-Path -LiteralPath $world -PathType Leaf)) { throw 'Generated world was not saved.' }
    if (-not (Test-Path -LiteralPath (Join-Path $deploy 'data/vega.db') -PathType Leaf)) {
        throw 'The baseline SQLite database was not created.'
    }
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}
