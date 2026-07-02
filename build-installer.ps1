# Gera o instalador DuoVoz-win-Setup.exe (Velopack) e publica a Release no GitHub.
# Requisitos: .NET 10 SDK, vpk (dotnet tool install -g vpk), gh autenticado (gh auth login).
#
# Uso:  ./build-installer.ps1 -Version 1.0.1
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "DuoVoz.csproj"
$pub  = Join-Path $root "vpk-publish"
$rel  = Join-Path $root "Releases"

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

# 1) Publica a app self-contained (pasta com todos os arquivos).
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $pub

# 2) Empacota o instalador + pacotes de atualizacao.
vpk pack -u DuoVoz -v $Version -p $pub -e DuoVoz.exe --packTitle "DuoVoz" --packAuthors "BanePlayss" -o $rel

# 3) Publica a Release no GitHub (usa o token do gh).
$token = gh auth token
vpk upload github -o $rel --repoUrl "https://github.com/BanePlayss/duovoz" --token $token --publish true --releaseName "DuoVoz $Version" --tag "v$Version"

Write-Host "OK: Release v$Version publicada. Link do instalador:"
Write-Host "https://github.com/BanePlayss/duovoz/releases/latest/download/DuoVoz-win-Setup.exe"
