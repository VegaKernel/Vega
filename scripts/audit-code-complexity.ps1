param([string]$OutputFile)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$auditDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('vega-complexity-audit-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $auditDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'complexity-audit.cs') -Destination (Join-Path $auditDirectory 'Program.cs')
$projectPath = Join-Path $auditDirectory 'ComplexityAudit.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.CodeAnalysis"><HintPath>$(MSBuildSDKsPath)/../Roslyn/bincore/Microsoft.CodeAnalysis.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis.CSharp"><HintPath>$(MSBuildSDKsPath)/../Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll</HintPath></Reference>
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $projectPath -Encoding UTF8
$report = & dotnet run --project $projectPath -c Release -- $repository
if ($LASTEXITCODE -ne 0) { throw 'Complexity audit failed.' }
if ($OutputFile) { $report | Set-Content -LiteralPath $OutputFile -Encoding UTF8 } else { $report }
