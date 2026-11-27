param(
    [Parameter(Mandatory = $true)]
    [string] $ServerRoot,
    [int] $TimeoutSeconds = 180,
    [string] $WorldFile,
    [switch] $TestSandboxIsolation,
    [switch] $TestOrderedTransport,
    [switch] $TestDeferredWake,
    [switch] $TestProducerBatching,
    [switch] $TestParityCommands,
    [switch] $KeepFixtureOnFailure
)

$ErrorActionPreference = 'Stop'
$smokePassed = $false
if ($TestProducerBatching) { $TestDeferredWake = $true }
if ($TestDeferredWake) { $TestOrderedTransport = $true }
$source = (Resolve-Path -LiteralPath $ServerRoot).Path
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$serverHost = if ($onWindows) { 'Vega.Server.exe' } else { 'Vega.Server' }
$required = @(
    $serverHost,
    'Vega.Server.dll',
    'libs/Vega.Contracts.dll',
    'libs/Vega.Core.dll',
    'libs/Vega.Persistence.dll',
    'libs/TerraRuntime.Schematics.dll',
    'libs/Vega.Regions.dll',
    'ServerPlugins/Vega.Modules.Pvp.dll',
    'ServerPlugins/Vega.Modules.Navigation.dll',
    'ServerPlugins/Vega.Modules.Restrictions.dll',
    'config/server.json',
    'config/network.json',
    'config/primary-character-persistence.json',
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
function Get-LoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}

function Get-TokenFingerprint([string] $token) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($token))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Wait-SmokeText([string] $path, [string] $pattern, [int] $seconds = 60) {
    $until = [DateTime]::UtcNow.AddSeconds($seconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $until) {
        if ((Test-Path -LiteralPath $path) -and (Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue) -match $pattern) { return }
        Start-Sleep -Milliseconds 200
    }
    throw "Expected '$pattern' was not observed in '$path'."
}

function Send-SmokeCommand([string] $command) {
    $encoding = if ($onWindows) { [Text.Encoding]::Unicode } else { [Text.Encoding]::UTF8 }
    $bytes = $encoding.GetBytes("$command`n")
    $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $process.StandardInput.BaseStream.Flush()
}

