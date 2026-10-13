# Publishes ExcelSearch as a framework-dependent win-x64 Release build into <repo>\publish\.   Scripts\publish.ps1
#   - needs the .NET 8 Desktop Runtime (or newer) on the target machine; no single file, no trimming
#   - the package uses the Production settings: edit src\ExcelSearch\appsettings.Production.json first (it holds the sa password and is NOT in git)
# Uses .NET APIs for paths because the repo folder name contains [D].
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$projectDir = [IO.Path]::Combine($root, 'src', 'ExcelSearch')
$project = [IO.Path]::Combine($projectDir, 'ExcelSearch.csproj')
$settings = [IO.Path]::Combine($projectDir, 'appsettings.Production.json')
$out = [IO.Path]::Combine($root, 'publish')

if (-not [IO.File]::Exists($settings)) {
    Write-Host "appsettings.Production.json is missing: $settings" -ForegroundColor Red
    Write-Host 'Copy appsettings.Production.sample.json to appsettings.Production.json (same folder) and put the real connection string in it.' -ForegroundColor Red
    exit 1
}

# Start clean so nothing from an earlier publish (or an old settings file) survives in the package.
if ([IO.Directory]::Exists($out)) { [IO.Directory]::Delete($out, $true) }

Write-Host 'Publishing (Release, win-x64, framework-dependent)...'
& dotnet publish $project -c Release -r win-x64 --self-contained false -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'Publish failed.' -ForegroundColor Red; exit $LASTEXITCODE }

# The package must carry the Production settings and never the Development ones (local credentials).
if (-not [IO.File]::Exists([IO.Path]::Combine($out, 'appsettings.Production.json'))) {
    Write-Host 'appsettings.Production.json is not in the publish folder.' -ForegroundColor Red; exit 1
}
if ([IO.File]::Exists([IO.Path]::Combine($out, 'appsettings.Development.json'))) {
    Write-Host 'appsettings.Development.json ended up in the publish folder; refusing to leave it there.' -ForegroundColor Red; exit 1
}

$bytes = ([IO.Directory]::EnumerateFiles($out, '*', 'AllDirectories') | ForEach-Object { ([IO.FileInfo]$_).Length } | Measure-Object -Sum).Sum
$files = [IO.Directory]::GetFiles($out, '*', 'AllDirectories').Length
Write-Host ("Done: {0} ({1} files, {2:N1} MB)" -f $out, $files, ($bytes / 1MB)) -ForegroundColor Green
Write-Host 'Needs the .NET 8 Desktop Runtime (x64) or newer on the target machine. Keep appsettings.Production.json private: it holds the SQL Server password.'
