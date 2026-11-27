param(
    [Parameter(Mandatory)][string]$ServerRoot,
    [Parameter(Mandatory)][string]$WorldFile,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BenchmarkPlugin,
    [ValidateRange(120,3600)][int]$Ticks = 1800,
    [ValidateSet('legacy','optimized')][string[]]$Modes = @('legacy','optimized','optimized','legacy'),
    [ValidateSet('recording','native','ordered')][string[]]$TransportModes = @('recording'),
    [switch]$DeferredWake,
    [switch]$ProducerBatching,
    [switch]$ServerTransportDefaults,
    [switch]$FullWorld,
    [ValidateRange(0.25,2)][double]$DeferredWakeMilliseconds = 1,
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
if ($ServerTransportDefaults -and ($DeferredWake -or $ProducerBatching -or $TransportModes -contains 'recording' -or $TransportModes -contains 'native')) {
    throw 'ServerTransportDefaults requires ordered transport and uses its own wake and batching settings.'
}
if ($FullWorld -and ($Modes -contains 'legacy')) { throw 'Full-world fixture uses the optimized production chat renderer; specify -Modes optimized.' }
if ($ProducerBatching -and (-not $DeferredWake -or $TransportModes -contains 'recording' -or $TransportModes -contains 'native')) {
    throw 'Producer batching requires -DeferredWake and ordered transport only.'
}
if (-not $BenchmarkPlugin) { $BenchmarkPlugin = Join-Path $PSScriptRoot '../benchmarks/Vega.MixedGameplay.Benchmarks/bin/Release/net11.0/Vega.MixedGameplay.Benchmarks.dll' }
$source = (Resolve-Path -LiteralPath $ServerRoot).Path
$worldSource = (Resolve-Path -LiteralPath $WorldFile).Path
$pluginSource = (Resolve-Path -LiteralPath $BenchmarkPlugin).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Send-Console($process, [string]$command) {
    $encoding = if ($env:OS -eq 'Windows_NT') { [Text.Encoding]::Unicode } else { [Text.Encoding]::UTF8 }
    $bytes = $encoding.GetBytes("$command`n")
    $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $process.StandardInput.BaseStream.Flush()
}

$runs = @(foreach ($transport in $TransportModes) {
    foreach ($mode in $Modes) { [pscustomobject]@{ Mode = $mode; Transport = $transport } }
})
for ($i = 0; $i -lt $runs.Count; $i++) {
    $mode = $runs[$i].Mode
    $transport = $runs[$i].Transport
    $label = if ($transport -eq 'recording') { $mode } else { "$mode-$transport" }
    $deploy = Join-Path $output ('{0:D2}-{1}-{2}' -f $i,$label,[Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $deploy | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $deploy -Recurse
    # Never run the fixture against existing production state.
    foreach ($name in @('data','logs','worlds','schematics')) {
        $generated = Join-Path $deploy $name
        if (Test-Path -LiteralPath $generated) {
            $resolved = (Resolve-Path -LiteralPath $generated).Path
            if (-not $resolved.StartsWith($deploy + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture path.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path (Join-Path $deploy 'worlds') | Out-Null
    $world = Join-Path $deploy 'worlds/fixture.wld'
    Copy-Item -LiteralPath $worldSource -Destination $world
    Copy-Item -LiteralPath $pluginSource -Destination (Join-Path $deploy 'ServerPlugins')
    $modulesPath = Join-Path $deploy 'config/modules.json'
    $modules = Get-Content -LiteralPath $modulesPath -Raw | ConvertFrom-Json
    $modules.enabled = $true
    $modules | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $modulesPath -Encoding UTF8
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $start = [Diagnostics.ProcessStartInfo]::new()
    $hostName = if ($env:OS -eq 'Windows_NT') { 'Vega.Server.exe' } else { 'Vega.Server' }
    $start.FileName = Join-Path $deploy $hostName
    $start.WorkingDirectory = $deploy
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['VEGA_SYNTHETIC_BENCHMARK'] = '1'
    $start.EnvironmentVariables['VEGA_BENCH_SERVER_DEFAULTS'] = if ($ServerTransportDefaults) { '1' } else { '0' }
    $start.EnvironmentVariables['VEGA_BENCH_PRODUCER_BATCHING'] = if ($ProducerBatching) { '1' } else { '0' }
    if ($DeferredWake) {
        $start.EnvironmentVariables['VEGA_BENCH_DEFER_WAKE'] = '1'
        $start.EnvironmentVariables['VEGA_BENCH_DEFER_MS'] = $DeferredWakeMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    $password = [Guid]::NewGuid().ToString('N')
    $start.Arguments = "--vega-ui=plain -world `"$world`" -ip 127.0.0.1 -port $port -maxplayers 255 -password $password -noupnp"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $stdout = $null
    $stderr = $null
    try {
        if (-not $process.Start()) { throw 'Server did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $logs = Join-Path $deploy 'logs/vega-operations.jsonl'
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            if ((Test-Path -LiteralPath $logs) -and (Get-Content -LiteralPath $logs -Raw -ErrorAction SilentlyContinue) -match 'NetworkReady') { break }
            Start-Sleep -Milliseconds 200
        }
        if ($process.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'Server failed to become ready.' }
        Start-Sleep -Seconds 5
        if ($FullWorld) {
            Send-Console $process "fullworldbench $Ticks $transport"
            $resultPath = Join-Path $deploy 'fullworld-optimized.json'
        } else {
            Send-Console $process "mixedbench $mode $Ticks $transport"
            $resultPath = Join-Path $deploy "mixed-$mode.json"
        }
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $result = $null
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            if (Test-Path -LiteralPath $resultPath) {
                try { $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json; break } catch { }
            }
            Start-Sleep -Milliseconds 200
        }
        if ($null -eq $result) { throw "No result from $mode fixture. See $deploy." }
        if ($result.Error) { throw $result.Error }
        Send-Console $process 'plugin unload Vega.Benchmarks.MixedGameplay'
        $operations = Join-Path $deploy 'logs/vega-operations.jsonl'
        $unloaded = $false
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            $events = Get-Content -LiteralPath $operations -Tail 30 -ErrorAction SilentlyContinue
            if ($events -match 'Vega.Benchmarks.MixedGameplay.*detached incompletely') { throw "Benchmark plugin remained loaded. See $operations." }
            if ($events -match 'Vega.Benchmarks.MixedGameplay.*detached and unloaded') { $unloaded = $true; break }
            Start-Sleep -Milliseconds 200
        }
        if (-not $unloaded) { throw "Benchmark plugin unload was not confirmed. See $operations." }
        $result | ConvertTo-Json -Depth 8
        Send-Console $process 'shutdown mixed gameplay benchmark'
        if (-not $process.WaitForExit(30000)) { throw 'Server did not shut down.' }
    } finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if ($stdout) { $stdout.Result | Set-Content -LiteralPath (Join-Path $deploy 'stdout.log') }
        if ($stderr) { $stderr.Result | Set-Content -LiteralPath (Join-Path $deploy 'stderr.log') }
        $process.Dispose()
    }
}
