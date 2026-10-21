# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Starts Edict on Windows in one step: fetches LibreQuake's free game data on the first run (as tools/fetch-data.sh
# does, its SHA-256 checked), builds the game (incrementally: a few seconds once it is built), and starts it with
# that data. In a release package (edict.exe beside this script) there is nothing to build. The arguments go to the
# game as they are:
#   .\play.ps1                     the start map
#   .\play.ps1 +map lq_e0m1        another map
#   .\play.ps1 -listen 4           a listen server others can join
#   .\play.ps1 +connect 127.0.0.1  join a server
#   .\play.ps1 -software           the exact software renderer instead of the GPU's
# Needs the .NET 10 SDK (https://dot.net), except in a release package. play.cmd runs this script by a double click.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'    # Windows PowerShell's progress bar slows the download down

$root = $PSScriptRoot
$libre = 'v0.09-beta'
$sha256 = '428e736b2f01d953e09a08c60bee975bdc4a0ac2219e97fa095c8af41754da83'
$url = "https://github.com/lavenderdotpet/LibreQuake/releases/download/$libre/lite.zip"
$data = Join-Path $root "data\libreq-$libre"
$package = Join-Path $root 'edict.exe'     # a release package: the game beside this script
$isPackage = Test-Path $package

if (-not $isPackage -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'Edict needs the .NET 10 SDK: https://dot.net (then run this again).' -ForegroundColor Red
    exit 1
}

# the game data, once
if (-not (Test-Path (Join-Path $data 'id1\pak0.pak'))) {
    Write-Host "Fetching LibreQuake $libre (free game data, BSD-3-Clause and GPL-2.0)..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("edict-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $zip = Join-Path $tmp 'lite.zip'
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $sha256) {
            throw "lite.zip: SHA-256 $hash, expected $sha256"
        }
        Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp 'x')
        $id1 = Join-Path $tmp 'x\lite\id1'
        $dest = Join-Path $data 'id1'
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        Copy-Item (Join-Path $id1 'pak0.pak'), (Join-Path $id1 'pak1.pak') -Destination $dest
        Copy-Item (Join-Path $id1 'docs') -Destination $dest -Recurse
        Write-Host "Fetched: $data"
    }
    finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

if ($isPackage) {
    $game = $package
}
else {
    # the game, built (incremental)
    dotnet build (Join-Path $root 'src\Edict.Platform') -c Release -v q -nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'The build failed (see above).' -ForegroundColor Red
        exit $LASTEXITCODE
    }
    $game = Join-Path $root 'src\Edict.Platform\bin\Release\net10.0\edict.exe'
}

# and started
& $game -basedir $data @args
exit $LASTEXITCODE
