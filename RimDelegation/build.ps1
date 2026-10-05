#Requires -Version 5.1
<#
.SYNOPSIS
    编译 RimDelegation，产物输出到 <模组根>\Assemblies\RimDelegation.dll

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

    # 覆盖游戏根目录（默认写在 csproj 里）
    [string]$RimWorldDir,

    # 覆盖 Harmony 所在目录（默认 brrainz.harmony 的 Current\Assemblies）
    [string]$HarmonyDir
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'Source\RimDelegation.csproj'

if (-not (Test-Path $proj)) { throw "找不到工程文件：$proj" }

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
    '/nologo'
    '/v:m'
)
if ($RimWorldDir) { $msbuildArgs += "/p:RimWorldDir=$RimWorldDir" }
if ($HarmonyDir) { $msbuildArgs += "/p:HarmonyDir=$HarmonyDir" }

Write-Host "编译中（$Configuration）..." -ForegroundColor Cyan
& $msbuild @msbuildArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，MSBuild 退出码 $LASTEXITCODE" }

# --- 3. 校验产物 ---
$out = Join-Path $root 'Assemblies\RimDelegation.dll'
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
Write-Host '下一步：pwsh -File .\deploy.ps1'
Write-Host '日志查看： %USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log'
