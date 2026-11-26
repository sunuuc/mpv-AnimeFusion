-- Aligns mpv's hardware decoding AND render API with the configured
-- inference backend.
--
-- The native filter consumes the decoder's GPU frames directly, and the
-- frame type must match the backend selected in animejanai.conf:
--   TensorRT  -> CUDA frames  (hwdec=nvdec, Vulkan VO: mpv's only CUDA
--                render interop is CUDA<->Vulkan)
--   DirectML  -> D3D11 frames (hwdec=d3d11va, D3D11 VO: mpv has no
--                D3D11->Vulkan interop, so a Vulkan VO would hw-download
--                every output frame)
--   Vulkan    -> Windows: D3D11 frames too: libaji's Vulkan backend runs on
--                DirectML's D3D11/D3D12 frame pipeline (and runs models
--                without Vulkan kernels on DirectML itself).
--                Linux: Vulkan frames (hwdec=vulkan, Vulkan VO): libaji runs
--                on the decoder's own Vulkan device.
-- (backend=ncnn is retired and treated as DirectML by the shim.)
--
-- Choose defaults before the first file, including when slot 0 is active:
-- a later slot change can then use the same decoder and render device.
--
-- Also applies the opt-in subtitle rendering mode: the Manager writes
-- [global] sub_render_mode=gpu into animejanai.conf, and this script applies
-- the [subs-gpu] profile from mpv-animejanai.conf. The settings live in that
-- profile (not here) so they can change with the player build without
-- touching this script or the Manager.

local mp = require 'mp'
local msg = require 'mp.msg'
local utils = require 'mp.utils'

local conf_path = mp.command_native({
    'expand-path', '~~/../animejanai/animejanai.conf'})
local filter_slot = 0
for _, filter in ipairs(mp.get_property_native('vf', {}) or {}) do
    if filter.name == 'animejanai' and filter.label == 'aji' then
        local options = filter.params or {}
        if options.conf then conf_path = mp.command_native({'expand-path', options.conf}) end
        filter_slot = tonumber(options.slot) or 1
        break
    end
end

