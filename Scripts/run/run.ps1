# Builds and launches ExcelSearch.   Scripts\run\run.ps1 -Configuration Debug|Release
#   Debug   -> Development settings (appsettings.Development.json)
#   Release -> Production settings  (appsettings.Production.json)
# Uses .NET APIs for paths because the repo folder name contains [D].
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = [IO.Path]::Combine($root, 'src', 'ExcelSearch', 'ExcelSearch.csproj')
$exe = [IO.Path]::Combine($root, 'src', 'ExcelSearch', 'bin', $Configuration, 'net8.0-windows', 'ExcelSearch.exe')

Write-Host "Building ($Configuration)..."
& dotnet build $project -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.'; exit $LASTEXITCODE }

$psi = New-Object Diagnostics.ProcessStartInfo $exe
$psi.WorkingDirectory = Split-Path -Parent $exe
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['DOTNET_ENVIRONMENT'] = if ($Configuration -eq 'Debug') { 'Development' } else { 'Production' }
[void][Diagnostics.Process]::Start($psi)
Write-Host "Started $Configuration ($($psi.EnvironmentVariables['DOTNET_ENVIRONMENT']) settings)."
