"""Isolated NVIDIA regression for cold RIFE activation and native slot changes.

Requires local TensorRT inference files, the Sharp1 Performance ONNX model and
its cached 1280x538 engine, RIFE v4.26 ONNX, and ffmpeg. No downloads, desktop,
user configuration, or user media are used. Run explicitly, outside CPU suites.
"""
from pathlib import Path
import argparse
import json
import os
import shutil
import subprocess
import sys
import zlib

ROOT = Path(__file__).resolve().parents[1]
MODEL = '2x_AnimeJaNai_HD_V3.1Sharp1_Performance_SPANF3_b5f48_unshuffle_fp16'
RIFE = 'rife_v4.26'
CONFIG = f'''[global]
config_version=3
backend=TensorRT
backend_auto_fallback=no
logging=no
default_slot=1

[slot_1]
profile_name=Native AI activation regression
chain_1_min_resolution=0x0
chain_1_max_resolution=1920x1080
chain_1_min_fps=0
chain_1_max_fps=30
chain_1_model_1_name={MODEL}
chain_1_model_1_resize_height_before_upscale=720
chain_1_model_1_resize_factor_before_upscale=100
chain_1_rife=yes
chain_1_rife_model=426
chain_1_rife_factor_numerator=2
chain_1_rife_factor_denominator=1
chain_1_rife_scene_detect_threshold=0.150
chain_1_rife_ensemble=no
chain_1_rife_before_upscale=yes
'''

VERIFY = r'''
local mp = require 'mp'
local utils = require 'mp.utils'
local settings = utils.parse_json(SETTINGS_JSON)
local started = mp.get_time()
local phase, stable, previous = 'initial', 0, nil
local samples, failures, completed = {}, {}, {}
local rife_builds, upscale_builds, seeks = 0, 0, 0
local finished = false
local timer
local function finish(reason)
    if finished then return end
    finished = true
    if timer then timer:kill() end
    if reason then failures[#failures+1] = {prefix='test',text=reason} end
    local result = {case=settings.case,passed=#failures==0,completed=completed,
        failures=failures,samples=samples,rife_builds=rife_builds,
        upscale_builds=upscale_builds,seeks=seeks}
    local file = assert(io.open(settings.report,'wb'))
    file:write(utils.format_json(result));file:close()
    print('RESULT '..utils.format_json(result))
    mp.commandv('quit',result.passed and 0 or 1)
end
mp.enable_messages('v')
mp.register_event('log-message', function(event)
    local text = event.text or ''
    if event.prefix == 'animejanai' then
        if event.level == 'error' or event.level == 'fatal' then
            failures[#failures+1] = {prefix=event.prefix,text=text}
        end
        if text:find('background engine build:',1,true) then
            if text:find('rife_v4.26.onnx',1,true) then rife_builds=rife_builds+1
            else upscale_builds=upscale_builds+1 end
        end
    elseif event.prefix == 'vf' and text:find('Disabling filter aji',1,true) then
        failures[#failures+1] = {prefix=event.prefix,text=text}
    end
    -- vo=null may try an unavailable display upload before selecting software
    -- download. Only AI failures and the measured output determine this test.
end)
mp.register_event('seek',function() seeks=seeks+1 end)
mp.register_event('end-file',function()
    if not finished then finish('Playback ended before all activation phases completed') end
end)
local function switch(slot,next_phase)
    local ok,why=mp.commandv('vf-command','aji','slot',tostring(slot))
    if not ok then finish('Native slot command failed: '..tostring(why));return end
    phase,stable=next_phase,0
end
timer=mp.add_periodic_timer(1,function()
    if #failures>0 then finish();return end
    local now=mp.get_time()
    local count=mp.get_property_number('vo-presented-frame-count')
    local params=mp.get_property_native('video-out-params') or {}
    local position=mp.get_property_number('time-pos')
    local sample={elapsed=now-started,phase=phase,count=count,position=position,
        fps=mp.get_property_number('estimated-vf-fps'),w=params.w,h=params.h,
        paused=mp.get_property_bool('pause')}
    if previous and count and previous.count then
        sample.rate=(count-previous.count)/(now-previous.time)
        if position and previous.position and position<previous.position-0.25 then
            finish('Slot change moved playback backwards');return
        end
    end
    previous={time=now,count=count,position=position}
    samples[#samples+1]=sample
    print('SAMPLE '..utils.format_json(sample))
    if seeks>0 then finish('Slot changes unexpectedly sought the video');return end
    if upscale_builds>0 then finish('Cold prerequisite failed: the upscale engine was not cached');return end
    if settings.case=='warm' and rife_builds>0 then finish('Warm run rebuilt its RIFE engine');return end
    local fps=phase=='off' and 24 or 48
    local w,h=phase=='off' and 1920 or 2560,phase=='off' and 808 or 1076
    local enabled=false
    for _,filter in ipairs(mp.get_property_native('vf',{})) do
        if filter.name=='animejanai' and filter.label=='aji' and filter.enabled~=false then enabled=true end
    end
    local matches=enabled and not sample.paused and position and sample.rate and
        math.abs(sample.rate-fps)<4 and sample.fps and math.abs(sample.fps-fps)<1 and
        sample.w==w and sample.h==h
    stable=matches and stable+1 or 0
    if stable>=3 then
        completed[#completed+1]={phase=phase,sample=sample}
        if phase=='initial' then
            if settings.case=='cold' and rife_builds==0 then
                finish('Cold run did not exercise the missing RIFE engine');return
            end
            switch(0,'off')
        elseif phase=='off' then switch(1,'restored')
        else finish() end
    elseif now-started>settings.timeout then
        finish('Timed out waiting for stable output in phase '..phase)
    end
end)
'''