try {
    New-Item -ItemType Directory -Path $deploy -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $deploy -Recurse -Force
    foreach ($relative in @('data', 'logs', 'worlds', 'schematics')) {
        $generated = Join-Path $deploy $relative
        if ([IO.Directory]::Exists($generated)) { [IO.Directory]::Delete($generated, $true) }
    }
    if ($TestOrderedTransport -or $TestDeferredWake) {
        # Change only the disposable deployment copy, never the source package.
        $networkPath = Join-Path $deploy 'config/network.json'
        $network = Get-Content -LiteralPath $networkPath -Raw | ConvertFrom-Json
        $network.useOrderedTcp = $true
        $network.orderedQueueDiagnostics = $true
        $network.orderedBatchRequests = 16
        if ($TestDeferredWake) {
            $network.orderedDeferredWake = $true
            $network.orderedDeferredWakeMilliseconds = 2
        }
        if ($TestProducerBatching) {
            $network.orderedProducerBatching = $true
        }
        $network | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $networkPath -Encoding UTF8
    }
    $world = Join-Path $deploy 'worlds/smoke.wld'
    New-Item -ItemType Directory -Path (Split-Path -Parent $world) -Force | Out-Null
    if ($WorldFile) { Copy-Item -LiteralPath (Resolve-Path -LiteralPath $WorldFile).Path -Destination $world }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $deploy $serverHost
    $start.WorkingDirectory = $deploy
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $quotedWorld = '"' + $world.Replace('"', '\"') + '"'
    $primaryPort = Get-LoopbackPort
    do { $restPort = Get-LoopbackPort } while ($restPort -eq $primaryPort)
    $readerToken = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $writerToken = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $start.EnvironmentVariables['VEGA_REST_PORT'] = [string]$restPort
    $start.EnvironmentVariables['VEGA_REST_TOKEN_SHA256'] = Get-TokenFingerprint $readerToken
    $start.EnvironmentVariables['VEGA_REST_MAINTENANCE_TOKEN_SHA256'] = Get-TokenFingerprint $writerToken
    $start.Arguments = "--vega-ui=plain -autocreate 1 -world $quotedWorld -ip 127.0.0.1 -port $primaryPort -maxplayers 4 -password deployment-smoke -noupnp"

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "$serverHost did not start." }
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

    $rest = "http://127.0.0.1:$restPort/v1/world"
    $readHeaders = @{ Authorization = 'Bearer ' + $readerToken }
    $writeHeaders = @{ Authorization = 'Bearer ' + $writerToken }
    $worldDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($true) {
        try { $initialWorld = Invoke-RestMethod -Uri $rest -Headers $readHeaders -TimeoutSec 5; break }
        catch {
            if ([DateTime]::UtcNow -ge $worldDeadline) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
    if ($initialWorld.widthTiles -le 0 -or $initialWorld.heightTiles -le 0) { throw 'REST world snapshot was unavailable.' }
    $timeResponse = Invoke-WebRequest -UseBasicParsing -Uri ($rest + '/time') -Method Post -Headers $writeHeaders `
        -ContentType 'application/json' -Body '{"isDaytime":true,"timeTicks":12000}' -TimeoutSec 10
    $weatherResponse = Invoke-WebRequest -UseBasicParsing -Uri ($rest + '/weather') -Method Post -Headers $writeHeaders `
        -ContentType 'application/json' -Body '{"raining":true,"rainIntensity":0.5,"windSpeed":-0.4}' -TimeoutSec 10
    $updatedWorld = Invoke-RestMethod -Uri $rest -Headers $readHeaders -TimeoutSec 10
    if ($timeResponse.StatusCode -ne 204 -or $weatherResponse.StatusCode -ne 204 -or
        -not $updatedWorld.isDaytime -or $updatedWorld.timeTicks -lt 12000 -or $updatedWorld.timeTicks -gt 13200 -or
        -not $updatedWorld.raining -or [Math]::Abs($updatedWorld.windSpeed + 0.4) -gt 0.1) {
        throw 'REST world mutations were not reflected by the native world snapshot.'
    }
    Wait-SmokeText $operationsLog 'action=time status=204'
    Wait-SmokeText $operationsLog 'action=weather status=204'
    Write-Output 'Native loopback REST world read, time/weather mutation and operation audit passed.'

    Send-SmokeCommand 'schematic export smoke-region 10 10 3 2'
    $schematicPath = Join-Path $deploy 'schematics/smoke-region.trschem'
    $schematicDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not $process.HasExited -and -not (Test-Path -LiteralPath $schematicPath) -and [DateTime]::UtcNow -lt $schematicDeadline) {
        Start-Sleep -Milliseconds 200
    }
    if (-not (Test-Path -LiteralPath $schematicPath) -or [BitConverter]::ToUInt32([IO.File]::ReadAllBytes($schematicPath), 0) -ne 0x43535254) {
        throw 'Native schematic export did not create a TRSC document.'
    }
    Start-Sleep -Milliseconds 500
    Send-SmokeCommand 'fake schematic smoke-preview smoke-region 20 20'
    Write-Output 'Native schematic document exported.'

    if ($TestOrderedTransport) {
        $tcpProbe = [Net.Sockets.TcpClient]::new()
        try { $tcpProbe.Connect([Net.IPAddress]::Loopback, $primaryPort) }
        finally { $tcpProbe.Dispose() }
        Wait-SmokeText $operationsLog '"source"\s*:\s*"ordered-tcp".*oldest_send_ms='
        if ($TestProducerBatching) {
            Wait-SmokeText $operationsLog 'Ordered producer-owned packet batching enabled with deferred wake'
        }
        Write-Output 'Native Ordered TCP listener, local connection lifecycle and queue diagnostics passed.'
    }

    if ($TestSandboxIsolation) {
        Send-SmokeCommand 'antibuild on'
        $buildPolicy = Join-Path $deploy 'data/build-protection.json'
        Wait-SmokeText $buildPolicy '"globalDisabled": true'
        # File publication precedes release of the asynchronous mutation gate.
        Start-Sleep -Milliseconds 500
        Send-SmokeCommand 'protectspawn radius 30'
        Wait-SmokeText $buildPolicy '"spawnRadius": 30'
        Start-Sleep -Milliseconds 500
        Send-SmokeCommand 'protectspawn on'
        Wait-SmokeText $buildPolicy '"spawnProtected": true'
        Start-Sleep -Milliseconds 500
        $backendPort = Get-LoopbackPort
        Send-SmokeCommand "sandbox create arena Arena $quotedWorld persistent 4 disabled $backendPort"
        Wait-SmokeText $operationsLog 'action=create arena.*outcome=Success'
        Send-SmokeCommand 'sandbox start arena'
        $backendLog = Join-Path $deploy 'logs/sandboxes/arena/vega-operations.jsonl'
        Wait-SmokeText $backendLog 'NetworkReady' $TimeoutSeconds
        Wait-SmokeText $operationsLog 'action=start arena.*outcome=Success'
        $instanceRuntime = Join-Path $deploy 'data/sandboxes/instances/arena/runtime'
        if (-not (Test-Path -LiteralPath (Join-Path $instanceRuntime 'saves')) -or
            (Test-Path -LiteralPath (Join-Path $instanceRuntime 'data/build-protection.json')) -or
            (Test-Path -LiteralPath (Join-Path $instanceRuntime 'schematics/smoke-region.trschem'))) {
            throw 'Sandbox save directory or mutable policy was not isolated from the primary world.'
        }
        # These are deployment-wide admission policies, not files scoped to a world worker.
        $sharedWhitelist = Join-Path $deploy 'data/whitelist.json'
        if (-not (Test-Path -LiteralPath $sharedWhitelist) -or
            -not (Test-Path -LiteralPath ($sharedWhitelist + '.lock')) -or
            (Test-Path -LiteralPath (Join-Path $instanceRuntime 'data/whitelist.json')) -or
            (Test-Path -LiteralPath (Join-Path $instanceRuntime 'data/vega.db'))) {
            throw 'Sandbox did not use the shared deployment admission storage.'
        }
        # Mutate the shared store after the secondary process is already running.
        Send-SmokeCommand 'whitelist add 127.0.0.1'
        Wait-SmokeText $sharedWhitelist '127\.0\.0\.1'
        Send-SmokeCommand 'whitelist on'
        Wait-SmokeText $sharedWhitelist '"enabled": true'
        Send-SmokeCommand 'whitelist off'
        Wait-SmokeText $sharedWhitelist '"enabled": false'
        Send-SmokeCommand 'sandbox stop arena'
        Wait-SmokeText $operationsLog 'action=stop arena.*outcome=Success'
        if ((Get-Content -LiteralPath $operationsLog -Raw) -match "Forcing sandbox 'arena' to stop" -or
            (Get-Content -LiteralPath $backendLog -Raw) -notmatch 'Stopping') {
            throw 'Sandbox did not reach a native graceful stopping stage.'
        }
        Write-Output 'Native sandbox readiness, local policy isolation, shared admission storage and graceful shutdown passed.'
    }

    # Vanilla uses UTF-16LE for console input on Windows and UTF-8 elsewhere.
    if ($TestParityCommands) {
        # All changes target this disposable copied world and its local database.
        foreach ($command in @('group add paritygroup', 'group prefix paritygroup "[P]"',
            'group suffix paritygroup "!"', 'group color paritygroup 00ff00', 'group show paritygroup',
            'region create parityzone 100 100 10 10', 'region expand parityzone r 5',
            'region rename parityzone parityrenamed', 'region info parityrenamed',
            'itemban add Torch', 'itemban allow Torch paritygroup', 'itemban disallow Torch paritygroup',
            'worldevent lanterns on', 'worldevent lanterns off', 'worldevent meteorshower on', 'worldevent meteorshower off',
            'worldevent rain off', 'worldevent coinrain on', 'worldevent coinrain off')) {
            Send-SmokeCommand $command
            Start-Sleep -Milliseconds 500
        }
    }
    foreach ($command in @('spawnrate 300', 'maxspawns 20', 'forcehalloween on', 'forcehalloween off', 'clearangler',
        'worldmode expert', 'time noon', 'wind -20', 'shutdown deployment smoke')) {
        Send-SmokeCommand $command
        Start-Sleep -Milliseconds 500
    }
    if (-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0) {
        throw 'Server did not shut down cleanly after the command smoke test.'
    }
    if ($TestOrderedTransport -and (Get-Content -LiteralPath $operationsLog -Raw) -match 'Ordered TCP transport shutdown (exceeded|reported)') {
        throw 'Ordered TCP did not shut down cleanly.'
    }
    if ($standardOutput.Result -notmatch 'World time set to 12:00' -or
        $standardOutput.Result -notmatch 'Wind speed set to -20 mph' -or
        $standardOutput.Result -notmatch 'spawnrate: 300' -or $standardOutput.Result -notmatch 'maxspawns: 20' -or
        $standardOutput.Result -notmatch 'Halloween forced override: True' -or
        $standardOutput.Result -notmatch 'World mode: expert' -or $standardOutput.Result -notmatch 'angler completion list has been cleared' -or
        $standardOutput.Result -notmatch "Schematic 'smoke-region' loaded as provider 'smoke-preview'") {
        throw "Native console hook did not execute Vega world commands:`n$($standardOutput.Result)`n$($standardError.Result)"
    }
    if ($TestParityCommands) {
        foreach ($expected in @('Prefix: \[P\] \| suffix: ! \| color: 00FF00', 'parityrenamed \| world',
            'X: 100; Y: 100; W: 15; H: 10', 'Item restriction 8 updated=True',
            'vega\.restrictions\.allow\.item\.8 enabled=True', 'vega\.restrictions\.allow\.item\.8 enabled=False',
            'World event lanternnight: enabled=True', 'World event lanternnight: enabled=False',
            'World event meteorshower: enabled=True', 'World event meteorshower: enabled=False',
            'World event coinrain: enabled=True', 'World event coinrain: enabled=False')) {
            if ($standardOutput.Result -notmatch $expected) {
                throw "Native parity command result missing ($expected):`n$($standardOutput.Result)`n$($standardError.Result)"
            }
        }
        Write-Output 'Native TShock-compatible group, region, content restriction and world-event command smoke passed.'
    }
    $smokePassed = $true
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { $process.Kill() }
            $process.WaitForExit()
        }
        if ($KeepFixtureOnFailure -and -not $smokePassed) {
            if ($standardOutput) { $standardOutput.Result | Set-Content -LiteralPath (Join-Path $smokeRoot 'stdout.log') }
            if ($standardError) { $standardError.Result | Set-Content -LiteralPath (Join-Path $smokeRoot 'stderr.log') }
        }
        $process.Dispose()
    }
    $resolvedSmokeRoot = [IO.Path]::GetFullPath($smokeRoot)
    $expectedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($KeepFixtureOnFailure -and -not $smokePassed) {
        Write-Output "Preserved failed disposable fixture: $resolvedSmokeRoot"
    }
    elseif ($resolvedSmokeRoot.StartsWith($expectedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
