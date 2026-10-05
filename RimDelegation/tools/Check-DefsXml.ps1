# RimDelegation Def XML 体检 + 流程数值对齐（每次改完 Defs/*.xml 都跑一次）
#
# ── 为什么需要它（两件事都要机器查）──────────────────────────────────────────────
#  ① **良构性**：Def XML 一旦不是良构 XML（最常见的坑是注释里出现连续两个减号，
#     或注释未闭合），RimWorld 只会把整份文件判为 "unknown parse failure" ——
#     表现为"某个功能整体消失"（S26 实例：`DelegationDef 0 个` ⇒ 右键没有委派选项），
#     而游戏不会崩、也不会弹任何提示，只能靠翻 Player.log 才发现。
#     ⚠️ 2026-10-05（RIM-11）起扫描根从 `Defs\` 扩到 **Defs + Patches + Languages** ——
#        当年英文 Keyed 里那个 `--` 就是被"只扫 Defs"漏掉的（脚本报 4/4 良构，
#        英文环境下 59 个 key 却整份作废）。
#  ② **流程数值一致性**（RIM-11）：18 段流程的时长 / 位置 / 条件 / 各池条数，
#     加上"每条委派的实际固定流程合计"与"随机事件期望频率"，全部从 Def 复算，
#     并与**注释里手写的数字**对账 —— P-A2（注释 5.5h vs 实算 7.0h）、
#     P-A7（注释 0.84 次/天 vs 实算 1.575）就是这么漂移出来的。
#
# ── 用法 ────────────────────────────────────────────────────────────────────
#   powershell -File tools\Check-DefsXml.ps1                # 默认取脚本上一级为 mod 根
#   powershell -File tools\Check-DefsXml.ps1 -ModRoot <路径>
#   powershell -File tools\Check-DefsXml.ps1 -Quiet         # 只打印 WARN/FAIL 与汇总
#
# ── 退出码 ──────────────────────────────────────────────────────────────────
#   0 = 良构且零 WARN
#   1 = 有 XML 不良构 / 目录缺失（**不要部署**）
#   2 = 良构、但有流程数值 WARN（可以部署，但应当先看清单）

