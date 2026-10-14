# Builds dist\ZayitMove.exe: one file, with Zayit's font and pictures inside, on the .NET Framework 4.8 that comes with
# Windows 10 and 11 (its compiler is part of Windows too). -Install also copies it to %LOCALAPPDATA%\Programs\Zayit Move
# and adds a Start menu shortcut.
param([switch]$Install)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force "$root\dist" | Out-Null

$refs = @("/r:$fw\WPF\PresentationFramework.dll", "/r:$fw\WPF\PresentationCore.dll", "/r:$fw\WPF\WindowsBase.dll",
          "/r:$fw\System.Xaml.dll", "/r:System.Core.dll")
$res = 'notoserifhebrew.ttf', 'wallpaper.jpg', 'wallpaper_dark.jpg', 'branch.png', 'app.png' | ForEach-Object { "/resource:$root\assets\$_,$_" }
$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
& "$fw\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe /platform:anycpu "/win32icon:$root\assets\app.ico" "/win32manifest:$root\src\app.manifest" `
    @refs @res "/out:$root\dist\ZayitMove.exe" @src
if ($LASTEXITCODE) { throw "build failed" }
Get-Item "$root\dist\ZayitMove.exe" | ForEach-Object { "{0}  ({1:N2} MB)" -f $_.FullName, ($_.Length / 1MB) }

if ($Install) {
    $dir = "$env:LOCALAPPDATA\Programs\Zayit Move"
    Get-Process ZayitMove -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item "$root\dist\ZayitMove.exe" "$dir\ZayitMove.exe" -Force
    $lnk = "$([Environment]::GetFolderPath('Programs'))\העברת הספרייה של זית.lnk"
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sh.TargetPath = "$dir\ZayitMove.exe"
    $sh.WorkingDirectory = $dir
    $sh.Description = 'העברת הספרייה של זית למקום אחר, וחזרה'
    $sh.Save()
    "installed: $dir\ZayitMove.exe, shortcut in the Start menu"
}
