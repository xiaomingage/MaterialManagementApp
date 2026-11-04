; ============================================================================
;  MaterialManagementApp 安装脚本（Inno Setup 6）
;
;  使用前提：
;    1. 先在解决方案根目录执行发布命令，生成自包含多文件产物：
;         dotnet publish MaterialManagementApp\MaterialManagementApp\MaterialManagementApp.csproj ^
;             -c Release -r win-x64 --self-contained true -o publish
;       （若目标平台是 x86 或 ARM64，请把 -r 换成 win-x86 / win-arm64，
;         并同步修改下方 [Files] 段的 Source 路径）
;    2. 用 Inno Setup Compiler 编译本脚本，产物输出到 installer\ 目录
;
;  设计说明：
;    - 支持两种安装模式（PrivilegesRequiredOverridesAllowed=dialog 由用户选择）：
;      ① 为自己安装（默认）：最低权限装到当前用户目录，数据放在 程序目录\Data，
;         数据跟程序走，绿色便携；
;      ② 为所有用户：提权装到 Program Files（程序目录普通用户不可写），
;         数据自动重定向到 %LOCALAPPDATA%\MaterialSystem，并写入
;         install-config.json（dataRoot 字段）告知程序数据根目录，
;         数据库/配置/图片统一落到用户本地数据目录，普通用户运行时可写。
;    - Data 目录属于用户数据，绝不打包进安装文件，只在安装时创建空目录。
; ============================================================================

