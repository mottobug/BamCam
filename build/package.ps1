<#
.SYNOPSIS
  Publishes BamCam self-contained for one runtime and packages it for that platform.

.DESCRIPTION
  win-*   : BamCam-<version>-<rid>.zip, plus a setup .exe when Inno Setup (iscc) is available
  linux-* : BamCam-<version>-<rid>.tar.gz with a .desktop entry, icon and install.sh
  osx-*   : BamCam-<version>-<rid>.zip containing an (unsigned) BamCam.app bundle

  Works with Windows PowerShell 5.1 and PowerShell 7 (pwsh) on Windows, Linux and macOS.
  The .app bundle and the Linux archive keep executable permissions only when built on macOS/Linux.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build/package.ps1 -Rid win-x64
  pwsh build/package.ps1 -Rid linux-x64 -Version 1.0.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ($OutDir -eq "") { $OutDir = Join-Path $root "artifacts" }
$project = Join-Path $root "src/BamCam.App/BamCam.App.csproj"
$assets = Join-Path $root "src/BamCam.App/Assets"

if ($Version -eq "") {
    $xml = [xml](Get-Content $project -Raw)
    $Version = ($xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $Version) { $Version = "0.0.0" }
}
# Tags like "v1.2.3" are accepted.
$Version = $Version.TrimStart("v")

$onWindows = [System.IO.Path]::DirectorySeparatorChar -eq '\'
$name = "BamCam-$Version-$Rid"
$publishDir = Join-Path $OutDir "publish/$Rid"
New-Item -ItemType Directory -Force $OutDir | Out-Null
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Write-Host "==> Publishing $Rid ($Version)"
& dotnet publish $project -c $Configuration -r $Rid --self-contained true -p:Version=$Version -o $publishDir -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

function Write-TextLf([string]$path, [string[]]$lines) {
    # LF line endings and no BOM: required for shell scripts, .desktop files and plists.
    [System.IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding $false))
}

function Set-Executable([string]$path) {
    if (-not $onWindows) { & chmod +x $path }
}

$results = @()

