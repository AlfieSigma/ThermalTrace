<#
.SYNOPSIS
    发布为单文件自包含 exe（目标机器无需安装 .NET）。

.PARAMETER Edition
    full（默认）＝极趣推送版，输出到 publish\，含墨水屏推送。
    standard     ＝普通版，输出到 publish-standard\，构建期裁掉全部推送代码与界面，
                   版本号后缀 +standard（基础版本与极趣推送版一致）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Edition standard
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Edition standard -OpenDir
#>
[CmdletBinding()]
param(
    [ValidateSet('full', 'standard')]
    [string]$Edition = 'full',

    [switch]$OpenDir,
    [switch]$SelfContained = $true
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }

if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[ERR] 找不到 dotnet。请先运行 scripts\install-dotnet.ps1" -ForegroundColor Red
    exit 1
}

$project = Join-Path $root 'src\ThermalTrace\ThermalTrace.csproj'

# 基础版本从 csproj 的 <Version> 读，保证两个版本不会各写各的版本号
. (Join-Path $PSScriptRoot 'version.ps1')
$versionInfo = Get-ThermalTraceVersion -ProjectPath $project
$baseVersion = $versionInfo.Version

if ($Edition -eq 'standard') {
    $outDir = Join-Path $root 'publish-standard'
    $publishVersion = "$baseVersion+standard"
    $editionArgs = @('-p:PushEnabled=false',
                     "-p:InformationalVersion=$publishVersion",
                     '-p:IncludeSourceRevisionInInformationalVersion=false')
} else {
    $outDir = Join-Path $root 'publish'
    $publishVersion = $baseVersion
    $editionArgs = @('-p:PushEnabled=true')
}

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$extra = if ($SelfContained) {
    @('-p:SelfContained=true', '-p:PublishSingleFile=true',
      '-p:IncludeNativeLibrariesForSelfExtract=true',
      '-p:EnableCompressionInSingleFile=true')
} else {
    @('-p:SelfContained=false', '-p:PublishSingleFile=false')
}

Write-Host "[*] 发布 $Edition 版（$publishVersion）..." -ForegroundColor Cyan
& $dotnet publish $project -c Release -r win-x64 -o $outDir @extra @editionArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERR] 发布失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $outDir 'ThermalTrace.exe'
if (-not (Test-Path $exe)) {
    Write-Host "[ERR] 未生成 ThermalTrace.exe" -ForegroundColor Red
    exit 1
}

$item = Get-Item $exe
$sizeMb = [math]::Round($item.Length / 1MB, 1)
$total = [math]::Round(((Get-ChildItem $outDir -Recurse | Measure-Object Length -Sum).Sum) / 1MB, 1)
$actualVersion = $item.VersionInfo.ProductVersion

Write-Host ""
Write-Host "[OK] 发布完成（$Edition）" -ForegroundColor Green
Write-Host "    版本:        $actualVersion"
Write-Host "    单文件:      $exe  ($sizeMb MB)"
Write-Host "    整目录:      $outDir  ($total MB)"
Write-Host ""

if ($Edition -eq 'standard') {
    Write-Host "普通版不含墨水屏推送；后续由第二批制作安装包分发。" -ForegroundColor Yellow
} else {
    Write-Host "安装到固定位置并注册开机自启（不要直接运行构建目录里的 exe 作为自启目标）:" -ForegroundColor Yellow
    Write-Host "    powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Register" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "（单文件 exe 也可拷贝到任意目录运行，双击会触发 UAC 提权。）" -ForegroundColor DarkGray
}

if ($OpenDir) { Invoke-Item $outDir }