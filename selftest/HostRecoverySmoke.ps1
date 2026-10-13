param([string]$ExePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) { $ExePath = Join-Path $projectRoot 'engine\bin\Release\net10.0-windows\MDViewerNative.exe' }
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'Close MDViewerNative before running this isolated host test.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-recovery-host-' + [guid]::NewGuid().ToString('N'))
$configDirectory = Join-Path $fixture 'config'
$draftDirectory = Join-Path $configDirectory 'drafts'
[void][IO.Directory]::CreateDirectory($configDirectory)
$previousConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = $configDirectory
$script:app = $null
$script:passed = 0

function Draft-Count { return @(Get-ChildItem -LiteralPath $draftDirectory -Filter '*.json' -ErrorAction SilentlyContinue).Count }
function Start-Host([int]$RecoveryChoice = 0) {
    $script:app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $source + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Host exited during startup.' }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    if ($RecoveryChoice -ne 0) {
        Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -ne [IntPtr]::Zero }
        Choose-Dialog ([HostNative]::Dialog([uint32]$app.Id)) $RecoveryChoice
        Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero }
    }
    Wait-Until { (Text-Of $script:window).Contains('source.md') -or (Text-Of $script:window).Contains('(未命名)') }
    [void](Send $script:window 0)
    [void][HostNative]::ShowWindow($script:window, 5)
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
}
function Crash-Host {
    # Only terminate the exact process created by this test, to exercise crash recovery.
    Stop-Process -Id $app.Id -Force
    if (-not $app.WaitForExit(8000)) { throw 'Test process did not terminate.' }
    $app.Dispose()
    $script:app = $null
}
function Close-Host {
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(8000) -or $app.ExitCode -ne 0) { throw 'Normal shutdown failed.' }
    $app.Dispose()
    $script:app = $null
}
function Wait-Draft([string]$Text) {
    Wait-Until {
        foreach ($file in @(Get-ChildItem -LiteralPath $draftDirectory -Filter '*.json' -ErrorAction SilentlyContinue)) {
            try { if (([IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json).Text -eq $Text) { return $true } } catch { }
        }
        return $false
    }
}

try {
    [IO.File]::WriteAllText((Join-Path $configDirectory 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,766,900,0")
    $source = Join-Path $fixture 'source.md'
    [IO.File]::WriteAllText($source, "# Original`n")
    Start-Host
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    $draft = "# Recovered 草稿`r`n"
    Edit-Text $draft
    Wait-Draft ($draft.Replace("`r`n", "`n"))
    Assert-Host 'live editor is checkpointed without saving source' ((Draft-Count) -eq 1 -and [IO.File]::ReadAllText($source) -eq "# Original`n")
    Crash-Host
    [IO.File]::WriteAllText($source, "# External edit`n")
    Start-Host 6
    Wait-Until { $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null); $script:editor -ne [IntPtr]::Zero -and [HostNative]::IsWindowVisible($script:editor) }
    Assert-Host 'crashed editor draft restores dirty and editable' ((Text-Of $script:editor) -eq $draft -and (Text-Of $script:window).StartsWith('*source.md'))
    Assert-Host 'recovery leaves external version untouched' ([IO.File]::ReadAllText($source) -eq "# External edit`n")
    Guarded-Command 1001 2
    Assert-Host 'recovered save detects stale disk version and supports cancel' ((Draft-Count) -eq 1 -and (Text-Of $script:editor) -eq $draft -and [IO.File]::ReadAllText($source) -eq "# External edit`n")
    Guarded-Command 1001 1001
    Assert-Host 'confirmed save removes recovery and preserves external backup' ((Draft-Count) -eq 0 -and [IO.File]::ReadAllText($source) -eq $draft.Replace("`r`n", "`n") -and [IO.File]::ReadAllText($source + '.bak') -eq "# External edit`n")
    Edit-Text "Discard checkpoint`r`n"
    Wait-Draft "Discard checkpoint`n"
    Discard-ToPreview
    Assert-Host 'explicit discard clears checkpoint' ((Draft-Count) -eq 0)
    Command 1008
    Command 1002
    $unnamed = "Unnamed 草稿`r`n"
    Edit-Text $unnamed
    Wait-Draft ($unnamed.Replace("`r`n", "`n"))
    Crash-Host

    Start-Host 7
    Assert-Host 'declining restoration retains draft for later' ((Draft-Count) -eq 1 -and -not (Text-Of $script:window).StartsWith('*'))
    Close-Host
    Assert-Host 'normal exit preserves deferred recovery records' ((Draft-Count) -eq 1)
    Start-Host 6
    Wait-Until { $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null); $script:editor -ne [IntPtr]::Zero -and [HostNative]::IsWindowVisible($script:editor) }
    Assert-Host 'unnamed draft restores alongside opened file' ((Text-Of $script:editor) -eq $unnamed -and (Text-Of $script:window).Contains('(未命名)'))
    Guarded-Command 1008 1001
    Assert-Host 'discarding recovered unnamed tab leaves original tab' ((Draft-Count) -eq 0 -and (Text-Of $script:window).Contains('source.md'))
    Close-Host
    Assert-Host 'recovery workflow shuts down normally' ($null -eq $app)

    $corrupt = Join-Path $draftDirectory ([guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($corrupt, '{truncated')
    Start-Host
    Assert-Host 'corrupt recovery is quarantined without blocking startup' ((Draft-Count) -eq 0 -and @(Get-ChildItem -LiteralPath $draftDirectory -Filter '*.corrupt-*').Count -eq 1)
    Assert-Host 'recovery errors use isolated persistent log' ([IO.File]::ReadAllText((Join-Path $configDirectory 'app.log')).Contains('read corrupt draft'))
    Close-Host
    Write-Output "HOST RECOVERY SMOKE: $script:passed PASS"
}
catch { Write-Output $_.ScriptStackTrace; if ($app) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)) }; throw }
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; [void]$app.WaitForExit(8000) }
    $env:MDV_CONFIG_DIR = $previousConfig
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the test temporary directory.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
