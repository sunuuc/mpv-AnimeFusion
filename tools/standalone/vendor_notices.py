"""Recover vendor notices omitted by the historical component repack.
Use the original TensorRT 11.1 / CUDA 13.3 vendor distributions, never replace
runtime binaries. Later builds reuse these notices from our full runtime seed.
"""
from pathlib import Path
import hashlib,json,shutil,urllib.request,zipfile
TRT='https://developer.nvidia.com/downloads/compute/machine-learning/tensorrt/11.1.0/zip/TensorRT-Enterprise-11.1.0.106-Windows-amd64-cuda-13.3-Release-external.zip'
CUDA='https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.3.29-archive.zip'
CUDA_SHA='1feb7dd266813ffe8dbc24e115183a5ac35a4795c8d34aca0df85ab616b64d9c'
SLA='https://docs.nvidia.com/deeplearning/tensorrt/latest/reference/sla.html'
def digest_stream(f):return hashlib.file_digest(f,'sha256').hexdigest()
def digest(path):
    with path.open('rb') as f:return digest_stream(f)
def collect(app,evidence):
    target=app/'animejanai/inference';record=target/'vendor-license-sources.json'
    if record.exists():
        old=json.loads(record.read_text(encoding='utf-8'))
        if old.get('notice_files') and all((target/n).is_file() and digest(target/n)==h for n,h in old['notice_files'].items()):return
    proof={'original_runtime':'TensorRT 11.1.0.106 / CUDA 13.3','downloads':[],'notice_files':{}}
    def save_proof():
        (evidence/'vendor-notices.json').write_text(json.dumps(proof,ensure_ascii=False,indent=2),encoding='utf-8')
    def fetch(url,path):
        request=urllib.request.Request(url,headers={'User-Agent':'mpv-AnimeFusion-license-preservation'})
        with urllib.request.urlopen(request,timeout=120) as source,path.open('wb') as out:
            final=source.url;shutil.copyfileobj(source,out,1024*1024)
        proof['downloads'].append({'url':url,'final_url':final,'sha256':digest(path),'bytes':path.stat().st_size})
        save_proof()
    for vendor,url,check,runtime in [('TensorRT',TRT,None,'nvinfer_11.dll'),('CUDA',CUDA,CUDA_SHA,'cudart64_13.dll')]:
        archive=evidence/(vendor+'-original.zip');fetch(url,archive)
        if check and digest(archive)!=check:raise RuntimeError('Vendor archive hash mismatch: '+vendor)
        with zipfile.ZipFile(archive) as z:
            names=z.namelist()
            bins=[n for n in names if Path(n).name.lower()==runtime.lower()]
            if len(bins)!=1:raise RuntimeError('Cannot identify matching vendor runtime '+runtime)
            with z.open(bins[0]) as f:original_hash=digest_stream(f)
            if original_hash!=digest(target/runtime):raise RuntimeError('Notice source does not match the packaged runtime: '+runtime)
            proof[vendor+'_runtime_sha256']=original_hash
            notices=[n for n in names if not n.endswith('/') and
                any(word in Path(n).name.lower() for word in ('license','acknowledg','eula','notice')) and
                Path(n).suffix.lower() in ('','.txt','.md','.html','.htm')]
            proof[vendor+'_notice_entries']=notices
            for n in notices:
                if z.getinfo(n).file_size>5*1024*1024:raise RuntimeError('Unexpectedly large notice '+n)
                dest=target/(vendor+'_LICENSE_'+hashlib.sha256(n.encode()).hexdigest()[:10]+'_'+Path(n).name)
                dest.write_bytes(z.read(n));proof['notice_files'][dest.name]=digest(dest)
            if vendor=='CUDA' and not notices:raise RuntimeError('CUDA distribution lacks license text')
        archive.unlink();save_proof()
    sla=target/'TensorRT_LICENSE.html';fetch(SLA,sla)
    text=sla.read_text(encoding='utf-8')
    required=['tensorrt','license agreement','distribution','software development kits']
    if not all(s in text.lower() for s in required):raise RuntimeError('Invalid TensorRT SLA response')
    proof['notice_files'][sla.name]=digest(sla)
    record.write_text(json.dumps(proof,ensure_ascii=False,indent=2),encoding='utf-8');save_proof()
    print('Preserved original vendor notices; packaged inference binaries unchanged',flush=True)
