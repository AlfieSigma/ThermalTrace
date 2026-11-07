; ============================================================================
;  ThermalTrace 安装器（Inno Setup 6）
;  目标产物: dist\ThermalTrace-<版本>-Zectrix-Setup.exe（默认，极趣推送版）
;            dist\ThermalTrace-<版本>-Setup.exe（-DStandardEdition，普通版）
;
;  构建与打包说明: 见仓库根目录 BUILD.md
;  构建方式: powershell -ExecutionPolicy Bypass -File scripts\build-beta.ps1
;            powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
;            powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Edition standard
;
;  【文件编码】本文件必须是 UTF-8 with BOM。Inno Setup 靠 BOM 识别 UTF-8，
;  没有 BOM 会按 ANSI 解析，所有中文文案变成乱码。
;
;  【为什么用 Default.isl + 全量自定义中文，而不是 ChineseSimplified.isl】
;  Inno Setup 官方发行包不附带中文翻译（Languages 目录下只有 28 种语言，没有中文）。
;  从网上下载第三方语言包会引入不可控的外部依赖与版本不匹配风险。
;  这里改用官方英文底板 + 在 [CustomMessages] 里逐条覆盖成中文：
;  界面语言完全由本仓库自己控制，以后改文案不需要额外下载。
; ============================================================================

; 版本号由构建脚本从 csproj 的 <Version> 读取后通过 /D 传入，这里不再保留第二份默认值。
; 直接双击编译会因为缺少定义而报错，必须走 scripts\build-beta.ps1 或 scripts\build-installer.ps1。
#ifndef AppVersion
  #error 缺少 /DAppVersion，请用 scripts\build-beta.ps1 或 scripts\build-installer.ps1 编译
#endif
#ifndef AppVersionNum
  #error 缺少 /DAppVersionNum，请用 scripts\build-beta.ps1 或 scripts\build-installer.ps1 编译
#endif

#define AppExeName "ThermalTrace.exe"
#define AppPublisher "ThermalTrace"
#define DataDirName "ThermalTrace"
#define ProductFileName "ThermalTrace"

#ifdef StandardEdition
  ; 【普通版独立身份：AppId 与安装目录都与极趣推送版不同】
  ; 极趣推送版装在 %LOCALAPPDATA%\Programs\ThermalTrace，
  ; 普通版必须另起一套：同 AppId 会被 Inno 认成"同款升级"，直接覆盖极趣推送版目录。
  ; 代价（同机并装时）：
  ;   1) 两个卸载项、两个开始菜单项；各自的卸载互不影响；
  ;   2) 数据目录仍是共用的 %LOCALAPPDATA%\ThermalTrace（程序内 AppPaths 写死），
  ;      两版同时运行会重复采样同一份 CSV。对外分发的只有普通版，
  ;      同机并装属于已知限制，见 README 的普通版说明。
  #define AppId "{{E2B9F7A4-6C31-4D8E-9F52-7A0C3B81D6E4}"
  #define AppName "ThermalTrace 普通版"
  #define InstallDirName "ThermalTrace-Standard"
  #define SetupSuffix ""
  #define PublishDir "publish-standard"
#else
  #define AppId "{{7B3D6C58-4E21-4F9A-8B0C-2D5E7A1F4C63}"
  #define AppName "ThermalTrace 极趣推送版（Zectrix）"
  #define InstallDirName "ThermalTrace"
  #define SetupSuffix "-Zectrix"
  #define PublishDir "publish"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersionNum}

; 安装只写 %LOCALAPPDATA%，不需要管理员权限。
; 【关键决策】程序本体需要提权读 CPU MSR，但那发生在"运行"时，不该让"安装"也弹 UAC。
; 每次安装都白弹一次 UAC 会显著提高用户对来路不明程序的戒心。
PrivilegesRequired=lowest