local route = (function()
-- inlined module: animejanai_route.lua
-- The backend and slot controllers use the same mpv GPU routing rules.
local mp = require 'mp'
local utils = require 'mp.utils'
local M = {}
local state_key = 'user-data/animejanai/backend-route'

-- Match the native INI reader's trimming and case-insensitive section and key names.
local function ini_line(line)
    line = line:gsub('^\239\187\191', '')
    local section = line:match('^%s*%[%s*(.-)%s*%]%s*$')
    if section then return section:lower() end
    local key, value = line:match('^%s*([%w_]+)%s*=%s*(.-)%s*$')
    return nil, key and key:lower(), value
end

local function read_conf(conf_path)
    local f = io.open(conf_path, 'r')
    if not f then
        return nil
    end
    local backend
    local default_slot
    local sub_render_mode
    local display_rate_match = false
    local gpus = {}
    local section
    for line in f:lines() do
        local header, key, value = ini_line(line)
        if header then
            section = header
        elseif section == 'global' and key then
            if key == 'backend' then backend = value
            elseif key == 'default_slot' then default_slot = tonumber(value)
            elseif key == 'sub_render_mode' then sub_render_mode = value
            elseif key == 'display_rate_match' then
                display_rate_match = value:lower() == 'yes' or value:lower() == 'true'
            elseif key == 'gpu_tensorrt' or key == 'gpu_directml' then
                gpus[key:sub(5)] = value ~= '' and value or nil
            end
        end
    end
    f:close()
    return backend, default_slot, sub_render_mode, display_rate_match, gpus
end

-- TensorRT needs an NVIDIA GPU with compute capability 7.5+ (GeForce RTX 20 /
-- GTX 16 series and newer): the shipped TensorRT 11 on CUDA 13 supports nothing
-- older, and aji's CUDA kernels start at sm75. The trt-ptx pack can't help an
-- older GPU (PTX only JIT-compiles forward), so such a GPU counts as no NVIDIA
-- GPU for TensorRT: DirectML on Windows. The updater (GpuSupport.cs,
-- TrtMinComputeCapability) and installer/linux/AppRun apply the same rule.
local TRT_MIN_COMPUTE = { 7, 5 }

local function trt_capable(g)
    return g.major > TRT_MIN_COMPUTE[1] or
        (g.major == TRT_MIN_COMPUTE[1] and g.minor >= TRT_MIN_COMPUTE[2])
end

-- Parses `nvidia-smi --query-gpu=uuid,compute_cap,name --format=csv,noheader`:
-- { {uuid, name, major, minor}, ... }, the uuid as the Manager writes
-- gpu_tensorrt (no "GPU-" prefix, lowercase). nil when a line can't be read
-- (a driver too old to report compute_cap): then nothing is decided from it.
local function parse_nvidia_gpus(text)
    local list = {}
    for line in (text or ''):gmatch('[^\r\n]+') do
        if line:match('%S') then
            local uuid, major, minor, name =
                line:match('^%s*([^,]-)%s*,%s*(%d+)%.(%d+)%s*,%s*(.-)%s*$')
            if not uuid then
                return nil
            end
            list[#list + 1] = {
                uuid = uuid:gsub('^[Gg][Pp][Uu]%-', ''):lower(),
                name = name,
                major = tonumber(major),
                minor = tonumber(minor),
            }
        end
    end
    return #list > 0 and list or nil
end

-- This PC's NVIDIA GPUs (parse_nvidia_gpus), from nvidia-smi, which the driver
-- installs; nil without it. Asked once, only while the backend is TensorRT.
-- ANIMEJANAI_NVIDIA_GPUS stands in for nvidia-smi's output (testing).
local nvidia_gpus_checked, nvidia_gpus_list
local function nvidia_gpus()
    if nvidia_gpus_checked then
        return nvidia_gpus_list
    end
    nvidia_gpus_checked = true
    local fake = os.getenv('ANIMEJANAI_NVIDIA_GPUS')
    if fake then
        nvidia_gpus_list = parse_nvidia_gpus(fake:gsub(';', '\n'))
        return nvidia_gpus_list
    end
    local exe = 'nvidia-smi'
    if mp.get_property('platform') == 'windows' then
        local sysroot = os.getenv('SystemRoot') or 'C:\\Windows'
        exe = sysroot .. '\\System32\\nvidia-smi.exe'
        if utils.file_info(exe) == nil then
            -- drivers before the DCH layout
            local pf = os.getenv('ProgramFiles') or 'C:\\Program Files'
            exe = pf .. '\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe'
            if utils.file_info(exe) == nil then
                return nil
            end
        end
    end
    local r = mp.command_native({
        name = 'subprocess',
        args = {exe, '--query-gpu=uuid,compute_cap,name', '--format=csv,noheader'},
        capture_stdout = true,
        capture_stderr = true,
        playback_only = false,
    })
    if r and r.status == 0 then
        nvidia_gpus_list = parse_nvidia_gpus(r.stdout)
    end
    return nvidia_gpus_list
end

-- The NVIDIA GPUs TensorRT can use and the ones too old for it, or nil when
-- that isn't known.
local function trt_gpus()
    local all = nvidia_gpus()
    if not all then
        return nil
    end
    local ok, old = {}, {}
    for _, g in ipairs(all) do
        if trt_capable(g) then
            ok[#ok + 1] = g
        else
            old[#old + 1] = g
        end
    end
    return ok, old
end

local function too_old_message(old)
    local names = {}
    for _, g in ipairs(old) do
        names[#names + 1] = string.format('%s 算力为 %d.%d', g.name, g.major, g.minor)
    end
    return string.format('TensorRT 需要算力 %d.%d 或更高的显卡；%s',
        TRT_MIN_COMPUTE[1], TRT_MIN_COMPUTE[2], table.concat(names, ', '))
end

local function explicit_option(name)
    local info = mp.get_property_native('option-info/' .. name) or {}
    local host = mp.get_property_native('script-opts') or {}
    return info['set-from-commandline'] or info['set-from-config'] or info['set-locally'] or
        host['animejanai_backend-' .. name:gsub('-', '_')] == 'explicit'
end

local function family(backend)
    backend = backend:lower()
    if backend == 'ncnn' then return 'directml' end
    if backend == 'vk' then return 'vulkan' end
    return backend
end

local function selected_device(backend, gpus)
    if backend == 'tensorrt' then return gpus.tensorrt or '' end
    return gpus.directml or ''
end

function M.apply(conf_path, backend, gpus)
    backend = family(backend)
    local previous = mp.get_property_native(state_key) or {}
    local owned = previous.owned or {}
    local windows = mp.get_property('platform') == 'windows'
    local hwdec, api = 'nvdec', 'vulkan,auto'
    if windows and (backend == 'directml' or backend == 'vulkan') then
        hwdec, api = 'd3d11va', 'd3d11'
    elseif backend == 'vulkan' then
        hwdec, api = 'vulkan', 'vulkan'
    end

    local function set_default(name, value, initial)
        if explicit_option(name) then return true end
        local current = mp.get_property(name)
        -- Preserve a custom setting changed while playback was running.
        if (owned[name] and current ~= owned[name]) or
           (not owned[name] and initial and current ~= initial) then return true end
        if current ~= value and not mp.set_property(name, value) then
            mp.msg.error('Cannot set ' .. name .. '=' .. value)
            return false
        end
        owned[name] = mp.get_property(name)
        return true
    end

    local hint
    if hwdec == 'nvdec' then
        local allowed, old = trt_gpus()
        local choice = gpus.tensorrt
        if allowed and #allowed > 0 and #old > 0 then
            for _, g in ipairs(old) do
                if choice and g.uuid == choice:lower() then
                    choice = nil
                    hint = too_old_message({g}) .. '，使用 ' .. allowed[1].name
                    break
                end
            end
            choice = choice or allowed[1].uuid
        end
        if not set_default('vulkan-device', choice or '', '') then
            set_default('vulkan-device', '', '')
        end
    elseif hwdec == 'd3d11va' then
        set_default('d3d11-adapter', gpus.directml or '', '')
        if not gpus.directml then
            set_default('d3d11-adapter-preference', 'high-performance', 'default')
        end
    end
    if not set_default('gpu-api', api) or not set_default('hwdec', hwdec) then
        return false
    end
    mp.set_property_native(state_key, {path=conf_path, backend=backend, owned=owned,
        device=selected_device(backend, gpus)})
    mp.msg.info(string.format('backend %s -> hwdec=%s, gpu-api=%s', backend,
                              mp.get_property('hwdec'), mp.get_property('gpu-api')))
    return true, hint
end

-- The manager saves its selection in the same INI the native filter reopens.
-- Only a changed backend or adapter needs a new decoder/output combination.
function M.pending()
    local previous = mp.get_property_native(state_key)
    if not previous or not previous.path then return end
    local backend, _, _, _, gpus = read_conf(previous.path)
    if not backend then return end
    backend = family(backend)
    if backend ~= previous.backend or selected_device(backend, gpus) ~= previous.device then
        return function() return M.apply(previous.path, backend, gpus) end
    end
end

M.ini_line = ini_line
M.read_conf = read_conf
M.trt_gpus = trt_gpus
M.too_old_message = too_old_message
M.explicit_option = explicit_option
return M
end)()
local ini_line = route.ini_line
local function exists(rel)
    -- the installed/writable tree (config-dir parent = install root)
    if utils.file_info(mp.command_native({'expand-path', '~~/../' .. rel})) ~= nil then
        return true
    end
    -- AppImage: the base runtime is bundled in the read-only payload, not under
    -- the writable data dir. $APPDIR (set by the AppImage runtime) is its root.
    local appdir = os.getenv('APPDIR')
    if appdir and utils.file_info(appdir .. '/' .. rel) ~= nil then
        return true
    end
    return false
end

local trt_packs
local function load_trt_packs()
    if trt_packs then return trt_packs end
    trt_packs = {}
    local path = mp.command_native({'expand-path', '~~/../app/build-info/standalone/components.json'})
    local file = io.open(path, 'rb')
    if file then
        local catalog = utils.parse_json(file:read('*a'))
        file:close()
        for _, pack in ipairs(catalog and catalog.packs or {}) do
            trt_packs[pack.name] = pack
        end
    end
    return trt_packs
end

-- Startup checks presence and size without hashing hundreds of MB of DLLs.
-- The updater performs the full digest check when managing components.
local function pack_present(pack)
    if not pack or not pack.files then return false end
    local total = 0
    for _, rel in ipairs(pack.files) do
        local name = rel:match('[^/]+$')
        if not name:upper():find('LICENSE', 1, true) then
            local info = utils.file_info(mp.command_native({'expand-path', '~~/../' .. rel}))
            if not info or not info.is_file or not info.size then return false end
            if pack.file_bytes and info.size ~= pack.file_bytes[rel] then return false end
            total = total + info.size
        end
    end
    return total > 0 and total == pack.installed_bytes
end

local function trt_runtime_installed()
    return pack_present(load_trt_packs()['trt-runtime'])
end

local function trt_builder_installed(gpus, device)
    local packs = load_trt_packs()
    if gpus and #gpus > 0 then
        local uuid = (device or ''):lower():gsub('^gpu%-', '')
        local selected
        for _, gpu in ipairs(gpus) do
            if gpu.uuid == uuid then selected = gpu end
        end
        for _, gpu in ipairs(selected and {selected} or gpus) do
            local pack = packs['trt-sm' .. gpu.major .. gpu.minor] or packs['trt-ptx']
            if not pack_present(pack) then return false end
        end
        return true
    end
    for name, pack in pairs(packs) do
        if (name:match('^trt%-sm%d+$') or name == 'trt-ptx') and pack_present(pack) then
            return true
        end
    end
    return false
end

-- Component-pack sanity: a slim install (or one slimmed with
-- AnimeJaNaiUpdater --remove) may lack the pieces the conf asks for.
-- The filter would fail with a loader error; say what to run instead.
local function check_components(backend, fallback_hint, gpus, device)
    local hints = {}
    if fallback_hint then
        hints[#hints + 1] = fallback_hint
    end
    if backend == 'tensorrt' then
        if not trt_runtime_installed() then
            hints[#hints + 1] =
                '缺少 TensorRT 组件，请按 Ctrl+E 安装'
        else
            -- builder resources are only needed to build new engines; cached
            -- engines still run without them, so this is a soft warning
            if not trt_builder_installed(gpus, device) then
                hints[#hints + 1] =
                    '缺少 TensorRT 构建组件，请按 Ctrl+E 安装'
            end
        end
    end
    if #hints == 0 then
        return
    end
    for _, h in ipairs(hints) do
        msg.warn(h)
    end
    -- The player shows the filename through the same shared OSD text
    -- slot we'd use, right after file-loaded and for osd-duration ms
    -- (default 1s). Posting our hint immediately just loses that slot to
    -- the filename. So wait until the filename has cleared, then show
    -- our hint in the normal OSD position (where the filename was) for a
    -- good while. Reading osd-duration adapts if the user changed it; if
    -- the player used a longer title duration ours simply replaces it.
    local shown = false
    mp.register_event('file-loaded', function()
        if shown then
            return
        end
        shown = true
        local after = mp.get_property_number('osd-duration', 1000) / 1000 + 0.5
        mp.add_timeout(after, function()
            mp.osd_message(table.concat(hints, '\n'), 10)
        end)
    end)
end

local trt_gpus, too_old_message = route.trt_gpus, route.too_old_message

-- Whether TensorRT can run on this Windows PC: it needs NVIDIA's driver
-- (nvcuda.dll is installed with it), an NVIDIA GPU new enough for it, and the
-- TensorRT runtime component. The second value says why not; the third is the
-- GPUs too old for TensorRT when that is the reason.
local function trt_usable()
    local sysroot = os.getenv('SystemRoot') or 'C:\\Windows'
    if utils.file_info(sysroot .. '\\System32\\nvcuda.dll') == nil then
        return false, 'no NVIDIA GPU driver'
    end
    local ok, old = trt_gpus()
    if ok and #ok == 0 and #old > 0 then
        return false, 'NVIDIA GPU too old for TensorRT', old
    end
    if not trt_runtime_installed() then
        return false, 'TensorRT runtime not installed'
    end
    return true
end

-- Rewrites [global] backend= to DirectML and marks it as an automatic
-- fallback, the same two keys the Manager writes when it makes this switch.
-- With backend_auto_fallback=yes the Manager switches back to TensorRT by
-- itself once TensorRT can run. Returns false if the conf can't be updated.
local function switch_conf_to_directml()
    local f = io.open(conf_path, 'rb')
    if not f then
        return false
    end
    local s = f:read('*a')
    f:close()
    local nl = s:find('\r\n', 1, true) and '\r\n' or '\n'
    local trailing = s:sub(-1) == '\n'
    if not trailing then
        s = s .. '\n'
    end
    local out = {}
    local in_global, seen_global = false, false
    local have_backend, have_fallback = false, false
    local function close_global()
        if not have_backend then
            out[#out + 1] = 'backend=DirectML'
        end
        if not have_fallback then
            out[#out + 1] = 'backend_auto_fallback=yes'
        end
    end
    for line in s:gmatch('([^\n]*)\n') do
        line = line:gsub('\r$', '')
        local sec, key = ini_line(line)
        if sec then
            if in_global then
                close_global()
            end
            in_global = sec == 'global'
            seen_global = seen_global or in_global
        elseif in_global and key == 'backend' then
            line = 'backend=DirectML'
            have_backend = true
        elseif in_global and key == 'backend_auto_fallback' then
            line = 'backend_auto_fallback=yes'
            have_fallback = true
        end
        out[#out + 1] = line
    end
    if in_global then
        close_global()
    end
    if not seen_global then
        return false
    end
    f = io.open(conf_path, 'wb')
    if not f then
        return false
    end
    f:write(table.concat(out, nl) .. (trailing and nl or ''))
    f:close()
    return true
end

local backend_raw, default_slot, sub_render_mode, display_rate_match, gpu_choice = route.read_conf(conf_path)
gpu_choice = gpu_choice or {}
local explicit_option = route.explicit_option
if display_rate_match and mp.get_property('platform') == 'windows'
        and not explicit_option('display-rate-match')
        and mp.get_property('display-rate-match') ~= 'test' then
    local ok, err = mp.set_property('display-rate-match', 'yes')
    if not ok then
        msg.error('Automatic Display Refresh Rate requires a matching AJN player build: ' .. tostring(err))
    end
end
local backend = (backend_raw or 'TensorRT'):lower()

-- A fresh install ships backend=TensorRT, and until now only the Manager
-- switched it to DirectML on a PC where TensorRT can't run, so AMD and Intel
-- users got no upscaling until they opened it. Make that switch here, at
-- startup, before the filter reads the conf.
local fallback_hint
-- A GPU too old for TensorRT where nothing else can upscale (Linux has no
-- DirectML, or the conf couldn't be switched): the filter is taken out at
-- startup with this message, rather than failing at the engine build.
local trt_unavailable
if backend == 'tensorrt' and mp.get_property('platform') == 'windows' then
    local ok, why, old = trt_usable()
    if not ok and switch_conf_to_directml() then
        msg.warn('TensorRT can\'t run here (' .. why .. '); switched animejanai.conf to DirectML')
        backend = 'directml'
        if why == 'TensorRT runtime not installed' then
            -- an NVIDIA GPU: TensorRT is faster, so say how to get it
            fallback_hint = '已切换到 DirectML，按 Ctrl+E 可安装 TensorRT'
        elseif old then
            fallback_hint = '已切换到 DirectML：' .. too_old_message(old)
        end
    elseif old then
        trt_unavailable = too_old_message(old) .. '，按 Ctrl+E 可选择 DirectML'
    end
elseif backend == 'tensorrt' then
    -- Linux: TensorRT, or Vulkan where the engine has it (libaji_vk.so)
    local ok, old = trt_gpus()
    if ok and #ok == 0 and #old > 0 then
        trt_unavailable = too_old_message(old) ..
            (exists('animejanai/inference/libaji_vk.so')
                and '，按 Ctrl+E 可选择 Vulkan'
                or '，当前缺少可用的超分后端')
    end
end
if trt_unavailable then
    msg.error(trt_unavailable)
    mp.commandv('vf', 'remove', '@aji')
    -- shown by check_components, without its TensorRT install hints
    fallback_hint = '超分已关闭：' .. trt_unavailable
end

local _, route_hint = route.apply(conf_path, backend, gpu_choice)
fallback_hint = route_hint or fallback_hint
if (backend == 'vulkan' or backend == 'vk') and mp.get_property('platform') ~= 'windows'
        and not exists('animejanai/inference/libaji_vk.so') then
    fallback_hint = '缺少 Vulkan 组件，请按 Ctrl+E 安装'
end
mp.add_hook('on_load', 50, function()
    local update = route.pending()
    if update then update() end
end)

-- Opt-in GPU subtitle rendering: only ever applied on top of the stable
-- defaults, never the reverse, so a user who set any of these options in
-- mpv.conf keeps them as long as the mode is off. Applied here rather than in
-- mpv.conf so the Manager stays the single writer of animejanai.conf.
--
-- A plain `apply-profile subs-gpu` would break mpv.conf's contract that your
-- own lines win: the config is fully parsed before scripts run, so the profile
-- would overwrite settings you made by hand. Instead each option is applied
-- only while it still holds the managed default from [animejanai] (or, for an
-- option that profile does not set, mpv's own default) - a different value can
-- only have come from your mpv.conf, so it is left alone. Both value sets are
-- read from profile-list, keeping the option lists in mpv-animejanai.conf.
--
-- Ambiguity worth knowing about: setting an option in mpv.conf to exactly the
-- managed default is indistinguishable from not setting it at all (mpv does
-- not record where a value came from), so that one does get the preset value.
local function profile_options(list, name)
    for _, p in ipairs(list) do
        if p.name == name then
            return p.options or {}
        end
    end
    return nil
end

local function apply_subs_gpu()
    local profiles = mp.get_property_native('profile-list') or {}
    local preset = profile_options(profiles, 'subs-gpu')
    if not preset then
        msg.warn('sub_render_mode=gpu but no subs-gpu profile - ' ..
                 'is mpv-animejanai.conf up to date?')
        return
    end
    local managed = profile_options(profiles, 'animejanai') or {}

    local function managed_value(key)
        for _, o in ipairs(managed) do
            if o.key == key then
                return o.value
            end
        end
        return mp.get_property('option-info/' .. key .. '/default-value')
    end

    local function norm(v)
        return (tostring(v or ''):match('^%s*(.-)%s*$')):lower()
    end

    for _, o in ipairs(preset) do
        if o.key == 'script-opts-append' then
            -- One "key=value" script option. It shares the script-opts map with
            -- everything else, so append it only when that key is unset.
            local k, v = tostring(o.value):match('^([^=]+)=(.*)$')
            local opts = mp.get_property_native('script-opts') or {}
            if k and opts[k] == nil then
                opts[k] = v
                mp.set_property_native('script-opts', opts)
            elseif k then
                msg.info('subs-gpu: keeping your script-opt ' .. k .. '=' .. tostring(opts[k]))
            end
        elseif norm(mp.get_property(o.key)) == norm(managed_value(o.key)) then
            mp.set_property(o.key, o.value)
        else
            msg.info(string.format('subs-gpu: keeping your %s=%s', o.key,
                                   tostring(mp.get_property(o.key))))
        end
    end
    msg.info('sub_render_mode gpu -> applied profile subs-gpu')
end

if (sub_render_mode or ''):lower() == 'gpu' then
    apply_subs_gpu()
end

local function current_slot()
    return mp.get_property_native('user-data/animejanai/requested-slot') or default_slot or filter_slot
end

-- With Win32 Vulkan's FIFO presentation, the default audio clock can miss a
-- display interval on interpolated frames. Use mpv's display clock only when
-- the configured chain actually interpolates and the display can show it.
local rife_factor, configuring_factor
local sync_managed, sync_local, sync_overridden = false, false, false

local function update_rife_sync()
    local mode = mp.get_property('video-sync')
    if sync_managed and mode ~= 'display-resample' then
        sync_managed, sync_overridden = false, true
    end
    local can_sync = false
    if rife_factor and not sync_overridden and mp.get_property('current-vo') == 'gpu-next'
        and mp.get_property('current-gpu-context') == 'winvk' then
        local fps = mp.get_property_number('container-fps', 0)
        local refresh = mp.get_property_number('display-fps', 0)
        local format = mp.get_property('audio-out-params/format')
        can_sync = mp.get_property_number('current-tracks/audio/id') and format and not format:match('^spdif')
            and fps > 0 and fps * rife_factor <= refresh * 1.001
    end
    if sync_managed and not can_sync then
        sync_managed = false
        mp.set_property('file-local-options/video-sync', 'audio')
    elseif can_sync and mode == 'audio' and (sync_local or not explicit_option('video-sync')) then
        if mp.set_property('file-local-options/video-sync', 'display-resample') then
            sync_managed, sync_local = true, true
        end
    end
end

local function reset_rife_sync()
    -- mpv restores file-local options when it unloads the file.
    rife_factor, configuring_factor = nil, nil
    sync_managed, sync_local, sync_overridden = false, false, false
end
mp.register_event('start-file', reset_rife_sync)
mp.register_event('end-file', reset_rife_sync)
for _, prop in ipairs({'current-vo', 'current-gpu-context', 'container-fps', 'display-fps', 'aid', 'audio-out-params', 'video-sync'}) do
    mp.observe_property(prop, 'native', update_rife_sync)
end
mp.observe_property('vf', 'native', function(_, filters)
    for _, filter in ipairs(filters or {}) do
        if filter.name == 'animejanai' and filter.label == 'aji' and filter.enabled ~= false then return end
    end
    rife_factor, configuring_factor = nil, nil
    update_rife_sync()
end)

-- The native filter reports this only when the selected chain for the
-- current video needs interpolation. Delay its first hint past the filename.
local missing_rife_slot, rife_timer
local function clear_rife_hint()
    if rife_timer then rife_timer:kill(); rife_timer = nil end
    missing_rife_slot = nil
end
mp.register_event('start-file', clear_rife_hint)
mp.register_event('end-file', clear_rife_hint)
mp.observe_property('user-data/animejanai/requested-slot', 'number', function(_, slot)
    if missing_rife_slot and slot and slot ~= missing_rife_slot then clear_rife_hint() end
end)
mp.enable_messages('v')
mp.register_event('log-message', function(event)
    if event.prefix == 'vf' and (event.text or ''):match('^Disabling filter aji because it has failed') then
        rife_factor, configuring_factor = nil, nil
        update_rife_sync()
    end
    if event.prefix ~= 'animejanai' then return end
    local text = event.text or ''
    local num, den = text:match('^RIFE active: (%d+)/(%d+) interpolation')
    if num then
        configuring_factor = tonumber(num) / tonumber(den)
        return
    elseif text:match('^Configured slot %d+:') or text:match('^No chain active for ') then
        rife_factor, configuring_factor = configuring_factor, nil
        update_rife_sync()
        return
    end
    local slot = tonumber((event.text or ''):match('^RIFE models are missing for slot (%d+)%s*$'))
    if not slot or slot == missing_rife_slot then return end
    clear_rife_hint()
    missing_rife_slot = slot
    rife_timer = mp.add_timeout(mp.get_property_number('osd-duration', 1000) / 1000 + 0.5, function()
        if current_slot() == slot then mp.osd_message('缺少 RIFE 模型，请按 Ctrl+E 安装', 10) end
        rife_timer = nil
    end)
end)

-- The Manager's "Set as Default Profile" stores the chosen slot here. mpv
-- rebuilds the filter chain from the vf string (which starts in bypass)
-- on every file, so the active slot must be re-applied after each file loads or
-- every file after the first would snap back to bypass. We re-apply the
-- *current* slot, not always default_slot: the default seeds the active slot at
-- startup, but once the user switches that choice sticks across file loads.
-- Restarting mpv re-reads default_slot and resets again.
mp.register_event('file-loaded', function()
    local slot = current_slot()
    msg.info('applying slot ' .. slot)
    mp.commandv('script-message', 'aji-slot', tostring(slot))
end)

check_components(backend, fallback_hint, backend == 'tensorrt' and trt_gpus(),
                 mp.get_property('vulkan-device'))
