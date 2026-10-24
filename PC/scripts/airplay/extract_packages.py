from pathlib import Path
import tarfile, shutil, json, os

root = Path(os.environ.get('MAJOR_AIRPLAY_WORK', Path(__file__).resolve().parent)).resolve()
prefix = root/'toolchain'/'prefix'
inventory = {}
metadata = []
links = []
for package in sorted((root/'toolchain'/'packages').glob('*.pkg.tar.zst')):
    with tarfile.open(package, 'r:*') as archive:
        info = archive.extractfile('.PKGINFO').read().decode()
        metadata.append({'archive': package.name, 'pkginfo': info})
        for member in archive:
            if member.name.startswith('.'):
                continue
            destination = prefix/member.name
            if not destination.resolve().is_relative_to(prefix.resolve()):
                raise ValueError(member.name)
            if member.isfile():
                destination.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(member) as source, destination.open('wb') as target:
                    shutil.copyfileobj(source, target)
                inventory[member.name] = package.name
            elif member.issym():
                links.append((destination, destination.parent/member.linkname))
            elif member.islnk():
                links.append((destination, prefix/member.linkname))
for destination, source in links:
    if source.is_file() and source.resolve().is_relative_to(prefix.resolve()):
        if destination.exists():
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
(root/'package-file-inventory.json').write_text(json.dumps(inventory, indent=2))
(root/'package-metadata.json').write_text(json.dumps(metadata, indent=2))
print(f'Extracted {len(metadata)} packages, {len(inventory)} regular files, copied available symlinks')