if ($Rid -like "win-*") {
    $zip = Join-Path $OutDir "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip
    $results += $zip

    $iscc = Get-Command iscc -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $candidate = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
        if ($env:ProgramFiles -and (Test-Path $candidate)) { $iscc = Get-Item $candidate }
    }
    if ($iscc) {
        $arch = if ($Rid -like "*arm64") { "arm64" } else { "x64compatible" }
        $iss = Join-Path $PSScriptRoot "windows/bamcam.iss"
        & $iscc.Source "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$OutDir" "/DOutputName=$name-setup" "/DArch=$arch" "/DIconFile=$assets\bamcam.ico" $iss
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
        $results += (Join-Path $OutDir "$name-setup.exe")
    }
    else {
        Write-Host "Inno Setup (iscc) not found: skipping the installer, the zip is complete."
    }
}
elseif ($Rid -like "linux-*") {
    $stage = Join-Path $OutDir "stage/$name"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    $appDir = Join-Path $stage "bamcam"
    New-Item -ItemType Directory -Force $appDir | Out-Null
    Copy-Item -Recurse (Join-Path $publishDir "*") $appDir
    Copy-Item (Join-Path $assets "bamcam.png") (Join-Path $stage "bamcam.png")
    Set-Executable (Join-Path $appDir "BamCam")

    Write-TextLf (Join-Path $stage "bamcam.desktop") @(
        "[Desktop Entry]",
        "Type=Application",
        "Name=BamCam",
        "Comment=2.5D/3D CAD/CAM",
        "Exec=@EXEC@ %f",
        "Icon=bamcam",
        "Terminal=false",
        "Categories=Graphics;Engineering;",
        "MimeType=application/x-bamcam-drawing;"
    )
    Write-TextLf (Join-Path $stage "install.sh") @(
        "#!/bin/sh",
        "# Installs BamCam for the current user (~/.local), or system-wide with: sudo ./install.sh --system",
        "set -e",
        "SRC=`$(cd `"`$(dirname `"`$0`")`" && pwd)",
        "if [ `"`$1`" = `"--system`" ]; then PREFIX=/opt/bamcam; SHARE=/usr/share; BIN=/usr/local/bin;",
        "else PREFIX=`"`$HOME/.local/opt/bamcam`"; SHARE=`"`$HOME/.local/share`"; BIN=`"`$HOME/.local/bin`"; fi",
        "mkdir -p `"`$PREFIX`" `"`$SHARE/applications`" `"`$SHARE/icons/hicolor/256x256/apps`" `"`$BIN`"",
        "cp -R `"`$SRC/bamcam`" `"`$PREFIX/`"",
        "chmod +x `"`$PREFIX/bamcam/BamCam`"",
        "cp `"`$SRC/bamcam.png`" `"`$SHARE/icons/hicolor/256x256/apps/bamcam.png`"",
        "sed `"s|@EXEC@|`$PREFIX/bamcam/BamCam|`" `"`$SRC/bamcam.desktop`" > `"`$SHARE/applications/bamcam.desktop`"",
        "ln -sf `"`$PREFIX/bamcam/BamCam`" `"`$BIN/bamcam`"",
        "command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database `"`$SHARE/applications`" || true",
        "echo `"BamCam installed to `$PREFIX (start with: bamcam)`""
    )
    Set-Executable (Join-Path $stage "install.sh")

    $tgz = Join-Path $OutDir "$name.tar.gz"
    if (Test-Path $tgz) { Remove-Item $tgz }
    & tar -czf $tgz -C (Join-Path $OutDir "stage") $name
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    $results += $tgz
}
elseif ($Rid -like "osx-*") {
    $stage = Join-Path $OutDir "stage/$name"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    $app = Join-Path $stage "BamCam.app"
    $macos = Join-Path $app "Contents/MacOS"
    $res = Join-Path $app "Contents/Resources"
    New-Item -ItemType Directory -Force $macos, $res | Out-Null
    Copy-Item -Recurse (Join-Path $publishDir "*") $macos
    Set-Executable (Join-Path $macos "BamCam")

    $iconEntry = @()
    if (Get-Command iconutil -ErrorAction SilentlyContinue) {
        # Build an .icns from the PNG (macOS only).
        $iconset = Join-Path $OutDir "stage/bamcam.iconset"
        New-Item -ItemType Directory -Force $iconset | Out-Null
        foreach ($s in 16, 32, 128, 256) {
            & sips -z $s $s (Join-Path $assets "bamcam.png") --out (Join-Path $iconset "icon_${s}x${s}.png") | Out-Null
            $d = $s * 2
            & sips -z $d $d (Join-Path $assets "bamcam.png") --out (Join-Path $iconset "icon_${s}x${s}@2x.png") | Out-Null
        }
        & iconutil -c icns $iconset -o (Join-Path $res "bamcam.icns")
        $iconEntry = @("  <key>CFBundleIconFile</key>", "  <string>bamcam</string>")
    }
    else {
        Write-Host "iconutil not found (not on macOS): the bundle has no Finder icon."
    }

    Write-TextLf (Join-Path $app "Contents/Info.plist") (@(
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">',
        '<plist version="1.0">',
        '<dict>',
        '  <key>CFBundleName</key>', '  <string>BamCam</string>',
        '  <key>CFBundleDisplayName</key>', '  <string>BamCam</string>',
        '  <key>CFBundleIdentifier</key>', '  <string>org.bamcam.BamCam</string>',
        '  <key>CFBundleExecutable</key>', '  <string>BamCam</string>',
        '  <key>CFBundlePackageType</key>', '  <string>APPL</string>',
        "  <key>CFBundleVersion</key>", "  <string>$Version</string>",
        "  <key>CFBundleShortVersionString</key>", "  <string>$Version</string>",
        '  <key>LSMinimumSystemVersion</key>', '  <string>12.0</string>',
        '  <key>NSHighResolutionCapable</key>', '  <true/>'
    ) + $iconEntry + @('</dict>', '</plist>'))

    $zip = Join-Path $OutDir "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    if (Get-Command ditto -ErrorAction SilentlyContinue) {
        & ditto -c -k --keepParent $app $zip   # keeps permissions and bundle structure
    }
    else {
        Compress-Archive -Path $app -DestinationPath $zip
    }
    $results += $zip
}
else {
    throw "Unsupported runtime identifier '$Rid'."
}

Write-Host "==> Done:"
$results | ForEach-Object { Write-Host "    $_" }
