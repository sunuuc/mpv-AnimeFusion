"""Audited native fixes for the pinned AnimeJaNai mpv fork."""
from pathlib import Path
import subprocess
import sys
root=Path(sys.argv[1] if len(sys.argv)>1 else 'mpv-source')
expected='d4c06dd3424dc06d90d4ea15b0fa92fa3ceeb62f'
actual=subprocess.check_output(['git','-C',str(root),'rev-parse','HEAD'],text=True).strip()
if actual!=expected: raise RuntimeError('Unexpected native source: '+actual)
subprocess.run(['git','-C',str(root),'config','core.autocrlf','true'],check=True)
def edit(name,old,new):
    p=root/name;s=p.read_text(encoding='utf-8')
    if s.count(old)!=1:raise RuntimeError('Patch context mismatch: '+name+' / '+old[:80])
    p.write_text(s.replace(old,new),encoding='utf-8',newline='\r\n')
edit('video/out/vo.c','    int64_t drop_count;\n','    int64_t drop_count;\n    int64_t presented_frame_count;\n    uint64_t presented_frame_id;\n')
edit('video/out/vo.c','        int64_t prev_drop_count = vo->in->drop_count;\n','        int64_t prev_drop_count = vo->in->drop_count;\n        uint64_t presented_id = frame->current ? frame->frame_id : 0;\n')
edit('video/out/vo.c','        in->dropped_frame = prev_drop_count < vo->in->drop_count;\n','''        in->dropped_frame = prev_drop_count < vo->in->drop_count;
        // Unique video-frame submissions after flip; not physical scanout.
        // Do not count OSD redraws, vsync repeats, or reported dropped frames.
        if (!in->dropped_frame && presented_id && presented_id != in->presented_frame_id) {
            in->presented_frame_count++;
            in->presented_frame_id = presented_id;
        }
''')
edit('video/out/vo.c','    frame->duration = -1;\n    mp_mutex_unlock(&in->lock);\n','''    frame->duration = -1;
    uint64_t presented_id = frame->current ? frame->frame_id : 0;
    int64_t previous_drops = in->drop_count;
    mp_mutex_unlock(&in->lock);
''')
edit('video/out/vo.c','    vo->driver->flip_page(vo);\n\n    if (frame != &dummy','''    vo->driver->flip_page(vo);

    mp_mutex_lock(&in->lock);
    if (previous_drops == in->drop_count && presented_id && presented_id != in->presented_frame_id) {
        in->presented_frame_count++;
        in->presented_frame_id = presented_id;
    }
    mp_mutex_unlock(&in->lock);

    if (frame != &dummy''')