param(
    [string]$ModRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Read-Lines([string]$path) {
    return [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
}

$script:WarnCount = 0
function Write-WarnMsg([string]$msg) {
    $script:WarnCount++
    Write-Host ("WARN  " + $msg) -ForegroundColor Yellow
}
function Write-OkMsg([string]$msg) {
    if (-not $Quiet) { Write-Host ("OK    " + $msg) -ForegroundColor Green }
}
function Write-Section([string]$msg) {
    if (-not $Quiet) {
        Write-Host ''
        Write-Host ("-- " + $msg + " " + ('-' * [Math]::Max(0, 60 - $msg.Length))) -ForegroundColor Cyan
    }
}

# ────────────────────────────────────────────────────────────────────────────
# 第 1 段：良构性（Defs / Patches / Languages）
# ────────────────────────────────────────────────────────────────────────────
Write-Section '1. XML 良构性'
$scanDirs = @('Defs', 'Patches', 'Languages')
$xmlFiles = @()
$missingDirs = @()
foreach ($d in $scanDirs) {
    $full = Join-Path $ModRoot $d
    if (-not (Test-Path $full)) { $missingDirs += $d; continue }
    $xmlFiles += Get-ChildItem -Path $full -Filter *.xml -Recurse -File
}
if ($missingDirs.Count -gt 0) {
    Write-Host ("FAIL  找不到目录：" + ($missingDirs -join ', ') + "（ModRoot = $ModRoot）") -ForegroundColor Red
    exit 1
}

$bad = 0
foreach ($f in $xmlFiles) {
    try {
        $doc = New-Object System.Xml.XmlDocument
        $doc.Load($f.FullName)
        Write-OkMsg $f.FullName.Substring($ModRoot.Length).TrimStart('\')
    }
    catch {
        $bad++
        Write-Host ("FAIL  " + $f.FullName.Substring($ModRoot.Length).TrimStart('\') + "  ->  " + $_.Exception.Message) -ForegroundColor Red
    }
}
if ($bad -gt 0) {
    Write-Host ''
    Write-Host ("有 $bad 个 XML 不是良构 —— 不要部署！RimWorld 会把整份文件丢掉（功能整体消失，且不报错）。") -ForegroundColor Red
    exit 1
}
Write-OkMsg ("共 " + $xmlFiles.Count + " 个 XML 文件良构（Defs / Patches / Languages）。")

# ────────────────────────────────────────────────────────────────────────────
# 第 2 段：流程数值对齐（RIM-11）
# ────────────────────────────────────────────────────────────────────────────
$defPath = Join-Path $ModRoot 'Defs\RimDelegation_Delegations.xml'
$evtPath = Join-Path $ModRoot 'Defs\RimDelegation_DelegationEvents.xml'
if (-not (Test-Path $defPath)) { Write-Host "FAIL  找不到 $defPath" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $evtPath)) { Write-Host "FAIL  找不到 $evtPath" -ForegroundColor Red; exit 1 }

$defLines = Read-Lines $defPath
$defText = [string]::Join("`n", $defLines)

function Field-Of([string]$body, [string]$name) {
    $m = [regex]::Match($body, "(?s)<$name>(.*?)</$name>")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}
function Flag-Of([string]$body, [string]$name) {
    return ((Field-Of $body $name) -eq 'true')
}
function Pool-Count([string]$body, [string]$name) {
    $m = [regex]::Match($body, "(?s)<$name>(.*?)</$name>")
    if (-not $m.Success) { return -1 }   # -1 = 这个池根本没写
    return ([regex]::Matches($m.Groups[1].Value, '<li>')).Count
}

$phaseMatches = [regex]::Matches($defText, '(?s)<RimDelegation\.DelegationPhaseDef>(.*?)</RimDelegation\.DelegationPhaseDef>')
$phases = @()
foreach ($m in $phaseMatches) {
    $body = $m.Groups[1].Value
    $lineNo = ($defText.Substring(0, $m.Index) -split "`n").Count
    $phases += [pscustomobject]@{
        defName     = Field-Of $body 'defName'
        label       = Field-Of $body 'label'
        hours       = [double](Field-Of $body 'hours')
        jitter      = Field-Of $body 'hoursJitter'
        afterWork   = Flag-Of $body 'afterWork'
        reqThreat   = Flag-Of $body 'requireThreat'
        reqNoThreat = Flag-Of $body 'requireNoThreat'
        position    = Field-Of $body 'squadPosition'
        skill       = Field-Of $body 'skillDef'
        category    = Field-Of $body 'flowCategory'
        pDefault    = Pool-Count $body 'ambientLines'
        pSolo       = Pool-Count $body 'ambientLinesSolo'
        pPacked     = Pool-Count $body 'ambientLinesPacked'
        pHostile    = Pool-Count $body 'ambientLinesHostile'
        line        = $lineNo
    }
}

if ($phases.Count -ne 18) {
    Write-WarnMsg ("流程段数量是 " + $phases.Count + " 个，预期 18 个（RIM-10 清点基线）—— 加减段之后请同步更新本脚本与文档。")
}

Write-Section '2. 流程段表（机器复算）'
if (-not $Quiet) {
    $fmt = "{0,-38} {1,-14} {2,5} {3,6} {4,4} {5,6} {6,11} {7,4} {8,4} {9,4} {10,4}"
    Write-Host ($fmt -f 'defName', 'label', 'hours', 'jitter', 'aftr', 'thrt', 'position', '默认', '单人', '驮兽', '敌情')
    foreach ($p in $phases) {
        Write-Host ($fmt -f $p.defName, $p.label, $p.hours, ($(if ($p.jitter) { $p.jitter } else { '-' })),
            ($(if ($p.afterWork) { '尾' } else { '前' })),
            ($(if ($p.reqThreat) { '有' } elseif ($p.reqNoThreat) { '无' } else { '-' })),
            ($(if ($p.position) { $p.position } else { '(继承)' })),
            ($(if ($p.pDefault -lt 0) { 'x' } else { $p.pDefault })),
            ($(if ($p.pSolo -lt 0) { 'x' } else { $p.pSolo })),
            ($(if ($p.pPacked -lt 0) { 'x' } else { $p.pPacked })),
            ($(if ($p.pHostile -lt 0) { 'x' } else { $p.pHostile })))
    }
}

# ---- 报警：同名段时长不一致 ----
foreach ($g in ($phases | Group-Object label)) {
    $distinct = ($g.Group | Select-Object -ExpandProperty hours -Unique)
    if ($distinct.Count -gt 1) {
        Write-WarnMsg ("同名段「" + $g.Name + "」的 hours 不一致：" + (($g.Group | ForEach-Object { $_.defName + '=' + $_.hours + 'h' }) -join ' / '))
    }
}

# ---- 报警：池条数 / 缺驮兽池 ----
foreach ($p in $phases) {
    $pools = [ordered]@{ 'ambientLines' = $p.pDefault; 'ambientLinesSolo' = $p.pSolo; 'ambientLinesPacked' = $p.pPacked; 'ambientLinesHostile' = $p.pHostile }
    foreach ($k in $pools.Keys) {
        $n = $pools[$k]
        if ($n -eq 0) { Write-WarnMsg ("$($p.defName)。$k 是空池（0 条）—— 这一格永远不会出旁白。") }
        elseif ($n -gt 0 -and $n -lt 3) { Write-WarnMsg ("$($p.defName)。$k 只有 $n 条（< 3）—— 池太小时会明显重复。") }
    }
    if ($p.pPacked -lt 0) { Write-WarnMsg ("$($p.defName) 没有 ambientLinesPacked —— 带驮兽时这一段只会用默认池。") }
}

# ---- 每条委派的固定流程合计 ----
$phaseByName = @{}
foreach ($p in $phases) { $phaseByName[$p.defName] = $p }

$delegMatches = [regex]::Matches($defText, '(?s)<RimDelegation\.DelegationDef>(.*?)</RimDelegation\.DelegationDef>')
$pathTotals = @()
foreach ($m in $delegMatches) {
    $body = $m.Groups[1].Value
    $dn = Field-Of $body 'defName'
    $fl = [regex]::Match($body, '(?s)<flowPhases>(.*?)</flowPhases>')
    if (-not $fl.Success) {
        $pathTotals += [pscustomobject]@{ name = $dn; branch = '无流程'; hours = 0.0; phases = '' }
        continue
    }
    $names = [regex]::Matches($fl.Groups[1].Value, '<li>([^<]+)</li>') | ForEach-Object { $_.Groups[1].Value.Trim() }
    foreach ($hasThreat in @($true, $false)) {
        $sum = 0.0; $used = @()
        foreach ($n in $names) {
            if (-not $phaseByName.ContainsKey($n)) { Write-WarnMsg ("$dn 的 flowPhases 里出现未知段 '$n'（defName 拼错？那一段会被静默跳过 ⇒ 工期凭空变短）"); continue }
            $ph = $phaseByName[$n]
            if ($ph.reqThreat -and -not $hasThreat) { continue }
            if ($ph.reqNoThreat -and $hasThreat) { continue }
            $sum += $ph.hours
            $used += $n
        }
        $pathTotals += [pscustomobject]@{ name = $dn; branch = $(if ($hasThreat) { '有敌情' } else { '无敌情' }); hours = $sum; phases = ($used -join ' + ') }
    }
}

Write-Section '3. 各委派的固定流程合计（机器复算）'
$seen = @{}
foreach ($t in $pathTotals) {
    $key = $t.name + '|' + $t.hours
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    Write-Host ("  {0,-34} {1,-8} {2,7:0.##}h   {3}" -f $t.name, $t.branch, $t.hours, $t.phases)
}
$computedTotals = @($pathTotals | Select-Object -ExpandProperty hours -Unique)
Write-Host ("  => 实算合计集合：" + (($computedTotals | Sort-Object | ForEach-Object { "$_ h" }) -join ' / '))

# ---- 报警：注释里手写的"固定 Xh"与实算对不上 ----
$declared = @([regex]::Matches($defText, '固定\s*([0-9]+(?:\.[0-9]+)?)\s*h') | ForEach-Object { [double]$_.Groups[1].Value } | Select-Object -Unique)
$mismatch = $false
foreach ($d in $declared) {
    if (-not ($computedTotals | Where-Object { [Math]::Abs($_ - $d) -lt 0.001 })) {
        $mismatch = $true
        Write-WarnMsg ("注释里写着「固定 $d h」，但复算合计里没有这个数（实算：" + (($computedTotals | Sort-Object) -join ' / ') + "）—— P-A2 那类过期注释。")
    }
}
if (-not $mismatch) {
    Write-OkMsg ("注释声明的固定合计 " + (($declared | Sort-Object) -join ' / ') + " h 与实算对得上。")
}

# ---- 随机事件期望频率 vs 注释 ----
$evtText = [string]::Join("`n", (Read-Lines $evtPath))
$mtbs = @([regex]::Matches($evtText, '<mtbDays>([0-9.]+)</mtbDays>') | ForEach-Object { [double]$_.Groups[1].Value })
$evtRate = 0.0
foreach ($v in $mtbs) { if ($v -gt 0) { $evtRate += 1.0 / $v } }
$declaredRate = [regex]::Match($evtText, '=\s*([0-9]+(?:\.[0-9]+)?)\s*次/天')
Write-Section '4. 随机事件期望频率'
Write-Host ("  事件条数 = {0}；MTB = {1}" -f $mtbs.Count, ($mtbs -join ' / '))
Write-Host ("  复算 Σ(1/mtbDays) = {0:0.###} 次/天" -f $evtRate)
if ($declaredRate.Success) {
    $dr = [double]$declaredRate.Groups[1].Value
    if ([Math]::Abs($dr - $evtRate) -gt 0.005) {
        Write-WarnMsg ("注释写的频率 $dr 次/天 与复算的 $evtRate 次/天 不符（P-A7 那类过期注释）。")
    } else {
        Write-OkMsg "注释里的期望频率与复算一致。"
    }
} else {
    Write-WarnMsg "事件注释里没有找到「= X 次/天」形式的期望频率，无法对账。"
}

# ---- 报警：段时长抖动写法 ----
foreach ($p in $phases) {
    if ($p.jitter) {
        $j = 0.0
        if (-not [double]::TryParse(($p.jitter -replace '^[±+-]', ''), [ref]$j)) {
            Write-WarnMsg ("$($p.defName) 的 hoursJitter '$($p.jitter)' 不是数字 —— 会被当成 0（不抖）。")
        } elseif ($j -ge $p.hours) {
            Write-WarnMsg ("$($p.defName) 的 hoursJitter $j >= hours $($p.hours) —— 抖动会一直撞到 0.25h 下限。")
        }
    }
}

# ────────────────────────────────────────────────────────────────────────────
# 第 5 段：Def 字段名白名单（从 C# 源现取现用）
# ────────────────────────────────────────────────────────────────────────────
# 为什么需要：字段名写错（例如 `<hoursJiter>`）**不会**让 XML 变得不良构 ——
# RimWorld 只在 Player.log 里写一行"doesn't correspond to any field"，然后照常加载，
# 表现为"改了没效果"。这里把 C# 的 public 字段名抓出来当白名单，离线就能抓到。
Write-Section '5. Def 字段名白名单（从 Source\*.cs 现取）'

function Get-PublicFieldNames([string]$csPath) {
    $names = @{}
    if (-not (Test-Path $csPath)) { return $names }
    $text = [string]::Join("`n", (Read-Lines $csPath))
    # 去掉块注释 / 行注释，避免把注释与示例 XML 当成字段
    $text = [regex]::Replace($text, '(?s)/\*.*?\*/', '')
    $text = [regex]::Replace($text, '//[^\n]*', '')
    foreach ($m in [regex]::Matches($text, '(?m)^\s*public\s+(?:readonly\s+|static\s+)*(?:[\w\.]+(?:<[^;=]*?>)?(?:\[\])?)\s+(\w+)\s*(?:[;=]|\{ get)')) {
        $names[$m.Groups[1].Value] = $true
    }
    return $names
}

$srcDir = Join-Path $ModRoot 'Source'
$baseDefFields = @('defName', 'label', 'description', 'ignoreConfigErrors', 'ignoreIllegalLabelCharacterConfigError', 'Name', 'Abstract', 'ParentName', 'MayRequire', 'MayRequireAnyOf', 'MayRequireAllOf', 'modExtensions', 'shortHash', 'generated', 'index', 'filePath', 'configErrors')
$defChecks = @(
    @{ tag = 'RimDelegation.DelegationPhaseDef'; cs = 'DelegationPhaseDef.cs' },
    @{ tag = 'RimDelegation.DelegationDef';      cs = 'DelegationDef.cs' },
    @{ tag = 'RimDelegation.DelegationModeDef';  cs = 'DelegationModeDef.cs' },
    @{ tag = 'RimDelegation.DelegationEventDef'; cs = 'DelegationEventDef.cs' }
)
$unknownTotal = 0
foreach ($chk in $defChecks) {
    $allowed = Get-PublicFieldNames (Join-Path $srcDir $chk.cs)
    foreach ($f in $baseDefFields) { $allowed[$f] = $true }
    $blocks = [regex]::Matches($defText, "(?s)<$([regex]::Escape($chk.tag))>(.*?)</$([regex]::Escape($chk.tag))>")
    foreach ($b in $blocks) {
        $body = [regex]::Replace($b.Groups[1].Value, '(?s)<!--.*?-->', '')
        $depth = 0
        foreach ($line in ($body -split "`n")) {
            $t = $line.Trim()
            if ($depth -eq 0) {
                $name = $null
                if ([regex]::IsMatch($t, '^<([\w\.]+)\s*/>')) { $name = [regex]::Match($t, '^<([\w\.]+)').Groups[1].Value }
                elseif ([regex]::IsMatch($t, '^<([\w\.]+)>')) { $name = [regex]::Match($t, '^<([\w\.]+)>').Groups[1].Value }
                if ($name -and -not $allowed.ContainsKey($name)) {
                    $unknownTotal++
                    Write-WarnMsg ("$($chk.tag) 里出现未知字段 <$name> —— RimWorld 不会报错，只会静默忽略（改了没效果）。")
                }
            }
            $opens        = ([regex]::Matches($t, '<[a-zA-Z][\w\.]*[^>]*[^/]>')).Count
            $closes       = ([regex]::Matches($t, '</[a-zA-Z][\w\.]*>')).Count
            $selfClosed   = ([regex]::Matches($t, '<[a-zA-Z][\w\.]*[^>]*/>')).Count
            $depth += ($opens - $closes - $selfClosed)
            if ($depth -lt 0) { $depth = 0 }
        }
    }
}
if ($unknownTotal -eq 0) {
    Write-OkMsg "流程 / 委派 / 模式 / 事件 Def 里没有未知字段名。"
}

# ────────────────────────────────────────────────────────────────────────────
Write-Host ''
if ($script:WarnCount -gt 0) {
    Write-Host ("良构 OK，但有 " + $script:WarnCount + " 条流程数值 WARN —— 见上面清单。") -ForegroundColor Yellow
    exit 2
}
Write-Host ("全部通过：" + $xmlFiles.Count + " 个 XML 良构，18 段流程与事件频率零 WARN。") -ForegroundColor Green
exit 0
