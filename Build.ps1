param([string]$OutputDirectory='bin', [switch]$SkipTests)
$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$bin=[IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
if (-not $bin.StartsWith($taskRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay inside this project.' }
if (-not (Test-Path -LiteralPath $bin)) { [void](New-Item -ItemType Directory -Path $bin) }
$references=@('/r:System.dll','/r:System.Core.dll','/r:Microsoft.CSharp.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll',('/r:'+ (Join-Path $framework 'WPF\UIAutomationClient.dll')),('/r:'+ (Join-Path $framework 'WPF\UIAutomationTypes.dll')),('/r:'+ (Join-Path $framework 'WPF\WindowsBase.dll')))
$icon=Join-Path $taskRoot 'store\Assets\SearchLayout.ico'
& (Join-Path $taskRoot 'BuildNative.ps1') -OutputDirectory $OutputDirectory -TestFixtures:(-not $SkipTests)
$core=@((Join-Path $taskRoot 'Core.cs'),(Join-Path $taskRoot 'KeyboardLayouts.cs'),(Join-Path $taskRoot 'SearchAtomicNative.cs'))
$application=@((Join-Path $taskRoot 'AssemblyInfo.cs'),(Join-Path $taskRoot 'App.cs'),(Join-Path $taskRoot 'StartupService.cs'))
if (-not (Test-Path -LiteralPath $icon)) { throw 'Run store/GenerateAssets.ps1 before building.' }
& $compiler /nologo /utf8output /target:winexe /platform:x64 /optimize+ ('/win32manifest:'+(Join-Path $taskRoot 'app.manifest')) ('/win32icon:'+$icon) ('/out:'+(Join-Path $bin 'SearchLayout.exe')) $references $core $application
if ($LASTEXITCODE -ne 0) { throw 'Prototype build failed' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'App.config') -Destination (Join-Path $bin 'SearchLayout.exe.config')
& $compiler /nologo /utf8output /target:winexe /platform:x64 /optimize+ ('/win32manifest:'+(Join-Path $taskRoot 'app.manifest')) ('/out:'+(Join-Path $bin 'StartupLauncher.exe')) /r:System.dll (Join-Path $taskRoot 'StartupLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Startup launcher build failed' }
if (-not $SkipTests -and (Test-Path -LiteralPath (Join-Path $taskRoot 'Tests.cs'))) {
  & $compiler /nologo /utf8output /target:exe /platform:x64 /optimize+ /main:SearchLayout.Tests /r:System.Web.Extensions.dll ('/out:'+(Join-Path $bin 'SearchLayout.Tests.exe')) $references $core (Join-Path $taskRoot 'Tests.cs')
  if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
}
if (-not $SkipTests -and (Test-Path -LiteralPath (Join-Path $taskRoot 'UiTests.cs'))) {
  & $compiler /nologo /utf8output /target:exe /platform:x64 /optimize+ /main:SearchLayout.UiTests /r:System.Web.Extensions.dll ('/win32manifest:'+(Join-Path $taskRoot 'app.manifest')) ('/win32icon:'+$icon) ('/out:'+(Join-Path $bin 'SearchLayout.UiTests.exe')) $references $core $application (Join-Path $taskRoot 'UiTests.cs')
  if ($LASTEXITCODE -ne 0) { throw 'UI test build failed' }
  Copy-Item -LiteralPath (Join-Path $taskRoot 'App.config') -Destination (Join-Path $bin 'SearchLayout.UiTests.exe.config')
}
if (-not $SkipTests) {
  & $compiler /nologo /utf8output /target:exe /platform:x64 /optimize+ /main:SearchLayout.StartupTests /r:System.dll /r:System.Core.dll /r:Microsoft.CSharp.dll ('/out:'+(Join-Path $bin 'StartupTests.exe')) (Join-Path $taskRoot 'StartupService.cs') (Join-Path $taskRoot 'StartupTests.cs')
  if ($LASTEXITCODE -ne 0) { throw 'Startup test build failed' }
  & $compiler /nologo /utf8output /target:exe /platform:x64 /optimize+ /main:SearchLayout.KeyboardLayoutTests /r:System.Core.dll /r:System.Web.Extensions.dll ('/out:'+(Join-Path $bin 'KeyboardLayoutTests.exe')) (Join-Path $taskRoot 'KeyboardLayouts.cs') (Join-Path $taskRoot 'KeyboardLayoutTests.cs')
  if ($LASTEXITCODE -ne 0) { throw 'Keyboard layout test build failed' }
}
Get-Item -LiteralPath (Join-Path $bin 'SearchLayout.exe') | Select-Object FullName,Length,LastWriteTime
