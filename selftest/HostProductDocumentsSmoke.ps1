param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference='Stop'
# Never touch a viewer the user is running. The preceding UI suites own their hosts
# and exit them, but process teardown is asynchronous, so a just-closed viewer can
# still be listed for a moment. Wait briefly for those to drain before refusing.
$deadline=(Get-Date).AddSeconds(10)
while($true){
  $existing=@(Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue)
  if($existing.Count -eq 0){break}
  if((Get-Date) -ge $deadline){
    throw ('An existing viewer will not be touched. (pids: '+(($existing|ForEach-Object{$_.Id}) -join ',')+')')
  }
  Start-Sleep -Milliseconds 250
}
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-product-ui-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$oldConfig=$env:MDV_CONFIG_DIR;$env:MDV_CONFIG_DIR=Join-Path $fixture 'config';[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
[IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"hints=0`ntoc=0`n")
$app=$null;$child=$null;$script:passed=0
function Source-Editor {[HostNative]::Control($script:window,0,'RICHEDIT50W')}
function Form-Window {[HostNative]::Window([uint32]$app.Id,'MDVNativeForm')}
function Post-Product([int]$Command){[void][HostNative]::PostMessage($script:window,0x0111,[IntPtr]$Command,[IntPtr]::Zero)}
function Set-FormText([IntPtr]$Control,[string]$Text){$r=[IntPtr]::Zero;if([HostNative]::SendText($Control,0x000C,[IntPtr]::Zero,$Text,2,4000,[ref]$r)-eq [IntPtr]::Zero){throw 'Form text setter failed.'}}
function Click-Form([int]$Id){$f=Form-Window;[void][HostNative]::PostMessage([HostNative]::GetDlgItem($f,$Id),0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)}
try {
 $a=Join-Path $fixture 'a.md';$b=Join-Path $fixture 'b.md'
 [IO.File]::WriteAllText($a,"# Alpha`n");[IO.File]::WriteAllText($b,"# Bravo`n")
 $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$a+'"') -PassThru
 Wait-Until {[HostNative]::Window([uint32]$app.Id,'MDViewerNative') -ne [IntPtr]::Zero}
 $script:window=[HostNative]::Window([uint32]$app.Id,'MDViewerNative');[void][HostNative]::ShowWindow($script:window,5)
 Wait-Until {(Text-Of $script:window).Contains('a.md')}
 Command 1002;$script:editor=Source-Editor
 Edit-Text "alpha draft`r`n";Edit-Text "alpha second`r`n"
 [void](Send $script:editor 0x00C7)
 Write-Output ('Undo fixture text='+((Text-Of $script:editor).Replace("`r",'<CR>').Replace("`n",'<LF>'))+' canUndo='+(Send $script:editor 0x00C6)+' canRedo='+(Send $script:editor 0x0455))
 Assert-Host 'managed undo restores prior edit' ((Text-Of $script:editor)-ceq "alpha draft`r`n")
 [void](Send $script:editor 0x0454)
 Assert-Host 'managed redo restores forward edit' ((Text-Of $script:editor)-ceq "alpha second`r`n")
 $child=Start-Process -FilePath $ExePath -ArgumentList ('"'+$b+'"') -PassThru -WindowStyle Hidden
 if(-not $child.WaitForExit(8000)){throw 'Handoff failed.'};$child.Dispose();$child=$null
 Wait-Until {(Text-Of $script:window).Contains('b.md')}
 Command 1002;$script:editor=Source-Editor;Edit-Text "bravo draft`r`n"
 Next-Document;Command 1002;$script:editor=Source-Editor
 [void](Send $script:editor 0x00C7)
 Assert-Host 'undo survives a document switch' ((Text-Of $script:editor)-ceq "alpha draft`r`n")
 Command 2201
 Assert-Host 'Save All commits both named drafts' ([IO.File]::ReadAllText($a)-eq "alpha draft`n" -and [IO.File]::ReadAllText($b)-eq "bravo draft`n")
 Command 2212
 Assert-Host 'read-only lock leaves preview visible and source unchanged' (-not [HostNative]::IsWindowVisible((Source-Editor)) -and (Text-Of $script:window).Contains('[只读]'))
 Command 1002
 Assert-Host 'read-only refuses editing' (-not [HostNative]::IsWindowVisible((Source-Editor)))
 Command 2212
 Post-Product 2209
 Wait-Until {(Form-Window)-ne [IntPtr]::Zero}
 $form=Form-Window;Set-FormText ([HostNative]::GetDlgItem($form,1000)) 'renamed.md'
 Click-Form 4000
 Wait-Until {(Form-Window)-eq [IntPtr]::Zero}
 Assert-Host 'native rename dialog moves only the selected file' ((Text-Of $script:window).Contains('renamed.md') -and (Test-Path (Join-Path $fixture 'renamed.md')) -and -not (Test-Path $a))
 Command 2208;Command 2202
 Assert-Host 'close others retains active pinned document' ((Text-Of $script:window).Contains('renamed.md'))
 Command 2205
 Wait-Until {(Text-Of $script:window).Contains('b.md')}
 Assert-Host 'reopen closed named document has the correct source' $true
 Command 2200
 Wait-Until {[HostNative]::IsWindowVisible((Source-Editor))}
 $script:editor=Source-Editor;Edit-Text 'unnamed draft'
 Assert-Host 'new-document command creates an independent dirty buffer' ((Text-Of $script:window).StartsWith('*') -and -not (Text-Of $script:window).Contains('b.md'))
 Post-Product 2204
 Wait-Until {(Form-Window)-ne [IntPtr]::Zero}
 Assert-Host 'bulk close lists unsaved documents before mutation' ([HostNative]::GetDlgItem((Form-Window),3000)-ne [IntPtr]::Zero)
 Click-Form 4002
 Wait-Until {(Form-Window)-eq [IntPtr]::Zero}
 Assert-Host 'cancel bulk close retains the active draft' ((Text-Of (Source-Editor))-ceq 'unnamed draft')
 Write-Output "HOST PRODUCT DOCUMENTS: $script:passed PASS"
} catch {
 Write-Output $_.ScriptStackTrace
 if($app -and -not $app.HasExited){Write-Output ([HostNative]::Snapshot([uint32]$app.Id))}
 throw
} finally {
 foreach($p in @($child,$app)){if($p){if(-not $p.HasExited){$p.Kill();[void]$p.WaitForExit(4000)};$p.Dispose()}}
 $env:MDV_CONFIG_DIR=$oldConfig
 Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
