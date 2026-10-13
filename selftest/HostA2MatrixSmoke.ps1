param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory, [ValidateSet('light','dark')][string]$Theme='light')
$ErrorActionPreference='Stop'
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'An existing reader must be left untouched.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
# A2-B5 integration matrix in the real host: one sample set, 1/2/20 tabs x
# minimum/640/960/1280 window widths, per-state screenshots and focus-sequence
# assertions, then save. Owned window messages only; isolated config and docs.
function U([string]$s) { return [regex]::Unescape($s) }
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-a2-matrix-'+[guid]::NewGuid().ToString('N'))
foreach ($d in 'one','two','config') { [void][IO.Directory]::CreateDirectory((Join-Path $fixture $d)) }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$oldConfig=$env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
$script:passed=0
$app=$null
$toolbarPrefix='['+(U '\u5DE5\u5177\u680F\uFF1A')
$descPattern=[regex]::Escape($toolbarPrefix)+'(?<d>[^\]]+)\]'
$tabLabel=U '\u6807\u7B7E\uFF1A'
$unsaved=U '\u672A\u4FDD\u5B58'
$docsLabel=U '\u6587\u6863\u5217\u8868'
$docsHidden=$docsLabel+(U '\uFF08\u542B\u672A\u4FDD\u5B58\u7684\u9690\u85CF\u6587\u6863\uFF09')
$summary=New-Object System.Collections.Generic.List[string]
function Handoff([string]$Path) {
    $p=Start-Process -FilePath $ExePath -ArgumentList ('"'+$Path+'"') -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(8000)) { throw 'Fixture handoff did not exit.' }
    $p.Dispose()
    $leaf=[IO.Path]::GetFileName($Path)
    Wait-Until { (Text-Of $script:window).TrimStart('*').StartsWith($leaf) } { 'title=' + (Text-Of $script:window) }
}
function Editor-Visible {
    $e=[HostNative]::FindWindowEx($script:window,[IntPtr]::Zero,'RICHEDIT50W',$null)
    return ($e -ne [IntPtr]::Zero -and [HostNative]::IsWindowVisible($e))
}
function Focus-Descriptions {
    [void][HostNative]::PostMessage($script:window,0x0100,[IntPtr]0x75,[IntPtr]::Zero)
    Wait-Until { (Text-Of $script:window).Contains($toolbarPrefix) } { 'title=' + (Text-Of $script:window) }
    $found=New-Object System.Collections.Generic.List[string]
    for ($i=0; $i -lt 60; $i++) {
        if ((Text-Of $script:window) -match $descPattern -and -not $found.Contains($Matches['d'])) { $found.Add($Matches['d']) }
        [void](Send $script:window 0x0100 0x27)
    }
    [void](Send $script:window 0x0100 0x1B)
    Wait-Until { -not (Text-Of $script:window).Contains($toolbarPrefix) }
    return ,@($found.ToArray())
}
function Check-State([int]$Count, [string]$Active, [bool]$HasDirty) {
    foreach ($w in 400,640,960,1280) {
        [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int]($w*$script:scale),[int](760*$script:scale),4)
        [void](Send $script:window 0x0200 0 -1)
        $cr=New-Object HostNative+RECT
        [void][HostNative]::GetClientRect($script:window,[ref]$cr)
        $client=[Math]::Round(($cr.R-$cr.L)/$script:scale)
        $label="$Theme-n$Count-w$w"
        Capture $label
        $d=Focus-Descriptions
        $tabs=@($d | Where-Object { $_.StartsWith($tabLabel) })
        $hasDocs=@($d | Where-Object { $_.StartsWith($docsLabel) }).Count -gt 0
        $dirtyTab=@($tabs | Where-Object { $_.Contains($unsaved) }).Count -gt 0
        $at=" [$label client=$client]"
        if ($Count -eq 1) {
            Assert-Host ('single document needs neither tabs nor list'+$at) ($tabs.Count -eq 0 -and -not $hasDocs)
        } else {
            Assert-Host ('active tab is reachable'+$at) (@($tabs | Where-Object { $_.StartsWith($tabLabel+$Active) }).Count -eq 1)
            Assert-Host ('list entry appears exactly when tabs are hidden'+$at) ($hasDocs -eq ($tabs.Count -lt $Count))
            if ($HasDirty) { Assert-Host ('unsaved state is visible on its tab or the list entry'+$at) ($dirtyTab -xor ($d -contains $docsHidden)) }
        }
        $summary.Add("$label client=$client visibleTabs=$($tabs.Count)/$Count list=$hasDocs dirtyOnTab=$dirtyTab dirtyOnList=$($d -contains $docsHidden)")
    }
}
try {
    $long=(U '\u9879\u76EE\u8BBE\u8BA1\u4E0E\u5F00\u53D1\u8BB0\u5F55\uFF08\u957F\u6807\u9898\u7528\u4E8E\u68C0\u9A8C\u7701\u7565\uFF09')+'.md'
    $emoji='emoji '+[char]::ConvertFromUtf32(0x1F4D8)+' notes.md'
    $names=@('alpha.md','bravo.md','one\README.md','two\README.md',$long,$emoji,'A&B.md','notes.MD')
    for ($i=9; $i -le 20; $i++) { $names+=('d{0:D2}.md' -f $i) }
    $files=@($names | ForEach-Object { Join-Path $fixture $_ })
    $sources=@{}
    foreach ($f in $files) { $s="# "+[IO.Path]::GetFileName($f)+"`n`nMatrix fixture body.`n"; [IO.File]::WriteAllText($f,$s); $sources[$f]=$s }
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`ntheme=$Theme`ntoc=0`nfont=16`nhints=0`nwin=120,120,1100,760,0`n")
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$files[0]+'"') -PassThru
    Wait-Until { if ($app.HasExited) {throw 'Matrix host exited.'}; [HostNative]::Window([uint32]$app.Id,'MorningDesk') -ne [IntPtr]::Zero }
    $script:window=[HostNative]::Window([uint32]$app.Id,'MorningDesk')
    Wait-Until { (Text-Of $script:window).StartsWith('alpha.md') }
    $script:scale=[HostNative]::GetDpiForWindow($script:window)/96.0
    Write-Output ("MATRIX_THEME=$Theme DPI="+[HostNative]::GetDpiForWindow($script:window))
    Check-State 1 'alpha' $false

    Handoff $files[1]
    Command 1010
    Wait-Until { (Text-Of $script:window).StartsWith('alpha.md') }
    Command 1002
    Wait-Until { Editor-Visible }
    $script:editor=[HostNative]::FindWindowEx($script:window,[IntPtr]::Zero,'RICHEDIT50W',$null)
    $edited="# alpha edited by the A2 matrix`r`n"
    Edit-Text $edited
    Command 1002
    Wait-Until { -not (Editor-Visible) }
    Command 1009
    Wait-Until { (Text-Of $script:window).StartsWith('bravo.md') }
    Check-State 2 'bravo' $true

    for ($i=2; $i -lt $files.Count; $i++) { Handoff $files[$i] }
    Check-State 20 'd20' $true

    Command 1009
    Wait-Until { (Text-Of $script:window).StartsWith('*alpha.md') } { 'title=' + (Text-Of $script:window) }
    Command 1001
    Wait-Until { (Text-Of $script:window).StartsWith('alpha.md') } { 'title=' + (Text-Of $script:window) }
    $saved=[IO.File]::ReadAllText($files[0])
    Assert-Host 'save writes the edited source and clears the unsaved title marker' ($saved.Replace("`r`n","`n").TrimEnd() -eq $edited.Replace("`r`n","`n").TrimEnd())
    $after=Focus-Descriptions
    Assert-Host 'after save no toolbar target reports unsaved' (@($after | Where-Object { $_.Contains($unsaved) }).Count -eq 0)
    Assert-Host 'other fixtures were not written' (@($files | Select-Object -Skip 1 | Where-Object { [IO.File]::ReadAllText($_) -ne $sources[$_] }).Count -eq 0)
    [void][HostNative]::PostMessage($script:window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $app.WaitForExit(8000)) {throw 'Matrix host did not exit.'}
    Assert-Host 'matrix host exits cleanly' ($app.ExitCode -eq 0)
    $summary | ForEach-Object { Write-Output ('STATE '+$_) }
    Write-Output "HOST A2 MATRIX SMOKE ($Theme): $script:passed PASS (owned messages; isolated documents; no real keyboard, mouse or settings changes)"
} catch {
    $summary | ForEach-Object { Write-Output ('STATE '+$_) }
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ('title=' + (Text-Of $script:window)); Capture "$Theme-failure" }
    throw
} finally {
    if ($app) {if (-not $app.HasExited) {$app.Kill();[void]$app.WaitForExit(4000)};$app.Dispose()}
    $env:MDV_CONFIG_DIR=$oldConfig
    if (Test-Path -LiteralPath $fixture) {Remove-Item -LiteralPath $fixture -Recurse -Force}
}
