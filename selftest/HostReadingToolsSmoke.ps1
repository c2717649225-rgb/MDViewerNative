param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference='Stop'
if(Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue){throw 'A viewer is already running; test will not touch it.'}
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'DesktopInputSupport.ps1')
. (Join-Path $PSScriptRoot 'ClipboardTestSupport.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ReaderMenuProbe {
 [DllImport("user32.dll")] public static extern uint GetMenuState(IntPtr menu,uint id,uint flags);
 [DllImport("user32.dll")] public static extern int GetMenuStringW(IntPtr menu,uint id,System.Text.StringBuilder text,int count,uint flags);
}
'@
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-tools-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$oldConfig=$env:MDV_CONFIG_DIR;$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
[IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`nhints=0`ntoc=0`nfont=16`nwin=100,100,800,650,0")
$previousWindow=[DesktopInput]::GetForegroundWindow()
$app=$null;$snapshot=$null;$touched=$false;$lastSequence=[uint32]0;$script:passed=0
function UI-Command([int]$Id){[void](Send $script:window 0x0111 $Id)}
function Control-Text([IntPtr]$Control,[string]$Text){$r=[IntPtr]::Zero;if([HostNative]::SendText($Control,0x000C,[IntPtr]::Zero,$Text,2,4000,[ref]$r)-eq [IntPtr]::Zero){throw 'Text setter timed out.'}}
function Scroll-Position { Scroll-Query 0 }
function Click-Document([int]$X,[int]$Y){$xy=($Y -shl 16)-bor($X-band 0xffff);[void](Send-Physical $script:window 0x0201 1 $xy);[void](Send-Physical $script:window 0x0202 0 $xy)}
function Select-NativeMenu([IntPtr]$Menu,[int]$Id) {
    # Native popup menus do not consistently interpret Home/End on this host.
    # Use their actual highlighted state and bounded arrow navigation instead.
    for($attempt=0;$attempt -lt 4;$attempt++) {
        if(([ReaderMenuProbe]::GetMenuState($Menu,$Id,0)-band 0x80)-ne 0){return}
        Press-Owned @(40)
        Start-Sleep -Milliseconds 100
    }
    throw ('Native menu did not highlight requested id '+$Id+'; caseFlags='+[ReaderMenuProbe]::GetMenuState($Menu,3007,0)+' wordFlags='+[ReaderMenuProbe]::GetMenuState($Menu,3008,0))
}
function Code-Copy([int]$X,[int]$Y,[string]$Expected){
    $pt=New-Object HostNative+POINT;$pt.X=$X;$pt.Y=$Y;[void][HostNative]::ClientToScreen($script:window,[ref]$pt)
    [void][HostNative]::PostMessage($script:window,0x007B,$script:window,[IntPtr](($pt.Y-shl 16)-bor($pt.X-band 0xffff)))
    Wait-Until {[HostNative]::Window([uint32]$app.Id,'#32768') -ne [IntPtr]::Zero}
    $menuWindow=[HostNative]::Window([uint32]$app.Id,'#32768')
    $menu=[IntPtr](Send $menuWindow 0x01E1)
    Assert-Host 'context menu exposes an enabled whole-code action' (([ReaderMenuProbe]::GetMenuState($menu,2080,0)-band 3)-eq 0)
    $script:touched=$true
    try {UI-Command 2080} finally {$script:lastSequence=[ClipboardSnapshot]::GetClipboardSequenceNumber()}
    $actual=[ClipboardSnapshot]::Text($script:window)
    if($actual -cne $Expected){Write-Output ('Code-copy diagnostic lengths expected='+$Expected.Length+' actual='+$actual.Length+' expandedTabs='+($actual -ceq $Expected.Replace([string][char]9,'    '))+' CRLF='+($actual -ceq $Expected.Replace("`n","`r`n"))+' trimLF='+($actual -ceq $Expected.TrimEnd([char]10)))}
    Assert-Host 'code action copies literal tabs quotes and terminal newlines' ($actual -ceq $Expected)
    [void][HostNative]::PostMessage($script:window,0x001F,[IntPtr]::Zero,[IntPtr]::Zero)
    Wait-Until {[HostNative]::Window([uint32]$app.Id,'#32768') -eq [IntPtr]::Zero}
}
try {
    $a=Join-Path $fixture 'tools-alpha.md';$b=Join-Path $fixture 'tools-bravo.md'
    $code=[char]9+'"literal" <x>&'+"`n"+'second '+[char]::ConvertFromUtf32(0x1F600)+"`n`n"
    $nested=[char]9+'nested-marker'+"`n"
    $source='```text'+"`n"+$code+'```'+"`n`n"+'- parent'+"`n`n"+'  ```text'+"`n  "+$nested+'  ```'+"`n`n"+'[Jump](tools-bravo.md#destination)'+"`n`n"
    [IO.File]::WriteAllText($a,$source)
    [IO.File]::WriteAllText($b,"# Begin`n`n"+("paragraph`n`n"*100)+"# Destination`n`nTarget`n")
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$a+'"') -PassThru
    Wait-Until {[HostNative]::Window([uint32]$app.Id,'MDViewerNative') -ne [IntPtr]::Zero}
    $script:window=[HostNative]::Window([uint32]$app.Id,'MDViewerNative')
    [void][HostNative]::ShowWindow($script:window,5);Wait-Until {(Text-Of $script:window).Contains('tools-alpha.md')}
    [void][DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)
    [void][HostNative]::RedrawWindow($script:window,[IntPtr]::Zero,[IntPtr]::Zero,0x0185)
    $snapshot=[ClipboardSnapshot]::Capture($script:window)
    $rect=New-Object HostNative+RECT;[void][HostNative]::GetClientRect($script:window,[ref]$rect)
    Code-Copy ([int]($rect.R/2)) 94 $code
    Assert-Host 'copy leaves the file and dirty state untouched' ([IO.File]::ReadAllText($a)-ceq $source -and -not (Text-Of $script:window).StartsWith('*'))
    Command 1005
    $findNested=[HostNative]::GetDlgItem($script:window,3011)
    Control-Text $findNested 'nested-marker'
    UI-Command 3005
    $script:touched=$true
    try {UI-Command 2080} finally {$script:lastSequence=[ClipboardSnapshot]::GetClipboardSequenceNumber()}
    Assert-Host 'selection-based action copies nested code with its literal tab' ([ClipboardSnapshot]::Text($script:window)-ceq $nested)


    Command 1002;$script:editor=[HostNative]::Control($script:window,0,'RICHEDIT50W')
    Edit-Text 'Cat cat catalog _cat cat2 CAT'
    Command 1013
    $query=[HostNative]::GetDlgItem($script:window,3011);$replacement=[HostNative]::GetDlgItem($script:window,3014)
    $options=[HostNative]::GetDlgItem($script:window,3006)
    Assert-Host 'search options have a native keyboard-accessible control' ($options -ne [IntPtr]::Zero -and [HostNative]::IsWindowVisible($options))
    Control-Text $query 'cat'
    [void][HostNative]::PostMessage($options,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
    Wait-Until {[HostNative]::Window([uint32]$app.Id,'#32768') -ne [IntPtr]::Zero}
    $firstMenu=[IntPtr](Send ([HostNative]::Window([uint32]$app.Id,'#32768')) 0x01E1)
    Select-NativeMenu $firstMenu 3007
    Press-Owned @(13)
    Wait-Until {[HostNative]::Window([uint32]$app.Id,'#32768') -eq [IntPtr]::Zero}
    Wait-Until {(Text-Of $options) -eq '选项*'}
    [void][HostNative]::PostMessage($options,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
    Wait-Until {[HostNative]::Window([uint32]$app.Id,'#32768') -ne [IntPtr]::Zero}
    $optionsMenu=[IntPtr](Send ([HostNative]::Window([uint32]$app.Id,'#32768')) 0x01E1)
    Assert-Host 'native options menu exposes its checked case state' (([ReaderMenuProbe]::GetMenuState($optionsMenu,3007,0)-band 8)-ne 0)
    Select-NativeMenu $optionsMenu 3008
    Press-Owned @(13)
    Wait-Until {[HostNative]::Window([uint32]$app.Id,'#32768') -eq [IntPtr]::Zero}
    [void](Send $script:editor 0x00B1 0 0);UI-Command 3004
    Assert-Host 'case and whole-word options select only the complete lowercase word' ((Send $script:editor 0x00B0)-eq ((7-shl 16)-bor 4))
    Control-Text $replacement '$1\x'
    UI-Command 3002
    Assert-Host 'replace-all honors both options and literal replacement' ((Text-Of $script:editor)-ceq 'Cat $1\x catalog _cat cat2 CAT')
    [void](Send $script:editor 0x00C7)
    Assert-Host 'filtered replace-all is natively undoable' ((Text-Of $script:editor)-ceq 'Cat cat catalog _cat cat2 CAT')
    [void](Send $script:editor 0x00B1 8 11);UI-Command 3001
    Assert-Host 'replace-one cannot replace an identifier substring' ((Text-Of $script:editor)-ceq 'Cat cat catalog _cat cat2 CAT')
    Command 1002
    Assert-Host 'search options persist into unsaved preview' ((Text-Of $options)-eq '选项*' -and -not [HostNative]::IsWindowVisible($script:editor))
    UI-Command 3004
    Assert-Host 'preview and editor report the same filtered hit count' ((Text-Of ([HostNative]::GetDlgItem($script:window,3012)))-eq '1/1')
    UI-Command 3005
    Command 1002
    Edit-Text ('[Jump](tools-bravo.md#destination)'+"`r`n")
    Command 1002
    $rect=New-Object HostNative+RECT;[void][HostNative]::GetClientRect($script:window,[ref]$rect)
    # One-line document: click just inside the column start (window size comes from config.txt).
    $dpiScale=[HostNative]::GetDpiForWindow($script:window)/96.0;$left=Column-Left ($rect.R/$dpiScale);Click-Document ([int][Math]::Round(($left+12)*$dpiScale)) ([int][Math]::Round(100*$dpiScale))
    Wait-Until {(Text-Of $script:window).Contains('tools-bravo.md')}
    Assert-Host 'cross-file link navigates to its heading' ((Scroll-Position)-gt 500)
    $destination=Scroll-Position
    Press-Owned @(18,37)
    Wait-Until {(Text-Of $script:window).Contains('tools-alpha.md')}
    Assert-Host 'real Alt Left returns without saving the prior draft' ((Text-Of $script:window).StartsWith('*tools-alpha.md') -and [IO.File]::ReadAllText($a)-ceq $source)
    Press-Owned @(18,39)
    Wait-Until {(Text-Of $script:window).Contains('tools-bravo.md')}
    Assert-Host 'real Alt Right restores the target reading location' ([Math]::Abs((Scroll-Position)-$destination)-le 2)
    Command 2098
    Command 1009
    Command 1008
    Assert-Host 'closing a visited tab keeps its history usable' ((Text-Of $script:window).Contains('tools-alpha.md'))
    Command 2099
    Wait-Until {(Text-Of $script:window).Contains('tools-bravo.md')}
    Assert-Host 'history can reopen a closed named document' ((Scroll-Position)-gt 500)
    Assert-Host 'history never rewrites Markdown files' ([IO.File]::ReadAllText($a)-ceq $source -and -not (Test-Path ($b+'.bak')))
    Write-Output "HOST READING TOOLS: $script:passed PASS"
} catch {
    Write-Output $_.ScriptStackTrace
    if($app -and -not $app.HasExited){Write-Output ([HostNative]::Snapshot([uint32]$app.Id))}
    throw
} finally {
    try {
        if($snapshot -and $touched -and $app -and -not $app.HasExited){
            $restored=$snapshot.Restore($script:window,$lastSequence)
            Write-Output ("Clipboard snapshot restored="+$restored+" (a newer external clipboard is never overwritten).")
        }
    } finally {
    if($app){if(-not $app.HasExited){$app.Kill();[void]$app.WaitForExit(4000)};$app.Dispose()}
    if([DesktopInput]::IsWindow($previousWindow)){[void][HostNative]::SetForegroundWindow($previousWindow)}
    $env:MDV_CONFIG_DIR=$oldConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
}
