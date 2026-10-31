; ─────────────────────────────────────────────────────────────────────────────
;  SnapMark — установщик (Inno Setup 6, https://jrsoftware.org/isdl.php)
;
;  Собирается скриптом build-release.ps1 из корня репозитория:
;     powershell -ExecutionPolicy Bypass -File .\build-release.ps1
;  или вручную: сначала dotnet publish (см. build-release.ps1) в папку publish\,
;  затем открыть этот файл в Inno Setup Compiler и нажать Build → Compile (Ctrl+F9).
;
;  Ставится в профиль пользователя (%LocalAppData%\Programs\SnapMark) — без прав администратора,
;  как Discord, VS Code (user setup) и т. п. Обновление — просто запуск нового установщика поверх.
; ─────────────────────────────────────────────────────────────────────────────

#define AppName      "SnapMark"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "SadlyBear.pro"
#define AppURL       "https://sadlybear.pro"
#define AppExe       "SnapMark.exe"

[Setup]
; AppId — постоянный идентификатор программы. НЕ МЕНЯТЬ между версиями, иначе обновление встанет второй копией.
AppId={{C41519DA-88B5-4939-906E-4B30FE5DD2B4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppCopyright=© {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}

PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir=..\release
OutputBaseFilename=SnapMark-{#AppVersion}-Setup
SetupIconFile=..\src\SnapMark.App\Assets\snapmark.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes

; Программа живёт в трее — перед установкой/удалением её нужно закрыть.
; Этот мьютекс создаёт SnapMark (Program.cs); установщик предложит закрыть программу.
AppMutex=SnapMark.SingleInstance.v1
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.AutoStart=Start SnapMark when I sign in to Windows
ru.AutoStart=Запускать SnapMark при входе в Windows
de.AutoStart=SnapMark beim Anmelden bei Windows starten
fr.AutoStart=Lancer SnapMark à l’ouverture de session Windows
it.AutoStart=Avvia SnapMark all’accesso a Windows
es.AutoStart=Iniciar SnapMark al iniciar sesión en Windows

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStart}"

[Files]
; Всё содержимое папки publish (single-file exe + возможные сопутствующие файлы).
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Тот же ключ, что использует SnapMark (Настройки → Основные → «Запускать при входе в систему»).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SnapMark"; \
  ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; На случай если программа всё ещё в трее.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillSnapMark"

[UninstallDelete]
; Скачанный самим SnapMark ffmpeg и временные файлы. Настройки (%AppData%\SnapMark) оставляем —
; при переустановке всё вернётся как было.
Type: filesandordirs; Name: "{localappdata}\SnapMark"

[Code]
// Первая установка: язык интерфейса SnapMark = язык, выбранный в установщике.
// Если настройки уже есть (обновление), ничего не трогаем.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir: String;
begin
  if CurStep = ssPostInstall then
  begin
    Dir := ExpandConstant('{userappdata}\SnapMark');
    if not FileExists(Dir + '\settings.json') then
    begin
      ForceDirectories(Dir);
      SaveStringToFile(Dir + '\settings.json', '{ "Language": "' + ActiveLanguage + '" }', False);
    end;
  end;
end;

// Отключённый в настройках автозапуск убираем и при удалении (значение могла создать сама программа).
// Если PrtSc у «Ножниц» отключал сам SnapMark (кнопка «Отключить автоматически») — возвращаем как было.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Flag: Cardinal;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SnapMark');
    if RegQueryDWordValue(HKEY_CURRENT_USER, 'Software\SnapMark', 'RestorePrintScreenSnipping', Flag) and (Flag = 1) then
      RegWriteDWordValue(HKEY_CURRENT_USER, 'Control Panel\Keyboard', 'PrintScreenKeyForSnippingEnabled', 1);
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\SnapMark');
  end;
end;
