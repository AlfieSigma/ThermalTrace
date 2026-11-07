<#
.SYNOPSIS
    ThermalTrace 版本号的唯一读取入口。

.DESCRIPTION
    权威定义只有一处：src\ThermalTrace\ThermalTrace.csproj 的 <Version>。
    本文件把它解析成构建、发布与安装包脚本需要的形态，避免各脚本各写一份正则。

    返回对象字段：
    - Version        展示版本，原样返回，例如 0.3.0 或 0.3.0-beta.1
    - NumericVersion Windows 文件版本资源要的四段数字，0.3.0 映射为 0.3.0.0

    四段映射：0.3.0 映射为 0.3.0.0；预发布标识不进入数字段。

.EXAMPLE
    . .\scripts\version.ps1
    $v = Get-ThermalTraceVersion -ProjectPath .\src\ThermalTrace\ThermalTrace.csproj
    $v.Version         # 0.3.0
    $v.NumericVersion  # 0.3.0.0
#>
function Get-ThermalTraceVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    if (-not (Test-Path $ProjectPath)) {
        throw "找不到项目文件：$ProjectPath"
    }

    $text = [IO.File]::ReadAllText($ProjectPath, [Text.Encoding]::UTF8)
    $versionMatch = [regex]::Match($text, '<Version>\s*([^<]+?)\s*</Version>')
    if (-not $versionMatch.Success) {
        throw '无法从 ThermalTrace.csproj 读取 <Version>；版本号必须只在 csproj 里定义一处。'
    }

    $version = $versionMatch.Groups[1].Value
    $coreMatch = [regex]::Match($version, '^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?$')
    if (-not $coreMatch.Success) {
        throw "Version 必须是 X.Y.Z 或 X.Y.Z-<预发布标识> 形式，当前为：$version"
    }

    [pscustomobject]@{
        Version        = $version
        NumericVersion = '{0}.{1}.{2}.0' -f [int]$coreMatch.Groups[1].Value,
                                                [int]$coreMatch.Groups[2].Value,
                                                [int]$coreMatch.Groups[3].Value
    }
}
