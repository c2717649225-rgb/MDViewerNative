Set-Location -LiteralPath (Split-Path -Parent $MyInvocation.MyCommand.Path)
$edgeDir = Join-Path (Split-Path -Parent (Get-Location)) "engine\edge"
foreach ($f in @('empty', 'no-eol', 'bom-crlf', 'lf-only', 'gbk')) {
  $path = Join-Path $edgeDir "$f.md"
  Start-Process ".\MDViewerNative.exe" -ArgumentList $path
  Start-Sleep -Seconds 2
  $p = Get-Process MDViewerNative -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($p) {
    Write-Host ("{0}: RUNNING title=[{1}] responding={2}" -f $f, $p.MainWindowTitle, $p.Responding)
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Milliseconds 500
  } else { Write-Host "${f}: NOT RUNNING" }
}
