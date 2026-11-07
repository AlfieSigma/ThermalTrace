# 构建与打包

本文面向从源码构建 ThermalTrace 的开发者。日常使用与安装说明见 [README.md](README.md)。

## 前置条件

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1607 及以上，或 Windows 11 |
| 架构 | x64（不支持 ARM64） |
| .NET | .NET 8 SDK |
| 安装包 | Inno Setup 6（仅生成安装程序时需要） |

`.NET 8 SDK` 可用仓库内的脚本安装到项目本地的 `.tools\` 目录，不会改动系统环境；已装好 SDK 时脚本会直接复用。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-dotnet.ps1
```

Inno Setup 6 需自行安装，构建脚本会在常见安装路径和 `PATH` 中查找 `ISCC.exe`。

## 目录结构

```
src\ThermalTrace\        程序源码
installer\               安装器脚本与语言文件
scripts\                 构建、发布、部署与冒烟脚本
ThermalTrace.sln         解决方案（build.ps1 在缺失时会自动生成）
```

## 编译

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

默认输出 Debug 构建；需要 Release 时传入 `-Configuration Release`。

产物位置：

```
src\ThermalTrace\bin\<Configuration>\net8.0-windows\ThermalTrace.exe
```

日常构建为依赖本机 .NET 运行时的手动调试版本，不会生成自包含单文件。

## 发布单文件 exe

发布产物是自包含单文件，目标机器无需安装 .NET：

```powershell
# 完整版（含墨水屏推送）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

# 普通版（构建期裁掉推送代码与界面）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Edition standard
```

产物位置：

```
publish\ThermalTrace.exe            # 完整版
publish-standard\ThermalTrace.exe   # 普通版
```

发布固定使用 `win-x64` 运行时标识，必须在 Windows x64 上执行。

## 生成安装包

一条命令完成 Release 编译、普通版单文件发布与 Inno Setup 打包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-beta.ps1
```

产物：

```
dist\ThermalTrace-<版本>-Setup.exe
```

需要完整版安装包，或需要复用已有的发布产物时：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Edition standard
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -SkipPublish
```

完整版安装包文件名形如 `ThermalTrace-<版本>-Zectrix-Setup.exe`。安装器必须通过脚本调用，`installer\ThermalTrace.iss` 依赖脚本传入的 `/DAppVersion` 与 `/DAppVersionNum`，直接编译会报错。

## 版本号

版本号只在 `src\ThermalTrace\ThermalTrace.csproj` 的 `<Version>` 中定义一处。构建、发布与安装包脚本统一通过 `scripts\version.ps1` 读取，避免多处漂移。

## 依赖还原

`src\ThermalTrace\packages.lock.json` 随源码提供，`dotnet restore` 会按锁定版本还原，保证不同机器上拿到相同的传递依赖。

## 冒烟测试

`scripts\smoke.ps1` 提供构建后的冒烟检查，覆盖命令行路径、发布产物与运行行为。部分检查需要管理员权限，非提权环境下会自动跳过。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1
```

## 说明

- 普通版通过 `-p:PushEnabled=false` 在编译期排除 `src\ThermalTrace\Push\*.cs`，并裁掉相关界面、调度与命令；运行时没有可绕过的推送入口。
- 安装器把 `LICENSE` 安装为 `LICENSE.txt`；分发二进制时需一并遵守 LibreHardwareMonitorLib（MPL-2.0）与 WinRing0（GPL-2.0）的许可证要求，详见 [README.md](README.md) 的许可一节。
