#Requires -Version 5.1
<#
.SYNOPSIS
    把 RimDelegation 模组文件夹部署（镜像复制）到 RimWorld 的 Mods 目录，或创建目录符号链接。

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
    [string]$ModFolderName = 'RimDelegation',
    [switch]$Link
)

$ErrorActionPreference = 'Stop'

$src = $PSScriptRoot
$modsRoot = Join-Path $RimWorldDir 'Mods'
if (-not (Test-Path $modsRoot)) { throw "找不到游戏 Mods 目录：$modsRoot" }

$dst = Join-Path $modsRoot $ModFolderName

# 预检：游戏运行时 Assemblies\*.dll 会被进程锁住，robocopy 必然失败。
# 这里直接说清楚，而不是让 robocopy 去重试。
$rw = Get-Process -Name RimWorldWin64 -ErrorAction SilentlyContinue
if ($rw) {
    throw "检测到 RimWorld 正在运行（PID $($rw.Id)）—— 请先完全关闭游戏再部署，否则 Assemblies 里的 dll 被占用，复制一定失败。"
}

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
foreach ($doc in @('DESIGN.md', 'README.md')) {
    $f = Join-Path $dst $doc
    if (Test-Path $f) { Remove-Item $f -Force }
}
# robocopy 退出码 0-7 均表示成功
# ⚠️ 必须显式限制重试次数：robocopy 的默认值是 /R:1000000 /W:30 ——
#    只要有一个文件被占用（游戏开着）或没有写权限，它就会"重试一百万次、每次等 30 秒"，
#    表现为**永久挂起**（而不是报错）。生产脚本里这是必须堵的坑。
$rcArgs = @(
    $src, $dst,
    '/MIR',
    '/R:2', '/W:1',
    '/XD', (Join-Path $src 'Source\obj'), (Join-Path $src 'Source\bin'), (Join-Path $src '.git'),
    '/XF', 'DESIGN.md', 'README.md',
    '/NFL', '/NDL', '/NJH', '/NJS', '/NP'
)
# **不要把输出吞掉**：失败时逐文件的原因（Access denied / 正在被另一进程使用）就在这里，
# 吞掉之后只能靠猜。
$rcOut = & robocopy @rcArgs
$code = $LASTEXITCODE
if ($code -ge 8) {
    Write-Host ''
    Write-Host '--- robocopy 末尾输出 ---' -ForegroundColor DarkYellow
    $rcOut | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
    throw "robocopy 失败，退出码 $code（8 及以上 = 有文件没复制成功）。常见原因：目标目录没有写权限（Access denied），或文件仍被占用。"
}

# ---- 复制后自检（2026-09-26 补）------------------------------------------------
# 为什么不能只看退出码：robocopy 在**目标目录没有写权限**时会打印
#   "ERROR 5 (0x00000005) Accessing Destination Directory ... Access is denied."
# 但退出码**仍然是 0**（目录被标为 skipped、文件数为 0）—— 只看 $code 就会喊
# "部署完成"，而游戏里加载的还是旧 dll（DSH 沙箱下实测踩到，排查了半天）。
# 两道：① 输出里出现 ERROR n 直接失败；② 比对源与目标的 dll 哈希。
$errLines = $rcOut | Select-String -Pattern 'ERROR \d+' -ErrorAction SilentlyContinue
if ($errLines) {
    Write-Host ''
    Write-Host '--- robocopy 报了错但退出码是 0（静默失败）---' -ForegroundColor DarkYellow
    $errLines | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
    throw "robocopy 输出里有 ERROR，判定部署失败（退出码 $code 不可信）。"
}
$srcDll = Join-Path $src 'Assemblies\RimDelegation.dll'
$dstDll = Join-Path $dst 'Assemblies\RimDelegation.dll'
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
Write-Host "产物：$(Join-Path $dst 'Assemblies\RimDelegation.dll')"
Write-Host '在游戏内 Mod 列表勾选启用即可（依赖 Harmony）。'