[Setup]
; AppId 一旦发布就不要再改，否则升级会被当成全新安装，旧版本不会自动卸载
AppId={{8F3C2A6E-4D71-4B9A-9E5C-2F7A1D8B6C04}
AppName=MaterialManagementApp
AppVersion=1.7.0
AppVerName=MaterialManagementApp 1.7.0
AppPublisher=MaterialManagementApp
VersionInfoVersion=1.7.0.0
VersionInfoDescription=工程项目物资管理系统

; 最低权限下 {autopf} 自动映射到当前用户可写目录，避免 UAC 提权
DefaultDirName={autopf}\MaterialManagementApp
DefaultGroupName=MaterialManagementApp
UninstallDisplayIcon={app}\MaterialManagementApp.exe

; 允许用户自定义安装位置（用户明确要求）
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no

; 最低权限：避免 UAC 提权后向自定义目录写 Data 失败
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=installer
OutputBaseFilename=MaterialManagementApp_Setup_1.7.0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
; 简体中文语言包随脚本放在同目录（Inno Setup 安装目录默认不含简体语言）
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 打包 dotnet publish 的输出目录（自包含多文件，不含单文件 exe）
; 注意：Data 目录属于用户数据，不在 Source 内，不会被安装包覆盖
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; 安装时创建 Data 目录并授予当前用户写权限，保证首次运行建库不报错
Name: "{app}\Data"; Permissions: users-modify

[Icons]
Name: "{group}\MaterialManagementApp"; Filename: "{app}\MaterialManagementApp.exe"
Name: "{group}\卸载 MaterialManagementApp"; Filename: "{uninstallexe}"
Name: "{autodesktop}\MaterialManagementApp"; Filename: "{app}\MaterialManagementApp.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MaterialManagementApp.exe"; Description: "启动 MaterialManagementApp"; Flags: nowait postinstall skipifsilent

[Code]
var
  { 本次升级过程中生成的备份文件完整路径 }
  BackupFilePath: string;
  { 是否成功备份，决定安装后是否需要还原 }
  NeedRestore: Boolean;

{ 生成 yyyyMMddHHmmss 时间戳，用于备份文件名 }
function GetTimestamp: string;
begin
  { Inno 的 Pascal Script 无 FormatDateTime，用内置 GetDateTimeString；
    日期/时间分隔符传 #0 表示不插入分隔符 }
  Result := GetDateTimeString('yyyymmddhhnnss', #0, #0);
end;

{ 返回数据目录（数据库与配置所在），随安装模式切换：
  为自己安装 —— 安装目录\Data（程序目录归当前用户所有，可写，数据跟程序走）
  为所有用户 —— 用户本地目录\MaterialSystem\Data（Program Files 普通用户不可写，
               数据放到当前用户本地数据目录，与程序内 AppPaths 历史约定一致。
               注意：本注释内不能出现花括号常量，花括号会提前闭合 Pascal 注释） }
function GetDataDir: string;
begin
  if IsAdminInstallMode then
    Result := ExpandConstant('{localappdata}') + '\MaterialSystem\Data'
  else
    Result := ExpandConstant('{app}') + '\Data';
end;

{ 返回数据根目录：为所有用户安装时作为 install-config.json 的 dataRoot 值写入，
  程序据此把数据库、settings.json、台账图片统一放到该目录下 }
function GetDataRoot: string;
begin
  if IsAdminInstallMode then
    Result := ExpandConstant('{localappdata}') + '\MaterialSystem'
  else
    Result := ExpandConstant('{app}');
end;

{ 返回数据库文件完整路径，与程序内 SettingsService.EffectiveDatabasePath 的默认值保持一致 }
function GetDatabasePath: string;
begin
  Result := GetDataDir + '\MaterialSystem.db';
end;

{ 为所有用户安装：把数据根目录写入 install-config.json（与 exe 同目录）。
  SaveStringToFile 按系统 ANSI 码页写入，中文用户名路径由程序内
  AppPaths.ReadBestEncoding 自动识别（GBK 回退），与历史安装程序行为一致 }
procedure WriteInstallConfig;
var
  ConfigPath: string;
  Root: string;
  Json: string;
begin
  ConfigPath := ExpandConstant('{app}') + '\install-config.json';
  Root := GetDataRoot;
  { JSON 字符串内的反斜杠必须双写转义（C:\Users\... → C:\\Users\\...） }
  StringChangeEx(Root, '\', '\\', True);
  Json := '{"dataRoot":"' + Root + '"}';
  if SaveStringToFile(ConfigPath, Json + #13#10, False) then
    Log('已写入数据目录配置：' + ConfigPath + ' -> ' + GetDataRoot)
  else
    Log('写入 install-config.json 失败：' + ConfigPath);
end;

{ 为自己安装：删除可能残留的 install-config.json，数据目录恢复为 程序目录\Data }
procedure RemoveInstallConfig;
begin
  DeleteFile(ExpandConstant('{app}') + '\install-config.json');
end;

{ 确保 Data 目录存在，否则备份路径不可写 }
function InitializeSetup: Boolean;
var
  DataDir: string;
begin
  Result := True;

  DataDir := GetDataDir;
  if not DirExists(DataDir) then
  begin
    if not ForceDirectories(DataDir) then
      Log('无法创建 Data 目录：' + DataDir);
  end;
end;

{ 安装阶段切换：
  ssInstall      —— 覆盖文件之前，先把旧库备份出来
  ssPostInstall  —— 文件铺完之后，把旧库还原回去，保证升级不丢数据 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: string;
  DbPath: string;
begin
  DataDir := GetDataDir;
  DbPath := GetDatabasePath;

  if CurStep = ssInstall then
  begin
    { 只在旧库真实存在时备份；全新安装不产生无意义的备份文件 }
    if FileExists(DbPath) then
    begin
      BackupFilePath := DataDir + '\MaterialSystem_backup_' + GetTimestamp + '.db';
      NeedRestore := FileCopy(DbPath, BackupFilePath, False);

      if NeedRestore then
        Log('已备份旧数据库到：' + BackupFilePath)
      else
        Log('旧数据库备份失败，路径：' + DbPath);
    end;
  end
  else if CurStep = ssPostInstall then
  begin
    { 覆盖安装后若旧库被新文件替换，用备份还原，确保升级后数据完整 }
    if NeedRestore and FileExists(BackupFilePath) then
    begin
      if FileCopy(BackupFilePath, DbPath, False) then
        Log('已还原数据库：' + DbPath)
      else
        MsgBox('数据库还原失败，备份文件仍保留在：' + #13#10 + BackupFilePath + #13#10 +
               '请手工将其复制为 MaterialSystem.db 后重新启动程序。', mbError, MB_OK);
    end;

    { 按安装模式落数据目录配置：
      为所有用户 → 写 install-config.json 把数据重定向到用户本地目录；
      为自己 → 清掉残留配置，数据仍跟程序目录走 }
    if IsAdminInstallMode then
      WriteInstallConfig
    else
      RemoveInstallConfig;
  end;
end;

{ 卸载时保留用户数据：默认不删除 Data 目录，避免误删台账 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := GetDataDir;
    if DirExists(DataDir) then
      MsgBox('用户数据保留在：' + #13#10 + DataDir + #13#10 + #13#10 +
             '如需彻底清除，请手工删除该目录。', mbInformation, MB_OK);
  end;
end;
