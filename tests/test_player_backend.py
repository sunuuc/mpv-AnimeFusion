"""Exercise the startup script with Windows mpv and an isolated component tree."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile


def verify(install, script, native):
    catalog_path = install / "app/build-info/standalone/components.json"
    catalog = json.loads(catalog_path.read_bytes())
    packs = {p["name"]: p for p in catalog["packs"]}
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        config_dir = root / "portable_config"
        config_dir.mkdir()
        config = root / "animejanai/animejanai.conf"
        (config.parent / "inference").mkdir(parents=True)
        index = root / "app/build-info/standalone/components.json"
        index.parent.mkdir(parents=True)

        def materialize(pack):
            sizes = {}
            for name in pack["files"]:
                data = name.encode()
                (root / name).write_bytes(data)
                sizes[name] = len(data)
            pack["file_bytes"] = sizes
            pack["installed_bytes"] = sum(size for name, size in sizes.items()
                                          if "LICENSE" not in Path(name).name.upper())

        materialize(packs["trt-runtime"])
        materialize(packs["trt-sm86"])
        materialize(packs["trt-sm120"])
        materialize(packs["trt-ptx"])
        index.write_text(json.dumps(catalog), encoding="utf-8")
        loader = root / "probe.lua"
        loader.write_text("""
local probe = assert(io.open(mp.command_native({'expand-path','~~/../animejanai/animejanai.conf'})))
assert(probe:read('*l') == '; isolated startup probe')
probe:close()
local utils = require 'mp.utils'
local info = utils.file_info
utils.file_info = function(p)
    if p:lower():match('system32[/\\\\]nvcuda%.dll$') then return {is_file=true} end
    return info(p)
end
dofile(os.getenv('PLAYER_BACKEND_SCRIPT'))
print('rate='..mp.get_property('display-rate-match')..' hwdec='..mp.get_property('hwdec')..
      ' gpu-api='..mp.get_property('gpu-api'))
mp.commandv('quit',0)
""", encoding="utf-8")

        def run(backend, cli=None, gpu="12.0", player_conf="", arguments=(), inference_conf=""):
            config.write_text("; isolated startup probe\n[global]\nbackend=" + backend +
                              "\ndisplay_rate_match=yes\n" + inference_conf, encoding="utf-8")
            (config_dir / "mpv.conf").write_text(player_conf, encoding="utf-8")
            args = [native, "--config-dir=" + str(config_dir),
                    "--load-scripts=no", "--idle=yes", "--vo=null", "--ao=null",
                    "--osc=no", "--save-watch-history=no", "--script=" + str(loader), *arguments]
            if cli:
                args.append("--display-rate-match=" + cli)
            environment = dict(os.environ, PLAYER_BACKEND_SCRIPT=str(script),
                               ANIMEJANAI_NVIDIA_GPUS=f"GPU-test,{gpu},Test GPU")
            result = subprocess.run(args, cwd=config_dir, env=environment,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=12)
            output = result.stdout.decode("utf-8", errors="replace")
            assert result.returncode == 0 and "rate=" in output, output
            return output

        for cli, expected in [(None, "yes"), ("no", "no"), ("test", "test")]:
            assert "rate=" + expected + " hwdec=d3d11va" in run("DirectML", cli)
        assert "hwdec=no gpu-api=opengl" in run("DirectML", player_conf="hwdec=no\ngpu-api=opengl\n")
        assert "hwdec=no gpu-api=auto" in run("DirectML", arguments=["--hwdec=no", "--gpu-api=auto"])
        assert "hwdec=nvdec gpu-api=d3d11" in run("DirectML", player_conf="hwdec=nvdec\n")
        assert "hwdec=d3d11va gpu-api=auto" in run("DirectML", player_conf="gpu-api=auto\n")
        assert "缺少 RIFE" not in run("DirectML", inference_conf="default_slot=0\n[slot_2]\nchain_1_rife=yes\n")
        missing_builder = "缺少 TensorRT 构建组件"
        assert missing_builder not in run("TensorRT")
        builder = root / packs["trt-sm120"]["files"][0]
        builder.write_bytes(b"truncated")
        assert missing_builder in run("TensorRT")
        builder.unlink()
        assert missing_builder in run("TensorRT")
        assert missing_builder not in run("TensorRT", gpu="8.6")
        assert missing_builder not in run("TensorRT", gpu="13.0")
        plugin = root / "animejanai/inference/nvinfer_plugin_11.dll"
        plugin.write_bytes(b"truncated")
        assert "hwdec=d3d11va" in run("TensorRT")
        plugin.unlink()
        assert "hwdec=d3d11va" in run("TensorRT")
        assert "backend=DirectML" in config.read_text()
        loader.write_text(Path(__file__).with_name("test_rife_sync.lua").read_text(encoding="utf-8"), encoding="utf-8")
        assert "rate=verified RIFE timing and explicit overrides" in run("DirectML")
    print("PASS explicit refresh modes, incomplete runtime, architecture-specific builders and PTX")


def verify_rife(install, script, native, sample):
    slot_script = script.with_name("animejanai_slot.lua")
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        config = root / "portable_config"
        config.mkdir()
        inference = root / "animejanai"
        (inference / "rife").mkdir(parents=True)
        (inference / "onnx").mkdir()
        probe = root / "rife.lua"
        probe.write_text("""
