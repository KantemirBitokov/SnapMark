<#
.SYNOPSIS
  Готовит релиз SnapMark: поднимает версию, дописывает CHANGELOG, проверяет сборку, коммитит и ставит тег.

.DESCRIPTION
  Дальше всё делает GitHub: тег vX.Y.Z запускает «Build SnapMark» (.github/workflows/release.yml),
  тот собирает Windows/macOS/Linux, публикует релиз с заметками из CHANGELOG.md,
  после чего job «manifests» пересобирает winget / Homebrew / AUR и коммитит их в main.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\prepare-release.ps1 -Version 1.9.25 `
      -Notes "- **Fix:** поворот маркера больше не перекрывает панель.", "- **UI:** ..."

.EXAMPLE
  # только подготовить и проверить, без пуша
  .\tools\prepare-release.ps1 -Version 1.9.25 -Notes "- **Fix:** ..." -NoPush
#>
[CmdletBinding()]
param(
    # Новая версия, например 1.9.25
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # Строки CHANGELOG для этой версии; каждая — отдельный пункт списка.
    [string[]]$Notes = @(),

    # Не собирать локально (быстрее, но без проверки компиляции).
    [switch]$SkipBuild,

    # Закоммитить и поставить тег, но не пушить.
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Write-Utf8NoBom([string]$Path, [string[]]$Lines) {
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines($Path, $Lines, $enc)
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Версия должна быть вида X.Y.Z, получено: $Version"
}

$projPath = Join-Path $root 'src\SnapMark.App\SnapMark.App.csproj'
$changelogPath = Join-Path $root 'CHANGELOG.md'

$dirty = @(git status --porcelain)
if ($dirty.Count -gt 0) {
    throw "Рабочее дерево не чистое — сначала закоммить или убери изменения:`n$($dirty -join "`n")"
}
if (@(git tag --list "v$Version").Count -gt 0) {
    throw "Тег v$Version уже существует"
}

$current = [regex]::Match((Get-Content -Raw $projPath), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $current) { throw "Не нашёл <Version> в $projPath" }
if ([version]$Version -le [version]$current) {
    throw "Новая версия $Version должна быть больше текущей $current"
}

Write-Host "SnapMark $current -> $Version" -ForegroundColor Cyan

# 1. Версия в csproj — источник правды для сборки, релиза и манифестов.
$text = Get-Content -Raw $projPath
$text = $text -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
Set-Content -Path $projPath -Value $text -Encoding utf8 -NoNewline

# 2. Раздел в CHANGELOG.md — из него packaging/release-notes.sh делает описание релиза.
$lines = @(Get-Content $changelogPath)
$idx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') { $idx = $i; break }
}
if ($idx -lt 0) { throw 'В CHANGELOG.md не найден ни один раздел "## версия"' }

$entry = @("## $Version")
if ($Notes.Count -gt 0) { $entry += $Notes } else { $entry += '- Maintenance release.' }

$new = @()
if ($idx -gt 0) { $new += $lines[0..($idx - 1)] }
$new += $entry
$new += ''
if ($idx -lt $lines.Count) { $new += $lines[$idx..($lines.Count - 1)] }
Write-Utf8NoBom $changelogPath $new

# 3. Проверка сборки — те же предупреждения, что в CI.
if (-not $SkipBuild) {
    Write-Host 'Сборка...' -ForegroundColor Cyan
    dotnet build src/SnapMark.App -c Release -warnaserror:CS8602,CS8604,CS0168,CS0219
    if ($LASTEXITCODE -ne 0) { throw 'Сборка упала — релиз не готовим' }
}

# 4. Коммит и тег; тег и запускает сборку на GitHub.
git add CHANGELOG.md src/SnapMark.App/SnapMark.App.csproj
git commit -m "Prepare release $Version"
if ($LASTEXITCODE -ne 0) { throw 'git commit не удался' }
git tag -a "v$Version" -m "SnapMark $Version"
if ($LASTEXITCODE -ne 0) { throw 'git tag не удался' }

if ($NoPush) {
    Write-Host "Готово локально. Пуш: git push origin HEAD --follow-tags" -ForegroundColor Yellow
    return
}

git push origin HEAD --follow-tags
if ($LASTEXITCODE -ne 0) { throw 'git push не удался' }

Write-Host "Тег v$Version отправлен — сборка запустится сама: https://github.com/KantemirBitokov/SnapMark/actions" -ForegroundColor Green
