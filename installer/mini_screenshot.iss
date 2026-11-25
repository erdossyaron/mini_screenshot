; mini_screenshot telepítő (Inno Setup 6)
; Fordítás: powershell -ExecutionPolicy Bypass -File tools\make-setup.ps1
; Rendszergazdai jog nélkül, csak az aktuális felhasználónak telepít.
;
; A telepítési hely kötött: %LOCALAPPDATA%\Programs\mini_screenshot\Screen.exe.
; Az app ebből ismeri fel, hogy telepítve van (Program.InstallPath); máshonnan indítva
; a saját beépített varázslóját hozná fel. Ezért nincs mappaválasztó oldal.
;
; Felhasználói adatok, amelyekhez eltávolításkor nem nyúl:
;   - {app}\elozmenyek          korábbi képek (előzmények)
;   - a mentési mappa           a mentett PNG-k (alapból Képek\Képernyőképek)
;   - HKCU\Software\mini_screenshot   beállítások

#define AppExe "..\dist\Screen.exe"
#define FullVer GetVersionNumbersString(AppExe)
; 1.2.0.0 -> 1.2, 1.2.3.0 -> 1.2.3
#define AppVer Copy(FullVer, Len(FullVer) - 3, 4) == ".0.0" ? Copy(FullVer, 1, Len(FullVer) - 4) : Copy(FullVer, 1, RPos(".", FullVer) - 1)

