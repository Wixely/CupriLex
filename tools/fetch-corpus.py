"""Pull the HyperFrames registry into corpus/.

    python tools/fetch-corpus.py [--force]
    python tools/fetch-corpus.py --ref main --force     # what upstream has RIGHT NOW
    python tools/fetch-corpus.py --print-pin            # the commit this repository measures

Still not vendored. The corpus is somebody else's work under Apache 2.0 and committing a snapshot
here would age silently. But it is now PINNED to a commit rather than to a branch, and the
difference is the whole point of this file.

Through 25 September 2026 this fetched `refs/heads/main`, so every fetch took whatever upstream
happened to be that day. Two fetches a day apart differed by 24 blocks removed and 9 added, and the
corpus mean fell from 40.8% to 37.1% without a line of this repository changing - the blocks that
left were the dense-text ones scoring 70-99%. Nothing said so, because nothing could: there was no
version to compare. Every number in the docs was describing a corpus that no longer existed.

Pinned, a measurement is reproducible and two of them are comparable. The cost is that the pin
ages, and `tools/survey.py` now describes what was there on the day somebody last moved it - so
moving it is a deliberate act, and CI opens an issue when upstream has drifted away from it.

To move the pin: change PIN, re-fetch with --force, re-run the corpus, and commit the new SHA
together with the numbers it produced. The two belong in one diff.
"""
import argparse
import io
import json
import os
import shutil
import tarfile
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORPUS = os.path.join(REPO, 'corpus')

OWNER = 'heygen-com'
NAME = 'hyperframes'

#: The commit this repository's numbers describe. See the note above before changing it.
PIN = 'c9b3d9c9628d4c51696147ecd2fd881080e72824'

#: When it was pinned, so a reader knows how stale the numbers may be without asking GitHub.
PINNED_ON = '2026-09-27'

KEEP = '/registry/'


def source(ref):
    return f'https://codeload.github.com/{OWNER}/{NAME}/tar.gz/{ref}'


def upstream_head():
    """The commit `main` points at now. Used by CI to notice the pin has aged."""
    url = f'https://api.github.com/repos/{OWNER}/{NAME}/commits/main'
    request = urllib.request.Request(url, headers={'Accept': 'application/vnd.github+json'})

    with urllib.request.urlopen(request) as response:
        return json.load(response)['sha']


def fetch(ref=None, force=False):
    ref = ref or PIN

    if os.path.isdir(CORPUS) and os.listdir(CORPUS) and not force:
        print('corpus/ already populated - pass --force to replace it')
        return

    print(f'fetching {source(ref)}')
    with urllib.request.urlopen(source(ref)) as response:
        blob = response.read()
    print(f'  {len(blob) / 1e6:.0f} MB')

    if os.path.isdir(CORPUS):
        shutil.rmtree(CORPUS)
    os.makedirs(CORPUS, exist_ok=True)

    kept = 0
    with tarfile.open(fileobj=io.BytesIO(blob), mode='r:gz') as tar:
        for member in tar:
            if not member.isfile() or KEEP not in member.name:
                continue

            # Strip the leading hyperframes-<ref>/ so paths are registry/... here. The prefix
            # carries the ref, so it is a commit hash for a pinned fetch and the branch name for
            # a --ref one; splitting on the first slash does not care which.
            parts = member.name.split('/', 1)
            if len(parts) < 2:
                continue

            target = os.path.join(CORPUS, parts[1].replace('/', os.sep))
            if not os.path.abspath(target).startswith(os.path.abspath(CORPUS)):
                continue        # a tar entry has no business escaping the directory

            os.makedirs(os.path.dirname(target), exist_ok=True)
            extracted = tar.extractfile(member)
            if extracted is None:
                continue

            with open(target, 'wb') as f:
                shutil.copyfileobj(extracted, f)
            kept += 1

    # Written beside the corpus, not into it: corpus/ is ignored, and what matters here is that a
    # run can say which fetch it measured without anybody remembering.
    with open(os.path.join(CORPUS, 'PIN'), 'w', encoding='utf-8') as f:
        f.write(ref + '\n')

    print(f'kept {kept} files under corpus/registry/, at {ref[:12]}')
    print('run: python tools/survey.py')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--force', action='store_true', help='replace an existing corpus/')
    p.add_argument('--ref', help='a commit, tag or branch to fetch instead of the pin')
    p.add_argument('--print-pin', action='store_true', help='print the pinned commit and exit')
    p.add_argument('--check', action='store_true',
                   help='compare the pin against upstream main; exit 1 if they differ')

    args = p.parse_args()

    if args.print_pin:
        print(PIN)
    elif args.check:
        head = upstream_head()
        if head == PIN:
            print(f'pin is current: {PIN[:12]}')
        else:
            print(f'pin {PIN[:12]} (set {PINNED_ON}) is behind upstream main {head[:12]}')
            raise SystemExit(1)
    else:
        fetch(ref=args.ref, force=args.force)
