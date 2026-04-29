$src  = Split-Path $PSCommandPath -Parent
$src  = Join-Path $src "bin"
$conf = Get-ChildItem $src | Where-Object { $_.PSIsContainer } | Select-Object -First 1 -ExpandProperty Name
$src  = Join-Path $src "$conf\Mods\mod"
$dest = Join-Path $env:APPDATA "VintagestoryData\Mods\truecolorlayer"

if (!(Test-Path $src)) { Write-Error "Source not found: $src"; exit 1 }
if (Test-Path $dest)   { Remove-Item $dest -Recurse -Force }

Copy-Item $src $dest -Recurse -Force
Get-ChildItem $dest -Filter "*.deps.json" -Recurse | Remove-Item -Force
Get-ChildItem $dest -Filter "*.pdb"       -Recurse | Remove-Item -Force
Write-Host "TrueColorLayer: installed as folder mod -> $dest"
