[CmdletBinding()]
param([string]$PngPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$majorProject = Split-Path $PSScriptRoot -Parent
$majorAssets = Join-Path $majorProject 'src\Major\Assets'
New-Item -ItemType Directory -Path $majorAssets -Force | Out-Null
$majorPng = Join-Path $majorAssets 'Major.png'
if ($PngPath) {
    $majorInput = (Resolve-Path -LiteralPath $PngPath).Path
    if ($majorInput -ne $majorPng) { Copy-Item -LiteralPath $majorInput -Destination $majorPng -Force }
}
$majorSource = [Drawing.Bitmap]::new($majorPng)
function Get-MajorPngFrame([int]$Size) {
    $majorBitmap = [Drawing.Bitmap]::new($Size,$Size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $majorGraphics = [Drawing.Graphics]::FromImage($majorBitmap)
    try {
        $majorGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $majorGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $majorGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $majorAttributes = [Drawing.Imaging.ImageAttributes]::new()
        try {
            $majorAttributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $majorGraphics.DrawImage($majorSource,[Drawing.Rectangle]::new(0,0,$Size,$Size),0,0,$majorSource.Width,$majorSource.Height,[Drawing.GraphicsUnit]::Pixel,$majorAttributes)
        } finally { $majorAttributes.Dispose() }
        $majorMemory = [IO.MemoryStream]::new()
        try { $majorBitmap.Save($majorMemory,[Drawing.Imaging.ImageFormat]::Png); return ,$majorMemory.ToArray() }
        finally { $majorMemory.Dispose() }
    } finally { $majorGraphics.Dispose(); $majorBitmap.Dispose() }
}
try {
    $majorSizes = @(16,24,32,48,64,128,256)
    $majorFrames = @($majorSizes | ForEach-Object { ,(Get-MajorPngFrame $_) })
    $majorFile = [IO.File]::Create((Join-Path $majorAssets 'Major.ico'))
    $majorWriter = [IO.BinaryWriter]::new($majorFile)
    try {
        $majorWriter.Write([uint16]0); $majorWriter.Write([uint16]1); $majorWriter.Write([uint16]$majorSizes.Count)
        $majorOffset = 6 + 16 * $majorSizes.Count
        for ($majorIndex=0; $majorIndex -lt $majorSizes.Count; $majorIndex++) {
            $majorDimension = if ($majorSizes[$majorIndex] -eq 256) { 0 } else { $majorSizes[$majorIndex] }
            $majorWriter.Write([byte]$majorDimension); $majorWriter.Write([byte]$majorDimension)
            $majorWriter.Write([byte]0); $majorWriter.Write([byte]0)
            $majorWriter.Write([uint16]1); $majorWriter.Write([uint16]32)
            $majorWriter.Write([uint32]$majorFrames[$majorIndex].Length); $majorWriter.Write([uint32]$majorOffset)
            $majorOffset += $majorFrames[$majorIndex].Length
        }
        foreach ($majorFrame in $majorFrames) { $majorWriter.Write([byte[]]$majorFrame) }
    } finally { $majorWriter.Dispose() }
    $majorAndroid = Join-Path (Split-Path $majorProject -Parent) 'MajorAndroid\app\src\main\res\drawable-nodpi'
    if (Test-Path -LiteralPath (Split-Path $majorAndroid -Parent)) {
        New-Item -ItemType Directory -Path $majorAndroid -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $majorAndroid 'major_logo.png'), (Get-MajorPngFrame 512))
    }
} finally { $majorSource.Dispose() }
Write-Host 'Major: PNG originale, ICO a 7 risoluzioni e icona Android generati.'
