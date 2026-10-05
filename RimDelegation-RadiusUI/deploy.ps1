#Requires -Version 5.1
<#
.SYNOPSIS
    把 RimDelegation - Radius UI 模组文件夹部署（镜像复制）到 RimWorld 的 Mods 目录，或创建目录符号链接。

.EXAMPLE
    pwsh -File .\deploy.ps1                 # 复制到默认游戏 Mods 目录
    pwsh -File .\deploy.ps1 -Link           # 改为符号链接（需管理员/开发者模式）
    pwsh -File .\deploy.ps1 -RimWorldDir "D:\SteamLibrary\steamapps\common\RimWorld"

.NOTES
    复制模式会排除 Source\obj、Source\bin 和 .git，保持 Mods 目录干净。
#>
[CmdletBinding()]
param(
    [string]$RimWorldDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
    [string]$ModFolderName = 'RimDelegation-RadiusUI',
    [switch]$Link
)

$ErrorActionPreference = 'Stop'

$src = $PSScriptRoot
$modsRoot = Join-Path $RimWorldDir 'Mods'
if (-not (Test-Path $modsRoot)) { throw "找不到游戏 Mods 目录：$modsRoot" }

$dst = Join-Path $modsRoot $ModFolderName

if ($Link) {
    if (Test-Path $dst) {
        Write-Host "目标已存在，先移除：$dst" -ForegroundColor Yellow
        Remove-Item $dst -Recurse -Force
    }
    New-Item -ItemType SymbolicLink -Path $dst -Target $src | Out-Null
    Write-Host "已创建符号链接：$dst -> $src" -ForegroundColor Green
    return
}

Write-Host "复制：$src"
Write-Host "  ->  $dst"

# 文档不随包发布：/XF 只阻止复制，不会删掉目标里已有的同名文件，所以先删一次
foreach ($doc in @('README.md')) {
    $f = Join-Path $dst $doc
    if (Test-Path $f) { Remove-Item $f -Force }
}
# robocopy 退出码 0-7 均表示成功
# ⚠️ 必须显式限制重试次数：robocopy 的默认值是 /R:1000000 /W:30 ——
#    只要有一个文件没有写权限（或游戏开着锁住了 dll），它就会"重试一百万次、每次等 30 秒"，
#    表现为**永久挂起**而不是报错。RimDelegation 的 deploy.ps1 早就堵了这个坑，这里补上。
$rcArgs = @(
    $src, $dst,
    '/MIR',
    '/R:2', '/W:1',
    '/XD', (Join-Path $src 'Source\obj'), (Join-Path $src 'Source\bin'), (Join-Path $src '.git'),
    '/XF', 'README.md',
    '/NFL', '/NDL', '/NJH', '/NJS', '/NP'
)
& robocopy @rcArgs | Out-Null
$code = $LASTEXITCODE
if ($code -ge 8) { throw "robocopy 失败，退出码 $code" }

# ---- 复制后自检（2026-09-26 补）：robocopy 在目标目录无写权限时会打印
#      "ERROR 5 (0x00000005) Accessing Destination Directory" 却**仍然退出 0**，
#      只看 $code 会喊"部署完成"而游戏加载的还是旧 dll。这里比对 dll 哈希。
$srcDll = Join-Path $src 'Assemblies\RimDelegation.RadiusUI.dll'
$dstDll = Join-Path $dst 'Assemblies\RimDelegation.RadiusUI.dll'
if (-not (Test-Path $srcDll)) { throw "找不到构建产物：$srcDll（先跑 build.ps1）" }
if (-not (Test-Path $dstDll)) { throw "部署后目标里没有 dll：$dstDll" }
$hSrc = (Get-FileHash $srcDll -Algorithm SHA256).Hash
$hDst = (Get-FileHash $dstDll -Algorithm SHA256).Hash
if ($hSrc -ne $hDst) {
    throw "部署校验失败：目标 dll 与构建产物不一致（源 $($hSrc.Substring(0,16))… / 目标 $($hDst.Substring(0,16))…）。游戏加载的还是旧 dll。"
}
Write-Host "自检通过：目标 dll 与构建产物一致（SHA256 $($hSrc.Substring(0,16))…）" -ForegroundColor Green

Write-Host ''
Write-Host "部署完成：$dst" -ForegroundColor Green
Write-Host "产物：$(Join-Path $dst 'Assemblies\RimDelegation.RadiusUI.dll')"
Write-Host ''
Write-Host '在游戏内 Mod 列表按此顺序启用：'
Write-Host '  Harmony  ->  Radius UI Framework  ->  RimDelegation 边缘委派  ->  RimDelegation - Radius UI'
