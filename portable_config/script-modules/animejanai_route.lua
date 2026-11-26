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
