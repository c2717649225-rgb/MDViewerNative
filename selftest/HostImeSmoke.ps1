param([Parameter(Mandatory=$true)][string]$ExePath,[switch]$AllowInput)
$ErrorActionPreference='Stop'
if (-not $AllowInput) { throw 'Real IME input requires -AllowInput and an owned idle desktop.' }
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'A viewer is already running; not touching it.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'DesktopInputSupport.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-ime-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$oldConfig=$env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
[IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`nhints=0`ntoc=0`nfont=16`n")
$previousWindow=[DesktopInput]::GetForegroundWindow()
$app=$null;$child=$null;$oldLayout=[IntPtr]::Zero;$script:passed=0
function Composing { (Text-Of $script:window).Contains('[输入法组词中') }
function Begin-Chinese([string]$Pinyin) {
    Type-OwnedLatin $Pinyin
    Wait-Until { Composing }
}
function Finish-Chinese {
    Press-Owned @(32)
    Wait-Until { -not (Composing) }
}
try {
    $file=Join-Path $fixture 'ime-alpha.md';$other=Join-Path $fixture 'ime-bravo.md'
    $original="base`n"
    [IO.File]::WriteAllText($file,$original);[IO.File]::WriteAllText($other,'other')
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$file+'"') -PassThru
    Wait-Until { [HostNative]::Window([uint32]$app.Id,'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window=[HostNative]::Window([uint32]$app.Id,'MDViewerNative')
    [void][HostNative]::ShowWindow($script:window,5)
    Wait-Until { (Text-Of $script:window).Contains('ime-alpha.md') }
    $child=Start-Process -FilePath $ExePath -ArgumentList ('"'+$other+'"') -PassThru -WindowStyle Hidden
    if (-not $child.WaitForExit(8000)) { throw 'Owned handoff timed out.' }
    $child.Dispose();$child=$null
    Wait-Until { (Text-Of $script:window).Contains('ime-bravo.md') }
    Command 1009
    [void][DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)
    Wait-Until { [DesktopInput]::GetForegroundWindow() -eq $script:window }
    Command 1002
    $script:editor=[HostNative]::FindWindowEx($script:window,[IntPtr]::Zero,'RICHEDIT50W',$null)
    $oldLayout=[DesktopInput]::Layout($script:window)
    [void][HostNative]::PostMessage($script:window,0x0050,[IntPtr]::Zero,[IntPtr]0x08040804)
    Start-Sleep -Milliseconds 500
    $ime=[DesktopInput]::ImmGetDefaultIMEWnd($script:editor)
    if ($ime -eq [IntPtr]::Zero) { throw 'A real Chinese IME is not available; no pass claimed.' }
    [void](Send $ime 0x0283 6 1)
    $length=(Text-Of $script:editor).Length
    [void](Send $script:editor 0x00B1 $length $length)
    Begin-Chinese 'nihao'
    Assert-Host 'real native IME start is observed' (Composing)
    Command 1002
    Assert-Host 'preview does not interrupt an unfinished composition' ([HostNative]::IsWindowVisible($script:editor) -and (Composing))
    Command 1001
    Assert-Host 'save cannot persist unfinished composition' ([IO.File]::ReadAllText($file) -eq $original -and (Composing))
    Command 1009
    Assert-Host 'tab switch preserves the composing document' ((Text-Of $script:window).Contains('ime-alpha.md') -and (Composing))
    [void][HostNative]::PostMessage($script:window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    [void](Send $script:window 0)
    Assert-Host 'close cannot discard an unfinished composition' (-not $app.HasExited -and [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero)
    $third=Join-Path $fixture 'ime-deferred.md'
    [IO.File]::WriteAllText($third,'deferred')
    $child=Start-Process -FilePath $ExePath -ArgumentList ('"'+$third+'"') -PassThru -WindowStyle Hidden
    if(-not $child.WaitForExit(8000)){throw 'Deferred handoff did not acknowledge receipt.'}
    $child.Dispose();$child=$null
    Assert-Host 'acknowledged handoff waits for composition completion' ((Text-Of $script:window).Contains('ime-alpha.md') -and (Composing))
    Finish-Chinese
    Wait-Until {(Text-Of $script:window).Contains('ime-deferred.md')}
    Assert-Host 'deferred handoff opens after the candidate commits' ([IO.File]::ReadAllText($file)-ceq $original)
    Command 1009
    Command 1002
    $committed=Text-Of $script:editor
    Assert-Host 'real candidate commits Chinese characters' ([regex]::IsMatch($committed,'[\u4e00-\u9fff]') -and -not $committed.Contains('nihao'))
    Command 1002;Command 1002
    Assert-Host 'Chinese commit survives preview and return exactly once' ((Text-Of $script:editor) -ceq $committed)
    $length=$committed.Length
    [void](Send $script:editor 0x00B1 $length $length)
    Begin-Chinese 'ceshi'
    Press-Owned @(27)
    Wait-Until { -not (Composing) }
    Assert-Host 'real Escape cancels only the candidate' ((Text-Of $script:editor) -ceq $committed)
    Command 1001
    Assert-Host 'confirmed Chinese text saves with original LF format' ([IO.File]::ReadAllText($file) -ceq $committed.Replace("`r`n","`n"))

    Command 1005
    $query=[HostNative]::GetDlgItem($script:window,3011)
    [void](Send $query 0x00B1 0 -1)
    Begin-Chinese 'nihao'
    [void](Send $script:window 0x0111 3005)
    Assert-Host 'search close is protected while its IME is composing' ([HostNative]::IsWindowVisible($query) -and (Composing))
    Press-Owned @(13)
    Wait-Until { -not (Composing) }
    $q=Text-Of $query
    Write-Output ('Native Enter query codes: '+(($q.ToCharArray() | ForEach-Object {([int]$_).ToString('X4')}) -join ' '))
    Assert-Host 'Enter finishes native IME input without closing the strip' (($q -ceq 'nihao' -or [regex]::IsMatch($q,'[\u4e00-\u9fff]')) -and [HostNative]::IsWindowVisible($query))
    [void](Send $query 0x00B1 0 -1)
    Begin-Chinese 'nihao'
    Finish-Chinese
    $q=Text-Of $query
    Assert-Host 'Space explicitly selects the Chinese search candidate' ([regex]::IsMatch($q,'[\u4e00-\u9fff]') -and $committed.Contains($q))
    Command 1013
    Press-Owned @(9)
    $replacement=[HostNative]::GetDlgItem($script:window,3014)
    Wait-Until { [HostNative]::FocusedControl($script:window) -eq $replacement }
    Assert-Host 'replacement input is keyboard reachable' ([HostNative]::FocusedControl($script:window) -eq $replacement)
    Begin-Chinese 'zhongwen'
    [void](Send $script:window 0x0111 3002)
    Assert-Host 'replace-all waits for replacement composition to finish' ((Text-Of $script:editor) -ceq $committed -and (Composing))
    Finish-Chinese
    Wait-Until { [regex]::IsMatch((Text-Of $replacement),'[\u4e00-\u9fff]') }
    $replacementText=Text-Of $replacement
    Assert-Host 'replacement candidate commits Chinese text' ([regex]::IsMatch($replacementText,'[\u4e00-\u9fff]'))
    [void](Send $script:window 0x0111 3002)
    Assert-Host 'confirmed Chinese replacement uses the shared literal path' ((Text-Of $script:editor) -ceq $committed.Replace($q,$replacementText))
    Assert-Host 'replacement does not automatically save the source' ([IO.File]::ReadAllText($file) -ceq $committed.Replace("`r`n","`n"))
    [void](Send $script:window 0x0111 3005)
    Write-Output ('IME profile HKL: '+[DesktopInput]::Layout($script:window).ToInt64().ToString('X'))
    Write-Output "HOST IME SMOKE: $script:passed PASS (real composition/candidate input; no global settings change)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) {Write-Output ([HostNative]::Snapshot([uint32]$app.Id))}
    throw
} finally {
    if ($app -and -not $app.HasExited) {
        if ([DesktopInput]::GetForegroundWindow() -eq $script:window) {try {Press-Owned @(27)} catch {}}
        if ($oldLayout -ne [IntPtr]::Zero) {[void][HostNative]::PostMessage($script:window,0x0050,[IntPtr]::Zero,$oldLayout);Start-Sleep -Milliseconds 100}
        $app.Kill();[void]$app.WaitForExit(4000)
    }
    foreach($p in @($child,$app)){if($p){if(-not $p.HasExited){$p.Kill();[void]$p.WaitForExit(4000)};$p.Dispose()}}
    if ([DesktopInput]::IsWindow($previousWindow)) {[void][HostNative]::SetForegroundWindow($previousWindow)}
    $env:MDV_CONFIG_DIR=$oldConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
