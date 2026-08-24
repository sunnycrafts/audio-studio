; Inno Setup script for AI Audio Restoration Studio
; Build with:  ISCC.exe installer\AudioStudio.iss
; Expects publish\AudioStudio.exe to exist (dotnet publish first).

#define AppName        "AI Audio Restoration Studio"
#define AppShortName   "AudioStudio"
#define AppVersion     "0.3.1"
#define AppPublisher   "Sunny"
#define AppExeName     "AudioStudio.exe"

[Setup]
AppId={{8F3C1E27-5B4A-4D96-9C2E-7A1D6B0E4F53}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
; Staged into the build folder alongside the published exe. The finished
; distributables are copied out to the Claude Code root's Portable and
; Executable folders and are deliberately not kept in the project itself.
OutputDir=..\publish
OutputBaseFilename=AudioStudio-Setup
SetupIconFile=..\src\AudioStudio.App\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The payload is a self-contained 64-bit build, so refuse to install anywhere
; it cannot run rather than failing confusingly on first launch.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-user install by default needs no elevation; the user can still choose
; a machine-wide location.
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
LicenseFile=
InfoBeforeFile=

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "associate"; Description: "Open common audio files with {#AppName}"; GroupDescription: "File associations:"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md";             DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion
Source: "..\CHANGELOG.md";          DestDir: "{app}"; DestName: "CHANGELOG.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";               Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}";     Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";         Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Association is opt-in: registering as the handler for someone's whole music
; library uninvited would be obnoxious.
Root: HKA; Subkey: "Software\Classes\{#AppShortName}.Audio"; ValueType: string; ValueName: ""; ValueData: "Audio recording"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#AppShortName}.Audio\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#AppShortName}.Audio\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: associate

Root: HKA; Subkey: "Software\Classes\.wav\OpenWithProgids";  ValueType: string; ValueName: "{#AppShortName}.Audio"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: string; ValueName: "{#AppShortName}.Audio"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.mp3\OpenWithProgids";  ValueType: string; ValueName: "{#AppShortName}.Audio"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.m4a\OpenWithProgids";  ValueType: string; ValueName: "{#AppShortName}.Audio"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.aiff\OpenWithProgids"; ValueType: string; ValueName: "{#AppShortName}.Audio"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Written beside the exe at runtime when something goes wrong.
Type: files; Name: "{app}\AudioStudio-crash.log"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  FFmpegOnPath: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    // The app needs FFmpeg to open or export anything at all. Warn now rather
    // than letting the first import fail with no explanation.
    FFmpegOnPath := FileExists(ExpandConstant('{app}\ffmpeg.exe'))
                 or FileExists('C:\ffmpeg\bin\ffmpeg.exe')
                 or FileExists(ExpandConstant('{pf}\ffmpeg\bin\ffmpeg.exe'));

    if not FFmpegOnPath then
      MsgBox('AI Audio Restoration Studio uses FFmpeg to read and write audio files.' + #13#10#13#10 +
             'FFmpeg was not found in the usual places. Install it and add it to PATH, ' +
             'or copy ffmpeg.exe and ffprobe.exe into:' + #13#10#13#10 +
             ExpandConstant('{app}') + #13#10#13#10 +
             'The application will still start, and will tell you the same thing if it cannot find FFmpeg.',
             mbInformation, MB_OK);
  end;
end;
