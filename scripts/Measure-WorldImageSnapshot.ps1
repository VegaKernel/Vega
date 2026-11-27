param(
    [Parameter(Mandatory)][string]$ServerRoot,
    [Parameter(Mandatory)][string]$WorldFile,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$TimeoutSeconds = 180,
    [switch]$RequireInitialCacheHit
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $ServerRoot).Path
$worldSource = (Resolve-Path -LiteralPath $WorldFile).Path
$deploy = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $deploy -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $deploy -Recurse
foreach ($name in @('data','logs','worlds','schematics')) {
    if (Test-Path -LiteralPath (Join-Path $deploy $name)) { throw "Source must be a clean publish, not an existing server: $name" }
}
$world = Join-Path $deploy 'fixture.wld'
Copy-Item -LiteralPath $worldSource -Destination $world
if ($RequireInitialCacheHit) {
    Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($worldSource,'.vworld')) -Destination ([IO.Path]::ChangeExtension($world,'.vworld'))
}
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$isWin = $env:OS -eq 'Windows_NT'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $deploy $(if ($isWin) { 'Vega.Server.exe' } else { 'Vega.Server' })
$start.WorkingDirectory = $deploy; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$password = [Guid]::NewGuid().ToString('N')
$start.Arguments = "--vega-ui=plain --vega-world-image=on -world `"$world`" -ip 127.0.0.1 -port $port -maxplayers 4 -password $password -noupnp"
$process = [Diagnostics.Process]::new(); $process.StartInfo = $start
$stdout = $null; $stderr = $null
function Send-Command($command) {
    $encoding = if ($isWin) { [Text.Encoding]::Unicode } else { [Text.Encoding]::UTF8 }
    $bytes = $encoding.GetBytes("$command`n")
    $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length); $process.StandardInput.BaseStream.Flush()
}
function Read-Ops {
    $log = Join-Path $deploy 'logs/vega-operations.jsonl'
    if (-not (Test-Path -LiteralPath $log)) { return @() }
    @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | ForEach-Object { try { $_ | ConvertFrom-Json } catch {} })
}
try {
    if (-not $process.Start()) { throw 'Server did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if (@(Read-Ops | Where-Object Text -match 'NetworkReady').Count) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($process.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw "Server not ready: $deploy" }
    $initialLoad = Read-Ops | Where-Object { $_.Source -eq 'world-image' -and $_.Text -match 'world_image cache=' } | Select-Object -First 1
    if ($RequireInitialCacheHit -and (!$initialLoad -or $initialLoad.Text -notmatch 'cache=hit.*fallback=false')) { throw "Expected native world-image cache hit: $deploy" }
    Start-Sleep -Seconds 3
    $previous = @(Read-Ops | Measure-Object Sequence -Maximum).Maximum
    Send-Command 'save'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $captured = $null; $built = $null
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $events = @(Read-Ops | Where-Object { $_.Sequence -gt $previous -and $_.Source -eq 'world-image' })
        $captured = $events | Where-Object Text -match 'snapshot=captured' | Select-Object -First 1
        if ($captured) { $built = $events | Where-Object { $_.Sequence -gt $captured.Sequence -and $_.Text -match 'build=complete' } | Select-Object -First 1 }
        if ($captured -and $built) { break }
        if ($events | Where-Object Text -match 'snapshot=failed|build=failed') { throw "Image capture/build failed: $deploy" }
        Start-Sleep -Milliseconds 200
    }
    if (-not $captured -or -not $built) { throw "No snapshot result: $deploy" }
    # Preserve the exact validated pair before graceful exit can trigger another save.
    Copy-Item -LiteralPath $world -Destination (Join-Path $deploy 'saved.wld')
    Copy-Item -LiteralPath ([IO.Path]::ChangeExtension($world,'.vworld')) -Destination (Join-Path $deploy 'saved.vworld')
    $result = [pscustomobject]@{ SourceSha256 = (Get-FileHash -LiteralPath $worldSource -Algorithm SHA256).Hash; InitialLoad = $initialLoad.Text; Capture = $captured.Text; Build = $built.Text; WorldBytes = (Get-Item -LiteralPath $world).Length; ImageBytes = (Get-Item -LiteralPath ([IO.Path]::ChangeExtension($world,'.vworld'))).Length; Artifact = $deploy }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $deploy 'snapshot-result.json') -Encoding UTF8
    $result | ConvertTo-Json
    Send-Command 'shutdown world image snapshot benchmark'
    if (-not $process.WaitForExit(30000)) { throw 'Server did not shut down.' }
} finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($stdout) { $stdout.Result | Set-Content -LiteralPath (Join-Path $deploy 'stdout.log') }
    if ($stderr) { $stderr.Result | Set-Content -LiteralPath (Join-Path $deploy 'stderr.log') }
    $process.Dispose()
}
