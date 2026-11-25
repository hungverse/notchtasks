; NotchTasks per-user installer (Inno Setup 6). Built by build.ps1, which passes:
;   /DAppVersion=<x.y.z>   the version from NotchTasks.csproj
;   /DStage=<dir>          the publish folder holding the app, the sensors helper and the hook
;   /O<dir>                where the setup exe goes
; Per-user, no elevation: it installs to %LOCALAPPDATA%\Programs\NotchTasks and never touches %APPDATA%\NotchTasks.

#ifndef AppVersion
  #define AppVersion "3.0.0"
#endif
#ifndef Stage
  #error Pass /DStage=<publish folder> (build.ps1 does this)
#endif

[Setup]
AppId={{E97D2302-2BE2-4B52-9D51-3899BC467163}
AppName=NotchTasks
AppVerName=NotchTasks {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=NotchTasks
DefaultDirName={localappdata}\Programs\NotchTasks
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
SetupIconFile=..\src\NotchTasks\Assets\NotchTasks.ico
UninstallDisplayIcon={app}\NotchTasks.exe
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
OutputBaseFilename=NotchTasks-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; The app's languages that Inno 6 ships a translation for. Not shipped by Inno: Vietnamese, Chinese (Hans, Hant), Indonesian, Hindi.
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "thai"; MessagesFile: "compiler:Languages\Thai.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[InstallDelete]
; Electron leftovers from the previous install, in the same folder.
Type: filesandordirs; Name: "{app}\resources"
Type: filesandordirs; Name: "{app}\locales"
Type: files; Name: "{app}\*.pak"
Type: files; Name: "{app}\*.dat"
Type: files; Name: "{app}\*.bin"
Type: files; Name: "{app}\chrome_*"
Type: files; Name: "{app}\ffmpeg.dll"
Type: files; Name: "{app}\d3dcompiler_47.dll"
Type: files; Name: "{app}\libEGL.dll"
Type: files; Name: "{app}\libGLESv2.dll"
Type: files; Name: "{app}\vk_swiftshader*"
Type: files; Name: "{app}\vulkan-1.dll"
Type: files; Name: "{app}\LICENSE*"
Type: files; Name: "{app}\Uninstall NotchTasks.exe"

[Icons]
Name: "{userprograms}\NotchTasks"; Filename: "{app}\NotchTasks.exe"; AppUserModelID: "com.notchtasks.windows"
Name: "{userdesktop}\NotchTasks"; Filename: "{app}\NotchTasks.exe"; AppUserModelID: "com.notchtasks.windows"; Tasks: desktopicon

[Run]
; Also after a silent update: the app starts again in place of the one the update closed.
Filename: "{app}\NotchTasks.exe"; Flags: nowait postinstall

[UninstallRun]
Filename: "{app}\NotchTasksHook.exe"; Parameters: "--uninstall-hooks"; RunOnceId: "hooks"; Flags: runhidden waituntilterminated

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';

// True while a process named NotchTasks.exe is listed (find exits 0 only when it finds the image name).
function AppRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // The tools are called by their full path in {sys} (no PATH lookup). {sys} has no spaces, so the paths are not quoted.
  Result := Exec(ExpandConstant('{sys}\cmd.exe'),
    '/C ' + ExpandConstant('{sys}\tasklist.exe') + ' /FI "IMAGENAME eq NotchTasks.exe" /NH | ' +
    ExpandConstant('{sys}\find.exe') + ' /I "NotchTasks.exe" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// A native install is running: ask it to quit (--quit blocks up to 5 s for it to exit), then force it if it is still there.
procedure StopNotchTasks();
var
  ResultCode, I: Integer;
begin
  try
    if FileExists(ExpandConstant('{app}\NotchTasks.exe')) then
      Exec(ExpandConstant('{app}\NotchTasks.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    for I := 1 to 10 do
    begin
      if not AppRunning() then
        Break;
      Sleep(500);
    end;
    if AppRunning() then
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM NotchTasks.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  except
    Log('Could not stop NotchTasks: ' + GetExceptionMessage);
  end;
end;

// The Electron install (electron-builder, per user) registers an uninstaller under HKCU. Run it silently in place, then
// remove its key. A failure is logged only: [InstallDelete] removes the leftover files anyway.
procedure RemoveElectronInstall();
var
  Names: TArrayOfString;
  I, P, ResultCode: Integer;
  Key, DisplayName, UninstallString, InstallLocation, Exe: String;
begin
  try
    if RegGetSubkeyNames(HKCU, UninstallKey, Names) then
    begin
      for I := 0 to GetArrayLength(Names) - 1 do
      begin
        Key := UninstallKey + '\' + Names[I];
        if RegQueryStringValue(HKCU, Key, 'DisplayName', DisplayName) and (Pos('NotchTasks', DisplayName) = 1) and
           RegQueryStringValue(HKCU, Key, 'UninstallString', UninstallString) and
           (Pos('Uninstall NotchTasks.exe', UninstallString) > 0) then
        begin
          // "<dir>\Uninstall NotchTasks.exe" /currentuser: the exe is the quoted part (or the text up to .exe).
          Exe := UninstallString;
          if Exe[1] = '"' then
          begin
            Delete(Exe, 1, 1);
            P := Pos('"', Exe);
            if P > 0 then
              Exe := Copy(Exe, 1, P - 1);
          end
          else
          begin
            P := Pos('.exe', LowerCase(Exe));
            if P > 0 then
              Exe := Copy(Exe, 1, P + 3);
          end;
          if not RegQueryStringValue(HKCU, Key, 'InstallLocation', InstallLocation) then
            InstallLocation := ExtractFilePath(Exe);
          InstallLocation := RemoveBackslash(InstallLocation);
          if FileExists(Exe) then
          begin
            Log('Running the Electron uninstaller in ' + InstallLocation);
            Exec(Exe, '/S /currentuser _?=' + InstallLocation, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
            if FileExists(Exe) then
              DeleteFile(Exe);
          end;
          RegDeleteKeyIncludingSubkeys(HKCU, Key);
        end;
      end;
    end;
  except
    Log('Could not remove the Electron install: ' + GetExceptionMessage);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\NotchTasks.dll')) then
    StopNotchTasks();
  RemoveElectronInstall();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // usUninstall runs after the "Are you sure?" confirmation, before [UninstallRun] and the file removal. Quitting earlier
  // (in InitializeUninstall) would leave the app stopped if the user answers No.
  if CurUninstallStep = usUninstall then
  begin
    if FileExists(ExpandConstant('{app}\NotchTasks.dll')) then
      StopNotchTasks();
  end;
  // Only the autostart entry. %APPDATA%\NotchTasks is the user's data and stays.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, RunKey, 'NotchTasks');
end;
