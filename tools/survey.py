"""What is actually in the corpus, so the plan is grounded in numbers rather than expectation.

    python tools/fetch-corpus.py
    python tools/survey.py [--json docs/survey.json]

Run this after any corpus refresh. If the numbers move, the plan may need to. The first run of it
is what established that the entire corpus is JavaScript-animated - the original plan had assumed
some meaningful fraction would be CSS and therefore nearly free to import. None of it is.
"""
import argparse
import collections
import glob
import json
import os
import re

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORPUS = os.path.join(REPO, 'corpus')

# A declaration, at the start of a line inside a <style>. Loose on purpose: this is a census, not a
# parser, and over-counting a shorthand is better than missing a property nobody expected.
DECLARATION = re.compile(r'(?m)^\s*([a-z-]{3,32})\s*:')
GSAP_CALL = re.compile(r'\b(?:gsap|tl|timeline)\s*\.\s*([a-zA-Z]+)\s*\(')

# Each is (label, pattern). Presence per block, not occurrences - "how many blocks would this
# rewrite rule have to handle" is the question a plan needs answered.
FEATURES = [
    ('loads GSAP', r'gsap[.@/-]'),
    ('inline <script>', r'<script(?![^>]*\bsrc=)[^>]*>\s*\S'),
    ('@keyframes', r'@keyframes'),
    ('CSS animation:', r'(?m)^\s*animation\s*:'),
    ('CSS transition:', r'(?m)^\s*transition\s*:'),
    ('data-start', r'data-start\s*='),
    ('data-duration', r'data-duration\s*='),
    ('data-composition-variables', r'data-composition-variables'),
    ('<img', r'<img\b'),
    ('<svg', r'<svg\b'),
    ('<canvas', r'<canvas\b'),
    ('<video', r'<video\b'),
    ('external font', r'fonts\.googleapis|@import'),
    ('@font-face', r'@font-face'),
    ('repeating gradient', r'repeating-(linear|radial)-gradient'),
    ('backdrop-filter', r'backdrop-filter'),
    ('filter:', r'(?m)^\s*filter\s*:'),
    ('mix-blend-mode', r'mix-blend-mode'),
    ('clip-path', r'clip-path'),
    ('CSS var()', r'var\(--'),
    ('display:grid', r'(?m)^\s*display\s*:\s*grid'),
    ('position:absolute', r'position\s*:\s*absolute'),
    ('letter-spacing', r'letter-spacing'),
    ('line-height', r'line-height'),
    ('3d transform', r'translate3d|rotate[XY]|perspective'),
]


def blocks():
    return sorted(glob.glob(os.path.join(CORPUS, 'registry', 'blocks', '*', '*.html')))


def survey():
    files = blocks()
    if not files:
        raise SystemExit('corpus/ is empty - run: python tools/fetch-corpus.py')

    properties = collections.Counter()
    gsap = collections.Counter()
    features = collections.Counter()
    sizes = []

    for path in files:
        with open(path, encoding='utf-8', errors='replace') as f:
            src = f.read()
        sizes.append(len(src))

        styles = '\n'.join(re.findall(r'<style[^>]*>(.*?)</style>', src, re.S))
        for m in DECLARATION.finditer(styles):
            properties[m.group(1)] += 1

        scripts = '\n'.join(re.findall(r'<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>', src, re.S))
        for m in GSAP_CALL.finditer(scripts):
            gsap[m.group(1)] += 1

        for label, pattern in FEATURES:
            if re.search(pattern, src, re.S | re.I):
                features[label] += 1

    return {
        'blocks': len(files),
        'medianBytes': sorted(sizes)[len(sizes) // 2],
        'largestBytes': max(sizes),
        # Every feature, including the zeroes - "0 blocks use @keyframes" is the single most
        # important number here, and a Counter would simply omit it.
        'features': {label: features[label] for label, _ in FEATURES},
        'gsap': dict(gsap.most_common()),
        'properties': dict(properties.most_common(60)),
    }


def report(s):
    n = s['blocks']
    print(f"blocks: {n}   median {s['medianBytes'] // 1024} KB, largest {s['largestBytes'] // 1024} KB")
    print()
    print(f'=== blocks using each feature (of {n}) ===')
    for label, count in sorted(s['features'].items(), key=lambda kv: -kv[1]):
        bar = '#' * (40 * count // n) if count else ''
        print(f'  {count:4} {100 * count // n:4}%  {label:28} {bar}')

    print()
    print('=== GSAP calls, by total occurrences ===')
    for name, count in s['gsap'].items():
        print(f'  {count:6}  {name}')

    print()
    print('=== top CSS properties ===')
    for name, count in list(s['properties'].items())[:30]:
        print(f'  {count:6}  {name}')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--json', help='also write the raw survey here')
    args = p.parse_args()

    result = survey()
    report(result)

    if args.json:
        target = args.json if os.path.isabs(args.json) else os.path.join(REPO, args.json)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, 'w', encoding='utf-8') as f:
            json.dump(result, f, indent=2)
        print()
        print('wrote ' + target)
