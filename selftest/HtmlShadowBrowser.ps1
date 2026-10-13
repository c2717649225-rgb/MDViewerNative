# E14 / TD-11:用真实浏览器(无头 Edge,独立临时配置目录)判定导出 HTML 是否执行脚本。按需运行,不在 Verify 里。
# 先运行 selftest 的 --html-shadow 生成 old-*/new-* 探针文件,再:powershell -File selftest/HtmlShadowBrowser.ps1 -Dir <输出目录>
# 探针里 P() 是标记函数;ctl-* 为本脚本写入的对照(两个应执行,一个不应执行),对照不符时判定结果不可信。
param([Parameter(Mandatory = $true)][string]$Dir)
$ErrorActionPreference = 'Continue'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$profileDir = Join-Path $env:TEMP 'mdv-html-shadow-edge'
$mark = "<script>function P(){document.documentElement.setAttribute('data-pwned','1')}</script>"
$click = "<script>addEventListener('load',function(){setTimeout(function(){document.querySelectorAll('a,button').forEach(function(e){try{e.dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true}))}catch(_){}})},300)})</script>"
Set-Content -LiteralPath (Join-Path $Dir 'ctl-onerror.html') -Value "<!DOCTYPE html><html><head>$mark</head><body><img src=x onerror=P()>$click</body></html>" -Encoding UTF8
Set-Content -LiteralPath (Join-Path $Dir 'ctl-jshref.html') -Value "<!DOCTYPE html><html><head>$mark</head><body><a href=`"javascript:P()`">x</a>$click</body></html>" -Encoding UTF8
Set-Content -LiteralPath (Join-Path $Dir 'ctl-none.html') -Value "<!DOCTYPE html><html><head>$mark</head><body><p>x</p>$click</body></html>" -Encoding UTF8
$files = Get-ChildItem -LiteralPath $Dir -Filter '*.html' | Sort-Object Name
foreach ($f in $files) {
    $out = Join-Path $env:TEMP ('e14-' + $f.BaseName + '.txt')
    $uri = ([Uri]$f.FullName).AbsoluteUri
    $argv = @('--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', "--user-data-dir=$profileDir", '--virtual-time-budget=3000', '--dump-dom', $uri)
    $p = Start-Process -FilePath $edge -ArgumentList $argv -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(20000)) {
        Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*mdv-html-shadow-edge*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
        "{0,-28} TIMEOUT" -f $f.BaseName
        continue
    }
    $dom = if (Test-Path $out) { Get-Content -Raw $out } else { '' }
    $state = if ($dom -match 'data-pwned="1"') { 'EXECUTED' } elseif ($dom.Length -gt 0) { 'inert' } else { 'NO-DOM' }
    "{0,-28} {1}" -f $f.BaseName, $state
    Remove-Item $out, "$out.err" -ErrorAction SilentlyContinue
}
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*mdv-html-shadow-edge*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath $profileDir -Recurse -Force -ErrorAction SilentlyContinue
