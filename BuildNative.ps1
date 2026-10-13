param([string]$OutputDirectory='bin',[switch]$StressTests,[switch]$TestFixtures)
$ErrorActionPreference='Stop'
$out=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
if(-not $out.StartsWith($PSScriptRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must stay inside project'}
$vswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $vs){throw 'Visual C++ build tools are required'}
$msvc=Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$version=(Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'cppwinrt\winrt\Windows.UI.UIAutomation.Core.h')} | Sort-Object Name -Descending | Select-Object -First 1).Name
[void][IO.Directory]::CreateDirectory($out)
$include=@(('/I'+(Join-Path $msvc.FullName 'include')))
foreach($part in @('ucrt','um','shared','winrt','cppwinrt')){$include+=('/I'+(Join-Path $sdk ('Include\'+$version+'\'+$part)))}
$lib=@(('/LIBPATH:'+(Join-Path $msvc.FullName 'lib\x64')),('/LIBPATH:'+(Join-Path $sdk ('Lib\'+$version+'\um\x64'))),('/LIBPATH:'+(Join-Path $sdk ('Lib\'+$version+'\ucrt\x64'))))
$defines=@('/DUNICODE','/D_UNICODE'); if($StressTests){$defines+='/DSEARCH_STRESS_TEST'}
& (Join-Path $msvc.FullName 'bin\Hostx64\x64\cl.exe') /nologo /EHsc /std:c++17 /MT /O2 /utf-8 /LD $defines $include ('/Fo'+(Join-Path $out 'SearchNative.obj')) ('/Fe'+(Join-Path $out 'SearchNative.dll')) (Join-Path $PSScriptRoot 'SearchNative.cpp') /link $lib windowsapp.lib uiautomationcore.lib ole32.lib oleaut32.lib user32.lib
if($LASTEXITCODE -ne 0){throw 'Native helper build failed'}
if($TestFixtures) {
 & (Join-Path $msvc.FullName 'bin\Hostx64\x64\cl.exe') /nologo /EHsc /std:c++17 /MT /O2 /utf-8 /DUNICODE /D_UNICODE $include ('/Fo'+(Join-Path $out 'TestShellLocator.obj')) ('/Fe'+(Join-Path $out 'TestShellLocator.exe')) (Join-Path $PSScriptRoot 'probes\TestShellLocator.cpp') /link $lib windowsapp.lib uiautomationcore.lib ole32.lib oleaut32.lib user32.lib
 if($LASTEXITCODE -ne 0){throw 'Native test fixture build failed'}
}
