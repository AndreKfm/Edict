#!/usr/bin/env python3
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
"""The CI's benchmark check (docs/PLAN.md F, job 4): every benchmark of BenchmarkDotNet's JSON reports must
allocate 0 bytes per operation (a hard failure otherwise); the times are printed, for information only (shared
runners vary).

Usage: tools/bench-check.py <BenchmarkDotNet.Artifacts/results directory>
"""
import glob
import json
import os
import sys


def main():
    reports = sorted(glob.glob(os.path.join(sys.argv[1], '*-report-full-compressed.json'))
                     or glob.glob(os.path.join(sys.argv[1], '*-report-full.json'))
                     or glob.glob(os.path.join(sys.argv[1], '*.json')))
    if not reports:
        print(f'no BenchmarkDotNet JSON report in {sys.argv[1]}')
        return 1
    failed = seen = 0
    for path in reports:
        with open(path, encoding='utf-8') as f:
            report = json.load(f)
        for b in report.get('Benchmarks', []):
            seen += 1
            name = b.get('FullName') or b.get('DisplayInfo') or '?'
            stats = b.get('Statistics') or {}
            mean = stats.get('Mean')
            allocated = (b.get('Memory') or {}).get('BytesAllocatedPerOperation')
            ok = allocated == 0
            failed += not ok
            time = f'{mean:14,.1f} ns' if isinstance(mean, (int, float)) else '             ? ns'
            print(f'{"ok  " if ok else "FAIL"} {time}  allocated {allocated!s:>6} B/op  {name}')
    if seen == 0:
        print('no benchmarks in the reports')
        return 1
    print(f'{seen} benchmarks, {failed} allocate')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