[Setup]
; Állandó azonosító: ezzel ismeri fel a frissítést, ne változtasd meg
AppId={{26C0C686-1C98-4843-A371-CA673ED987F5}
AppName=mini_screenshot
AppVersion={#AppVer}
AppVerName=mini_screenshot {#AppVer}
AppPublisher=mini_screenshot
AppPublisherURL=https://github.com/erdossyaron/mini_screenshot
AppUpdatesURL=https://github.com/erdossyaron/mini_screenshot/releases
VersionInfoVersion={#FullVer}
VersionInfoDescription=mini_screenshot telepítő
; PrivilegesRequired=lowest mellett {autopf} = %LOCALAPPDATA%\Programs
DefaultDirName={autopf}\mini_screenshot
DisableDirPage=yes
DefaultGroupName=mini_screenshot
DisableProgramGroupPage=yes
DisableWelcomePage=no
AlwaysShowDirOnReadyPage=yes
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename=mini_screenshot-Setup-{#AppVer}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\Screen.exe
UninstallDisplayName=mini_screenshot
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; A futó példányt a [Code] rész állítja le (kilépés-jelzéssel); ez csak tartalék
CloseApplications=yes

[Languages]
Name: "hu"; MessagesFile: "compiler:Languages\Hungarian.isl"

[Messages]
hu.WelcomeLabel2=Ez a varázsló telepíti a [name/ver] programot.%n%nMinimalista képernyőkép-készítő: a háttérben fut, a PrtSc gombra befagyasztja a képernyőt, vágás és jelölés után egy kattintással vágólapra másol és PNG-be ment.%n%nRendszergazdai jog nem kell. Frissítéskor a korábbi képek és a beállítások megmaradnak.

; Nincs [Tasks] rész:
; - Az "Indítás a Windows-zal" a Beállítások oldal jelölőnégyzete ([Code]), mert az Inno a feladatoknál
;   a korábbi telepítéskori választást venné alapul, nem a valós állapotot (a tálcaikonon azóta átállíthatta).
; - Asztali parancsikon nem kell: tálcán futó app, a Start menü bejegyzés elég.

[Files]
Source: "{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\mini_screenshot"; Filename: "{app}\Screen.exe"; Comment: "Képernyőkép a PrtSc gombra"

[Registry]
; Beállítások: eltávolításkor megmaradnak (nincs uninsdelete jelző)
Root: HKCU; Subkey: "Software\mini_screenshot"; ValueType: string; ValueName: "SaveFolder"; ValueData: "{code:GetSaveFolder}"
; Az automatikus indulásról a felhasználó döntött, az app ne kapcsolja vissza magától.
; Maga a Run bejegyzés a [Code] CurStepChanged részében készül.
Root: HKCU; Subkey: "Software\mini_screenshot"; ValueType: dword; ValueName: "AutostartInitialized"; ValueData: 1

[UninstallDelete]
; Az app által írt napló (nem felhasználói adat); az elozmenyek mappa marad
Type: files; Name: "{app}\hosszu_kep.log"
Type: dirifempty; Name: "{app}"

[Run]
Filename: "{app}\Screen.exe"; Parameters: "--welcome"; Description: "mini_screenshot indítása"; Flags: nowait postinstall skipifsilent

[Code]
const
  SettingsKey = 'Software\mini_screenshot';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunName = 'ScreenPrtSc';
  // Az app egypéldányos mutexe és kilépés-jelzése (Program.cs: MutexName, QuitEventName)
  AppMutexName = 'Screen.PrtSc.SingleInstance';
  QuitEventName = 'mini_screenshot.Quit';
  EVENT_MODIFY_STATE = $0002;

var
  FolderPage: TInputDirWizardPage;
  AutostartBox: TNewCheckBox;

function OpenEvent(dwDesiredAccess: Cardinal; bInheritHandle: Integer; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): Integer;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

// A korábbi mentési mappa (frissítéskor), különben Képek\Képernyőképek
function DefaultSaveFolder: String;
begin
  if not RegQueryStringValue(HKCU, SettingsKey, 'SaveFolder', Result) or (Result = '') then
    Result := ExpandConstant('{userpics}\Képernyőképek');
end;

function GetSaveFolder(Param: String): String;
begin
  Result := Trim(FolderPage.Values[0]);
end;

// Alapból be; csak akkor ki, ha korábban (a tálcaikonon vagy a telepítőben) kikapcsolta
function DefaultAutostart: Boolean;
begin
  Result := RegValueExists(HKCU, RunKey, RunName) or
            not RegValueExists(HKCU, SettingsKey, 'AutostartInitialized');
end;

procedure InitializeWizard;
var
  Edit: TEdit;
begin
  // Beállítások oldal: mentési mappa + automatikus indulás (mint az app beépített varázslójában)
  FolderPage := CreateInputDirPage(wpWelcome,
    'Beállítások', 'Hova mentse a képeket, és induljon-e a Windows-zal?',
    'A Másolás gomb a képet ide is elmenti PNG-ként (ha a mentés kapcsoló be van kapcsolva). ' +
    'Később a tálcaikonon jobb gombbal, a Mentési mappa… ponttal módosíthatod.',
    False, '');
  FolderPage.Add('');
  FolderPage.Values[0] := DefaultSaveFolder;

  Edit := FolderPage.Edits[0];
  AutostartBox := TNewCheckBox.Create(FolderPage);
  AutostartBox.Parent := FolderPage.Surface;
  AutostartBox.Left := Edit.Left;
  AutostartBox.Top := Edit.Top + Edit.Height + ScaleY(24);
  AutostartBox.Width := FolderPage.SurfaceWidth - Edit.Left;
  AutostartBox.Height := ScaleY(20);
  AutostartBox.Caption := 'Indítás a &Windows-zal (így a PrtSc mindig ezt az appot indítja)';
  AutostartBox.Checked := DefaultAutostart;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = FolderPage.ID) and (Trim(FolderPage.Values[0]) = '') then
  begin
    MsgBox('Add meg, hova mentse a képeket.', mbError, MB_OK);
    Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Auto: String;
begin
  if AutostartBox.Checked then Auto := 'igen' else Auto := 'nem';
  Result := MemoDirInfo + NewLine + NewLine +
            'Képek mentési helye:' + NewLine + Space + GetSaveFolder('') + NewLine + NewLine +
            'Indítás a Windows-zal:' + NewLine + Space + Auto;
end;

// A futó példányt udvariasan leállítja (kilépés-jelzés); ha 3 mp alatt nem lép ki, kilövi.
// Csak a telepített exe-t, más "Screen" nevű programot nem.
procedure StopRunningApp;
var
  H: THandle;
  I, ResultCode: Integer;
begin
  if not CheckForMutexes(AppMutexName) then Exit;

  H := OpenEvent(EVENT_MODIFY_STATE, 0, QuitEventName);
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandle(H);
  end;

  for I := 1 to 30 do
  begin
    if not CheckForMutexes(AppMutexName) then Exit;
    Sleep(100);
  end;

  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process Screen -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -eq ''' + ExpandConstant('{app}\Screen.exe') + ''' } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Automatikus indulás (ugyanaz a bejegyzés, amit az app tálcamenüje kezel)
    if AutostartBox.Checked then
      RegWriteStringValue(HKCU, RunKey, RunName, '"' + ExpandConstant('{app}\Screen.exe') + '"')
    else
      RegDeleteValue(HKCU, RunKey, RunName);
    ForceDirectories(GetSaveFolder(''));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Folder, Msg: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp;
    // Az automatikus indulás bejegyzése ne mutasson törölt exe-re. Ha be volt kapcsolva,
    // a "döntött" jelző is törlődik, így egy újratelepítés újra bekapcsolva ajánlja.
    // Ha a felhasználó kikapcsolta, a jelző marad, és újratelepítéskor is kikapcsolva marad.
    if RegValueExists(HKCU, RunKey, RunName) then
    begin
      RegDeleteValue(HKCU, RunKey, RunName);
      RegDeleteValue(HKCU, SettingsKey, 'AutostartInitialized');
    end;
  end;

  // Eltávolítás végén: tájékoztatás, hogy a képek megmaradtak (szándékosan nem törli őket)
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    Msg := 'A mini_screenshot eltávolítva.' + #13#10#13#10 +
           'A képeid megmaradtak, egy újratelepítés után újra elérhetők:';
    if RegQueryStringValue(HKCU, SettingsKey, 'SaveFolder', Folder) and DirExists(Folder) then
      Msg := Msg + #13#10 + '- mentett képek: ' + Folder;
    if DirExists(ExpandConstant('{app}\elozmenyek')) then
      Msg := Msg + #13#10 + '- korábbi képek (előzmények): ' + ExpandConstant('{app}\elozmenyek');
    Msg := Msg + #13#10#13#10 + 'Ha végleg nem kellenek, ezeket a mappákat kézzel törölheted.';
    MsgBox(Msg, mbInformation, MB_OK);
  end;
end;