DefaultDirName={localappdata}\Programs\{#InstallDirName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=no
OutputDir=..\dist
OutputBaseFilename={#ProductFileName}-{#AppVersion}{#SetupSuffix}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}

; 运行中的实例会锁住 exe，交给 Inno 自动探测并提示
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

; 【为什么锁 x64】程序集是 win-x64 单文件自包含，装到 arm64 上跑不起来
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseCustom.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; 显示名按版本区分：开始菜单、卸载项一眼能看出装的是哪一版。
LaunchProgram=启动 {#AppName}
UninstallProg=卸载 {#AppName}
; 数据目录与两版共用 %LOCALAPPDATA%\ThermalTrace，删数据前必须提醒这一点。
deleteDataMsg=是否一并删除历史数据？%n%n选择"是"将永久删除所有温度记录、传感器绑定、设置与生成的报告，此操作不可撤销。%n若同机还装过另一个版本，这个目录是两版共用的，删除会同时清掉另一版的数据。%n选择"否"则保留数据，重新安装后继续可用。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: checkedonce
#ifndef StandardEdition
; 普通版的安装器不注册计划任务，所以没有这一项：
; 注册 HIGHEST 任务必须提权，勾选后安装中途会弹 UAC，与"安装全程不提权"冲突。
; 程序首次启动时会自己问一次要不要开机自启，那时点「是」即注册。
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: checkedonce
#endif

[Files]
; 单文件自包含发布产物：目标机无需安装 .NET 运行时
Source: "..\{#PublishDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 许可页里明确指向这个文件，必须一起装（GPL / MPL 要求分发时附带许可文本）
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
; 用户说明装在安装目录里，装完即可查阅，不必回头找仓库
Source: "..\USAGE.md"; DestDir: "{app}"; DestName: "使用说明.md"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProg}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
#ifndef StandardEdition
; 【为什么自启注册用 shellexec】程序本体 requireAdministrator，Explorer/ShellExecute
; 会依清单自动弹一次 UAC 提权后执行（建 HIGHEST 计划任务必须提权）。
Filename: "{app}\{#AppExeName}"; Parameters: "--install"; Description: "{cm:AutoStartTask}"; \
    Tasks: autostart; Flags: shellexec waituntilterminated
#endif
; 【为什么这里也必须有 shellexec —— 2026-10-13 修 740】
; 安装器本身是 PrivilegesRequired=lowest（标准权限，不弹 UAC）。不带 shellexec 时
; Inno 用 CreateProcess 启动 ThermalTrace.exe，而 exe 清单是 requireAdministrator：
; 非提权进程调 CreateProcess 启提权 exe，Windows 不弹 UAC、直接返回
; ERROR_ELEVATION_REQUIRED(740)，安装收尾弹出"Unable to execute file"错误框。
; （Windows 11 实测；UAC 关闭或不提示的环境中同一路径可能不复现。）
; shellexec 走 ShellExecuteEx：按清单弹一次 UAC，用户点「是」程序以管理员启动，
; 点「否」也只是不启动，安装照常完成、不再报错。与"第一次启动程序才弹 UAC"的
; 既有文档口径一致。
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram}"; \
    Flags: shellexec nowait postinstall skipifsilent

[Code]
{---------------------------------------------------------------------------
  计划任务名与 Core\AutoStart.cs:105 硬编码的 "ThermalTrace" 必须一致。
  改成别的名字会导致卸载时清理不到，任务残留在任务计划程序里指向已删除的 exe。
---------------------------------------------------------------------------}
const TaskName = 'ThermalTrace';

var
  DataDir: String;

function SchtasksExe: String;
begin
  Result := ExpandConstant('{sys}\schtasks.exe');
end;

{ 是否存在本工具注册的开机自启计划任务 }
function HasAutoStartTask: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(SchtasksExe, '/query /tn ' + TaskName, '',
                SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

{---------------------------------------------------------------------------
  安装向导的许可页。
  【为什么必须包含第三方声明】LibreHardwareMonitorLib 是 MPL-2.0、WinRing0 是 GPL-2.0，
  分发二进制时必须附带其许可文本。只放自己的 MIT 是不够的。
---------------------------------------------------------------------------}
procedure InitializeWizard();
var
  S: String;
begin
  S := 'ThermalTrace ' + '{#AppVersion}' + #13#10#13#10 +
       'Copyright (c) 2026 AlfieSigma' + #13#10#13#10 +
       '本软件按 MIT 许可证分发。完整文本见安装目录下的 LICENSE.txt。' + #13#10#13#10 +
       '----- 第三方组件声明 -----' + #13#10#13#10 +
       'LibreHardwareMonitorLib 0.9.6 -- Mozilla Public License 2.0' + #13#10 +
       '  读取 CPU/GPU 传感器。本项目通过 NuGet 引入，未修改其源码。' + #13#10#13#10 +
       'WinRing0 -- GNU General Public License v2' + #13#10 +
       '  LibreHardwareMonitor 加载的内核驱动，用于读取 CPU 的 MSR / EC 寄存器。' + #13#10#13#10 +
       '注意：部分安全软件会拦截 WinRing0。若被拦截，程序不会崩溃，' + #13#10 +
       '但所有温度会显示为空。请把本程序加入安全软件白名单后重试。' + #13#10#13#10 +
       '----- 系统要求 -----' + #13#10 +
       'Windows 10 1607 或更高版本，64 位（x64）。约 80 MB 磁盘空间。' + #13#10#13#10 +
       '首次启动会请求管理员权限，这是读取 CPU 传感器所必需的。';

#ifdef StandardEdition
  S := S + #13#10#13#10 +
       '本安装包为普通版：不含墨水屏推送功能。' + #13#10 +
       '安装过程不请求管理员权限；第一次启动程序时会请求一次管理员权限，' + #13#10 +
       '这是读取 CPU 传感器所必需的。安装器不注册开机自启，' + #13#10 +
       '首次启动时程序会自己询问一次。';
#else
  S := S + #13#10#13#10 +
       '本安装包为极趣推送版（Zectrix）：带有极趣 Note4 墨水屏推送功能，' + #13#10 +
       '只有在连接极趣 Note4 时才有用。';
#endif

  WizardForm.LicenseMemo.Lines.Text := S;
end;

#ifdef StandardEdition
{---------------------------------------------------------------------------
  计划任务是否指向本安装目录。
  计划任务名与极趣推送版共用（Core\AutoStart.cs:105 硬编码 "ThermalTrace"），
  卸载时必须先确认任务命令是本安装目录的 exe 才删除，否则会把同机极趣推送版的自启拆掉。

  【为什么用 findstr 的退出码，不读文件再解析】
  Inno 6 的脚本里没有可靠的 AnsiString -> String 转换，读 XML 再比较会绕进编码细节。
  findstr 只看退出码（0 = 命中），判不出来就当作"不是本目录"：最坏结果是留下一条
  指向已删除 exe 的任务（登录时静默失败），绝不会误删极趣推送版的任务。
  搜索串用固定目录名（无空格、无引号），避免 cmd 的引号嵌套问题。
---------------------------------------------------------------------------}
function TaskTargetsOurInstall: Boolean;
var
  CmdLine: String;
  ResultCode: Integer;
begin
  Result := False;
  if not HasAutoStartTask then Exit;

  CmdLine := '/c ""' + SchtasksExe + '" /query /tn ' + TaskName +
             ' /xml | findstr /i /c:{#InstallDirName}"';
  Result := Exec(ExpandConstant('{cmd}'), CmdLine, '', SW_HIDE,
                 ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;
#endif

{---------------------------------------------------------------------------
  卸载：极趣推送版先注销计划任务、再结束进程，最后才轮到删除文件。
  usUninstall 阶段 exe 还在，所以还能调用它的 --uninstall。
  普通版的差异见下方 #ifdef 内的说明。
---------------------------------------------------------------------------}
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
#ifndef StandardEdition
  ExePath: String;
#endif
begin
  if CurUninstallStep = usUninstall then
  begin
#ifdef StandardEdition
    { 普通版：这里不做 runas 提权，也不按进程名结束进程。
      1) taskkill /IM ThermalTrace.exe 会把同机正在运行的极趣推送版一起杀掉；
      2) 计划任务名与极趣推送版共用，无条件注销会拆掉极趣推送版的自启；
      3) runas 会弹 UAC，与"普通版卸载不弹 UAC、静默卸载可脚本化"不符。
      安装器本身从未注册过任务，所以只有任务命令确实指向本安装目录时才删除它。
      若程序仍在运行，Inno 的 CloseApplications（按文件路径探测）会提示先关闭；
      提权进程关不掉时卸载会中断并提示重试，先从托盘退出再卸载即可。 }
    if TaskTargetsOurInstall then
      Exec(SchtasksExe, '/delete /tn ' + TaskName + ' /f', '',
           SW_HIDE, ewWaitUntilTerminated, ResultCode);
#else
    { 1) 结束仍在运行的实例。程序是 requireAdministrator 提权起的，
        标准权限的 taskkill 杀不掉它（拒绝访问），exe 会被锁住导致删不掉文件。
        taskkill 自身不带提权清单，runas 动词确定性地触发一次 UAC 再杀；
        UAC 被取消（ResultCode=1223）也继续往下走，删不掉文件时 Inno 会让用户重试。 }
    ShellExec('runas', ExpandConstant('{sys}\taskkill.exe'), '/F /IM ThermalTrace.exe',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);

    { 2) 只有确实注册过自启才去注销，否则白白弹一次 UAC。
        【为什么必须 ShellExec + runas，不能 Exec —— 2026-10-13 修 740】
        app.manifest 是 requireAdministrator，而 Exec 走的是 CreateProcess：
        非提权进程用 CreateProcess 启提权 exe，Windows 不弹 UAC、直接返回
        ERROR_ELEVATION_REQUIRED(740)，计划任务永远注销不掉（旧版实测踩坑，
        旧注释里"Windows 自己就会弹 UAC"对 Exec 不成立，只对 ShellExecute 成立）。
        runas 动词显式请求提权：弹一次 UAC，点「是」才会以管理员执行 --uninstall。
        UAC 被取消（ResultCode=1223）不中断卸载——残留任务指向已删除的 exe，
        开机时静默失败，可接受；文件照删。 }
    if HasAutoStartTask then
    begin
      ExePath := ExpandConstant('{app}\') + '{#AppExeName}';
      if FileExists(ExePath) then
      begin
        ShellExec('runas', ExePath, '--uninstall', ExpandConstant('{app}'),
                  SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Sleep(1500);
      end;
    end;
#endif
  end;

  { 3) 文件删完之后再问要不要删数据：历史温度记录是用户的资产，
      不能被卸载静默清掉，必须由用户明确选择。
      【为什么用 SuppressibleMsgBox 而不是 MsgBox】静默卸载（/VERYSILENT）下
      MsgBox 仍会弹窗并阻塞脚本，卸载会卡住；SuppressibleMsgBox 在静默模式下
      直接返回默认按钮。默认选「否」= 不删数据：数据目录与极趣推送版共用，
      静默卸载绝不能顺手清掉它。 }
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\') + '{#DataDirName}';
    if DirExists(DataDir) then
    begin
      if SuppressibleMsgBox(CustomMessage('deleteDataMsg') + #13#10#13#10 + DataDir,
                            mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