edit('video/out/vo.c','int64_t vo_get_drop_count(struct vo *vo)\n','''int64_t vo_get_presented_frame_count(struct vo *vo)
{
    mp_mutex_lock(&vo->in->lock);
    int64_t value = vo->in->presented_frame_count;
    mp_mutex_unlock(&vo->in->lock);
    return value;
}

int64_t vo_get_drop_count(struct vo *vo)
''')
edit('video/out/vo.h','int64_t vo_get_drop_count(struct vo *vo);','int64_t vo_get_presented_frame_count(struct vo *vo);\nint64_t vo_get_drop_count(struct vo *vo);')
edit('player/command.c','static int mp_property_frame_drop_vo(','''static int mp_property_presented_frame_count(void *ctx, struct m_property *prop,
                                             int action, void *arg)
{
    MPContext *mpctx = ctx;
    if (!mpctx->vo_chain || !mpctx->video_out)
        return M_PROPERTY_UNAVAILABLE;
    return m_property_int64_ro(action, arg, vo_get_presented_frame_count(mpctx->video_out));
}

static int mp_property_frame_drop_vo(''')
edit('player/command.c','    {"frame-drop-count", mp_property_frame_drop_vo},','    {"vo-presented-frame-count", mp_property_presented_frame_count},\n    {"frame-drop-count", mp_property_frame_drop_vo},')
# Synchronous property reads may run on a short-lived Lua/client thread.
# Create, unregister, release and CoUninitialize notifications on one MTA
# thread which lives until hotplug_uninit. Its event wait does not poll.
edit('audio/out/ao_wasapi.h','    change_notify change;\n','''    HANDLE hHotplugThread;
    HANDLE hHotplugReady;
    HANDLE hHotplugStop;
    HRESULT hotplug_result;
    change_notify change;
''')
s=(root/'audio/out/ao_wasapi.c').read_text(encoding='utf-8')
a=s.index('static void hotplug_uninit(struct ao *ao)\n')
b=s.index('#define OPT_BASE_STRUCT struct wasapi_state',a)
edit('audio/out/ao_wasapi.c',s[a:b],'''// Notification objects must not outlive the apartment which created them.
static DWORD WINAPI HotplugThread(LPVOID opaque)
{
    struct ao *ao = opaque;
    struct wasapi_state *state = ao->priv;
    HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    bool com_initialized = SUCCEEDED(hr);
    if (com_initialized)
        hr = wasapi_change_init(ao, true);
    state->hotplug_result = hr;
    SetEvent(state->hHotplugReady);
    if (SUCCEEDED(hr))
        WaitForSingleObject(state->hHotplugStop, INFINITE);
    // wasapi_change_init cleans up partial initialization on failure.
    if (SUCCEEDED(hr))
        wasapi_change_uninit(ao);
    if (com_initialized)
        CoUninitialize();
    return 0;
}

static void hotplug_uninit(struct ao *ao)
{
    MP_DBG(ao, "Hotplug uninit\\n");
    struct wasapi_state *state = ao->priv;
    if (state->hHotplugStop)
        SetEvent(state->hHotplugStop);
    if (state->hHotplugThread)
        WaitForSingleObject(state->hHotplugThread, INFINITE);
    SAFE_DESTROY(state->hHotplugThread, CloseHandle(state->hHotplugThread));
    SAFE_DESTROY(state->hHotplugReady, CloseHandle(state->hHotplugReady));
    SAFE_DESTROY(state->hHotplugStop, CloseHandle(state->hHotplugStop));
}

static int hotplug_init(struct ao *ao)
{
    MP_DBG(ao, "Hotplug init\\n");
    struct wasapi_state *state = ao->priv;
    state->log = ao->log;
    state->hotplug_result = E_FAIL;
    state->hHotplugReady = CreateEventW(NULL, TRUE, FALSE, NULL);
    state->hHotplugStop = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!state->hHotplugReady || !state->hHotplugStop)
        goto fail;
    state->hHotplugThread = CreateThread(NULL, 0, HotplugThread, ao, 0, NULL);
    if (!state->hHotplugThread)
        goto fail;
    if (WaitForSingleObject(state->hHotplugReady, INFINITE) != WAIT_OBJECT_0 ||
        FAILED(state->hotplug_result))
        goto fail;
    return 0;
fail:
    MP_ERR(ao, "Failed to initialize audio-device notifications\\n");
    hotplug_uninit(ao);
    return -1;
}

''')
# Enumeration is synchronous; balance COM on its calling thread as well.
edit('audio/out/ao_wasapi_utils.c','''void wasapi_list_devs(struct ao *ao, struct ao_device_list *list)
{
    struct enumerator *enumerator = create_enumerator(ao->log);
    if (!enumerator)
        return;
''','''void wasapi_list_devs(struct ao *ao, struct ao_device_list *list)
{
    HRESULT com_hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(com_hr) && com_hr != RPC_E_CHANGED_MODE)
        return;
    struct enumerator *enumerator = create_enumerator(ao->log);
    if (!enumerator) {
        if (SUCCEEDED(com_hr)) CoUninitialize();
        return;
    }
''')
edit('audio/out/ao_wasapi_utils.c','''exit_label:
    destroy_enumerator(enumerator);
}

static bool load_device''','''exit_label:
    destroy_enumerator(enumerator);
    if (SUCCEEDED(com_hr)) CoUninitialize();
}

static bool load_device''')
print('Applied native counter and COM lifetime fix')

from apply_secondary_sub_sync import apply
apply(root)
