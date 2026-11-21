#!/usr/bin/env python3
"""
Lightweight structural sanity check for the C# sources in windows-host/.

This is NOT a compiler and NOT a substitute for `dotnet build`. It exists because the
sandbox used to author the prototype has no .NET SDK, so the following classes of
mistake can otherwise go unnoticed:

  * unbalanced braces / parentheses / brackets (after stripping comments, strings,
    verbatim strings and char literals)
  * Python-isms accidentally pasted into C# (b"...", f"...", True/False/None)
  * duplicate top-level type declarations

It cannot detect type errors, missing usings, wrong overloads or API misuse.
Run `dotnet build windows-host/` on a machine with the .NET 8 SDK for real validation.

Usage: python3 tools/check_csharp_structure.py [windows-host]
"""

from __future__ import annotations

import os
import re
import sys

PYTHONISMS = [
    (re.compile(r'(?<![\w"])b"'), 'Python bytes-literal b"..." in C#'),
    (re.compile(r'\bf"[^"\n]*"'), 'Python f-string f"..." in C#'),
    (re.compile(r'^\s*(True|False|None)\s*[;,\)]'), 'Python True/False/None in C#'),
]


def strip_noise(src: str) -> str:
    """Remove comments and string/char literals so delimiters can be counted."""
    out = []
    i = 0
    n = len(src)
    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ""

        if c == "/" and nxt == "/":
            while i < n and src[i] != "\n":
                i += 1
            continue
        if c == "/" and nxt == "*":
            i += 2
            while i + 1 < n and not (src[i] == "*" and src[i + 1] == "/"):
                i += 1
            i += 2
            continue
        if c == "@" and nxt == '"':  # verbatim string
            i += 2
            while i < n:
                if src[i] == '"':
                    if i + 1 < n and src[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            continue
        if c == '"':  # normal string (handles \" and ${})
            i += 1
            while i < n:
                if src[i] == "\\":
                    i += 2
                    continue
                if src[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if c == "'":  # char literal
            i += 1
            while i < n:
                if src[i] == "\\":
                    i += 2
                    continue
                if src[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def check_file(path: str) -> list[str]:
    problems: list[str] = []
    src = open(path, encoding="utf-8").read()

    for pattern, message in PYTHONISMS:
        if pattern.search(src):
            problems.append(message)

    stripped = strip_noise(src)
    for opener, closer, label in (("{", "}", "brace"), ("(", ")", "paren"), ("[", "]", "bracket")):
        depth = 0
        minimum = 0
        for ch in stripped:
            if ch == opener:
                depth += 1
            elif ch == closer:
                depth -= 1
                minimum = min(minimum, depth)
        if depth != 0:
            problems.append(f"unbalanced {label}s (net {depth:+d})")
        if minimum < 0:
            problems.append(f"{label} closes before it opens")

    types = re.findall(r"\b(?:class|struct|interface|enum)\s+(\w+)", stripped)
    dupes = {t for t in types if types.count(t) > 1}
    if dupes:
        problems.append(f"duplicate type declarations: {sorted(dupes)}")

    return problems


def main() -> int:
    root = sys.argv[1] if len(sys.argv) > 1 else "windows-host"
    if not os.path.isdir(root):
        print(f"not a directory: {root}")
        return 2

    files = []
    for base, _dirs, names in os.walk(root):
        for name in names:
            if name.endswith(".cs"):
                files.append(os.path.join(base, name))
    files.sort()

    failed = 0
    for path in files:
        problems = check_file(path)
        rel = os.path.relpath(path, root)
        if problems:
            failed += 1
            print(f"  [FAIL] {rel}")
            for p in problems:
                print(f"         - {p}")
        else:
            print(f"  [PASS] {rel}")

    print(f"\n  {len(files)} file(s) checked, {failed} with structural problems")
    print("  NOTE: structural only — run `dotnet build windows-host/` for real validation.")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
