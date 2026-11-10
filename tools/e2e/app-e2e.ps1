# Проверка собранного SnapMark.exe как у пользователя: запуск, PrtSc, команды, настройки, пошаговая инструкция.
# Используется для сравнения обычной и урезанной сборок (trim-check.yml). Результаты — в $out (e2e.txt, снимки экрана).
param([string]$exe, [string]$out)
$ErrorActionPreference = 'Continue'
$env:SNAPMARK_TEST_INPUT = '1'
New-Item -ItemType Directory -Force $out | Out-Null
$report = Join-Path $out 'e2e.txt'
function Say($s) { $s | Tee-Object -Append $report }
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace E -Name U -MemberDefinition @'
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  public struct RECT { public int L, T, R, B; }
  public static System.Collections.Generic.List<string> Windows(uint pid) {
    var list = new System.Collections.Generic.List<string>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); RECT r;
      if (p == pid && IsWindowVisible(h) && GetWindowRect(h, out r)) { var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256);
        list.Add(h.ToInt64() + "|" + sb + "|" + (r.R - r.L) + "x" + (r.B - r.T)); } return true; }, IntPtr.Zero);
    return list; }
'@
$W = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
function Shot($name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save((Join-Path $out "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function Key($vk) { [E.U]::keybd_event($vk, 0, 0, [IntPtr]::Zero); [E.U]::keybd_event($vk, 0, 2, [IntPtr]::Zero) }
function Click($x, $y) { [E.U]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 80; [E.U]::mouse_event(2,0,0,0,[IntPtr]::Zero); [E.U]::mouse_event(4,0,0,0,[IntPtr]::Zero) }
function Wins($p) { [E.U]::Windows([uint32]$p.Id) }
function Fullscreen($p) { @(Wins $p | Where-Object { [int]($_.Split('|')[2].Split('x')[0]) -ge $W }).Count -gt 0 }
function WaitFor([scriptblock]$cond, $sec) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.Elapsed.TotalSeconds -lt $sec) { if (& $cond) { return $sw.Elapsed.TotalSeconds }; Start-Sleep -Milliseconds 200 }; return -1 }

$errLog = Join-Path $env:APPDATA 'SnapMark\error.log'
Remove-Item $errLog -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'SnapMark\settings.json') -ErrorAction SilentlyContinue

# 1. Запуск и PrtSc.
$sw = [Diagnostics.Stopwatch]::StartNew()
$app = Start-Process $exe -PassThru
$t = -1
while ($sw.Elapsed.TotalSeconds -lt 30) {
  Start-Sleep -Milliseconds 250; Key 0x2C; Start-Sleep -Milliseconds 250
  if (Fullscreen $app) { $t = $sw.Elapsed.TotalSeconds; break }
}
Say ("start-prtsc: " + $(if ($t -ge 0) { 'ok' } else { 'FAIL' }) + " ($([math]::Round($t,1)) s)")
Start-Sleep -Milliseconds 800; Shot 'e2e-overlay'
$fp = [uint32]0; [E.U]::GetWindowThreadProcessId([E.U]::GetForegroundWindow(), [ref]$fp) | Out-Null
Key 0x1B
$closed = WaitFor { -not (Fullscreen $app) } 3
$second = ''
if ($closed -lt 0) { Shot 'e2e-esc-fail'; Key 0x1B; $second = if ((WaitFor { -not (Fullscreen $app) } 3) -ge 0) { 'closed by 2nd Esc' } else { 'still open' } }
$fgName = (Get-Process -Id $fp -ErrorAction SilentlyContinue).ProcessName
$snip = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'ScreenClipping|SnippingTool' } | ForEach-Object ProcessName) -join ','
$fgName = "$fgName; snipping processes: $snip"
# Справка, не проверка: PrtSc жали каждые полсекунды с самого запуска — первые нажатия достаются Windows
# («Ножницы»), и фокус после этого бывает ничей. Так же и в обычной сборке; см. first-shot-esc и prtsc-esc-cycles.
Say ("info-esc-after-early-presses: " + $(if ($closed -ge 0) { 'closed' } else { 'open' }) + " (foreground: $fgName; $second)")
# Пять раз подряд: PrtSc → редактор открылся → Esc → закрылся.
$cycles = 0
for ($k = 1; $k -le 5; $k++) {
  Start-Sleep -Milliseconds 600
  Key 0x2C
  if ((WaitFor { Fullscreen $app } 5) -lt 0) { Shot "e2e-cycle$k-noopen"; continue }
  Start-Sleep -Milliseconds 700
  Key 0x1B
  if ((WaitFor { -not (Fullscreen $app) } 3) -lt 0) { Shot "e2e-cycle$k-noclose"; Key 0x1B; Start-Sleep -Milliseconds 800; continue }
  $cycles++
}
Say ("prtsc-esc-cycles: " + $(if ($cycles -eq 5) { 'ok' } else { 'FAIL' }) + " ($cycles/5)")

