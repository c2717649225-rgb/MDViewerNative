param([string]$ExePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) { $ExePath = Join-Path $projectRoot 'engine\bin\Release\net10.0-windows\MDViewerNative.exe' }
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'Close MDViewerNative before running this isolated UI test.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'DesktopInputSupport.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'work\ui-smoke' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-ui-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$previousConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$app = $null
$script:passed = 0
function Key([IntPtr]$Target, [int]$Code) {
    [void][HostNative]::PostMessage($Target, 0x0100, [IntPtr]$Code, [IntPtr]1)
    Start-Sleep -Milliseconds 80
    [void][HostNative]::PostMessage($Target, 0x0101, [IntPtr]$Code, [IntPtr]1)
}
function Bounds([IntPtr]$Control) {
    $rect = New-Object HostNative+RECT
    [void][HostNative]::GetWindowRect($Control, [ref]$rect)
    # Capture may move the host. Compare physical host-client coordinates, not
    # screen positions recorded before an intervening capture or DPI change.
    $origin = New-Object HostNative+POINT
    [void][HostNative]::ClientToScreen($script:window, [ref]$origin)
    $rect.L -= $origin.X; $rect.R -= $origin.X
    $rect.T -= $origin.Y; $rect.B -= $origin.Y
    return $rect
}
function Set-Input([IntPtr]$Control, [string]$Text) {
    $result = [IntPtr]::Zero
    [void][HostNative]::SendText($Control, 0x000C, [IntPtr]::Zero, $Text, 2, 4000, [ref]$result)
}
try {
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`ntheme=light`ntoc=1`nfont=16`nhints=0`nwin=140,90,960,800,0")
    $source = @'
# 把注意力留给文字

一个好的阅读工具，应该让文档成为画面的中心。打开一份笔记，找到正在阅读的章节，然后继续自己的工作。

## 阅读与记录

正文需要舒适的行宽、清晰的标题，以及安静的界面。工具栏只在需要操作时吸引注意，目录则帮助我们保持方向。

- 阅读项目说明和技术文档
- 整理会议记录与待办事项
- 在预览和源码之间快速切换

> 留白并不是浪费空间，它让内容之间有了呼吸。

## 一段代码

```csharp
var document = DocumentFile.Read(path);
Console.WriteLine(document.Text);
```

## 下一步

先解决窗口缩窄后的可用性，再调整字体、间距和控件状态。所有改变都应服务于阅读。
'@
    $path = Join-Path $fixture '阅读与记录.md'
    [IO.File]::WriteAllText($path, $source)
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $path + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'UI host exited during startup.' }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    # A test process started hidden has its first ShowWindow replaced by SW_HIDE (STARTUPINFO); retry until visible.
    Wait-Until { [void][HostNative]::ShowWindow($script:window, 5); [HostNative]::IsWindowVisible($script:window) }
    $scale = [HostNative]::GetDpiForWindow($script:window) / 96.0
    $environment = [ordered]@{ windows = [Environment]::OSVersion.Version.ToString(); processArchitecture = $env:PROCESSOR_ARCHITECTURE; dpi = [int]($scale * 96); scale = $scale; executableSha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash.ToLowerInvariant() }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'compatibility-environment.json'), (($environment | ConvertTo-Json) + "`n"), (New-Object Text.UTF8Encoding($false)))
    Capture '6-reading'
    Command 1004
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    $beforeFind = Bounds $script:editor
    Command 1013
    $find = [HostNative]::GetDlgItem($script:window, 3011)
    $replacement = [HostNative]::GetDlgItem($script:window, 3014)
    $previous = [HostNative]::GetDlgItem($script:window, 3003)
    $next = [HostNative]::GetDlgItem($script:window, 3004)
    $close = [HostNative]::GetDlgItem($script:window, 3005)
    $all = [HostNative]::GetDlgItem($script:window, 3002)
    $afterFind = Bounds $script:editor
    Assert-Host 'search strip reserves space above the editor' (($afterFind.T - $beforeFind.T) -eq [int](80 * $scale) -and (Bounds $all).B -le $afterFind.T)
    Assert-Host 'editing exposes native previous next and close buttons' ([HostNative]::IsWindowVisible($previous) -and [HostNative]::IsWindowVisible($next) -and [HostNative]::IsWindowVisible($close))
    Capture '7-dark-replace'
    Command 1004
    Capture '8-light-replace'
    Command 1004
    Edit-Text 'Alpha alpha'
    Set-Input $find 'alpha'
    [void](Send $next 0x00F5)
    Assert-Host 'native next button selects the second match' ((Send $script:editor 0x00B0) -eq ((11 -shl 16) -bor 6))
    [void](Send $previous 0x00F5)
    Assert-Host 'native previous button returns to the first match' ((Send $script:editor 0x00B0) -eq (5 -shl 16))
    Command 1005
    Key $find 9
    Assert-Host 'Tab moves from query to replacement' ([HostNative]::FocusedControl($script:window) -eq $replacement)
    Key $replacement 9
    Assert-Host 'Tab moves to the native replace button' ([HostNative]::FocusedControl($script:window) -eq [HostNative]::GetDlgItem($script:window, 3001))
    Set-Input $replacement 'Beta'
    Key $replacement 13
    Assert-Host 'Enter in replacement applies one replacement' ((Text-Of $script:editor) -eq 'Beta alpha')
    [void](Send $script:editor 0x00B1 0 4)
    $selection = Send $script:editor 0x00B0
    $font = Send $script:editor 0x0031
    Command 1014
    Assert-Host 'editor zoom changes font and preserves selection' ((Send $script:editor 0x0031) -ne $font -and (Send $script:editor 0x00B0) -eq $selection)
    [void](Send $script:editor 0x00C7)
    Assert-Host 'editor zoom preserves native undo history' ((Text-Of $script:editor) -eq 'Alpha alpha')
    Key $all 27
    Wait-Until { -not [HostNative]::IsWindowVisible($find) }
    Assert-Host 'Escape from a search button restores editor space' (-not [HostNative]::IsWindowVisible($find) -and (Bounds $script:editor).T -eq $beforeFind.T)
    Assert-Host 'Escape from a search button restores editor focus' ([HostNative]::FocusedControl($script:window) -eq $script:editor)
    Command 1016
    Discard-ToPreview
    1..5 | ForEach-Object {
        $tabPath = Join-Path $fixture ('项目设计与开发记录-' + $_ + '.md')
        [IO.File]::WriteAllText($tabPath, $source)
        $handoff = Start-Process -FilePath $ExePath -ArgumentList ('"' + $tabPath + '"') -PassThru -WindowStyle Hidden
        if (-not $handoff.WaitForExit(8000)) { throw 'UI handoff timed out.' }
        Wait-Until { (Text-Of $script:window).Contains([IO.Path]::GetFileName($tabPath)) }
    }
    Capture '9-tabs'
    [void](Send $script:window 0x0231)
    [void][HostNative]::SetWindowPos($script:window, [IntPtr]::Zero, 120, 120, [int](540 * $scale), [int](750 * $scale), 4)
    [void](Send $script:window 0x0232)
    Capture '10-narrow-tabs'
    [void][DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)
    Wait-Until { [HostNative]::GetForegroundWindow() -eq $script:window }
    Key $script:window 0x75
    # A2 focus includes visible tabs/close slots. Find the semantic document-list
    # target instead of assuming it is always the sixth Right key.
    $focusSteps=0
    while ((Text-Of $script:window) -notmatch '\[\u5DE5\u5177\u680F\uFF1A\u6587\u6863\u5217\u8868\]' -and $focusSteps -lt 64) {
        Key $script:window 0x27
        Start-Sleep -Milliseconds 40
        $focusSteps++
    }
    Assert-Host 'document list is a reachable semantic toolbar target' ((Text-Of $script:window) -match '\[\u5DE5\u5177\u680F\uFF1A\u6587\u6863\u5217\u8868\]')
    Key $script:window 13
    Wait-Until { [HostNative]::Window([uint32]$app.Id, '#32768') -ne [IntPtr]::Zero }
    Assert-Host 'F6 and arrow keys open the overflow menu' $true
    Key $script:window 0x28
    Key $script:window 13
    Wait-Until { (Text-Of $script:window).Contains('阅读与记录.md') }
    Assert-Host 'overflow menu switches to a hidden document' ((Text-Of $script:window).Contains('阅读与记录.md'))
    Key $script:window 27
    Command 1002
    Command 1013
    $client = New-Object HostNative+RECT
    [void][HostNative]::GetClientRect($script:window, [ref]$client)
    Assert-Host 'narrow search controls fit and do not cover editor' ((Bounds $find).R -le (Bounds $previous).L -and (Bounds $replacement).R -le (Bounds $all).L -and (Bounds $close).R -le $client.R -and (Bounds $all).B -le (Bounds $script:editor).T)
    Capture '11-narrow-replace'
    [void](Send $close 0x00F5)
    Assert-Host 'mouse close button dismisses the entire search strip' (-not [HostNative]::IsWindowVisible($replacement) -and -not [HostNative]::IsWindowVisible($close))
    Command 1002
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(5000)) { throw 'UI host did not exit.' }
    Assert-Host 'UI exercise exits without crashing' ($app.ExitCode -eq 0)
    Write-Output "HOST UI SMOKE: $script:passed PASS"
}
catch {
    Write-Output $_.ScriptStackTrace
    Write-Output ("Foreground HWND: " + [HostNative]::GetForegroundWindow())
    if ($app) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)) }
    throw
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; [void]$app.WaitForExit(4000) }
    $env:MDV_CONFIG_DIR = $previousConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
