# One-off dev helper: decode a JPEG/PNG screenshot with GDI+ and dump it as a
# grayscale PGM (P5) file, so the Python side can analyze pixels without Pillow.
# Read-only with respect to the project; writes only the requested output file.
param(
    [Parameter(Mandatory=$true)][string]$In,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$W = 1280
)

Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile((Resolve-Path $In).Path)
try {
    $h = [int][math]::Round($src.Height * $W / $src.Width)
    if ($h -lt 1) { $h = 1 }
    $bmp = New-Object System.Drawing.Bitmap($W, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, 0, 0, $W, $h)
    $g.Dispose()

    $rect = New-Object System.Drawing.Rectangle(0, 0, $W, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $stride = $data.Stride
    $buf = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()

    $header = [System.Text.Encoding]::ASCII.GetBytes("P5`n$W $h`n255`n")
    $pixels = New-Object byte[] ($W * $h)
    for ($y = 0; $y -lt $h; $y++) {
        $row = $y * $stride
        $dst = $y * $W
        for ($x = 0; $x -lt $W; $x++) {
            $o = $row + $x * 3
            # luminance (BGR order in memory)
            $v = [int](0.114 * $buf[$o] + 0.587 * $buf[$o + 1] + 0.299 * $buf[$o + 2])
            if ($v -gt 255) { $v = 255 }
            $pixels[$dst + $x] = [byte]$v
        }
    }

    $fs = [System.IO.File]::Create($Out)
    $fs.Write($header, 0, $header.Length)
    $fs.Write($pixels, 0, $pixels.Length)
    $fs.Close()
    Write-Output ("wrote $Out  ${W}x${h}  (source $($src.Width)x$($src.Height))")
}
finally {
    $src.Dispose()
}
