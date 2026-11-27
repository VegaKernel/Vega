param(
    [Parameter(Mandatory)][string]$TShockRoot,
    [Parameter(Mandatory)][string]$WorldFile,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BenchmarkPlugin,
    [ValidateRange(120,1800)][int]$Ticks = 600,
    [int]$TimeoutSeconds = 300,
    [switch]$FullWorld
)
$ErrorActionPreference = 'Stop'
if (-not $BenchmarkPlugin) { $BenchmarkPlugin = Join-Path $PSScriptRoot '../benchmarks/Vega.TShockComparison.Benchmarks/bin/Release/net9.0/Vega.TShockComparison.Benchmarks.dll' }
$source = (Resolve-Path -LiteralPath $TShockRoot).Path
$worldSource = (Resolve-Path -LiteralPath $WorldFile).Path
$pluginSource = (Resolve-Path -LiteralPath $BenchmarkPlugin).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$deploy = Join-Path $output ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $deploy -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $deploy -Recurse
# Source must be a clean release archive, not an existing production installation.
foreach ($name in @('tshock','worlds','logs')) {
    if (Test-Path -LiteralPath (Join-Path $deploy $name)) { throw "Comparison deployment source contains non-release state: $name" }
}
$world = Join-Path $deploy 'fixture.wld'
Copy-Item -LiteralPath $worldSource -Destination $world
Copy-Item -LiteralPath $pluginSource -Destination (Join-Path $deploy 'ServerPlugins')
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$start = [Diagnostics.ProcessStartInfo]::new()
$isWin = $env:OS -eq 'Windows_NT'
$start.FileName = Join-Path $deploy $(if ($isWin) { 'TShock.Server.exe' } else { 'TShock.Server' })
$start.WorkingDirectory = $deploy
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.EnvironmentVariables['VEGA_SYNTHETIC_BENCHMARK'] = '1'
$start.EnvironmentVariables['VEGA_TSHOCK_COMPARISON_TICKS'] = [string]$Ticks
if ($FullWorld) { $start.EnvironmentVariables['VEGA_TSHOCK_FULL_WORLD'] = '1' }
$password = [Guid]::NewGuid().ToString('N')
$start.Arguments = "-world `"$world`" -ip 127.0.0.1 -port $port -maxplayers 255 -password $password -noupnp"
$process = [Diagnostics.Process]::new(); $process.StartInfo = $start
$stdout = $null; $stderr = $null
try {
    if (-not $process.Start()) { throw 'TShock did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds); $result = $null
    $path = Join-Path $deploy 'tshock-mixed.json'
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $path) {
            try { $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; break } catch { }
        }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $result) { throw "No TShock result; see $deploy." }
    if ($result.Error) { throw $result.Error }
    $result | ConvertTo-Json -Depth 8
} finally {
    # The process only owns the disposable local copy; preserve exact test evidence.
    if (-not $process.HasExited) {
        try { $process.StandardInput.WriteLine('exit-nosave'); $process.StandardInput.Flush() } catch { }
        if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    if ($stdout) { $stdout.Result | Set-Content -LiteralPath (Join-Path $deploy 'stdout.log') }
    if ($stderr) { $stderr.Result | Set-Content -LiteralPath (Join-Path $deploy 'stderr.log') }
    $process.Dispose()
}
