; LightShot Translate - Inno Setup 6.x script
; 打包请运行 build_installer.bat（会先 publish 成自带运行时的版本，再调用本脚本，并生成便携 zip）。
#ifndef AppVersion
  #define AppVersion "0.8.1"
#endif
#define AppName "LightShot Translate"
#define AppExe  "LightShotTranslate.exe"

[Setup]
AppId={{E5C30949-2633-4AAE-AA7F-6455310A7FAC}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher={#AppName}
DefaultDirName={autopf}\LightShotTranslate
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=LightShotTranslate-Setup-{#AppVersion}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 仅 64 位 Windows 10 1607 及以上（.NET 8 的最低要求）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
; 默认当前用户安装（不需要管理员权限），向导里可改为所有用户
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 升级时自动关闭正在运行的程序（含 OCR 子进程）
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; 升级时清掉旧版本遗留的动态库（Paddle / OpenCV 时代），新版 dll 会在随后的复制步骤里重新装上
Type: files; Name: "{app}\*.dll"

[Files]
; publish 目录由 publish.ps1 -SelfContained 生成：含 .NET 运行时、VC++ 运行库、ONNX Runtime 和 models
Source: "publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前结束正在运行的实例（含 OCR 子进程）
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"

[Registry]
; 程序设置里的“开机自启”写在这里；卸载时一并清除
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "LightShotTranslate"; Flags: dontcreatekey uninsdeletevalue
