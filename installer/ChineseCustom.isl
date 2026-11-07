; ============================================================================
;  ThermalTrace 中文语言文件（Inno Setup 6）
;
;  【为什么自己写而不下载现成的 ChineseSimplified.isl】
;  Inno Setup 官方发行包不附带中文翻译（Languages 目录只有 28 种西/北欧/东亚语言）。
;  从网上拉第三方翻译会引入不可控的外部依赖与版本漂移风险，
;  而这个安装器的界面文案总共就几十条，自己维护成本远低于长期追第三方包。
;
;  【工作方式】本文件只写"要覆盖的条目"，没写的全部回落到 compiler:Default.isl。
;  所以它是增量补丁而不是完整翻译，升级 Inno Setup 时也不会失效。
;
;  【文件编码】必须 UTF-8 with BOM，否则 Inno 按 ANSI 解析，中文全变乱码。
;  注意：Inno 的 [Messages] 键名不带 "Wizard." 前缀——
;  WelcomeTitle2 这种带前缀的是内置常量名，不是可覆盖的消息名。
; ============================================================================

[Messages]

LanguageName=简体中文

; ---- 按钮 ----
ButtonBack= < 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)

; ---- 通用 ----
SelectLanguageTitle=选择安装语言
SelectLanguageLabel=请选择安装过程中要使用的语言。
ClickNext=点击"下一步"继续，或点"取消"退出安装。
BrowseDialogTitle=浏览文件夹
BrowseDialogLabel=请在下面的列表中选择一个文件夹，然后点"确定"。
NewFolderName=新建文件夹

; ---- 欢迎页 ----
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=这将把 [name/ver] 安装到你的电脑上。%n%n建议先关闭所有其他程序。%n%n点击"下一步"继续。

; ---- 许可协议页 ----
WizardLicense=许可协议
LicenseLabel3=安装前请阅读以下许可协议。必须同意后才能继续安装。
LicenseAccepted=我同意上述条款(&A)
LicenseNotAccepted=我不同意上述条款(&D)

; ---- 选择安装位置 ----
WizardSelectDir=选择安装位置
SelectDirDesc=[name] 应安装到哪里？
SelectDirLabel3=[name] 将安装到下列文件夹：
SelectDirBrowseLabel=点击"下一步"继续。若要更改文件夹，请点"浏览"。%n%n安装路径建议保持默认。
DirDoesntExistTitle=文件夹不存在
DirDoesntExist=文件夹：%n%n%1%n%n不存在。是否创建它？
DirExistsTitle=文件夹已存在
DirExists=文件夹：%n%n%1%n%n已经存在。是否仍要安装到该文件夹？
InvalidPath=必须输入带盘符的完整路径，例如：%n%nC:\APP

; ---- 选择开始菜单文件夹 ----
WizardSelectProgramGroup=选择开始菜单文件夹
SelectStartMenuFolderDesc=快捷方式要放到哪里？
SelectStartMenuFolderLabel3=程序快捷方式将创建在下列开始菜单文件夹中：
SelectStartMenuFolderBrowseLabel=点击"下一步"继续。若要更改文件夹，请点"浏览"。
NoProgramGroupCheck2=不创建开始菜单文件夹(&D)

; ---- 选择附加任务 ----
WizardSelectTasks=选择附加任务
SelectTasksDesc=还要执行哪些附加任务？
SelectTasksLabel2=选择你希望安装程序在安装 [name] 时执行的附加任务，然后点"下一步"。

; ---- 准备安装 ----
WizardReady=准备安装
ReadyLabel1=安装程序已准备好，开始把 [name] 安装到你的电脑上。
ReadyLabel2a=点击"安装"继续，或点击"上一步"返回修改设置。
ReadyLabel2b=点击"安装"继续。
ReadyMemoDir=安装位置：
ReadyMemoGroup=开始菜单文件夹：
ReadyMemoTasks=附加任务：

; ---- 安装中 ----
WizardInstalling=正在安装
InstallingLabel=请稍候，安装程序正在把 [name] 安装到你的电脑上。
StatusClosingApplications=正在关闭相关程序...
StatusCreateDirs=正在创建文件夹...
StatusExtractFiles=正在解压文件...
StatusCreateIcons=正在创建快捷方式...
StatusSavingUninstall=正在写入卸载信息...
StatusRunProgram=正在完成安装...

; ---- 安装完成 ----
FinishedHeadingLabel=[name] 安装向导已完成
FinishedLabelNoIcons=[name] 已经安装完成。
FinishedLabel=[name] 已经安装完成，你可以通过快捷方式启动它。
ClickFinish=点击"完成"退出安装向导。

; ---- 退出确认 ----
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。如果现在退出，程序不会被安装。%n%n你可以在之后重新运行安装程序继续。%n%n确定要退出吗？

; ---- 卸载 ----
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 %1
ConfirmUninstall=你确定要完全卸载 %1 及其所有组件吗？
UninstallStatusLabel=请稍候，正在从你的电脑上卸载 %1。
UninstalledAll=%1 已经从你的电脑上卸载完成。
UninstalledMost=%1 卸载完成。%n%n仍有部分内容无法删除，可能需要手动清理。
UninstallNotFound=文件"%1"不存在，无法卸载。
UninstallOpenError=无法打开文件"%1"，无法卸载。
UninstallAppRunningError=卸载程序检测到 %1 正在运行。%n%n请先关闭它的所有实例，然后点"确定"继续，或点"取消"退出。
WizardUninstalling=卸载状态
StatusUninstalling=正在卸载 %1...
UninstallProgram=卸载 %1

; ---- 错误与选择 ----
AbortRetryIgnoreSelectAction=请选择操作
AbortRetryIgnoreRetry=重试(&T)
AbortRetryIgnoreIgnore=忽略错误并继续(&I)
AbortRetryIgnoreCancel=取消安装
RetryCancelSelectAction=请选择操作
RetryCancelRetry=重试(&T)
RetryCancelCancel=取消
ApplicationsFound2=以下程序正在使用安装程序需要更新的文件。%n%n%1%n%n建议允许安装程序自动关闭它们。
CloseApplications=自动关闭这些程序(&A)
DontCloseApplications=不关闭这些程序(&D)
ErrorCloseApplications=安装程序无法自动关闭所有相关程序。建议你先手动关闭它们再继续。
SetupAborted=安装未完成。%n%n请修正问题后重新运行安装程序。

; ---- 磁盘空间 ----
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=安装至少需要 %1 KB 可用空间，但所选驱动器只有 %2 KB 可用。%n%n仍要继续吗？

; ---- 关于对话框 ----
AboutSetupTitle=关于安装
AboutSetupMessage=%1 版本 %2%n%3%n%n%1 主页：%n%4

[CustomMessages]
CreateDesktopIcon=创建桌面快捷方式
AdditionalTasks=附加任务:
AutoStartTask=开机自动启动（强烈建议勾选）
AutoStartDesc=在登录后自动运行 ThermalTrace 并常驻托盘。读取 CPU 温度需要管理员权限，本工具用"计划任务"实现自启（注册表启动项对提权程序无效）。
LaunchProgram=启动 ThermalTrace
UninstallProg=卸载 ThermalTrace
deleteDataMsg=是否一并删除历史数据？%n%n选择"是"将永久删除所有温度记录、传感器绑定、设置与生成的报告，此操作不可撤销。%n选择"否"则保留数据，重新安装后继续可用。
