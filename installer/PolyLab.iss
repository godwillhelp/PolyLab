; Poly Lab launcher installer. Per user, without admin rights: the launcher installs the tools into tools\ and GRAFT into
; graft\ beside PolyLab.exe, so its folder must stay writable (Program Files is not).
; Built by tools\release.ps1: ISCC /DAppVersion=<version> /DSourceDir=<publish folder> /O<out> installer\PolyLab.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir must name the folder with the published PolyLab.exe
#endif

[Setup]
AppId={{53799F70-955C-4C2B-B26F-8B35F60CC50B}
AppName=Poly Lab
AppVersion={#AppVersion}
AppPublisher=Poly Lab contributors
AppPublisherURL=https://github.com/godwillhelp/PolyLab
AppSupportURL=https://github.com/godwillhelp/PolyLab/issues
AppUpdatesURL=https://github.com/godwillhelp/PolyLab/releases
PrivilegesRequired=lowest
DefaultDirName={autopf}\PolyLab
DisableProgramGroupPage=yes
AppMutex=Local\PolyLab_Launcher_mtx
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=PolyLab-{#AppVersion}-setup
SetupIconFile=..\src\Ui\Assets\Brand\polylab.ico
UninstallDisplayIcon={app}\PolyLab.exe
UninstallDisplayName=Poly Lab
WizardStyle=modern
ShowLanguageDialog=auto
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\PolyLab.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD_PARTY.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Poly Lab"; Filename: "{app}\PolyLab.exe"
Name: "{autodesktop}\Poly Lab"; Filename: "{app}\PolyLab.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PolyLab.exe"; Description: "{cm:LaunchProgram,Poly Lab}"; Flags: nowait postinstall skipifsilent

; The tools and GRAFT the launcher downloaded. Settings (%LOCALAPPDATA%\PolyLab) and the tools' data (%APPDATA%\PolyLab)
; stay.
[UninstallDelete]
Type: filesandordirs; Name: "{app}\tools"
Type: filesandordirs; Name: "{app}\graft"
