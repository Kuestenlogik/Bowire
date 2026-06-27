# Crops the four UI regions out of the method-detail-{theme}.png
# screenshots so each ui-guide sub-page can show the matching slice
# instead of the whole workbench. Pixel coordinates are tied to the
# 1280×720 method-detail capture; if the layout changes (fixed sidebar
# width, header height), recompute and rerun.

Add-Type -AssemblyName System.Drawing

$root  = Join-Path $PSScriptRoot '..'
$srcDir = Join-Path $root 'docs/images'
$siteOut = Join-Path $root 'site/assets/images/ui-guide'
$docsOut = Join-Path $root 'docs/images/ui-guide'

foreach ($d in @($siteOut, $docsOut)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# Region map — x, y, w, h per ui-guide section, derived from the
# method-detail capture (1280×720, header band 56 px).
$regions = @(
    @{ Name = 'sidebar';        X =   0; Y =   0; W = 325; H = 720 },
    @{ Name = 'request-pane';   X = 325; Y =  56; W = 470; H = 605 },
    @{ Name = 'response-pane';  X = 795; Y =  56; W = 485; H = 605 },
    @{ Name = 'action-bar';     X = 325; Y = 660; W = 955; H =  60 }
)

foreach ($theme in @('dark', 'light')) {
    $src = Join-Path $srcDir "bowire-method-detail-$theme.png"
    if (-not (Test-Path $src)) { Write-Output "missing: $src"; continue }

    $img = [System.Drawing.Image]::FromFile($src)
    foreach ($r in $regions) {
        $rect = New-Object System.Drawing.Rectangle($r.X, $r.Y, $r.W, $r.H)
        $crop = New-Object System.Drawing.Bitmap($r.W, $r.H)
        $g = [System.Drawing.Graphics]::FromImage($crop)
        $g.DrawImage($img, (New-Object System.Drawing.Rectangle(0, 0, $r.W, $r.H)), $rect, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()

        $siteFile = Join-Path $siteOut "$($r.Name)-$theme.png"
        $docsFile = Join-Path $docsOut "$($r.Name)-$theme.png"
        $crop.Save($siteFile, [System.Drawing.Imaging.ImageFormat]::Png)
        $crop.Save($docsFile, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($theme -eq 'dark') {
            $crop.Save((Join-Path $siteOut "$($r.Name).png"), [System.Drawing.Imaging.ImageFormat]::Png)
            $crop.Save((Join-Path $docsOut "$($r.Name).png"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $crop.Dispose()
        Write-Output "  -> $($r.Name)-$theme.png ($($r.W)x$($r.H))"
    }
    $img.Dispose()
}
