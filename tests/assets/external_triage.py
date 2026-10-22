#!/usr/bin/env python3
"""Summarizes the per-file results that ExternalTests writes to tests/assets/external/results/ as one table.

Run with:  python3 -I tests/assets/external_triage.py [--all]   (default: only pages that are not exact)
"""

import collections
import os
import sys

RESULTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "external", "results")


def main():
    show_all = "--all" in sys.argv
    counts = collections.Counter()
    for dirpath, _, names in sorted(os.walk(RESULTS)):
        for name in sorted(names):
            with open(os.path.join(dirpath, name), encoding="utf-8") as f:
                for line in f:
                    file, page, layout, outcome = line.rstrip("\n").split("\t")
                    if page == "-":
                        continue
                    kind = ("exact" if outcome.startswith("exact") else "differs" if outcome.startswith("max ")
                            else "decoded, libtiff rejects" if outcome.startswith("decoded")
                            else "unsupported" if "NotSupportedException" in outcome
                            else "error, libtiff converts" if "ImageFormatException" in outcome and outcome.startswith("libtiff converts;")
                            else "error" if "ImageFormatException" in outcome else "other exception")
                    counts[kind] += 1
                    if show_all or kind != "exact":
                        print(f"{kind:26} {file} [{page}] {layout}: {outcome}")
    print(", ".join(f"{k}: {v}" for k, v in sorted(counts.items())))


if __name__ == "__main__":
    main()
