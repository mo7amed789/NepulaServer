[Setup]
; NOTE: The value of AppId uniquely identifies this application.
; Do not use the same AppId value in installers for other applications.
AppId={{5E9E8D14-E4B2-436A-8A19-9B3E7A3482A1}
AppName=Tanzeel Media Downloader
AppVersion=1.0.0
AppPublisher=Mohamed
AppPublisherURL=https://github.com/mo7amed789
AppSupportURL=https://github.com/mo7amed789
AppUpdatesURL=https://github.com/mo7amed789
DefaultDirName={autopf}\Tanzeel
DefaultGroupName=Tanzeel
AllowNoIcons=yes
; Output installer to Desktop
OutputDir=C:\Users\mohamed\Desktop
OutputBaseFilename=Tanzeel_Setup_v1.0
SetupIconFile=compiler:SetupClassicIcon.ico
Compression=lzma2/fast
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Run Tanzeel in the background when Windows starts"; GroupDescription: "Windows Startup:"; Flags: checkedonce

#define AppPublishDir "bin\Release\net10.0-windows\win-x64\publish"

[Files]
Source: "{#AppPublishDir}\NebulaServer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\Tanzeel Media Downloader"; Filename: "{app}\NebulaServer.exe"
Name: "{group}\{cm:UninstallProgram,Tanzeel Media Downloader}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Tanzeel Media Downloader"; Filename: "{app}\NebulaServer.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Tanzeel"; ValueData: """{app}\NebulaServer.exe"" --background"; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\NebulaServer.exe"; Description: "{cm:LaunchProgram,Tanzeel Media Downloader}"; Flags: nowait postinstall skipifsilent
