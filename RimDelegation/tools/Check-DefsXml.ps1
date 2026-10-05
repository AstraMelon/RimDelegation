# RimDelegation Def XML 体检（每次改完 Defs/*.xml 都跑一次）
#
# 为什么需要它：Def XML 一旦不是"良构 XML"（最常见的坑是**注释里出现连续两个减号**，
# 或注释未闭合），RimWorld 只会把整份文件判为 "unknown parse failure" ——
# 表现为"某个功能整体消失"（S26 实例：`DelegationDef 0 个` ⇒ 右键没有委派选项），
# 而游戏不会崩、也不会弹任何提示，只能靠翻 Player.log 才发现。
#
# 用法：powershell -File tools\Check-DefsXml.ps1 <mod根目录>
# 退出码 0 = 全部良构；1 = 有文件失败（并打印解析器给的行号/列号）。

param(
    [string]$ModRoot = (Split-Path -Parent $PSScriptRoot)
)

$defsDir = Join-Path $ModRoot "Defs"
if (!(Test-Path $defsDir)) {
    Write-Host "找不到 Defs 目录：$defsDir" -ForegroundColor Red
    exit 1
}

$fail = 0
$files = Get-ChildItem -Path $defsDir -Filter *.xml -Recurse
foreach ($f in $files) {
    try {
        $doc = New-Object System.Xml.XmlDocument
        $doc.Load($f.FullName)
        Write-Host ("OK   " + $f.Name) -ForegroundColor Green
    }
    catch {
        $fail++
        Write-Host ("FAIL " + $f.Name + "  ->  " + $_.Exception.Message) -ForegroundColor Red
    }
}

if ($fail -gt 0) {
    Write-Host ""
    Write-Host "有 $fail 个 Def 文件不是良构 XML —— 不要部署！RimWorld 会把整份文件丢掉（功能整体消失，且不报错）。" -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host ("全部 " + $files.Count + " 个 Def 文件良构。") -ForegroundColor Green
exit 0
