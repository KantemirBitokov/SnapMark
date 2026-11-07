# ─────────────────────────────────────────────────────────────────────────────
#  SnapMark — сборка релиза: один exe + установщик.
#
#  Нужно один раз установить:
#    • .NET 8 SDK (идёт вместе с Visual Studio 2022)
#    • Inno Setup 6:  winget install JRSoftware.InnoSetup   (или https://jrsoftware.org/isdl.php)
#
#  Запуск из корня репозитория (где лежит SnapMark.sln):
#    powershell -ExecutionPolicy Bypass -File .\build-release.ps1
#
#  Результат:
#    publish-portable\SnapMark.exe           — портативная версия (работает без установки)
#    release\SnapMark-<версия>-Setup.exe     — установщик
#    release\SnapMark-<версия>-portable.zip  — портативная версия в архиве
# ─────────────────────────────────────────────────────────────────────────────
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$proj = 'src\SnapMark.App\SnapMark.App.csproj'
[xml]$xml = Get-Content $proj
$version = ($xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "SnapMark $version" -ForegroundColor Cyan

# 1. Чистая публикация: self-contained (на ПК пользователя .NET не нужен), один exe. Две сборки:
#    • для установщика — ReadyToRun (код скомпилирован заранее: запуск примерно вдвое быстрее, PrtSc срабатывает
#      раньше) и без сжатия внутри exe: его сжимает сам установщик (LZMA2), и сильнее, чем .NET, — установщик
#      получается даже меньше прежнего, а программе не нужно распаковывать себя при каждом запуске;
#    • для портативного zip — как раньше, сжатый exe без ReadyToRun: самый маленький архив.
Remove-Item -Recurse -Force publish, publish-portable, release -ErrorAction SilentlyContinue
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false')
dotnet publish $proj @common -p:PublishReadyToRun=true -o publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }
dotnet publish $proj @common -p:EnableCompressionInSingleFile=true -o publish-portable
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (portable) завершился с ошибкой' }

# 2. Портативный zip.
New-Item -ItemType Directory -Force release | Out-Null
Compress-Archive -Path publish-portable\* -DestinationPath "release\SnapMark-$version-portable.zip"

# 3. Установщик (Inno Setup).
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning 'Inno Setup 6 не найден — установщик не собран. Поставь: winget install JRSoftware.InnoSetup'
    exit 0
}
& $iscc "/DAppVersion=$version" installer\SnapMark.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup завершился с ошибкой' }

Write-Host ''
Write-Host "Готово: release\SnapMark-$version-Setup.exe" -ForegroundColor Green
