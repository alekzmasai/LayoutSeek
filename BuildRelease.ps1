param([string]$Version = '0.3.4', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a numeric release version.' }
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -SkipTests }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$folderName = "LayoutSeek-$Version-windows-x64"
$payload = Join-Path $artifacts $folderName
$zip = Join-Path $artifacts ($folderName + '.zip')
if (Test-Path -LiteralPath $payload) { throw 'Release folder already exists; preserve it and use a new version.' }
if (Test-Path -LiteralPath $zip) { throw 'Release archive already exists.' }
[void][IO.Directory]::CreateDirectory($payload)
foreach ($name in @('SearchLayout.exe','SearchLayout.exe.config','SearchNative.dll','StartupLauncher.exe')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('bin\' + $name)) -Destination (Join-Path $payload $name)
}
foreach ($name in @('README.md','README.ru.md','PRIVACY.md','PRIVACY.ru.md','LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $payload $name)
}
$images = Join-Path $payload 'docs\images'
[void][IO.Directory]::CreateDirectory($images)
foreach ($name in @('app.png','search-prefix.png')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('docs\images\' + $name)) -Destination (Join-Path $images $name)
}
$hashLines = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($payload.Length + 1).Replace('\','/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
})
[IO.File]::WriteAllLines((Join-Path $payload 'SHA256SUMS.txt'), $hashLines, [Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath $payload -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zip + '.sha256'), $hash + '  ' + [IO.Path]::GetFileName($zip) + "`n", [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $zip | Select-Object Name, Length
