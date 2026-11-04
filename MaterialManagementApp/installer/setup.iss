; ============================================================================
; MaterialManagementApp 安装程序脚本（Inno Setup 6）
; 编译方式（任选其一）：
;   1. 图形界面：用 Inno Setup 打开本文件，按 F9 编译
;   2. 命令行：  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\setup.iss
; 产物输出：installer\output\MaterialManagementApp_Setup_1.5.0.exe
;
; 数据路径策略（绿色便携）：
;   数据库默认保存在程序安装目录下的 Data 子目录内，数据随软件走；
;   备份保留在本机应用数据目录（%LOCALAPPDATA%\MaterialSystem\Backup），
;   与程序目录分离，程序目录被覆盖或误删时备份依然完好。
;   历史版本（曾通过安装向导选择过数据目录并留下 install-config.json）升级后
;   仍会沿用其数据目录——程序内 AppPaths 读取该文件，老用户数据无感平滑迁移。
;   卸载时若程序目录中仍有 Data 文件夹，卸载程序会自动把它移到
;   %LOCALAPPDATA%\MaterialSystem\OrphanedData_时间戳 下保存，绝不静默删除数据。
; ============================================================================

#define MyAppName "物资管理系统"
#define MyAppNameEn "MaterialManagementApp"
#define MyAppVersion "1.7.0"
#define MyAppPublisher "MaterialManagementApp"
#define MyAppExeName "MaterialManagementApp.exe"
; 发布产物目录（dotnet publish -c Release -r win-x64 的输出，相对本脚本所在目录）
#define PublishDir "..\MaterialManagementApp\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
; 固定 AppId，升级安装时识别同一应用，勿随意修改
AppId={{8F3C2A1E-5B6D-4E7A-9C1F-2D3A4B5C6D7E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; {autopf} 在免管理员（lowest）安装时解析为 %LocalAppData%\Programs，
; 既保证普通用户可写，又允许在向导中改为其它目录
DefaultDirName={autopf}\{#MyAppName}
; 安装时始终显示"选择安装位置"页，由用户自定安装目录（默认值仍为上方 autopf 路径）；
 ; 若不显式关闭，Inno 在检测到旧版本时会跳过该页直接沿用上次目录
DisableDirPage=no
DefaultGroupName={#MyAppName}
OutputDir=output
OutputBaseFilename=MaterialManagementApp_Setup_{#MyAppVersion}
; 安装包与卸载程序的图标
SetupIconFile=..\MaterialManagementApp\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
; 自包含包为 x64，仅允许 64 位系统
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 免管理员：按当前用户安装，不触发 UAC
PrivilegesRequired=lowest
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Files]
; 打包内容只含程序文件（exe/dll/资源）；用 **\ 跨目录通配符排除任意嵌套层级的
; 用户数据：数据库（.db + WAL/SHM 附属文件）、运行时日志、临时锁文件。
; 升级覆盖安装绝不会覆盖用户数据库；数据库随软件放在 {app}\Data（便携），
; 历史安装版仍在 install-config.json 指定的数据目录，两者均不随安装包分发或覆盖。
; settings.json 是程序首次运行必需的默认配置，**不排除**，安装时作为初始模板。
Source: "{#PublishDir}\*"; Excludes: "**\*.db **\*.db-wal **\*.db-shm **\*.db-journal **\Logs\** **\*.log **\*.trn"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 桌面快捷方式
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; 开始菜单快捷方式
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 删除整个程序目录；Data 数据文件夹由下方 [Code] 的卸载钩子先行移出保护，
; 若移动失败（极端占用场景）会弹窗提醒用户手动处理后再卸载
Type: filesandordirs; Name: "{app}"

[Code]
{ ============================================================================
  卸载保护：在文件删除开始前，把程序安装目录（app 常量）下的 Data 文件夹整体
  移出程序目录，落到本机应用数据目录 MaterialSystem\OrphanedData_时间戳，
  并弹窗告知新位置。数据随软件放在安装目录内，绝不能随卸载静默删除。
  注意：Pascal 注释里不能出现成对大括号，描述目录常量时一律不加大括号。
  ============================================================================ }

{ 递归复制目录（Inno 没有 DirMove/DirCopy 内置函数，RenameFile 只能同盘改名；
  复制成功后由调用方用 DelTree 删除源目录，实现跨盘移动语义。 }
function CopyDirRecurse(const FromDir, ToDir: String): Boolean;
var
  SR: TFindRec;
  SrcPath, DstPath: String;
begin
  Result := True;
  if not ForceDirectories(ToDir) then
  begin
    Result := False;
    Exit;
  end;

  if FindFirst(AddBackslash(FromDir) + '*', SR) then
  try
    repeat
      if (SR.Name <> '.') and (SR.Name <> '..') then
      begin
        SrcPath := AddBackslash(FromDir) + SR.Name;
        DstPath := AddBackslash(ToDir) + SR.Name;
        if (SR.Attributes and $10) <> 0 then
        begin
          { 子目录：递归复制（$10 = FILE_ATTRIBUTE_DIRECTORY） }
          if not CopyDirRecurse(SrcPath, DstPath) then
          begin
            Result := False;
            Exit;
          end;
        end
        else if not FileCopy(SrcPath, DstPath, False) then
        begin
          Result := False;
          Exit;
        end;
      end;
    until not FindNext(SR);
  finally
    FindClose(SR);
  end;
end;

{ 移动目录：优先同盘改名（瞬时），失败回退为递归复制。
  源目录无需手动删除——随后执行的 [UninstallDelete] 会清理整个程序目录。 }
function MoveDir(const FromDir, ToDir: String): Boolean;
begin
  Result := RenameFile(FromDir, ToDir);
  if not Result then
  begin
    Result := CopyDirRecurse(FromDir, ToDir);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, OrphanRoot, TargetDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    DataDir := AddBackslash(ExpandConstant('{app}')) + 'Data';
    if DirExists(DataDir) then
    begin
      OrphanRoot := AddBackslash(ExpandConstant('{localappdata}')) + 'MaterialSystem';
      ForceDirectories(OrphanRoot);
      TargetDir := AddBackslash(OrphanRoot) + 'OrphanedData_' + GetDateTimeString('yyyymmdd_hhnnss', '-', '_');

      { 同盘改名瞬时完成；跨盘/改名失败时回退递归复制后删除源。
        两者都失败（数据被占用等极端情况）时只警告——卸载继续会删除
        程序目录，必须让用户知道数据仍留在原处需要手动处理。 }
      if MoveDir(DataDir, TargetDir) then
        MsgBox('已把程序目录中的数据文件夹（Data）安全移至：' + #13#10 + #13#10 +
               TargetDir + #13#10 + #13#10 +
               '您的数据库与数据文件完好保留，可随时取回。', mbInformation, MB_OK)
      else
        MsgBox('警告：无法自动保护程序目录中的 Data 数据文件夹（可能正被其他程序占用）。' + #13#10 + #13#10 +
               '如果继续卸载，程序目录将被删除，数据将丢失！' + #13#10 +
               '建议：立即取消卸载，手动备份 ' + DataDir + ' 后再执行卸载。', mbError, MB_OK);
    end;
  end;
end;