# 1б. Первый снимок после спокойного запуска: подождали, пока программа загрузится, и нажали PrtSc один раз.
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2
$app = Start-Process $exe -PassThru
Start-Sleep -Seconds 5
Key 0x2C
if ((WaitFor { Fullscreen $app } 5) -ge 0) {
  Start-Sleep -Milliseconds 800
  Key 0x1B
  Say ("first-shot-esc: " + $(if ((WaitFor { -not (Fullscreen $app) } 3) -ge 0) { 'ok' } else { 'FAIL' }))
  if (Fullscreen $app) { Key 0x1B; Start-Sleep -Milliseconds 800 }
} else { Say "first-shot-esc: FAIL (did not open)" }

# 1в. Двойное нажатие PrtSc: весь экран сразу в буфер и в папку скриншотов, редактор закрывается сам.
$shotDir = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'SnapMark'
$before = @(Get-ChildItem $shotDir -File -ErrorAction SilentlyContinue).Count
powershell.exe -NoProfile -STA -Command "Set-Clipboard -Value 'empty'" | Out-Null
Start-Sleep -Seconds 1
Key 0x2C; Start-Sleep -Milliseconds 200; Key 0x2C
$gone = WaitFor { -not (Fullscreen $app) } 4
Start-Sleep -Seconds 2
$after = @(Get-ChildItem $shotDir -File -ErrorAction SilentlyContinue).Count
$img = powershell.exe -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; `$i = [Windows.Forms.Clipboard]::GetImage(); if (`$i) { '' + `$i.Width + 'x' + `$i.Height }"
Shot 'e2e-double-press'
Say ("double-press: " + $(if ($gone -ge 0 -and $after -gt $before -and "$img" -match 'x') { 'ok' } else { 'FAIL' }) + " (closed=$($gone -ge 0), files $before->$after, clipboard=$img)")
if (Fullscreen $app) { Key 0x1B; Start-Sleep -Milliseconds 800 }

# 2. Команды из командной строки (как у ярлыков и второго запуска).
& $exe --full; $t = WaitFor { Fullscreen $app } 8
Say ("cmd-full: " + $(if ($t -ge 0) { 'ok' } else { 'FAIL' })); Start-Sleep -Milliseconds 800; Shot 'e2e-full'; Key 0x1B; Start-Sleep -Milliseconds 800
& $exe --region; $t = WaitFor { Fullscreen $app } 8
Say ("cmd-region: " + $(if ($t -ge 0) { 'ok' } else { 'FAIL' })); Key 0x1B; Start-Sleep -Milliseconds 800

& $exe --settings
$t = WaitFor { @(Wins $app | Where-Object { $_ -match '\|.*SnapMark.*\|' -and [int]($_.Split('|')[2].Split('x')[0]) -lt $W -and [int]($_.Split('|')[2].Split('x')[0]) -gt 400 }).Count -gt 0 } 8
Say ("cmd-settings: " + $(if ($t -ge 0) { 'ok' } else { 'FAIL' }))
Start-Sleep -Seconds 1; Shot 'e2e-settings'
foreach ($w in (Wins $app)) { $h = [IntPtr][long]$w.Split('|')[0]; $wd = [int]($w.Split('|')[2].Split('x')[0]); if ($wd -gt 400 -and $wd -lt $W) { [E.U]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null } }
Start-Sleep -Seconds 1

# 3. Пошаговая инструкция: три клика → серия в буфере обмена.
& $exe --steps; Start-Sleep -Seconds 3; Shot 'e2e-steps-on'
Click 260 620; Start-Sleep -Milliseconds 1500
Click 900 450; Start-Sleep -Milliseconds 1500
Click 1250 700; Start-Sleep -Milliseconds 2500
& $exe --steps; Start-Sleep -Seconds 5; Shot 'e2e-steps-done'
$text = powershell.exe -NoProfile -STA -Command "Get-Clipboard -Format Text"
$files = @(powershell.exe -NoProfile -STA -Command "Get-Clipboard -Format FileDropList | ForEach-Object { `$_.FullName }") | Where-Object { $_ }
$text | Out-File -Encoding utf8 (Join-Path $out 'e2e-clipboard.txt')
Say ("steps-clipboard: " + $(if ($files.Count -ge 3 -and "$text" -match 'Click') { 'ok' } else { 'FAIL' }) + " (files=$($files.Count))")

# 4. Процесс жив, ошибок в журнале нет.
Say ("alive: " + $(if (-not $app.HasExited) { 'ok' } else { 'FAIL' }))
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
if (Test-Path $errLog) { Copy-Item $errLog (Join-Path $out 'error.log'); Say "error-log: FAIL (see error.log)" } else { Say "error-log: ok (empty)" }
