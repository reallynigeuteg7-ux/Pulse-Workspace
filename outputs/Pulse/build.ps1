param([switch]$Test)
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkPath 'csc.exe'))) {
    $frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'The .NET Framework C# compiler was not found. Install .NET Framework 4.8 or build Pulse.csproj in Visual Studio.' }
$references = @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo', '/utf8output', '/target:winexe', '/optimize+', '/platform:anycpu', '/warn:4', ('/out:' + (Join-Path $PSScriptRoot 'Pulse.exe')), ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')), ('/resource:' + (Join-Path $PSScriptRoot 'src\Dashboard.xaml') + ',Pulse.Dashboard.xaml'))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $frameworkPath $reference) }
$compilerArgs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'App.config') -Destination (Join-Path $PSScriptRoot 'Pulse.exe.config')
Write-Output ('Built: ' + (Join-Path $PSScriptRoot 'Pulse.exe'))
if ($Test) {
    $testOutput = Join-Path $PSScriptRoot 'tests\Pulse.Tests.exe'
    $testArgs = @('/nologo', '/utf8output', '/target:exe', '/optimize+', '/warn:4', ('/out:' + $testOutput))
    foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Net.Http.dll')) { $testArgs += '/reference:' + (Join-Path $frameworkPath $reference) }
    $testArgs += (Join-Path $PSScriptRoot 'src\LoadEngine.cs'), (Join-Path $PSScriptRoot 'tests\EngineTests.cs')
    & $compilerPath @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
