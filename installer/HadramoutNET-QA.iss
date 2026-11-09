; HadramoutNET temporary QA candidate installer.
; Never signed/published as a production version until parity and device acceptance.
#ifndef Arch
  #error Specify /DArch=x86 or /DArch=x64
#endif
#ifndef PublishDir
  #error Specify /DPublishDir=path
#endif
#ifndef OutputDir
  #error Specify /DOutputDir=path
#endif

[Setup]
#if Arch == "x64"
AppId={{6D6D2E7D-C212-46D0-BB8A-83E5ED84EC0D}
#else
AppId={{028B60C7-5B86-489D-A624-8848F0CEDA14}
#endif
AppName=HadramoutNET QA Candidate ({#Arch})
AppVerName=HadramoutNET QA Candidate ({#Arch}) 0.0.1
AppVersion=0.0.1
VersionInfoVersion=0.0.1.0
AppPublisher=HadramoutNET
DefaultDirName={localappdata}\Programs\HadramoutNET\{#Arch}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=no
UninstallDisplayIcon={app}\HadramoutNET.App.exe
OutputDir={#OutputDir}
OutputBaseFilename=HadramoutNET-win-{#Arch}-Installer-QA
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\HadramoutNET QA ({#Arch})"; Filename: "{app}\HadramoutNET.App.exe"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
