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

param([string]$Destination = '')

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$res = 'tools\ps5pkg-data\res'
$names = ['passcode.bin', 'mount_image.bin', 'metadata_modulus.bin', 'right.sprx', 'ks_fp3.bin', 'ks_mac3.bin', 'ks_fp2.bin', 'ks_mac2.bin']
$missing = $names | Where-Object { -not (Test-Path (Join-Path $res $_)) }
if ($missing) { throw ('Missing in ' + $res + ': ' + ($missing -join ', ') + '. See ' + $res + '\README.md.') }

if (-not $Destination) { $Destination = Join-Path 'release\PS5GFC' "dll's" }
New-Item -ItemType Directory -Force $Destination | Out-Null

$work = Join-Path ([IO.Path]::GetTempPath()) ('ps5gfc-data-' + [Guid]::NewGuid().ToString('N'))
dotnet build 'tools\ps5pkg-data\ProsperoPkgTool.Data.csproj' -c Release -o $work
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
Copy-Item (Join-Path $work 'ProsperoPkgTool.Data.dll') $Destination -Force
Remove-Item -LiteralPath $work -Recurse -Force
Write-Host ('Created ' + (Join-Path $Destination 'ProsperoPkgTool.Data.dll')) -ForegroundColor Green
