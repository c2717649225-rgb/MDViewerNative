param([string]$ExePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) { $ExePath = Join-Path $projectRoot 'engine\bin\Release\net10.0-windows\MDViewerNative.exe' }
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'Close MDViewerNative before running this isolated host test.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'DesktopInputSupport.ps1') # FocusOwned only; no real input is sent.
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'work\architecture-ui' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-host-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$configDirectory = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($configDirectory)
$previousConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = $configDirectory
$app = $null
$script:passed = 0

try {
    [IO.File]::WriteAllText((Join-Path $configDirectory 'config.txt'), "theme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,766,900,0")
    $a = Join-Path $fixture 'a.md'
    $b = Join-Path $fixture 'b.md'
    # This scenario asserts LF preservation. Git autocrlf may change the checked-out corpus,
    # so establish the fixture's line ending explicitly rather than depending on the checkout.
    $corpus = [IO.File]::ReadAllText((Join-Path $projectRoot 'MDViewerNative\全格式测试.md'))
    [IO.File]::WriteAllText($a, $corpus.Replace("`r`n", "`n"))
    Assert-Host 'LF fixture is independent of Git checkout settings' (-not [IO.File]::ReadAllText($a).Contains("`r"))
    [IO.File]::WriteAllText($b, "# Bravo`n`nSecond document.`n")
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $a + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw "Host exited during startup: $($app.ExitCode)" }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    [void][HostNative]::ShowWindow($script:window, 5)
    Capture '1-light'
    $beforeDirectory = New-Object HostNative+RECT
    [void][HostNative]::GetClientRect($script:window, [ref]$beforeDirectory)
    Command 1006
    Capture '2-light-toc'
    $afterDirectory = New-Object HostNative+RECT
    [void][HostNative]::GetClientRect($script:window, [ref]$afterDirectory)
    Assert-Host 'directory toggle never resizes the window' ($afterDirectory.R -eq $beforeDirectory.R -and $afterDirectory.B -eq $beforeDirectory.B)
    Command 1004
    Capture '3-dark-toc'
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    Capture '4-dark-edit'
    Assert-Host 'editor opens' ($script:editor -ne [IntPtr]::Zero)
    $editScroll = New-Object HostNative+SCROLLINFO
    $editScroll.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($editScroll)
    $editScroll.fMask = 3
    [void][HostNative]::GetScrollInfo($script:editor, 1, [ref]$editScroll)
    Assert-Host 'editor scrollbar reports its visible page' ($editScroll.nPage -gt 0 -and $editScroll.nMax -ge $editScroll.nPage)
    Edit-Text "A longer first line`r`n- "
    $listLength = (Text-Of $script:editor).Length
    [void](Send $script:editor 0x00B1 $listLength $listLength)
    [void][HostNative]::PostMessage($script:editor, 0x0100, [IntPtr]0x0D, [IntPtr]1)
    Start-Sleep -Milliseconds 150
    Assert-Host 'editor instance callback exits an empty later list item' ((Text-Of $script:editor) -eq "A longer first line`r`n")
    $longList = "x`r`n- " + ('y' * 70000)
    Edit-Text $longList
    [void](Send $script:editor 0x00B1 $longList.Length $longList.Length)
    [void][HostNative]::PostMessage($script:editor, 0x0100, [IntPtr]0x0D, [IntPtr]1)
    Start-Sleep -Milliseconds 150
    Assert-Host 'editor instance callback continues a wrapped long list item' ((Text-Of $script:editor) -eq ($longList + "`r`n- "))
    Edit-Text "# Alpha saved`r`n`r`nFirst document.`r`n"
    Assert-Host 'editor changes mark current tab dirty' ((Text-Of $script:window).StartsWith('*'))
    [void](Send $script:editor 0x00B1 4 9)
    $selection = Send $script:editor 0x00B0
    Command 1001
    Assert-Host 'save writes the active draft' ([IO.File]::ReadAllText($a) -eq "# Alpha saved`n`nFirst document.`n")
    Assert-Host 'save preserves editor selection' ((Send $script:editor 0x00B0) -eq $selection)
    Assert-Host 'save clears dirty state' (-not (Text-Of $script:window).StartsWith('*'))
    Command 1002
    Command 1002
    Assert-Host 'reenter editor retains saved snapshot' ((Text-Of $script:editor) -eq "# Alpha saved`r`n`r`nFirst document.`r`n")
    Command 1002
    Command 1004
    Command 1006
    [void][HostNative]::ShowWindow($script:window, 3)
    $height16 = Scroll-Extent
    $handoff = Start-Process -FilePath $ExePath -ArgumentList ('"' + $b + '"') -PassThru -WindowStyle Hidden
    if (-not $handoff.WaitForExit(8000)) { throw 'Single-instance handoff did not finish.' }
    Wait-Until { (Text-Of $script:window).Contains('b.md') }
    Command 1002
    Assert-Host 'new tab uses its own editor content' ((Text-Of $script:editor) -eq "# Bravo`r`n`r`nSecond document.`r`n")
    Edit-Text "# Bravo draft`r`n"
    [void](Send $script:editor 0x00B1 2 6)
    $bravoSelection = Send $script:editor 0x00B0
    Next-Document
    Assert-Host 'switch preserves a draft without saving or prompting' ((Text-Of $script:window).Contains('a.md') -and [IO.File]::ReadAllText($b) -eq "# Bravo`n`nSecond document.`n" -and [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero)
    Next-Document
    Command 1002
    Assert-Host 'returning to a dirty tab restores text and selection' ((Text-Of $script:editor) -eq "# Bravo draft`r`n" -and (Send $script:editor 0x00B0) -eq $bravoSelection)
    Guarded-Command 1008 2
    Assert-Host 'cancel close still protects the dirty source tab' ((Text-Of $script:window).StartsWith('*b.md') -and (Text-Of $script:editor) -eq "# Bravo draft`r`n")
    Command 1001
    Assert-Host 'explicit save writes the source tab only' ([IO.File]::ReadAllText($b) -eq "# Bravo draft`n" -and [IO.File]::ReadAllText($a) -eq "# Alpha saved`n`nFirst document.`n")
    Command 1002
    1..4 | ForEach-Object { Command 1014 }
    Next-Document
    Assert-Host 'inactive tab refreshes font at fixed window width' ((Scroll-Extent) -gt $height16)
    Command 1002
    Edit-Text "Discard this draft`r`n"
    Discard-ToPreview
    Command 1002
    Assert-Host 'discard then reopen restores saved text' ((Text-Of $script:editor) -eq "# Alpha saved`r`n`r`nFirst document.`r`n")
    Command 1002
    Command 1008
    Assert-Host 'closing active tab activates remaining document' ((Text-Of $script:window).Contains('b.md'))
    Close-LastTab
    Assert-Host 'closing last tab leaves a usable blank document' ((Text-Of $script:window).Contains('(未命名)'))
    $imagePath = Join-Path $fixture 'image.png'
    $imageDocument = Join-Path $fixture 'image.md'
    $sample = New-Object Drawing.Bitmap 160, 80
    $drawing = [Drawing.Graphics]::FromImage($sample)
    try {
        $drawing.FillRectangle([Drawing.Brushes]::Red, 0, 0, 80, 80)
        $drawing.FillRectangle([Drawing.Brushes]::Blue, 80, 0, 80, 80)
        $drawing.FillRectangle([Drawing.Brushes]::Lime, 20, 20, 10, 10)
        $sample.Save($imagePath)
    } finally { $drawing.Dispose(); $sample.Dispose() }
    [IO.File]::WriteAllText($imageDocument, "![fixture](image.png)`n")
    $handoff = Start-Process -FilePath $ExePath -ArgumentList ('"' + $imageDocument + '"') -PassThru -WindowStyle Hidden
    if (-not $handoff.WaitForExit(8000)) { throw 'Image document handoff did not finish.' }
    Wait-Until { (Text-Of $script:window).Contains('image.md') }
    # Acquire only our process-owned window before measuring: FocusOwned can
    # restore a maximized window, which changes the image hit-test coordinates.
    if (-not [DesktopInput]::FocusOwned($script:window, [uint32]$app.Id)) {
        throw 'Owned foreground unavailable for lightbox acceptance; no real input was sent.'
    }
    Command 1016
    # S80 起正文随窗口铺满,旧的居中公式不再成立:截图找到图片的红色半边,按物理像素点击。
    Capture 'image-fixture'
    $render = [Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory 'image-fixture.png'))
    $imageX = -1; $imageY = -1
    try {
        for ($y = 0; $y -lt $render.Height -and $imageX -lt 0; $y += 2) {
            for ($x = 0; $x -lt $render.Width; $x += 2) {
                $c = $render.GetPixel($x, $y)
                if ($c.R -gt 200 -and $c.G -lt 60 -and $c.B -lt 60) { $imageX = $x + 12; $imageY = $y + 12; break }
            }
        }
    } finally { $render.Dispose() }
    Assert-Host 'image fixture is painted before the lightbox click' ($imageX -ge 0)
    $imagePoint = $imageX -bor ($imageY -shl 16)
    Write-Output ("Image fixture point={0},{1} (physical) foreground={2} owner={3}" -f $imageX,$imageY,[HostNative]::GetForegroundWindow(),$script:window)
    Wait-Until { [HostNative]::GetForegroundWindow() -eq $script:window }
    [void][HostNative]::RedrawWindow($script:window, [IntPtr]::Zero, [IntPtr]::Zero, 0x0185)
    [void](Send-Physical $script:window 0x0201 0 $imagePoint)
    Wait-Until { [HostNative]::Window([uint32]$app.Id, 'MDLightbox') -ne [IntPtr]::Zero }
    $photo = [HostNative]::Window([uint32]$app.Id, 'MDLightbox')
    Assert-Host 'image preview opens independently of the document view' ([HostNative]::IsWindowVisible($photo))
    function Photo-Bitmap {
        $rect = New-Object HostNative+RECT
        [void][HostNative]::GetClientRect($photo, [ref]$rect)
        $bitmap = New-Object Drawing.Bitmap $rect.R, $rect.B
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        try { [void][HostNative]::PrintWindow($photo, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
        return $bitmap
    }
    function Photo-Hash {
        $bitmap = Photo-Bitmap
        $stream = New-Object IO.MemoryStream
        $hash = [Security.Cryptography.SHA256]::Create()
        try {
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            return [Convert]::ToBase64String($hash.ComputeHash($stream.ToArray()))
        } finally { $hash.Dispose(); $stream.Dispose(); $bitmap.Dispose() }
    }
    $photoBitmap = Photo-Bitmap
    try {
        $left = $photoBitmap.GetPixel([int]($photoBitmap.Width / 4), [int]($photoBitmap.Height / 2))
        $right = $photoBitmap.GetPixel([int]($photoBitmap.Width * 3 / 4), [int]($photoBitmap.Height / 2))
        Assert-Host 'image preview draws both halves of the bitmap' ($left.R -gt 200 -and $left.B -lt 50 -and $right.B -gt 200 -and $right.R -lt 50)
        $photoBitmap.Save((Join-Path $OutputDirectory '5-image-window.png'))
    } finally { $photoBitmap.Dispose() }
    $beforeZoom = Photo-Hash
    [void](Send $photo 0x020A (120 -shl 16))
    Assert-Host 'image preview wheel zoom changes the rendered frame' ((Photo-Hash) -ne $beforeZoom)
    $beforePan = Photo-Hash
    [void](Send-Physical $photo 0x0201 0 (100 -bor (100 -shl 16)))
    [void](Send $photo 0x0200 0 (150 -bor (120 -shl 16)))
    [void](Send $photo 0x0202 0 (150 -bor (120 -shl 16)))
    Assert-Host 'image preview dragging pans without closing' ([HostNative]::IsWindowVisible($photo) -and (Photo-Hash) -ne $beforePan)
    [void](Send $photo 0x0100 0x1B)
    Wait-Until { [HostNative]::Window([uint32]$app.Id, 'MDLightbox') -eq [IntPtr]::Zero }
    Assert-Host 'image preview Escape closes its native window' (-not $app.HasExited)
    [void][HostNative]::RedrawWindow($script:window, [IntPtr]::Zero, [IntPtr]::Zero, 0x0185)
    [void](Send-Physical $script:window 0x0201 0 $imagePoint)
    Wait-Until { [HostNative]::Window([uint32]$app.Id, 'MDLightbox') -ne [IntPtr]::Zero }
    $photo = [HostNative]::Window([uint32]$app.Id, 'MDLightbox')
    [void](Send $photo 0x0008 $script:window.ToInt64())
    Wait-Until { [HostNative]::Window([uint32]$app.Id, 'MDLightbox') -eq [IntPtr]::Zero }
    Assert-Host 'image preview reopens and releases on focus loss' (-not $app.HasExited)
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Assert-Host 'normal shutdown succeeds' ($app.WaitForExit(8000) -and $app.ExitCode -eq 0)
    Write-Output "HOST SMOKE: $script:passed PASS"
}
catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) {
        Write-Output ([HostNative]::Snapshot([uint32]$app.Id))
        try { Capture 'host-smoke-failure' } catch { Write-Output $_.Exception.Message }
    }
    throw
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; [void]$app.WaitForExit(8000) }
    $env:MDV_CONFIG_DIR = $previousConfig
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the test temporary directory.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
