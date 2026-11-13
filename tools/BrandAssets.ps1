# WPF vector renderer shared by app, splash and the generated icon assets.
$repoRoot=Split-Path $PSScriptRoot -Parent
dotnet run --project (Join-Path $PSScriptRoot 'BrandGen/BrandGen.csproj') -- (Join-Path $repoRoot 'src/EliteSIP.App/Assets/AppIcon')
if ($LASTEXITCODE -ne 0) { throw 'Logo generation failed' }
