[Setup]
AppId={{742CF026-B6B9-41A0-B684-AC7E6547A689}
AppName=TechPaper
AppVersion=0.9.0-preview.22
AppPublisher=TechPaper contributors
AppPublisherURL=https://github.com/Zeruik/TechPaper
DefaultDirName={localappdata}\Programs\TechPaper
DefaultGroupName=TechPaper
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=no
LicenseFile=docs\TERMS.txt
SetupIconFile=assets\TechPaper.ico
UninstallDisplayIcon={app}\TechPaper.exe
OutputDir=release
OutputBaseFilename=TechPaper-Setup-0.9.0-preview.22
ArchitecturesAllowed=x64compatible
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式 / Create desktop shortcut"; Flags: unchecked

[Files]
Source: "build\TechPaper.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "docs\TERMS.txt"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "docs\RESEARCH.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "docs\VALIDATION.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "assets\logo.png"; DestDir: "{app}\assets"; Flags: ignoreversion
Source: "assets\workspace.png"; DestDir: "{app}\assets"; Flags: ignoreversion
Source: "tests\sample.docx"; DestDir: "{app}\tests"; Flags: ignoreversion
Source: "build\dist\review-worker\*"; DestDir: "{app}\worker"; Flags: ignoreversion recursesubdirs
Source: "build\third-party\*"; DestDir: "{app}\third-party"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\TechPaper"; Filename: "{app}\TechPaper.exe"
Name: "{userdesktop}\TechPaper"; Filename: "{app}\TechPaper.exe"; Tasks: desktopicon

[Messages]
WizardLicense=使用条款
LicenseAccepted=我已阅读并同意以上条款(&A)
LicenseNotAccepted=我不同意以上条款(&D)
WizardSelectDir=选择安装路径
WizardSelectTasks=快捷方式
ButtonNext=下一步(&N) >
ButtonBack=< 上一步(&B)
ButtonCancel=取消
ButtonBrowse=浏览(&B)...
ButtonInstall=安装(&I)
ButtonFinish=完成(&F)
