; SourceDir, Arch and AppVersion are passed by scripts/build_windows.py.
#ifndef SourceDir
  #error SourceDir required
#endif
#ifndef Arch
  #error Arch required
#endif
#ifndef AppVersion
  #error AppVersion required
#endif
[Setup]
AppId={{D25F2938-58D9-47CD-A5D2-A9169D903B90}
AppName=Hadramout Cards
AppVersion={#AppVersion}
AppPublisher=Hadramout
DefaultDirName={localappdata}\Programs\Hadramout Cards
DefaultGroupName=Hadramout Cards
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
WizardStyle=modern
UninstallDisplayIcon={app}\Hadramout.exe
Compression=lzma2
SolidCompression=yes
SetupIconFile={#SourceDir}\assets\hadramout_icon.ico
VersionInfoVersion={#AppVersion}.0
CloseApplications=yes
RestartApplications=no
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x86compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Hadramout Cards"; Filename: "{app}\Hadramout.exe"
Name: "{autodesktop}\Hadramout Cards"; Filename: "{app}\Hadramout.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Hadramout.exe"; Description: "Launch Hadramout Cards"; Flags: nowait postinstall skipifsilent
