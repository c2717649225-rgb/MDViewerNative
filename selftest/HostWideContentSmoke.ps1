param([Parameter(Mandatory=$true)][string]$ExePath, [string]$OutputDirectory, [string]$CaseName = '')
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before wide-content tests.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\ui' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-wide-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$oldConfig = $env:MDV_CONFIG_DIR
$script:passed = 0
$app = $null
function Body-Hash([string]$Name, [double]$Scale) {
    $image = [Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($Name + '.png')))
    $stream = New-Object IO.MemoryStream
    $sha = [Security.Cryptography.SHA256]::Create()
    $crop = $null
    try {
        $top = [int](48 * $Scale)
        $rect = New-Object Drawing.Rectangle 0, $top, $image.Width, ($image.Height - $top)
        $crop = $image.Clone($rect, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $crop.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return [Convert]::ToBase64String($sha.ComputeHash($stream.ToArray()))
    } finally { if ($crop) { $crop.Dispose() }; $sha.Dispose(); $stream.Dispose(); $image.Dispose() }
}
$long = 'LEFT_START ' + ('alpha beta gamma delta ' * 35) + ' RIGHT_END'
$table = "| ID | SECOND_COLUMN | THIRD_COLUMN | FOURTH_COLUMN | FIFTH_COLUMN | SIXTH_COLUMN | SEVENTH_COLUMN | LAST_COLUMN |`n|---|---|---|---|---|---|---|---|`n| A | alpha beta | alpha beta | alpha beta | alpha beta | alpha beta | alpha beta | NEEDLE_LAST |`n"
$cases = [ordered]@{
    'nested-code' = "- item`n`n    ```````n    $long`n    ```````n"
    'plain-code' = "```````n$long`n```````n"
    'highlighted-code' = "``````csharp`nvar value = `"$long`";`n```````n"
    'quoted-code' = "> ```````n> $long`n> ```````n"
    'wide-table' = $table
    'nested-table' = "- item`n`n" + (($table.TrimEnd("`n") -split "`n" | ForEach-Object { '    ' + $_ }) -join "`n") + "`n"
}
if ($CaseName -and -not $cases.Contains($CaseName)) { throw 'Unknown wide-content fixture.' }
try {
    foreach ($name in $cases.Keys) {
        if ($CaseName -and $name -ne $CaseName) { continue }
        $env:MDV_CONFIG_DIR = Join-Path $fixture ('config-' + $name)
        [void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
        [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "theme=light`ntoc=0`nfont=16`nhints=0`nwin=120,120,820,600,0")
        $source = $cases[$name]
        $path = Join-Path $fixture ($name + '.md')
        [IO.File]::WriteAllText($path, $source)
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $path + '"') -PassThru -WindowStyle Hidden
        Wait-Until { if ($app.HasExited) { throw 'Wide-content host exited during startup.' }; [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero }
        $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
        Wait-Until { (Text-Of $script:window).Contains($name + '.md') }
        [void][HostNative]::ShowWindow($script:window, 4)
        $scale = [HostNative]::GetDpiForWindow($script:window) / 96.0
        [void][HostNative]::SetWindowPos($script:window, [IntPtr]::Zero, 120, 120, [int](820 * $scale), [int](600 * $scale), 4)
        Capture ('wide-' + $name + '-start')
        $before = Body-Hash ('wide-' + $name + '-start') $scale
        # Messages go only to our HWND; this is not SendInput or a real keyboard press.
        1..6 | ForEach-Object { [void](Send $script:window 0x0100 0x27) }
        Capture ('wide-' + $name + '-right')
        Assert-Host ($name + ' right-arrow messages move the painted body') ((Body-Hash ('wide-' + $name + '-right') $scale) -ne $before)
        1..6 | ForEach-Object { [void](Send $script:window 0x0100 0x25) }
        Capture ('wide-' + $name + '-back')
        Assert-Host ($name + ' left-arrow messages restore the exact frame') ((Body-Hash ('wide-' + $name + '-back') $scale) -eq $before)
        Command 1002
        $editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
        Assert-Host ($name + ' keeps exact source and clean state') ((Text-Of $editor).Replace("`r`n","`n") -eq $source -and -not (Text-Of $script:window).StartsWith('*'))
        Assert-Host ($name + ' does not write source, backup or network cache') ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $hash -and -not (Test-Path ($path + '.bak')) -and -not (Test-Path (Join-Path $env:MDV_CONFIG_DIR 'image-cache')))
        [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $app.WaitForExit(6000)) { throw 'Wide-content host did not close.' }
        Assert-Host ($name + ' closes normally') ($app.ExitCode -eq 0)
        $app.Dispose(); $app = $null
    }
    Write-Output "HOST WIDE CONTENT SMOKE: $script:passed PASS (owned messages; no real keyboard, mouse or network)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)) }
    throw
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    $env:MDV_CONFIG_DIR = $oldConfig
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
