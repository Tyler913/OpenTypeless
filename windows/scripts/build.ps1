<#
Builds OpenTypeless and installs it as the ONE copy on this PC: %LOCALAPPDATA%\Programs\OpenTypeless.

  scripts\build.ps1                 build, install, add a Start menu shortcut and launch it
  scripts\build.ps1 -Package        build a distributable zip in dist\ (doesn't touch the installed copy)
  scripts\build.ps1 -Arch arm64     build for Windows on ARM (default: this PC's architecture)

The app is self-contained (it carries the .NET runtime and the Windows App SDK), so the zip runs on any
Windows 10 1904x / Windows 11 PC without installing anything else. OpenTypeless.Cli.exe ships next to it.
#>
param(
    [switch]$Package,
    [ValidateSet('x64', 'arm64')][string]$Arch = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' })
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$AppName = 'OpenTypeless'
$Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$Rid = "win-$Arch"
$Platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
$Staging = ".build\publish\$AppName"
$Installed = Join-Path $env:LOCALAPPDATA "Programs\$AppName"

Write-Host "==> Testing"
dotnet test tests\TypelessCore.Tests --nologo -v quiet
if ($LASTEXITCODE) { throw 'tests failed' }

Write-Host "==> Compiling (release, $Rid)"
if (Test-Path $Staging) { Remove-Item -Recurse -Force $Staging }
dotnet publish src\OpenTypeless\OpenTypeless.csproj -c Release -r $Rid -p:Platform=$Platform --self-contained -o $Staging --nologo -v quiet
if ($LASTEXITCODE) { throw 'app build failed' }
dotnet publish src\OpenTypeless.Cli\OpenTypeless.Cli.csproj -c Release -r $Rid -p:Platform=$Platform --self-contained -o $Staging --nologo -v quiet
if ($LASTEXITCODE) { throw 'CLI build failed' }
Get-ChildItem $Staging -Filter *.pdb -Recurse | Remove-Item -Force
# WinUI ships its control strings in ~85 languages; the app itself is English / Simplified Chinese.
Get-ChildItem $Staging -Directory | Where-Object { $_.Name -match "^[a-z]{2,3}(-[A-Za-z]+)*-[A-Z]{2}$" -and $_.Name -notmatch "^(en-US|en-GB|zh-CN|zh-TW)$" } | Remove-Item -Recurse -Force

if ($Package) {
    New-Item -ItemType Directory -Force dist | Out-Null
    $zip = "dist\$AppName-$Version-windows-$Arch.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path $Staging -DestinationPath $zip -CompressionLevel Optimal
    $size = '{0:N1} MB' -f ((Get-Item $zip).Length / 1MB)
    Write-Host "OK Packaged $zip ($size)"
    Write-Host "  SHA-256: $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant())"
    exit 0
}

Write-Host "==> Replacing $Installed"
Get-Process $AppName -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(3000)) { $_ | Stop-Process -Force }
}
if (Test-Path $Installed) { Remove-Item -Recurse -Force $Installed }
New-Item -ItemType Directory -Force (Split-Path $Installed) | Out-Null
Move-Item $Staging $Installed

Write-Host "==> Start menu shortcut"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $Installed "$AppName.exe"
$link.WorkingDirectory = $Installed
$link.Description = 'Voice typing that holds up on long dictation'
$link.Save()

Start-Process (Join-Path $Installed "$AppName.exe")
Write-Host "OK Installed $AppName $Version -> $Installed"