local show = mp.osd_message
local hints = 0
mp.osd_message = function(text, duration)
    if text:find("缺少 RIFE 模型", 1, true) then hints = hints + 1 end
    return show(text, duration)
end
dofile(os.getenv('PLAYER_BACKEND_SCRIPT'))
mp.register_event('file-loaded', function()
    if os.getenv('RIFE_SWITCH') == 'yes' then
        mp.add_timeout(0.2, function() mp.commandv('script-message', 'aji-slot', '2') end)
    end
    mp.add_timeout(4, function()
        print('rife-hints=' .. hints)
        mp.commandv('quit', 0)
    end)
end)
""", encoding="utf-8")

        def quote(path):
            value = path.as_posix()
            return f"%{len(value.encode())}%{value}"

        library = quote(install / "animejanai/inference/aji.dll")
        for slot, switch, expected in [(0, False, 0), (1, False, 0), (2, False, 1), (0, True, 1)]:
            (inference / "animejanai.conf").write_text(
                f"[global]\nbackend=DirectML\ndefault_slot={slot}\n"
                "[slot_1]\nchain_1_rife=no\nchain_1_min_resolution=0x0\n"
                "chain_2_rife=yes\nchain_2_rife_factor_numerator=2\nchain_2_rife_factor_denominator=1\n"
                "[slot_2]\nchain_1_rife=yes\nchain_1_rife_factor_numerator=2\nchain_1_rife_factor_denominator=1\n",
                encoding="utf-8")
            vf = (f"@aji:animejanai:slot={slot}:lib={library}:conf=animejanai/animejanai.conf:"
                  "model-dir=animejanai/onnx:rife-model-dir=animejanai/rife")
            env = dict(os.environ, LOCALAPPDATA=directory, APPDATA=directory,
                       PLAYER_BACKEND_SCRIPT=str(script), RIFE_SWITCH="yes" if switch else "no")
            args = [native, "--no-config", "--config-dir=" + str(config), "--load-scripts=no",
                    "--vo=null", "--ao=null", "--no-force-window", "--hwdec=d3d11va", "--osc=no",
                    "--save-watch-history=no", "--resume-playback=no", "--keep-open=always",
                    "--osd-duration=20", "--script=" + str(slot_script), "--script=" + str(probe),
                    "--vf=" + vf, str(sample)]
            result = subprocess.run(args, cwd=root, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
            output = result.stdout.decode("utf-8", errors="replace")
            assert result.returncode == 0 and f"rife-hints={expected}" in output, (slot, switch, output)
            assert ("RIFE models are missing for slot 2" in output) == bool(expected), (slot, switch, output)
    print("PASS RIFE warnings for bypass, inactive chains, active chains and manual slot switches")


def verify_frontend(install, script, sample):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        config_dir = root / "portable_config"
        config_dir.mkdir()
        (root / "animejanai").mkdir()
        (root / "animejanai/animejanai.conf").write_text(
            "[global]\nbackend=DirectML\ndisplay_rate_match=yes\n", encoding="utf-8")
        result = root / "observed.txt"
        observer = root / "observer.lua"
        observer.write_text("""
mp.register_event('file-loaded', function()
    local file = assert(io.open(os.getenv('PLAYER_TEST_RESULT'), 'wb'))
    file:write(mp.get_property('display-rate-match'), ' ', mp.get_property('hwdec'), ' ',
               (mp.get_property_native('script-opts') or {})['caller-value'] or 'missing')
    file:close()
end)
""", encoding="utf-8")
        environment = dict(os.environ, PLAYER_TEST_RESULT=str(result),
                           LOCALAPPDATA=str(root), APPDATA=str(root))
        for mode in ("no", "test", None):
            result.unlink(missing_ok=True)
            args = [install / "mpv-AnimeFusion.exe", "--process-instance=multi",
                    "--config-dir=" + str(config_dir), "--load-scripts=no",
                    "--scripts=" + os.pathsep.join(map(str, [script, observer])),
                    "--script-opts=caller-value=retained", "--save-watch-history=no",
                    "--audio=no", "--ovc=libx264", "--o=" + str(root / "result.mkv")]
            if mode:
                args.append("--display-rate-match=" + mode)
            subprocess.run([*args, sample], cwd=root, env=environment, check=True, timeout=30)
            assert result.read_text() == (mode or "yes") + " d3d11va retained"
    print("PASS embedded player refresh modes, caller script options and encoder shutdown")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("install", type=Path)
    parser.add_argument("--script", type=Path)
    parser.add_argument("--native", type=Path, help="staged mpv.exe with configuration provenance support")
    parser.add_argument("--rife-sample", type=Path, help="local hardware-decodable clip for RIFE selection")
    parser.add_argument("--sample", type=Path, help="local test clip for the embedded player")
    args = parser.parse_args()
    install = args.install.resolve()
    script = (args.script or install / "portable_config/scripts/animejanai_backend.lua").resolve()
    native = (args.native or install / "app/mpv.exe").resolve()
    verify(install, script, native)
    if args.rife_sample:
        verify_rife(install, script, native, args.rife_sample.resolve(strict=True))
    if args.sample:
        verify_frontend(install, script, args.sample.resolve(strict=True))
