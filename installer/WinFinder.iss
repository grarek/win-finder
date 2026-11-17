; WinFinder Setup (Inno Setup 6). Builds WinFinder-<version>-setup-<arch>.exe from a
; "dotnet publish" folder. CI passes the version, architecture and folder:
;
;   iscc /DAppVersion=1.0.11 /DArch=x64 /DSourceDir=C:\...\publish\win-x64 installer\WinFinder.iss
;
; Installs for the current user only (no admin prompt) into %LocalAppData%\Programs\WinFinder,
; the same place tools\install.ps1 uses, so WinFinder can update itself in place.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
; Overridable so a test build can be installed side by side without touching a real install.
#ifndef AppName
  #define AppName "WinFinder"
#endif
#ifndef AppGuid
  #define AppGuid "8CF547EC-BE43-4EF4-AD39-5E15BDEF6AC9"
#endif
#ifndef DataDir
  #define DataDir "{localappdata}\WinFinder"
#endif

#define RepoUrl "https://github.com/grarek/win-finder"

#if Arch == "arm64"
  #define AllowedArch "arm64"
#else
  ; x64 also installs on ARM64 Windows 11, which runs it under emulation.
  #define AllowedArch "x64compatible"
#endif

[Setup]
AppId={{{#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=grarek
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={userpf}\{#AppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#AllowedArch}
ArchitecturesInstallIn64BitMode={#AllowedArch}
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=WinFinder-{#AppVersion}-setup-{#Arch}
SetupIconFile=..\src\WinFinder.App\Assets\WinFinder.ico
UninstallDisplayIcon={app}\WinFinder.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Close a running WinFinder (via Restart Manager) before replacing its files.
CloseApplications=yes
RestartApplications=no
; Tells Explorer to re-read folder handlers after uninstalling (see RemoveFolderHandler).
ChangesAssociations=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start in the user's folder, not the install folder, so in-app updates can swap the folder.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\WinFinder.exe"; WorkingDir: "{%USERPROFILE}"; Comment: "Browse files the way the Mac Finder does"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WinFinder.exe"; WorkingDir: "{%USERPROFILE}"; Tasks: desktopicon

[Run]
Filename: "{app}\WinFinder.exe"; WorkingDir: "{%USERPROFILE}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Setup closes a running WinFinder itself (CloseApplications), but the uninstaller doesn't,
// and files in use can't be removed. Close the windows of this copy (so it saves its tabs),
// and end it if it hasn't quit after 10 seconds. Other copies of WinFinder are left alone.
function InitializeUninstall(): Boolean;
var
  Dir, Command: String;
  Code: Integer;
begin
  Dir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  StringChangeEx(Dir, '''', '''''', True);
  Command :=
    '$mine = { Get-Process WinFinder -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -and $_.Path.StartsWith(''' + Dir + '\'', [StringComparison]::OrdinalIgnoreCase) } }; ' +
    '$deadline = (Get-Date).AddSeconds(10); ' +
    'while ((& $mine) -and (Get-Date) -lt $deadline) { & $mine | ForEach-Object { [void]$_.CloseMainWindow() }; Start-Sleep -Milliseconds 500 }; ' +
    '& $mine | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "' + Command + '"',
       '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := True;
end;

// "Use Instead of File Explorer" points folders, drives and Win+E at WinFinder.exe. If that is
// this copy, hand them back to File Explorer; otherwise folders would stop opening.
procedure RemoveFolderHandler(Dir: String);
var
  I: Integer;
  Key, Value, Previous: String;
  Classes: array[0..1] of String;
begin
  Dir := Lowercase(Dir + '\');
  Classes[0] := 'Directory';
  Classes[1] := 'Drive';
  for I := 0 to 1 do
  begin
    Key := 'Software\Classes\' + Classes[I] + '\shell';
    if RegQueryStringValue(HKCU, Key + '\WinFinder\command', '', Value) and (Pos(Dir, Lowercase(Value)) > 0) then
    begin
      if not RegQueryStringValue(HKCU, Key + '\WinFinder', 'WinFinderPreviousDefault', Previous) then
        Previous := '';
      if RegQueryStringValue(HKCU, Key, '', Value) and (Value = 'WinFinder') then
      begin
        if Previous <> '' then
          RegWriteStringValue(HKCU, Key, '', Previous)
        else
          RegDeleteValue(HKCU, Key, '');
      end;
      RegDeleteKeyIncludingSubkeys(HKCU, Key + '\WinFinder');
    end;
  end;

  Key := 'Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}';
  if RegQueryStringValue(HKCU, Key + '\shell\opennewwindow\command', '', Value) and (Pos(Dir, Lowercase(Value)) > 0) then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, Key + '\shell\opennewwindow');
    RegDeleteKeyIfEmpty(HKCU, Key + '\shell');
    RegDeleteKeyIfEmpty(HKCU, Key);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir, Data: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  RemoveFolderHandler(RemoveBackslashUnlessRoot(ExpandConstant('{app}')));

  // In-app updates replace the whole folder, so it can hold files this uninstaller never
  // recorded. Remove the rest, but only when the folder is clearly WinFinder's own.
  Dir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if (Length(Dir) > 3) and (CompareText(ExtractFileName(Dir), '{#AppName}') = 0) then
  begin
    DelTree(Dir, True, True, True);
    DelTree(Dir + '.new', True, True, True); // left by an interrupted update
    DelTree(Dir + '.old', True, True, True);
  end;

  // Settings, favorites and tags are kept unless the user says otherwise.
  Data := ExpandConstant('{#DataDir}');
  if (not UninstallSilent) and DirExists(Data) then
    if MsgBox('Also delete your WinFinder settings, favorites and tags?' + #13#10#13#10 +
              'They''re kept in ' + Data + ' and will be used again if you reinstall.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(Data, True, True, True);
end;
