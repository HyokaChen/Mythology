; Mythology Desktop Windows 安装程序（Inno Setup 6）
;
; 由 installer/build-windows.ps1 调用：
;   ISCC.exe /DVersion=<X.Y.Z> /DVersionNumeric=<X.Y.Z.0> /DPayloadDir=<publish 目录>
;            /DOutputDir=<dist> /DRepoRoot=<仓库根绝对路径> /DSetupIcon=<app.ico 绝对路径>
;            installer/windows/MythologyDesktop.iss
;
; 设计要点：
;   · 安装器需要提权（写 Program Files），但允许 /CURRENTUSER 覆盖为当前用户安装，
;     CI 冒烟就用这条路（无需 UAC 交互）。
;   · 载荷里【没有】Assets 目录：app.ico 是 AvaloniaResource，被嵌进程序集，
;     所以所有图标只能指 {app}\MythDesktop.exe（指 {app}\Assets\app.ico 会静默变成默认图标）。
;   · 不写 [Registry]：应用不读注册表安装路径，且 HKLM 写入在 /CURRENTUSER 下会失败。
;   · 不写 AppMutex：Desktop 没有单实例互斥体。
;   · 没有 LicenseFile：仓库当前无 LICENSE 文件，不臆造授权声明。
;   · 本文件必须是 UTF-8 with BOM（否则 ISCC 按系统 ANSI 码页解析，中文消息乱码）。
;   · 卸载只删程序目录，绝不动 ~/.mythology（会话/设定/小说数据与 TUI 共用）。
#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef VersionNumeric
  #define VersionNumeric "0.0.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\..\dist\publish\desktop-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#ifndef RepoRoot
  #define RepoRoot "..\.."
#endif
#ifndef SetupIcon
  #define SetupIcon "..\..\src\Desktop\Assets\app.ico"
#endif
#ifndef AppIdGuid
  #define AppIdGuid "C4E1A7D2-9B63-4C58-A1F7-6D2E8B40C915"
#endif

[Setup]
AppId={{{#AppIdGuid}}
AppName=Mythology Desktop
AppVersion={#Version}
AppVerName=Mythology Desktop {#Version}
AppPublisher=Mythology
AppPublisherURL=https://github.com/HyokaChen/Mythology
AppSupportURL=https://github.com/HyokaChen/Mythology
DefaultDirName={autopf}\Mythology Desktop
DefaultGroupName=Mythology
DisableProgramGroupPage=yes
DisableWelcomePage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
UsePreviousAppDir=yes
ChangesAssociations=no
ChangesEnvironment=no
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={app}\MythDesktop.exe
UninstallDisplayName=Mythology Desktop
OutputDir={#OutputDir}
OutputBaseFilename=Mythology-Desktop-{#Version}-win-x64-setup
MinVersion=10.0.17763
VersionInfoVersion={#VersionNumeric}
VersionInfoCompany=Mythology
VersionInfoDescription=Mythology Desktop Setup
VersionInfoProductName=Mythology Desktop
VersionInfoProductVersion={#VersionNumeric}

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.LaunchApp=立即启动 Mythology Desktop
chinesesimplified.AdditionalIcons=附加任务：
english.CreateDesktopIcon=Create a desktop shortcut
english.LaunchApp=Launch Mythology Desktop now
english.AdditionalIcons=Additional tasks:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Mythology Desktop"; Filename: "{app}\MythDesktop.exe"; WorkingDir: "{app}"; IconFilename: "{app}\MythDesktop.exe"; IconIndex: 0
Name: "{group}\卸载 Mythology Desktop"; Filename: "{uninstallexe}"; IconFilename: "{app}\MythDesktop.exe"; IconIndex: 0
Name: "{autodesktop}\Mythology Desktop"; Filename: "{app}\MythDesktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon; IconFilename: "{app}\MythDesktop.exe"; IconIndex: 0

[Run]
Filename: "{app}\MythDesktop.exe"; Description: "{cm:LaunchApp}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
