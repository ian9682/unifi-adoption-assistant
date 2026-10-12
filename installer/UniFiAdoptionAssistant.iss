; Inno Setup 6 script: per-user install, no administrator rights needed.
; Built by build/publish.ps1, which passes AppVersion and SourceDir.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x86"
#endif

[Setup]
AppId={{8C3E6D1A-3C1B-4D55-9C7B-2F1E9A7B5D10}
AppName=UniFi Adoption Assistant
AppVersion={#AppVersion}
AppPublisher=IT
DefaultDirName={localappdata}\Programs\UniFi Adoption Assistant
DefaultGroupName=UniFi Adoption Assistant
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=UniFiAdoptionAssistant-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\UniFiAdoptionAssistant.exe
SetupIconFile=..\src\UniFiAdoption.App\Assets\app.ico

[Tasks]
Name: "desktopicon"; Description: "Put a shortcut on the desktop"

[Files]
Source: "{#SourceDir}\UniFiAdoptionAssistant.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\settings.example.json"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists(AddBackslash(SourceDir) + "settings.json")
Source: "{#SourceDir}\settings.json"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\UniFi Adoption Assistant"; Filename: "{app}\UniFiAdoptionAssistant.exe"
Name: "{autoprograms}\UniFi Adoption Assistant (test mode)"; Filename: "{app}\UniFiAdoptionAssistant.exe"; Parameters: "--test"
Name: "{autodesktop}\UniFi Adoption Assistant"; Filename: "{app}\UniFiAdoptionAssistant.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\UniFiAdoptionAssistant.exe"; Description: "Open UniFi Adoption Assistant now"; Flags: nowait postinstall skipifsilent