def quote(value):
    """mpv length-quoted option values also allow commas and Unicode paths."""
    value = str(value).replace('\\', '/')
    return '%' + str(len(value.encode('utf-8'))) + '%' + value


def copy_models(source, destination, model, engines):
    destination.mkdir()
    shutil.copy2(source / (model + '.onnx'), destination)
    timing = source / (model + '.timing.cache')
    if timing.is_file():
        shutil.copy2(timing, destination)
    copied = []
    if engines:
        prefix = f'aji-{zlib.crc32(model.encode("utf-8")):08x}.'
        for path in sorted(source.glob('*.engine')):
            if path.name.startswith((prefix, model + '.')):
                shutil.copy2(path, destination)
                copied.append(path.name)
        if not copied:
            raise RuntimeError('Supply a cached Sharp1 Performance 1280x538 engine for this GPU')
    return copied


def run_case(args, name, video, config, models, rife, environment):
    case = args.work / name
    case.mkdir()
    report = case / 'result.json'
    settings = json.dumps({'case': name, 'report': report.as_posix(), 'timeout': args.timeout},
                          ensure_ascii=False)
    script = case / 'verify.lua'
    script.write_text(VERIFY.replace('SETTINGS_JSON', json.dumps(settings, ensure_ascii=False)),
                      encoding='utf-8')
    options = {'lib': args.inference_dir / 'aji.dll', 'conf': config,
               'model-dir': models, 'rife-model-dir': rife,
               'trtexec': args.inference_dir / 'trtexec.exe',
               'trtexec-libdir': args.inference_dir, 'stats': case / 'stats.log', 'slot': 1}
    vf = '@aji:animejanai=' + ':'.join(key + '=' + quote(value) for key, value in options.items())
    command = [str(args.native_output / 'mpv.exe'), '--no-config', '--load-scripts=no',
               '--idle=yes', '--vo=null', '--ao=null', '--hwdec=nvdec', '--osc=no',
               '--force-window=no', '--terminal=yes', '--msg-level=all=v',
               '--save-position-on-quit=no', '--resume-playback=no', '--save-watch-history=no',
               '--vf=' + vf, '--script=' + str(script),
               '--script=' + str(ROOT / 'portable_config/scripts/animejanai_engine_monitor.lua'),
               '--script-opts=animejanai_engine_monitor-stats_path=' + quote(case / 'stats.log'),
               str(video)]
    with (case / 'player.log').open('wb') as log:
        process = subprocess.Popen(command, cwd=case, env=environment, stdout=log,
                                   stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            code = process.wait(args.timeout + 30)
        except subprocess.TimeoutExpired:
            # End only this test's process tree, including its isolated builder.
            subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                           stdout=log, stderr=subprocess.STDOUT, check=False)
            process.wait(15)
            raise RuntimeError(name + ' native process timed out; see ' + str(case / 'player.log'))
    if not report.is_file():
        raise RuntimeError(name + ' did not produce a report; see ' + str(case / 'player.log'))
    result = json.loads(report.read_text(encoding='utf-8'))
    if code or not result['passed'] or [item['phase'] for item in result['completed']] != ['initial', 'off', 'restored']:
        raise RuntimeError(name + ' activation failed; see ' + str(report))
    print('PASS', name, '48fps 2560x1076 -> slot 0 24fps 1920x808 -> slot 1 48fps 2560x1076', flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('native_output', 'inference_dir', 'model_dir', 'rife_dir', 'work'):
        parser.add_argument(name, type=Path)
    parser.add_argument('--ffmpeg', default='ffmpeg')
    parser.add_argument('--timeout', type=int, default=300, help='Maximum wall-clock seconds per native run')
    args = parser.parse_args()
    if os.name != 'nt':
        parser.error('This regression requires the Windows NVIDIA native runtime')
    for name in ('native_output', 'inference_dir', 'model_dir', 'rife_dir', 'work'):
        setattr(args, name, getattr(args, name).resolve())
    if args.timeout < 30:
        parser.error('--timeout must be at least 30 seconds')
    for source in (args.native_output, args.inference_dir, args.model_dir, args.rife_dir):
        if args.work == source or source in args.work.parents:
            parser.error('Choose a work directory outside the supplied runtime and model directories')
    required = [args.native_output / 'mpv.exe', args.native_output / 'libmpv-2.dll',
                args.inference_dir / 'aji.dll', args.inference_dir / 'trtexec.exe',
                args.model_dir / (MODEL + '.onnx'), args.rife_dir / (RIFE + '.onnx')]
    for path in required:
        if not path.is_file():
            parser.error('Required local file is missing: ' + str(path))
    if not shutil.which(args.ffmpeg):
        parser.error('ffmpeg is unavailable; supply its local executable with --ffmpeg')
    args.work.mkdir(parents=True, exist_ok=False)
    models, rife = args.work / 'onnx', args.work / 'rife'
    copied = copy_models(args.model_dir, models, MODEL, engines=True)
    copy_models(args.rife_dir, rife, RIFE, engines=False)
    assert not list(rife.glob('*.engine')), 'Cold RIFE directory must start without any engines'
    config = args.work / 'animejanai.conf'
    config.write_text(CONFIG, encoding='utf-8')
    environment = os.environ.copy()
    for key, folder in (('LOCALAPPDATA', 'localdata'), ('APPDATA', 'roaming'), ('TEMP', 'temp'), ('TMP', 'temp')):
        path = args.work / folder
        path.mkdir(exist_ok=True)
        environment[key] = str(path)
    environment['PATH'] = str(args.inference_dir) + os.pathsep + environment.get('PATH', '')
    video = args.work / 'generated-1920x808-24fps.mp4'
    with (args.work / 'ffmpeg.log').open('wb') as log:
        subprocess.run([args.ffmpeg, '-hide_banner', '-loglevel', 'error', '-f', 'lavfi',
                        '-i', 'testsrc2=size=1920x808:rate=24', '-t', '120', '-c:v', 'libx264',
                        '-preset', 'ultrafast', '-crf', '32', '-pix_fmt', 'yuv420p', str(video)],
                       env=environment, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=120)
    results = []
    try:
        results.append(run_case(args, 'cold', video, config, models, rife, environment))
        if not list(rife.glob('*.engine')):
            raise RuntimeError('Cold run did not persist its new RIFE engine')
        results.append(run_case(args, 'warm', video, config, models, rife, environment))
    finally:
        (args.work / 'results.json').write_text(json.dumps({
            'passed': len(results) == 2 and all(item['passed'] for item in results),
            'upscale_engine_inputs': copied, 'cases': results,
            'desktop_interaction': False, 'network': False, 'synthetic_video': True,
        }, ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        print('FAIL native AI activation:', error, file=sys.stderr)
        sys.exit(1)
