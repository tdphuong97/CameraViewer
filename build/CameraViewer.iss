; Camera Viewer installer (Inno Setup 6). Built by build.ps1 from dist\app.
; Installs per user (no admin prompt). Data in %LOCALAPPDATA%\CameraViewer is never touched by setup or uninstall.
#define AppVersion GetEnv("CV_VERSION")
#if AppVersion == ""
  #define AppVersion "0.9.0"
#endif

[Setup]
AppId={{F6D3246A-3049-42AF-9211-5BDE4B35A1AE}
AppName=Camera Viewer
AppVersion={#AppVersion}
AppVerName=Camera Viewer {#AppVersion}
AppPublisher=Camera Viewer
AppComments=Xem camera trực tiếp, xem lại và lưu video từ đầu ghi Hikvision
DefaultDirName={localappdata}\Programs\CameraViewer
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=CameraViewer-Setup-{#AppVersion}
SetupIconFile=..\src\Cv.App\Assets\cv.ico
UninstallDisplayIcon={app}\CameraViewer.exe
UninstallDisplayName=Camera Viewer
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=2
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "vi"; MessagesFile: "Vietnamese.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài màn hình"; GroupDescription: "Thêm:"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[InstallDelete]
; 0.9.1 kept the guide next to the exe; it lives in help\ from 0.9.2.
Type: files; Name: "{app}\huong-dan.html"

[Icons]
Name: "{autoprograms}\Camera Viewer"; Filename: "{app}\CameraViewer.exe"
Name: "{autodesktop}\Camera Viewer"; Filename: "{app}\CameraViewer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CameraViewer.exe"; Description: "Mở Camera Viewer ngay"; Flags: nowait postinstall skipifsilent
; An update started from inside the app runs silently: open the app again when it is done.
Filename: "{app}\CameraViewer.exe"; Parameters: "--updated"; Flags: nowait; Check: IsUpdate

[Code]
// The app starts its own update with /UPDATE=1 and then closes.
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;

// Best effort: give the app up to 30 seconds to close before files are replaced.
// A copy started with CV_DATA may use another mutex name this wait cannot see; Inno's
// CloseApplications=yes handles that case.
function InitializeSetup: Boolean;
var
  i: Integer;
begin
  if IsUpdate then
    for i := 1 to 60 do
    begin
      if not CheckForMutexes('Local\CameraViewer-' + GetUserNameString) then Break;
      Sleep(500);
    end;
  Result := True;
end;
