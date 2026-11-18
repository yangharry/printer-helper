"""Copies the license and notice files that the bundled components ask redistributors to pass on into one folder of
a release: licenses\\ in the Windows zip, PrinterHelper.app/Contents/Resources/licenses/ in the Mac app.

    python3 packaging/copy-notices.py <rid> <destDir> [project.assets.json]

What goes in:
- pdfium/: packaging/licenses/pdfium. The bblanchon.PDFium packages carry no license files, so the texts of PDFium
  and of the code built into it are kept in this repository.
- <PackageId>/: the LICENSE, NOTICE and THIRD-PARTY-NOTICES files at the root of every restored NuGet package that
  puts a file into the <rid> publish (SkiaSharp and HarfBuzzSharp carry the notices of Skia, HarfBuzz, libpng,
  libjpeg-turbo, FreeType and the rest of their native libraries), and of the .NET runtime pack
  Microsoft.NETCore.App.Runtime.<rid>. The files are copied unchanged.
- README.txt: which package each folder came from.

It reads the restore output of the app (src/PrinterHelper/obj/project.assets.json), so run it after dotnet publish.
"""

import json
import os
import re
import shutil
import sys
from pathlib import Path

NOTICE_FILE = re.compile(r'^(licen[cs]e|notice|third-party-notices|copying)(\.(txt|md))?$', re.IGNORECASE)
# Packages whose notices must be found; a missing one means the restore changed and the release would lack them.
REQUIRED = ('SkiaSharp', 'HarfBuzzSharp', 'Microsoft.NETCore.App.Runtime.')


def dotnet_packs():
    root = os.environ.get('DOTNET_ROOT')
    if not root:
        dotnet = shutil.which('dotnet')
        root = os.path.dirname(os.path.realpath(dotnet)) if dotnet else None
    return Path(root, 'packs') if root else None


def main():
    if len(sys.argv) not in (3, 4):
        sys.exit('usage: copy-notices.py <rid> <destDir> [project.assets.json]')
    rid, dest = sys.argv[1], Path(sys.argv[2])
    repo = Path(__file__).resolve().parent.parent
    assets_path = Path(sys.argv[3]) if len(sys.argv) == 4 else repo / 'src' / 'PrinterHelper' / 'obj' / 'project.assets.json'
    if dest.exists() and any(dest.iterdir()):
        sys.exit(f'{dest} is not empty')
    if not assets_path.is_file():
        sys.exit(f'{assets_path} not found: publish src/PrinterHelper first')
    assets = json.loads(assets_path.read_text(encoding='utf-8'))
    folders = [Path(f) for f in assets['packageFolders']]

    target = next((t for name, t in assets['targets'].items() if name.endswith('/' + rid)), None)
    if target is None:
        sys.exit(f'{assets_path} has no {rid} target: publish with -r {rid} first')

    # package id -> (version, candidate folders): packages that put a runtime or native file into the publish.
    packages = {}
    for key, library in target.items():
        if library.get('type') != 'package':
            continue
        files = [f for kind in ('runtime', 'native') for f in library.get(kind, {})]
        if any(not f.endswith('/_._') for f in files):
            name, version = key.split('/', 1)
            relative = assets['libraries'][key]['path']
            packages[name] = (version, [folder / relative for folder in folders])

    # The runtime pack of a self-contained publish is downloaded, not referenced, so it is not in the targets.
    runtime = 'Microsoft.NETCore.App.Runtime.' + rid
    for framework in assets['project']['frameworks'].values():
        for download in framework.get('downloadDependencies', []):
            if download['name'].lower() == runtime.lower():
                version = download['version'].strip('[]').split(',')[0].strip()
                candidates = [folder / runtime.lower() / version for folder in folders]
                packs = dotnet_packs()
                if packs:
                    candidates.append(packs / runtime / version)
                packages[runtime] = (version, candidates)
    if runtime not in packages:
        sys.exit(f'{assets_path} does not download {runtime}: publish with -r {rid} --self-contained')

    dest.mkdir(parents=True, exist_ok=True)
    shutil.copytree(repo / 'packaging' / 'licenses' / 'pdfium', dest / 'pdfium')
    index = [
        f'License and notice files of the components in this build of PrinterHelper ({rid}).',
        'THIRD-PARTY-NOTICES.md, next to this folder, lists every component and its license.',
        '',
        'pdfium/: PDFium and the third-party code built into it (pdfium/README.txt names the source of each file).',
        '',
        'The other folders hold the files of the NuGet package they are named after, unchanged:',
    ]
    found = set()
    for name in sorted(packages, key=str.lower):
        version, candidates = packages[name]
        root = next((c for c in candidates if c.is_dir()), None)
        if root is None:
            sys.exit(f'{name} {version} not found in ' + ', '.join(str(c) for c in candidates))
        notices = sorted((p for p in root.iterdir() if p.is_file() and NOTICE_FILE.match(p.name)), key=lambda p: p.name.lower())
        for notice in notices:
            (dest / name).mkdir(exist_ok=True)
            shutil.copyfile(notice, dest / name / notice.name)
            if notice.name.lower().startswith('third-party-notices'):
                found.update(r for r in REQUIRED if name.startswith(r))
        if notices:
            listed = ', '.join(f'{name}/{n.name}' for n in notices)
        elif name.lower().startswith('bblanchon.pdfium'):
            listed = 'no license file in the package; see pdfium/'
        else:
            listed = 'no license file in the package; its license text is in THIRD-PARTY-NOTICES.md'
        index.append(f'- {name} {version}: {listed}')
        print(f'{name} {version}: {listed}')

    missing = [r for r in REQUIRED if r not in found]
    if missing:
        sys.exit('no THIRD-PARTY-NOTICES file found for ' + ', '.join(m.rstrip('.') for m in missing))
    with open(dest / 'README.txt', 'w', encoding='utf-8', newline='\n') as readme:  # Python 3.9 (Xcode's) too
        readme.write('\n'.join(index) + '\n')
    print(f'copied the notices to {dest}')


if __name__ == '__main__':
    main()
