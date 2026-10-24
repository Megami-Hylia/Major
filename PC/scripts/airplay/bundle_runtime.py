from pathlib import Path
import hashlib, json, re, shutil, subprocess, os

root=Path(os.environ.get('MAJOR_AIRPLAY_WORK', Path(__file__).resolve().parent)).resolve()
project=Path(os.environ.get('MAJOR_AIRPLAY_PROJECT', root.parents[1]/'outputs'/'Major')).resolve()
prefix=root/'toolchain'/'prefix'/'ucrt64'
runtime=project/'tools'/'airplay'
notices=project/'third-party'/'airplay'
objdump=prefix/'bin'/'objdump.exe'
inventory=json.loads((root/'package-file-inventory.json').read_text())
metadata={x['archive']:x for x in json.loads((root/'package-metadata.json').read_text())}
for item in metadata.values():
    parsed={}
    for line in item['pkginfo'].splitlines():
        if ' = ' in line:
            key,value=line.split(' = ',1)
            parsed.setdefault(key,[]).append(value)
    item['parsed']=parsed

plugins=['app','coreelements','audioconvert','audioparsers','audioresample','autodetect',
         'd3d11','level','libav','playback','typefindfunctions','videoconvertscale',
         'videofilter','videoparsersbad','volume','wasapi']
seeds=[(root/'build-uxplay'/'uxplay.exe',Path('uxplay.exe')),
       (prefix/'bin'/'gst-inspect-1.0.exe',Path('bin/gst-inspect-1.0.exe')),
       (prefix/'bin'/'gst-launch-1.0.exe',Path('bin/gst-launch-1.0.exe')),
       (prefix/'libexec'/'gstreamer-1.0'/'gst-plugin-scanner.exe',Path('libexec/gstreamer-1.0/gst-plugin-scanner.exe'))]
seeds += [(prefix/'lib'/'gstreamer-1.0'/f'libgst{x}.dll',Path('lib/gstreamer-1.0')/f'libgst{x}.dll') for x in plugins]
queue=list(seeds)
copied={}
used_packages=set()
custom={x.name.lower():x for x in (root/'ffmpeg-prefix'/'bin').glob('*.dll')}
if len(custom)<5:
    raise RuntimeError('Minimal LGPL FFmpeg build not complete')
native={x.name.lower():x for x in (prefix/'bin').glob('*.dll')}
system=Path('C:/Windows/System32')
unresolved=[]
while queue:
    source,destination=queue.pop(0)
    key=destination.as_posix().lower()
    if key in copied: continue
    output=runtime/destination
    output.parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(source,output)
    copied[key]=(destination.as_posix(),source)
    if source.is_relative_to(prefix):
        package=inventory.get('ucrt64/'+source.relative_to(prefix).as_posix())
        if package: used_packages.add(package)
    report=subprocess.run([str(objdump),'-p',str(source)],check=True,capture_output=True,text=True).stdout
    for library in re.findall(r'DLL Name:\s*(\S+)',report):
        lower=library.lower()
        if lower.startswith(('api-ms-','ext-ms-')) or (system/library).exists():
            continue
        dependency=custom.get(lower) or native.get(lower)
        if dependency:
            queue.append((dependency,Path('bin')/dependency.name))
        else:
            unresolved.append({'source':source.name,'dependency':library})
if unresolved:
    raise RuntimeError(unresolved)
records=[]
for path,source in sorted(copied.values()):
    output=runtime/path
    records.append({'path':path,'sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'bytes':output.stat().st_size})
notices.mkdir(parents=True,exist_ok=True)
(notices/'runtime-files.sha256.json').write_text(json.dumps(records,indent=2)+'\n')
selected=[]
for package in sorted(used_packages):
    item=metadata[package]
    pkgbase=item['parsed']['pkgbase'][0]
    version=item['parsed']['pkgver'][0]
    selected.append({'archive':package,'pkgbase':pkgbase,'version':version,
                     'sourceUrl':f'https://repo.msys2.org/mingw/sources/{pkgbase}-{version}.src.tar.zst',
                     'license':item['parsed'].get('license',[]),'pkginfo':item['pkginfo']})
    real_name=item['parsed']['pkgname'][0].removeprefix('mingw-w64-ucrt-x86_64-')
    for name in (real_name,pkgbase.removeprefix('mingw-w64-')):
        license_dir=prefix/'share'/'licenses'/name
        if license_dir.is_dir():
            shutil.copytree(license_dir,notices/'licenses'/name,dirs_exist_ok=True)
(notices/'runtime-packages.json').write_text(json.dumps(selected,indent=2)+'\n')
print(f'Runtime: {len(records)} files, {sum(x["bytes"] for x in records)} bytes; {len(selected)} official package sources required')
print('\n'.join(x['sourceUrl'] for x in selected))
