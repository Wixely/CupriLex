"""Pull the HyperFrames registry into corpus/.

    python tools/fetch-corpus.py [--force]

Deliberately NOT vendored. The corpus is 187 blocks of somebody else's work under Apache 2.0, it
changes, and a snapshot committed here would age silently while the survey numbers in the docs went
on claiming to describe it. Fetching on demand means `tools/survey.py` always describes what is
actually there, and a number that moves is a signal rather than a surprise.

One tarball, not 400 requests: the whole repository is ~110 MB compressed and only registry/ is
kept.
"""
import argparse
import io
import os
import shutil
import sys
import tarfile
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORPUS = os.path.join(REPO, 'corpus')
SOURCE = 'https://codeload.github.com/heygen-com/hyperframes/tar.gz/refs/heads/main'
KEEP = '/registry/'


def fetch(force=False):
    if os.path.isdir(CORPUS) and os.listdir(CORPUS) and not force:
        print(f'corpus/ already populated - pass --force to replace it')
        return

    print(f'fetching {SOURCE}')
    with urllib.request.urlopen(SOURCE) as response:
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

            # Strip the leading hyperframes-main/ so paths are registry/... here.
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

    print(f'kept {kept} files under corpus/registry/')
    print('run: python tools/survey.py')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--force', action='store_true', help='replace an existing corpus/')
    fetch(**vars(p.parse_args()))
