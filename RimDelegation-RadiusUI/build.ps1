#Requires -Version 5.1
<#
.SYNOPSIS
    编译 RimDelegation - Radius UI，产物输出到 <模组根>\Assemblies\RimDelegation.RadiusUI.dll

.EXAMPLE
    pwsh -File .\build.ps1
    pwsh -File .\build.ps1 -Configuration Debug
    pwsh -File .\build.ps1 -RimWorldDir "D:\SteamLibrary\steamapps\common\RimWorld"

.NOTES
    使用 VS2022 的 MSBuild + .NET Framework 4.7.2 目标包，不涉及 NuGet 还原，可离线编译。
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$RimWorldDir,

    [string]$HarmonyDir,

    # Radius UI Framework 的 Assemblies 目录（默认取 workshop 3786107692）
    [string]$RadiusUIDir,

    # RimDelegation 的 Assemblies 目录（默认取同级的兄弟模组）
    [string]$RimDelegationDir
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'Source\RimDelegation.RadiusUI.csproj'

if (-not (Test-Path $proj)) { throw "找不到工程文件：$proj" }

# --- 0. 先确认三个外部引用都在（缺一个 MSBuild 只会给一句含糊的报错）---
if (-not $RimWorldDir) { $RimWorldDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld' }
if (-not $HarmonyDir) { $HarmonyDir = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077\Current\Assemblies' }
if (-not $RadiusUIDir) { $RadiusUIDir = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\3786107692\Assemblies' }
if (-not $RimDelegationDir) { $RimDelegationDir = Join-Path (Split-Path $root -Parent) 'RimDelegation\Assemblies' }

$required = @(
    @{ Name = 'Assembly-CSharp.dll'; Path = Join-Path $RimWorldDir 'RimWorldWin64_Data\Managed\Assembly-CSharp.dll' },
    @{ Name = '0Harmony.dll'; Path = Join-Path $HarmonyDir '0Harmony.dll' },
    @{ Name = 'RadiusUI.Framework.dll'; Path = Join-Path $RadiusUIDir 'RadiusUI.Framework.dll' },
    @{ Name = 'RimDelegation.dll'; Path = Join-Path $RimDelegationDir 'RimDelegation.dll' }
)
foreach ($r in $required) {
    if (-not (Test-Path $r.Path)) { throw "缺少引用：$($r.Name) —— 期望位置 $($r.Path)" }
    Write-Host ("引用 {0,-26} {1}" -f $r.Name, $r.Path) -ForegroundColor DarkGray
}

# --- 1. 定位 MSBuild ---
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
        -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
}
if (-not $msbuild) {
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { $msbuild = $cmd.Source }
}
if (-not $msbuild) {
    throw '未找到 MSBuild。请安装 Visual Studio 2022（勾选“.NET 桌面开发”工作负载）。'
}
Write-Host "MSBuild   : $msbuild" -ForegroundColor Cyan

# --- 2. 编译 ---
$msbuildArgs = @(
    $proj
    '/t:Build'
    "/p:Configuration=$Configuration"
    "/p:RimWorldDir=$RimWorldDir"
    "/p:HarmonyDir=$HarmonyDir"
    "/p:RadiusUIDir=$RadiusUIDir"
    "/p:RimDelegationDir=$RimDelegationDir"
    '/nologo'
    '/v:m'
)

Write-Host "编译中（$Configuration）..." -ForegroundColor Cyan
& $msbuild @msbuildArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，MSBuild 退出码 $LASTEXITCODE" }

# --- 3. 校验产物 ---
$out = Join-Path $root 'Assemblies\RimDelegation.RadiusUI.dll'
if (-not (Test-Path $out)) { throw "编译流程结束但没有产物：$out" }

$info = Get-Item $out
$refs = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($out).GetReferencedAssemblies() |
    ForEach-Object { $_.Name }

Write-Host ''
Write-Host "编译成功：$($info.FullName)" -ForegroundColor Green
Write-Host "  大小    : $($info.Length) 字节"
Write-Host "  修改时间: $($info.LastWriteTime)"
Write-Host "  引用    : $($refs -join ', ')"
Write-Host ''
Write-Host '提醒：Assemblies\ 里应当【只有】RimDelegation.RadiusUI.dll，'
Write-Host '      不应出现 RadiusUI.Framework.dll 或 RimDelegation.dll（副本会导致程序集重复加载）。'
Write-Host '日志查看： %USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log'
