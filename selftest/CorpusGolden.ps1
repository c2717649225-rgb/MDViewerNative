# CorpusGolden.ps1 — corpus golden manifest (PLAN-EXECUTE I4). 结构 dump + KP 断行 hash 单一真相源。
# E13 起从 work/rec.ps1 迁来(清单原为 work/corpus_golden/manifest.tsv),内容与判定不变。
# Usage: powershell -File selftest/CorpusGolden.ps1 -Verify      # 任何漂移 exit 1(重录增量之外这是常驻门)
#        powershell -File selftest/CorpusGolden.ps1 -ReRecord    # 重写 selftest/corpus_golden.tsv(必须人审 git diff)
# 使用 PATH 上的 dotnet:经 build.ps1 -SdkDirectory 调用时,PATH 与 DOTNET_ROOT 已指向所选 SDK。
param([switch]$Verify, [switch]$ReRecord)
$ErrorActionPreference = 'Stop'
$savedConsoleEncoding = [Console]::OutputEncoding
try {
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$root = $PSScriptRoot
$st = $root
$files = [ordered]@{
  big     = (Resolve-Path "$root\..\engine\big.md").Path
  big20k  = (Resolve-Path "$root\..\engine\big20k.md").Path
  torture = (Resolve-Path "$root\..\samples\edge_torture.md").Path
  qf      = (Resolve-Path "$root\..\samples\全格式测试.md").Path
  test2   = (Resolve-Path "$root\..\engine\test2.md").Path
  full    = (Resolve-Path "$root\..\samples\全格式测试.md").Path
  # 宽内容回归网:宽表格 + 项内嵌代码块。原有六个语料都不含这两类结构,
  # 两次横向滚动改动的金标准都没能拦住,故单列此文件补网(30)。
  wide    = (Resolve-Path "$root\..\samples\wide_torture.md").Path
}
$manifest = Join-Path $root "corpus_golden.tsv"
if (-not (Test-Path (Join-Path $st "bin\Release\net10.0-windows"))) { throw "build selftest first (dotnet build -c Release in selftest dir)" }
$rows = @()
foreach ($k in $files.Keys) {
  $p = $files[$k]
  $dumpLines = dotnet run --project $st -c Release --no-build -- --dump $p
  if ($LASTEXITCODE -ne 0) { throw "block dump failed: $p" }
  $dump = $dumpLines -join "`n"
  $sha = [BitConverter]::ToString((New-Object System.Security.Cryptography.SHA256CryptoServiceProvider).ComputeHash((New-Object System.Text.UTF8Encoding).GetBytes($dump))).Replace("-","").Substring(0,16).ToLower()
  # qf covers missing relative images (base = selftest/, which holds none of test_*.bmp/jpg/png);
  # full covers the same source with real image assets next to it in MorningDesk/.
  $kpArgs = @('run', '--project', $st, '-c', 'Release', '--no-build', '--', '--kp', $p)
  if ($k -eq 'qf') { $kpArgs += $root }
  $kpLines = & dotnet @kpArgs
  if ($LASTEXITCODE -ne 0) { throw "layout dump failed: $p" }
  $hash = ($kpLines | Select-String -Pattern 'hash=([0-9a-f]+)').Matches[0].Groups[1].Value
  $lines = ($kpLines | Select-String -Pattern 'kpLines=(\d+)').Matches[0].Groups[1].Value
  $tH   = ($kpLines | Select-String -Pattern 'totalH=(\d+)').Matches[0].Groups[1].Value
  if (-not $hash) { $hash = "none"; $lines = "0"; $tH = ($tH -join "") }
  $rows += "$k`t$sha`t$hash`t$lines`t$tH"
}
$new = $rows -join "`n"
if ($ReRecord) {
  Set-Content -Path $manifest -Value $rows -Encoding ASCII
  "re-recorded -> $manifest"
  exit 0
}
if ($Verify) {
  if (-not (Test-Path $manifest)) { "NO GOLDEN MANIFEST"; exit 1 }
  $old = (Get-Content $manifest) -join "`n"
  if ($old -eq $new) { "corpus golden: OK" ; exit 0 }
  "corpus golden DRIFT:"
  $o = Get-Content $manifest; $n = $rows
  for ($i = 0; $i -lt $o.Count; $i++) { if ($o[$i] -ne $n[$i]) { "  old: $($o[$i])"; "  new: $($n[$i])" } }
  exit 1
}
"usage: -Verify | -ReRecord"
exit 1
} finally { [Console]::OutputEncoding = $savedConsoleEncoding }
