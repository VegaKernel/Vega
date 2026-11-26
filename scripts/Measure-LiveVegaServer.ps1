param(
    [Parameter(Mandatory = $true)] [ValidateRange(1, 65535)] [int] $RestPort,
    [ValidateRange(5, 86400)] [int] $DurationSeconds = 300,
    [ValidateRange(1, 60)] [int] $IntervalSeconds = 2,
    [ValidateRange(0, 255)] [int] $MinimumPlayers = 0,
    [Parameter(Mandatory = $true)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$token = [Environment]::GetEnvironmentVariable('VEGA_REST_READ_TOKEN')
if ([string]::IsNullOrWhiteSpace($token)) { throw 'Set VEGA_REST_READ_TOKEN to the existing local read token.' }
$destination = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $destination) { throw 'Measurement output already exists.' }
$directory = [IO.Path]::GetDirectoryName($destination)
[IO.Directory]::CreateDirectory($directory) | Out-Null
$headers = @{ Authorization = 'Bearer ' + $token }
$uri = 'http://127.0.0.1:' + $RestPort
$timer = [Diagnostics.Stopwatch]::StartNew()
$minimumObserved = 256
$count = 0
function Format-Invariant($value) { return [Convert]::ToString($value, [Globalization.CultureInfo]::InvariantCulture) }
while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
    $status = Invoke-RestMethod -Uri ($uri + '/v1/status') -Headers $headers -TimeoutSec 5
    $runtime = Invoke-RestMethod -Uri ($uri + '/v1/metrics') -Headers $headers -TimeoutSec 5
    $network = Invoke-RestMethod -Uri ($uri + '/v1/network') -Headers $headers -TimeoutSec 5
    $queue = Invoke-RestMethod -Uri ($uri + '/v1/scheduler') -Headers $headers -TimeoutSec 5
    if ($status.stage -notin @('NetworkReady', 'BackgroundWarmup', 'FullyWarmed') -or -not $status.state.isWorldReady) {
        throw 'Vega did not remain ready during the measurement.'
    }
    $minimumObserved = [Math]::Min($minimumObserved, [int]$status.online)
    [pscustomobject]@{
        timestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        elapsedSeconds = Format-Invariant ([Math]::Round($timer.Elapsed.TotalSeconds, 3))
        world = $status.world
        online = $status.online
        ticksPerSecond = Format-Invariant $status.state.ticksPerSecond
        tickP50Ms = Format-Invariant $status.state.tickRate.tickDurationP50Milliseconds
        tickP95Ms = Format-Invariant $status.state.tickRate.tickDurationP95Milliseconds
        tickP99Ms = Format-Invariant $status.state.tickRate.tickDurationP99Milliseconds
        tickSampleCount = $status.state.tickRate.sampleCount
        workingSetBytes = $runtime.workingSetBytes
        privateMemoryBytes = $runtime.privateMemoryBytes
        cpuMilliseconds = Format-Invariant $runtime.cpuMilliseconds
        totalAllocatedBytes = $runtime.totalAllocatedBytes
        totalGcPauseMilliseconds = Format-Invariant $runtime.totalGcPauseMilliseconds
        gen0 = $runtime.gen0Collections
        gen1 = $runtime.gen1Collections
        gen2 = $runtime.gen2Collections
        inboundPackets = $network.inboundPackets
        outboundPackets = $network.outboundPackets
        inboundBytes = $network.inboundBytes
        outboundBytes = $network.outboundBytes
        rejectedInboundPackets = $network.rejectedInboundPackets
        pendingGameThread = $queue.pending
        droppedGameThread = $queue.dropped
    } | Export-Csv -LiteralPath $destination -NoTypeInformation -Append -Encoding UTF8
    $count++
    $remaining = $DurationSeconds - $timer.Elapsed.TotalSeconds
    if ($remaining -gt 0) { Start-Sleep -Seconds ([Math]::Min($IntervalSeconds, [Math]::Ceiling($remaining))) }
}
if ($count -eq 0) { throw 'No samples collected.' }
Write-Output "Collected $count samples, minimum observed connected players: $minimumObserved. Output: $destination"
if ($minimumObserved -lt $MinimumPlayers) {
    throw "Qualification failed: required $MinimumPlayers fully connected players, observed at least one sample with $minimumObserved. Raw measurements were retained."
}
