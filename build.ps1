# Builds SpeakerBoost.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# Usage: powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# Draw the app icon (speaker with sound waves) and save it as a PNG-based .ico.
if (-not (Test-Path app.ico)) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object Drawing.Bitmap 256, 256
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(14, 124, 102))), 8, 8, 240, 240)
    $white = [Drawing.Brushes]::White
    $g.FillRectangle($white, 56, 100, 34, 56)
    $g.FillPolygon($white, [Drawing.Point[]]@(
        (New-Object Drawing.Point 88, 100), (New-Object Drawing.Point 132, 64),
        (New-Object Drawing.Point 132, 192), (New-Object Drawing.Point 88, 156)))
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), 13
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawArc($pen, 112, 98, 60, 60, -50, 100)
    $g.DrawArc($pen, 104, 70, 116, 116, -50, 100)
    $g.Dispose()

    $png = New-Object IO.MemoryStream
    $bmp.Save($png, [Drawing.Imaging.ImageFormat]::Png)
    $bytes = $png.ToArray()
    $w = New-Object IO.BinaryWriter ([IO.File]::Create("$PSScriptRoot\app.ico"))
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)        # ICONDIR
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0)  # 256x256
    $w.Write([uint16]1); $w.Write([uint16]32)                               # planes, bpp
    $w.Write([uint32]$bytes.Length); $w.Write([uint32]22)                   # size, offset
    $w.Write($bytes)
    $w.Close()
}

& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /win32icon:app.ico /win32manifest:app.manifest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /out:SpeakerBoost.exe SpeakerBoost.cs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $PSScriptRoot\SpeakerBoost.exe"
