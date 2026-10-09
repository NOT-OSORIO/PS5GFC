# PS5GFC — PS5 Game Format Converter
# Copyright (C) 2026 OSØRIO
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, version 3.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with this program. If not, see <https://www.gnu.org/licenses/>.

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$saida  = Join-Path (Get-Location) 'release\PS5GFC'
$aberto = Get-Process -Name PS5GFC -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($saida, [StringComparison]::OrdinalIgnoreCase) }
if ($aberto) { throw "Feche o PS5GFC (aberto em $saida) e rode de novo." }

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path', 'User') + ';' +
            (Join-Path $env:USERPROFILE '.cargo\bin')

if (-not (Test-Path 'node_modules')) {
  npm install
  if ($LASTEXITCODE -ne 0) { throw 'npm install falhou.' }
}

$pkgProj = 'tools\ps5pkg-builder\ps5pkg-builder.csproj'
$temSdk  = $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue) -and [bool](dotnet --list-sdks)
Remove-Item Env:PS5GFC_EMBED_BUILDER -ErrorAction SilentlyContinue
if ($temSdk) {
  Write-Host 'Compilando o builder de PKG (.NET, arquivo unico)...' -ForegroundColor Cyan
  $unico = Join-Path (Get-Location) 'target\builder-single'
  dotnet publish $pkgProj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:ExternalData=true -o $unico
  if ($LASTEXITCODE -ne 0) { throw 'dotnet publish do builder de PKG falhou.' }
  $env:PS5GFC_EMBED_BUILDER = Join-Path $unico 'ps5pkg-builder.exe'
} else {
  Write-Warning 'SDK do .NET 10 nao encontrado: os executaveis saem SEM o destino PKG. Instale o SDK e rode de novo.'
}

$flags = @('-C', 'target-feature=+crt-static', '-C', 'link-arg=/PDBALTPATH:%_PDB%', "--remap-path-prefix=$env:USERPROFILE=~", "--remap-path-prefix=$((Get-Location).Path)=.")
$env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]0x1f

Write-Host 'Compilando o aplicativo (release)...' -ForegroundColor Cyan
npm run tauri:build
if ($LASTEXITCODE -ne 0) { throw 'tauri build falhou.' }

Write-Host 'Compilando a CLI (release)...' -ForegroundColor Cyan
cargo build -p ps5gfc-cli --release
if ($LASTEXITCODE -ne 0) { throw 'cargo build da CLI falhou.' }

$destino = Join-Path (Get-Location) 'release\PS5GFC'
New-Item -ItemType Directory -Force $destino | Out-Null
Get-ChildItem -LiteralPath $destino -Force | Where-Object { $_.Name -ne '.git' -and $_.Name -ne "dll's" } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
Copy-Item 'target\release\ps5gfc-app.exe' (Join-Path $destino 'PS5GFC.exe')
Copy-Item 'target\release\ps5gfc.exe'     (Join-Path $destino 'ps5gfc-cli.exe')
foreach ($doc in 'README.md', 'LICENSE', 'NOTICE.md') { Copy-Item (Join-Path 'scripts\release-docs' $doc) $destino }
$dlls = Join-Path $destino "dll's"
New-Item -ItemType Directory -Force $dlls | Out-Null
Copy-Item 'scripts\release-docs\dlls-README.txt' (Join-Path $dlls 'README.txt')

Get-ChildItem $destino | Select-Object Name, @{n = 'Tamanho'; e = { '{0:N1} MiB' -f ($_.Length / 1MB) } } | Format-Table -AutoSize
Write-Host "Pronto: $destino" -ForegroundColor Green
