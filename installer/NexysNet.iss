; NexysNet — Inno Setup script (ARINC 664 app split from NexysBus, RUN-SPLIT). Build: ISCC.exe installer\NexysNet.iss
; Input: ..\net\dist\app\* (dotnet publish, self-contained win-x64), bootstrapper MicrosoftEdgeWebview2Setup.exe in installer\redist\
#define MyAppName "NexysNet"
#define MyAppVersion "0.1.0-beta.2"
#define MyAppVersionText "0.1.0 beta.2"
#define MyAppPublisher "Nexys"
#define MyAppExeName "NexysNet.exe"
#define MyAppUrl "https://github.com/Kim-Hakseong/nexys-net"

[Setup]
AppId={{6E2B1D44-9A7C-4B3E-A1F0-NEXYSNET0010}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersionText}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersionText}
OutputDir=..\net\dist
OutputBaseFilename=NexysNet-{#MyAppVersion}-setup
SetupIconFile=..\net\NexysNet.App\Assets\brand\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
ChangesAssociations=yes
LicenseFile=..\installer\LICENSE.txt
VersionInfoVersion=0.1.0.0
VersionInfoProductTextVersion={#MyAppVersionText}
MinVersion=10.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc_nxn"; Description: ".nxn 프로젝트를 {#MyAppName}으로 열기"; GroupDescription: "파일 연결 (선택):"; Flags: unchecked
Name: "assoc_pcapng"; Description: ".pcapng 패킷 캡처를 {#MyAppName}으로 열기"; GroupDescription: "파일 연결 (선택):"; Flags: unchecked

[Files]
Source: "..\net\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\installer\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\installer\redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not WebView2Installed

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.nxn\OpenWithProgids"; ValueType: string; ValueName: "NexysNet.nxn"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_nxn
Root: HKA; Subkey: "Software\Classes\NexysNet.nxn"; ValueType: string; ValueName: ""; ValueData: "NexysNet project"; Flags: uninsdeletekey; Tasks: assoc_nxn
Root: HKA; Subkey: "Software\Classes\NexysNet.nxn\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc_nxn
Root: HKA; Subkey: "Software\Classes\NexysNet.nxn\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_nxn
Root: HKA; Subkey: "Software\Classes\.pcapng\OpenWithProgids"; ValueType: string; ValueName: "NexysNet.pcapng"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_pcapng
Root: HKA; Subkey: "Software\Classes\NexysNet.pcapng"; ValueType: string; ValueName: ""; ValueData: "Packet capture"; Flags: uninsdeletekey; Tasks: assoc_pcapng
Root: HKA; Subkey: "Software\Classes\NexysNet.pcapng\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc_pcapng
Root: HKA; Subkey: "Software\Classes\NexysNet.pcapng\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_pcapng

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Microsoft Edge WebView2 런타임 설치 중…"; Check: not WebView2Installed; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function WebView2Installed: Boolean;
var
  pv: string;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) then
    Result := (pv <> '') and (pv <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) then
      Result := (pv <> '') and (pv <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) then
      Result := (pv <> '') and (pv <> '0.0.0.0');
end;
