param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'tests\screenshots'))
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkPath 'csc.exe'))) { $frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$testPath = Join-Path $PSScriptRoot 'Pulse.UiTests.exe'
$compilerArgs = @('/nologo', '/utf8output', '/target:exe', '/main:Pulse.UiTests', ('/out:' + $testPath), ('/resource:' + (Join-Path $PSScriptRoot 'src\Dashboard.xaml') + ',Pulse.Dashboard.xaml'))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')) { $compilerArgs += '/reference:' + (Join-Path $frameworkPath $reference) }
$compilerArgs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$compilerArgs += (Join-Path $PSScriptRoot 'tests\EngineTests.cs'), (Join-Path $PSScriptRoot 'tests\UiTests.cs')
try {
    & (Join-Path $frameworkPath 'csc.exe') @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
    & $testPath $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
} finally {
    if (Test-Path -LiteralPath $testPath) { Remove-Item -LiteralPath $testPath }
}
