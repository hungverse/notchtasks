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
; Electron leftovers from the previous install, in the same folder. Exact names only: a wildcard here also matches Inno's own
; unins000.dat and the native files. None of these names is in the native stage.
Type: filesandordirs; Name: "{app}\resources"
Type: filesandordirs; Name: "{app}\locales"
Type: files; Name: "{app}\chrome_100_percent.pak"
Type: files; Name: "{app}\chrome_200_percent.pak"
Type: files; Name: "{app}\resources.pak"
Type: files; Name: "{app}\icudtl.dat"
Type: files; Name: "{app}\snapshot_blob.bin"
Type: files; Name: "{app}\v8_context_snapshot.bin"
Type: files; Name: "{app}\d3dcompiler_47.dll"
Type: files; Name: "{app}\ffmpeg.dll"
Type: files; Name: "{app}\libEGL.dll"
Type: files; Name: "{app}\libGLESv2.dll"
Type: files; Name: "{app}\vk_swiftshader.dll"
Type: files; Name: "{app}\vk_swiftshader_icd.json"
Type: files; Name: "{app}\vulkan-1.dll"
Type: files; Name: "{app}\LICENSE.electron.txt"
Type: files; Name: "{app}\LICENSES.chromium.html"
Type: files; Name: "{app}\Uninstall NotchTasks.exe"

[Icons]
Name: "{userprograms}\NotchTasks"; Filename: "{app}\NotchTasks.exe"; AppUserModelID: "com.notchtasks.windows"
Name: "{userdesktop}\NotchTasks"; Filename: "{app}\NotchTasks.exe"; AppUserModelID: "com.notchtasks.windows"; Tasks: desktopicon

[Run]
; Also after a silent update: the app starts again in place of the one the update closed.
Filename: "{app}\NotchTasks.exe"; Flags: nowait postinstall

[UninstallDelete]
; Folders the stage ships (they would stay empty after the files go). Only inside {app}; the user's data is never listed.
Type: filesandordirs; Name: "{app}\l10n"
Type: filesandordirs; Name: "{app}\Sprites"
Type: filesandordirs; Name: "{app}\runtimes"
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\fr"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\ko"
Type: filesandordirs; Name: "{app}\pl"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"
Type: dirifempty; Name: "{app}"

[UninstallRun]
Filename: "{app}\NotchTasksHook.exe"; Parameters: "--uninstall-hooks"; RunOnceId: "hooks"; Flags: runhidden waituntilterminated

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';

// The image-name filter with the user filter: only this user's NotchTasks.exe counts (another signed-in user's copy is theirs).
function UserFilter(): String;
begin
  Result := ' /FI "USERNAME eq ' + GetUserNameString() + '"';
end;

// True while this user's process named NotchTasks.exe is listed (find exits 0 only when it finds the image name).
function AppRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // The tools are called by their full path in {sys} (no PATH lookup). {sys} has no spaces, so the paths are not quoted.
  Result := Exec(ExpandConstant('{sys}\cmd.exe'),
    '/C ' + ExpandConstant('{sys}\tasklist.exe') + ' /FI "IMAGENAME eq NotchTasks.exe"' + UserFilter() + ' /NH | ' +
    ExpandConstant('{sys}\find.exe') + ' /I "NotchTasks.exe" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// A native install is running: ask it to quit (--quit blocks up to 5 s for it to exit), then force it if it is still there.
// The forced kill has no /T: the update's own installer runs as a child of the app, and a tree kill would end it too.
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
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM NotchTasks.exe' + UserFilter(), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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

// The temperature helper's sign-in task runs as SYSTEM after NotchTasks is gone, so the user is offered its removal. Removing it
// needs administrator rights, so it is opt-in: the default is No, and a silent uninstall keeps it (logged). Yes runs the helper's own
// uninstall, elevated, for this user. The question is asked when any user's sign-in marker is there (another user's helper is kept).
procedure RemoveSensorSignIn();
var
  Found: TFindRec;
  Helper: String;
  ResultCode: Integer;
begin
  Helper := ExpandConstant('{app}\NotchTasksSensors.exe');
  if not FileExists(Helper) then
    Exit;
  if not FindFirst(ExpandConstant('{pf}\NotchTasks Sensors\*.signin'), Found) then
    Exit;
  FindClose(Found);
  if UninstallSilent then
  begin
    Log('Silent uninstall: the temperature helper sign-in task is kept (removing it needs administrator rights).');
    Exit;
  end;
  if MsgBox('The temperature helper is set to start when you sign in. Removing its sign-in task needs administrator rights ' +
     '(Windows asks once).' + #13#10#13#10 + 'Remove the sign-in task too?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    try
      if Exec(Helper, '--uninstall-own', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
        Log('Removed the temperature helper sign-in task.')
      else
        Log(Format('The temperature helper sign-in task was not removed (exit code %d).', [ResultCode]));
    except
      Log('Could not remove the temperature helper sign-in task: ' + GetExceptionMessage);
    end;
  end
  else
    Log('The temperature helper sign-in task was kept at the user''s choice.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // usUninstall runs after the "Are you sure?" confirmation, before [UninstallRun] and the file removal. Quitting earlier
  // (in InitializeUninstall) would leave the app stopped if the user answers No.
  if CurUninstallStep = usUninstall then
  begin
    if FileExists(ExpandConstant('{app}\NotchTasks.dll')) then
      StopNotchTasks();
    RemoveSensorSignIn();
  end;
  // Only the autostart entries (ours, and the one the Electron build wrote). %APPDATA%\NotchTasks is the user's data and stays.
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, RunKey, 'NotchTasks');
    RegDeleteValue(HKCU, RunKey, 'com.notchtasks.windows');
  end;
end;
