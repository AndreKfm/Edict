#!/usr/bin/env python3
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
"""The .NET runtime's license and third-party notices for a release package (.github/workflows/release.yml).

A self-contained publish takes the runtime from the NuGet runtime pack Microsoft.NETCore.App.Runtime.<rid>, not from
the SDK's installation (whose license file can carry other terms, as Microsoft's Windows installer does). This reads
the pack's version from the project's restore (obj/project.assets.json after the publish), checks that the pack is
under the MIT license (its nuspec), and copies its LICENSE.TXT and THIRD-PARTY-NOTICES.TXT into the package as
dotnet-LICENSE.txt and dotnet-THIRD-PARTY-NOTICES.txt. A pack under other terms is an error: a GPL program may
carry the runtime under the MIT license.

Usage: tools/runtime-notices.py <project dir> <rid> <package dir>
"""
import json
import os
import re
import shutil
import subprocess
import sys


def main():
    project, rid, package = sys.argv[1:4]
    with open(os.path.join(project, 'obj', 'project.assets.json'), encoding='utf-8') as f:
        assets = json.load(f)
    name = f'microsoft.netcore.app.runtime.{rid}'
    versions = {re.sub(r'[\[\]\s]', '', d['version']).split(',')[0]
                for fw in assets['project']['frameworks'].values()
                for d in fw.get('downloadDependencies', []) if d['name'].lower() == name}
    if len(versions) != 1:
        print(f'{name}: the restore has {sorted(versions)}, expected one version (run after a self-contained publish)')
        return 1
    version = versions.pop()
    out = subprocess.run(['dotnet', 'nuget', 'locals', 'global-packages', '-l'], capture_output=True, text=True,
                         check=True).stdout
    packages = out.split('global-packages:', 1)[1].strip()
    pack = os.path.join(packages, name, version)
    with open(os.path.join(pack, f'{name}.nuspec'), encoding='utf-8') as f:
        nuspec = f.read()
    license = re.search(r'<license[^>]*>([^<]*)</license>', nuspec)
    if not license or license.group(1).strip() != 'MIT':
        print(f'{name} {version}: license {license.group(1) if license else "?"}, not MIT: no release')
        return 1
    for src, dst in (('LICENSE.TXT', 'dotnet-LICENSE.txt'), ('THIRD-PARTY-NOTICES.TXT', 'dotnet-THIRD-PARTY-NOTICES.txt')):
        shutil.copyfile(os.path.join(pack, src), os.path.join(package, dst))
    print(f'{name} {version}: MIT; LICENSE.TXT and THIRD-PARTY-NOTICES.TXT copied')
    return 0


if __name__ == '__main__':
    sys.exit(main())
