# 存档足迹验收 —— 用法: .\check_save.ps1 [存档路径]  不带参数则取最新存档
param([string]$SavePath)

Add-Type -AssemblyName System.IO.Compression.FileSystem

$sg = Join-Path $env:USERPROFILE 'AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Saved Games'
if (-not $SavePath) {
    $SavePath = (Get-ChildItem $sg -Filter '*.zks' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
Write-Host ("存档: " + (Split-Path $SavePath -Leaf)) -ForegroundColor Cyan
Write-Host ("时间: " + (Get-Item $SavePath).LastWriteTime)
Write-Host ""

# 蓝图索引：guid -> 名字
$idx = @{}
foreach ($line in [System.IO.File]::ReadLines('D:\RT_RetinueMod\ref\rt_probe\bp_index.tsv')) {
    $p = $line -split "`t"
    if ($p.Count -ge 2) { $idx[$p[1]] = $p[0] }
}
$cd = [System.IO.File]::ReadAllText('D:\RT_RetinueMod\ref\rt_probe\cd2.txt')
foreach ($m in [regex]::Matches($cd, '"Name":"([^"]*)","Guid":"([0-9a-f]{32})"')) {
    $idx[$m.Groups[2].Value] = $m.Groups[1].Value
}
Write-Host ("蓝图索引已载入: " + $idx.Count + " 条")
Write-Host ""

$zip = [System.IO.Compression.ZipFile]::OpenRead($SavePath)
try {
    foreach ($name in @('party.json','player.json')) {
        $e = $zip.Entries | Where-Object { $_.Name -eq $name }
        if (-not $e) { Write-Host ("[" + $name + "] 不存在") -ForegroundColor Yellow; continue }

        $sr = New-Object System.IO.StreamReader($e.Open())
        $t = $sr.ReadToEnd(); $sr.Close()
        Write-Host ("=== " + $name + "  (" + [math]::Round($t.Length/1MB,2) + " MB) ===") -ForegroundColor Cyan

        # 1) 卫兵标记
        $g = ([regex]::Matches($t, 'kgd\.guard')).Count
        $c = if ($g -eq 0) { 'Green' } else { 'Yellow' }
        Write-Host ("  kgd.guard 命中: " + $g) -ForegroundColor $c

        # 2) 蓝图位置上的 guid —— 这些必须全部可解析，且不能是 DLC
        $bp = [regex]::Matches($t, '"(?:Blueprint|m_[A-Za-z]*Blueprint|Selection|Feature|Path|m_Race)"\s*:\s*"([0-9a-f]{32})"') |
              ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        $unknown = @($bp | Where-Object { -not $idx.ContainsKey($_) })
        $dlc     = @($bp | Where-Object { $idx.ContainsKey($_) -and $idx[$_] -like 'DLC*' })
        Write-Host ("  蓝图引用: " + $bp.Count + " 个唯一 guid")
        $c = if ($unknown.Count -eq 0) { 'Green' } else { 'Red' }
        Write-Host ("    索引里查不到: " + $unknown.Count + "   <- 必须为 0，否则该存档已有解析风险") -ForegroundColor $c
        if ($unknown.Count -gt 0) { $unknown | Select-Object -First 5 | ForEach-Object { Write-Host ("      " + $_) } }
        $c = if ($dlc.Count -eq 0) { 'Green' } else { 'Yellow' }
        Write-Host ("    DLC 蓝图  : " + $dlc.Count + "   <- 非 0 表示卸载对应 DLC 后此档打不开") -ForegroundColor $c
        if ($dlc.Count -gt 0) { $dlc | Select-Object -First 8 | ForEach-Object { Write-Host ("      " + $idx[$_]) } }

        # 3) 战斗组分布
        $cg = [regex]::Matches($t, 'PartCombatGroup, Code","m_Id":"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }
        if ($cg.Count -gt 0) {
            Write-Host "  战斗组:"
            $cg | Group-Object | Sort-Object Count -Descending | Select-Object -First 5 |
                ForEach-Object { Write-Host ("    " + $_.Count + "  " + $_.Name) }
        }
        Write-Host ""
    }
} finally { $zip.Dispose() }

Write-Host "判据：" -ForegroundColor Cyan
Write-Host "  索引里查不到 = 0        必须满足，否则存档已有解析风险"
Write-Host "  遣散后 kgd.guard = 0    验证 DismissAll 清理干净"
Write-Host "  DLC 蓝图数            非 0 只是提醒：别在带卫兵时关 DLC"