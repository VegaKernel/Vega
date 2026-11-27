param(
    [Parameter(Mandatory)][string]$ServerRoot,
    [Parameter(Mandatory)][string]$WorldFile,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('authoritative','disabled')][string]$CharacterMode = 'authoritative',
    [ValidateSet('password','passwordless')][string]$JoinMode = 'password',
    [string]$ClientExecutable,
    [switch]$OrderedTcp,
    [switch]$SmokeReady,
    [switch]$RunServer,
    [ValidateRange(15,300)][int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
if ($SmokeReady -and $RunServer) { throw 'Choose a readiness smoke or an interactive server, not both.' }
$source = (Resolve-Path -LiteralPath $ServerRoot).Path
$worldSource = (Resolve-Path -LiteralPath $WorldFile).Path
if (-not $ClientExecutable) {
    $ClientExecutable = Join-Path ${env:ProgramFiles(x86)} 'Steam/steamapps/common/Terraria/Terraria.exe'
}
$clientPath = (Resolve-Path -LiteralPath $ClientExecutable).Path
$version = (Get-Item -LiteralPath $clientPath).VersionInfo.ProductVersion
$assembly = [Reflection.AssemblyName]::GetAssemblyName($clientPath)
if ($version -ne '1.4.6.1' -or $assembly.Name -ne 'Terraria' -or $assembly.Version.ToString() -ne '1.4.6.1') {
    throw "The installed client is not the expected Terraria 1.4.6.1 executable (product=$version; assembly=$($assembly.Version))."
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($source.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must not be inside the source server deployment.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$caseRoot = Join-Path $output ("$CharacterMode-$JoinMode-" + [Guid]::NewGuid().ToString('N'))
$deploy = Join-Path $caseRoot 'server'
$nativeSave = Join-Path $caseRoot 'server-native-saves'
$clientSave = Join-Path $caseRoot 'client-local-saves'
foreach ($folder in @($deploy,$nativeSave,$clientSave,(Join-Path $deploy 'worlds'),(Join-Path $deploy 'ServerPlugins'),(Join-Path $deploy 'config'))) {
    New-Item -ItemType Directory -Path $folder | Out-Null
}

# Copy a current published package only, never its data/logs/profile or arbitrary external plugins.
foreach ($file in Get-ChildItem -LiteralPath $source -File -Filter 'Vega.Server*') {
    Copy-Item -LiteralPath $file.FullName -Destination $deploy
}
foreach ($folder in Get-ChildItem -LiteralPath $source -Directory) {
    if ($folder.Name -in @('libs','runtimes') -or $folder.Name -match '^[a-z]{2}(-[A-Za-z0-9]{2,8})+$') {
        Copy-Item -LiteralPath $folder.FullName -Destination $deploy -Recurse
    }
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $source 'ServerPlugins') -File -Filter 'Vega.Modules.*') {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $deploy 'ServerPlugins')
}
# Use this checkout's defaults, not a deployed server's potentially secret-bearing config.
$defaults = Join-Path $PSScriptRoot '../config'
foreach ($file in Get-ChildItem -LiteralPath $defaults -File -Filter '*.json') {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $deploy 'config')
}
$hostPath = Join-Path $deploy 'Vega.Server.exe'
if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw 'ServerRoot must be a current Windows publish of Vega.Server.' }
$world = Join-Path $deploy 'worlds/fixture.wld'
Copy-Item -LiteralPath $worldSource -Destination $world
function Set-FixtureConfig([string]$name, [hashtable]$values) {
    $path = Join-Path $deploy "config/$name.json"
    $config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    foreach ($key in $values.Keys) { $config.$key = $values[$key] }
    $config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
}
Set-FixtureConfig database @{provider='sqlite'; sqlitePath='data/vega.db'; connectionString=$null}
Set-FixtureConfig server @{worldPath=$null; password=$null}
Set-FixtureConfig modules @{enabled=$false}
Set-FixtureConfig 'debug-api' @{enabled=$false}
Set-FixtureConfig 'soft-restart' @{enabled=$false}
Set-FixtureConfig backups @{enabled=$false}
Set-FixtureConfig security @{enableDimensionsPacket=$false}
Set-FixtureConfig sandboxes @{backendBindAddress='127.0.0.1'}
$enabled = $CharacterMode -eq 'authoritative'
@{enabled=$enabled; requireLogin=$enabled; requireCharacterReady=$enabled; authoritative=$enabled} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $deploy 'config/primary-character-persistence.json') -Encoding UTF8
Set-FixtureConfig network @{useOrderedTcp=[bool]$OrderedTcp}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
try { $listener.Start(); $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
$password = if ($JoinMode -eq 'password') { [Guid]::NewGuid().ToString('N') } else { $null }
$arguments = "--vega-ui=plain -world `"$world`" -savedirectory `"$nativeSave`" -ip 127.0.0.1 -port $port -maxplayers 4 -noupnp"
if ($password) { $arguments += " -password $password" }
$manifest = [ordered]@{
    clientProductVersion=$version; clientAssemblyVersion=$assembly.Version.ToString(); clientExecutable=$clientPath
    clientSha256=(Get-FileHash -LiteralPath $clientPath -Algorithm SHA256).Hash
    serverSha256=(Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash
    sourceWorldSha256=(Get-FileHash -LiteralPath $worldSource -Algorithm SHA256).Hash
    fixtureWorldSha256=(Get-FileHash -LiteralPath $world -Algorithm SHA256).Hash
    caseRoot=$caseRoot; serverRoot=$deploy; nativeSaveDirectory=$nativeSave; clientSaveDirectory=$clientSave
    host='127.0.0.1'; port=$port; fixturePassword=$password; characterMode=$CharacterMode; joinMode=$JoinMode
    transport=if ($OrderedTcp) {'ordered'} else {'native'}
    serverReadiness='not-run'; actualClientQualification='pending-manual'
    checks=[ordered]@{ join='pending'; firstLogin='pending'; initialCharacter='pending'; inventoryLifeManaLoadout='pending'; logout='pending';
        sameSocketRelog='pending'; disconnectReconnect='pending'; differentAccount='pending'; unauthorizedSandboxJoin='pending' }
}
$manifestPath = Join-Path $caseRoot 'qualification.json'
function Save-Manifest { $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8 }
Save-Manifest
$clientCommand = "& '" + $clientPath.Replace("'","''") + "' -savedirectory '" + $clientSave.Replace("'","''") + "'"
@("Launch only when you choose to run the graphical client:",
    ("Push-Location '" + (Split-Path -Parent $clientPath).Replace("'","''") + "'"), $clientCommand, 'Pop-Location',
    "Use a new disposable local character (never select or move a Steam Cloud character).",
    "Join via IP 127.0.0.1:$port", "Fixture password: $password", "Record observations in qualification.json.") |
    Set-Content -LiteralPath (Join-Path $caseRoot 'client-manual.txt') -Encoding UTF8
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $hostPath
$start.WorkingDirectory = $deploy
$start.UseShellExecute = $false
$start.Arguments = $arguments
# Remove inherited deployment settings from the child process only.
foreach ($key in @($start.EnvironmentVariables.Keys)) {
    if ($key.StartsWith('VEGA_', [StringComparison]::OrdinalIgnoreCase)) { $start.EnvironmentVariables.Remove($key) }
}
if ($RunServer -or $SmokeReady) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $processStarted = $false
    $stdout = $null; $stderr = $null
    if ($SmokeReady) {
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
    }
    try {
        Write-Host "Fixture: $caseRoot; loopback port $port; password mode $JoinMode; SSC $CharacterMode"
        if (-not $process.Start()) { throw 'Disposable server did not start.' }
        $processStarted = $true
        if ($RunServer) {
            # A user explicitly chose interactive server mode; the script never launches the client.
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw "Server exited with code $($process.ExitCode)." }
            $manifest.serverReadiness = 'interactive-exit'
        }
        else {
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            $logPath = Join-Path $deploy 'logs/vega-operations.jsonl'
            $ready = $false
            while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                if ((Test-Path -LiteralPath $logPath) -and (Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue) -match 'NetworkReady') { $ready = $true; break }
                Start-Sleep -Milliseconds 200
            }
            if (-not $ready -or $process.HasExited) { throw 'Disposable server did not reach NetworkReady.' }
            if (-not (Test-Path -LiteralPath (Join-Path $deploy 'data/vega.db'))) { throw 'Isolated SQLite database was not created.' }
            $log = Get-Content -LiteralPath $logPath -Raw
            if (($log -match 'Native server-side character mode enabled') -ne $enabled) { throw 'Observed SSC startup mode differs from requested mode.' }
            Start-Sleep -Seconds 3
            $bytes = [Text.Encoding]::Unicode.GetBytes("shutdown stock SSC readiness smoke`n")
            $process.StandardInput.BaseStream.Write($bytes,0,$bytes.Length); $process.StandardInput.BaseStream.Flush()
            if (-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0) { throw 'Disposable server did not exit cleanly.' }
            $manifest.serverReadiness = 'passed'
        }
    }
    catch { $manifest.serverReadiness = 'failed'; throw }
    finally {
        if ($processStarted -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if ($stdout) { $stdout.Result | Set-Content -LiteralPath (Join-Path $caseRoot 'stdout.log') -Encoding UTF8 }
        if ($stderr) { $stderr.Result | Set-Content -LiteralPath (Join-Path $caseRoot 'stderr.log') -Encoding UTF8 }
        $process.Dispose(); Save-Manifest
    }
}
Write-Output "Qualification manifest: $manifestPath"
