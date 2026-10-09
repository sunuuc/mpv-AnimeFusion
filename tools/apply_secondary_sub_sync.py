"""Add display-paced secondary ASS presentation to the pinned mpv source."""
from pathlib import Path
import sys


def apply(root):
    def edit(name, old, new):
        path = root / name
        text = path.read_text(encoding='utf-8')
        if text.count(old) != 1:
            raise RuntimeError('Native source context mismatch: ' + name + ' / ' + old[:80])
        path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

    edit('options/options.h', '    int osd_render_res_cap;\n',
         '    int osd_render_res_cap;\n    bool secondary_sub_display_sync;\n'
         '    int secondary_sub_render_fps;\n')
    edit('options/options.c', '        {"osd-render-res-cap", OPT_INT(osd_render_res_cap), M_RANGE(0, 4320)},',
         '        {"osd-render-res-cap", OPT_INT(osd_render_res_cap), M_RANGE(0, 4320)},\n'
         '        {"secondary-sub-display-sync", OPT_BOOL(secondary_sub_display_sync),\n'
         '            .flags = UPDATE_SUB_HARD},\n'
         '        {"secondary-sub-render-fps", OPT_INT(secondary_sub_render_fps), M_RANGE(1, 240)},')
    edit('options/options.c', '        .osd_scale = 1,',
         '        .osd_scale = 1,\n        .secondary_sub_render_fps = 60,')
    edit('sub/osd_state.h', '    _Atomic double force_video_pts;\n',
         '    _Atomic double force_video_pts;\n    _Atomic double secondary_pts;\n'
         '    atomic_bool secondary_display_sync;\n    atomic_bool secondary_has_output;\n'
         '    atomic_int_fast64_t secondary_interval_ns;\n')
    edit('sub/osd.h', 'double osd_get_force_video_pts(struct osd_state *osd);',
         'double osd_get_force_video_pts(struct osd_state *osd);\n'
         'void osd_set_secondary_pts(struct osd_state *osd, double pts);\n'
         'bool osd_secondary_display_sync(struct osd_state *osd);\n'
         'bool osd_secondary_needs_redraw(struct osd_state *osd);\n'
         'int64_t osd_secondary_interval(struct osd_state *osd);')
    edit('sub/osd.c', '        .force_video_pts = MP_NOPTS_VALUE,',
         '        .force_video_pts = MP_NOPTS_VALUE,\n        .secondary_pts = MP_NOPTS_VALUE,\n'
         '        .secondary_interval_ns = MP_TIME_S_TO_NS(1.0 / 60),')
    edit('sub/osd.c', '        obj->sub = dec_sub;',
         '        obj->sub = dec_sub;\n        if (index == 1)\n'
         '            atomic_store(&osd->secondary_has_output, false);')
    edit('sub/osd.c', 'double osd_get_force_video_pts(struct osd_state *osd)\n', '''void osd_set_secondary_pts(struct osd_state *osd, double pts)
{
    if (osd)
        atomic_store(&osd->secondary_pts, pts);
}

bool osd_secondary_display_sync(struct osd_state *osd)
{
    return osd && atomic_load(&osd->secondary_display_sync);
}

bool osd_secondary_needs_redraw(struct osd_state *osd)
{
    return osd_secondary_display_sync(osd) &&
           atomic_load(&osd->secondary_has_output);
}

int64_t osd_secondary_interval(struct osd_state *osd)
{
    return atomic_load(&osd->secondary_interval_ns);
}

double osd_get_force_video_pts(struct osd_state *osd)
''')
    edit('sub/osd.c', '''        if (obj->sub && sub_is_secondary_visible(obj->sub))
            res = sub_get_bitmaps(obj->sub, obj->vo_res, format, video_pts, draw_flags);''', '''        double pts = atomic_load(&osd->secondary_pts);
        if (!osd->opts->secondary_sub_display_sync || pts == MP_NOPTS_VALUE)
            pts = video_pts;
        if (obj->sub && sub_is_secondary_visible(obj->sub))
            res = sub_get_bitmaps(obj->sub, obj->vo_res, format, pts, draw_flags);
        atomic_store(&osd->secondary_has_output, res && res->num_parts > 0);
        if (res && res->num_parts > 0)
            MP_TRACE(osd, "Secondary ASS pts=%.6f x=%d y=%d\\n", pts,
                     res->parts[0].x, res->parts[0].y);''')
    edit('sub/osd.c', '    m_config_cache_update(osd->opts_cache);',
         '    m_config_cache_update(osd->opts_cache);\n'
         '    atomic_store(&osd->secondary_display_sync, osd->opts->secondary_sub_display_sync);\n'
         '    atomic_store(&osd->secondary_interval_ns,\n'
         '                 MP_TIME_S_TO_NS(1.0 / osd->opts->secondary_sub_render_fps));')
    edit('video/out/vo.h', 'struct vo_frame {\n',
         'struct vo_frame {\n    double playback_speed; // media seconds per wall-clock second\n')
    edit('player/video.c', '        .can_drop = opts->frame_dropping & 1,',
         '        .can_drop = opts->frame_dropping & 1,\n        .playback_speed = mpctx->video_speed,')
    edit('video/out/vo.c', '    bool request_redraw;            // redraw request from player to VO',
         '    double secondary_anchor_pts;\n    int64_t secondary_anchor_ns;\n'
         '    double secondary_clock_speed;\n    int64_t secondary_next_ns;\n'
         '    bool secondary_clock_valid;\n'
         '    bool request_redraw;            // redraw request from player to VO')
    edit('video/out/vo.c', 'static bool render_frame(struct vo *vo)\n', '''// The video frame remains immutable. Only secondary ASS receives this clock;
// primary subtitles and video/AI processing keep their original timestamps.
static int64_t secondary_interval(struct vo *vo)
{
    return MPMAX(vo->in->vsync_interval, osd_secondary_interval(vo->osd));
}

static int64_t secondary_next_deadline(struct vo *vo, int64_t now)
{
    int64_t interval = secondary_interval(vo);
    return (now / interval + 1) * interval;
}

static bool secondary_redraw_due(struct vo *vo)
{
    struct vo_internal *in = vo->in;
    struct vo_frame *frame = in->current_frame;
    if (!frame || frame->playback_speed <= 0 || frame->display_synced ||
        in->paused ||
        !in->hasframe || !in->visible || in->vsync_interval <= 1 ||
        !in->secondary_clock_valid || !osd_secondary_needs_redraw(vo->osd))
        return false;
    // Video at or above the ASS cadence already supplies enough presentations.
    // Extra swaps compete with those frames; only fill low-cadence gaps.
    int64_t duration = frame->duration >= 0 ? frame->duration :
        MP_TIME_S_TO_NS(frame->approx_duration / frame->playback_speed);
    return duration > secondary_interval(vo);
}

static double secondary_clock_pts(struct vo_internal *in, int64_t now)
{
    if (!in->secondary_clock_valid)
        return MP_NOPTS_VALUE;
    double elapsed = in->paused ? 0 :
        MP_TIME_NS_TO_S(MPMAX(0, now - in->secondary_anchor_ns));
    return in->secondary_anchor_pts + elapsed * in->secondary_clock_speed;
}

static void update_secondary_clock(struct vo *vo, bool new_frame)
{
    struct vo_internal *in = vo->in;
    struct vo_frame *frame = in->current_frame;
    int64_t now = mp_time_ns();
    int64_t sample_time = now;
    if (!frame || !frame->current || frame->current->pts == MP_NOPTS_VALUE ||
        frame->playback_speed <= 0) {
        osd_set_secondary_pts(vo->osd, MP_NOPTS_VALUE);
        return;
    }
    if (new_frame && !frame->display_synced)
        sample_time = MPMAX(now, frame->pts);

    double video_pts = frame->current->pts;
    if (frame->display_synced)
        video_pts += frame->ideal_frame_vsync;
    if (!in->secondary_clock_valid || (new_frame && in->paused)) {
        in->secondary_anchor_pts = video_pts;
        in->secondary_anchor_ns = sample_time;
        in->secondary_clock_speed = frame->playback_speed;
        in->secondary_clock_valid = true;
    } else if (new_frame) {
        // Rebase without changing the current position. Correct clock drift
        // over one second, with at most a 1% rate adjustment, rather than
        // snapping to each video's PTS or clamping to its frame end.
        double pts = secondary_clock_pts(in, sample_time);
        double max_correction = frame->playback_speed * 0.01;
        double correction = MPCLAMP(video_pts - pts,
                                    -max_correction, max_correction);
        in->secondary_anchor_pts = pts;
        in->secondary_anchor_ns = sample_time;
        in->secondary_clock_speed = frame->playback_speed + correction;
    }
    // Every presentation samples the continuous clock. The refresh deadline
    // controls when to draw, not which older timestamp to reuse for that draw.
    osd_set_secondary_pts(vo->osd, secondary_clock_pts(in, sample_time));
}

static bool render_frame(struct vo *vo)
''')
    edit('video/out/vo.c', '    if (in->frame_queued) {\n        talloc_free(in->current_frame);',
         '''    // Keep the displayed frame available for ASS redraws until the next video
    // presentation is due. Consuming it early would block this thread in
    // wait_until() and starve the secondary subtitle deadlines.
    if (in->frame_queued && !in->frame_queued->display_synced &&
        secondary_redraw_due(vo) &&
        in->frame_queued->pts - in->flip_queue_offset > mp_time_ns())
        goto done;

    bool new_frame = in->frame_queued != NULL;
'''
         '    if (in->frame_queued) {\n        talloc_free(in->current_frame);')
    edit('video/out/vo.c', '    frame = vo_frame_ref(in->current_frame);\n    mp_assert(frame);',
         '    update_secondary_clock(vo, new_frame);\n'
         '    frame = vo_frame_ref(in->current_frame);\n    mp_assert(frame);')
    edit('video/out/vo.c', '    bool full_redraw = in->dropped_frame;',
         '    update_secondary_clock(vo, false);\n    bool full_redraw = in->dropped_frame;')
    edit('video/out/vo.c', '        in->hasframe_rendered = true;',
         '        in->hasframe_rendered = true;\n'
         '        vo->previous_redraw_time = now;\n'
         '        if (new_frame || now >= in->secondary_next_ns)\n'
         '            in->secondary_next_ns = secondary_next_deadline(vo, now);')
    edit('video/out/vo.c', '        bool redraw = in->request_redraw;', '''        // Render deadlines, not a player/Lua poll. The existing VO condition
        // variable sleeps until the configured ASS deadline while it is visible.
        bool secondary_active = secondary_redraw_due(vo);
        if (!secondary_active) {
            in->secondary_next_ns = 0;
        } else if (!in->secondary_next_ns) {
            in->secondary_next_ns = secondary_next_deadline(vo, now);
        }
        bool secondary_due = secondary_active && now >= in->secondary_next_ns;
        bool redraw = in->request_redraw || secondary_due;
        if (secondary_active)
            wait_until = MPMIN(wait_until, in->secondary_next_ns);''')
    edit('video/out/vo.c', '        if (in->wakeup_pts) {',
         '''        if (in->frame_queued && !in->frame_queued->display_synced)
            wait_until = MPMIN(wait_until,
                              in->frame_queued->pts - in->flip_queue_offset);
        if (in->wakeup_pts) {''')
    # A due animation deadline must be serviced before sleeping, and must not
    # replace or reorder a queued video frame. Normal display-sync repeats are
    # already rendered by render_frame above, so do not submit an extra redraw.
    edit('video/out/vo.c', '        if (wait_until > now && redraw) {',
         '        if (!working && redraw) {')
    edit('video/out/vo.c', '                vo->previous_redraw_time = now;',
         '                vo->previous_redraw_time = now;\n'
         '                in->secondary_next_ns = secondary_next_deadline(vo, vo->previous_redraw_time);')
    edit('video/out/vo.c', '                wait_vo(vo, now + max_interval);',
         '                wait_vo(vo, vo->previous_redraw_time + max_interval);')
    edit('video/out/gpu/video.c', '                bool repeats = frame->num_vsyncs > 1 && frame->display_synced;',
         '                bool repeats = (frame->num_vsyncs > 1 && frame->display_synced) ||\n'
         '                    osd_secondary_display_sync(p->osd_state);')
    edit('video/out/vo_gpu_next.c', '    bool cache_frame = will_redraw || frame->still || p->paused;',
         '    bool cache_frame = will_redraw || frame->still || p->paused ||\n'
         '        osd_secondary_display_sync(vo->osd);')
    edit('video/out/vo.c', '    in->hasframe = false;\n    in->hasframe_rendered = false;',
         '    in->hasframe = false;\n    in->hasframe_rendered = false;\n'
         '    in->secondary_clock_valid = false;\n'
         '    in->secondary_anchor_ns = 0;\n    in->secondary_clock_speed = 0;\n'
         '    in->secondary_next_ns = 0;\n'
         '    osd_set_secondary_pts(vo->osd, MP_NOPTS_VALUE);')
    edit('video/out/vo.c', '    if (in->paused != paused) {\n        in->paused = paused;',
         '''    if (in->paused != paused) {
        // The core uses this transition for both user pause and cache pause.
        // Preserve the position and restart only the wall-clock anchor.
        int64_t now = mp_time_ns();
        if (in->secondary_clock_valid) {
            in->secondary_anchor_pts = secondary_clock_pts(in, now);
            in->secondary_anchor_ns = now;
            osd_set_secondary_pts(vo->osd, in->secondary_anchor_pts);
        }
        in->secondary_next_ns = 0;
        in->paused = paused;''')

    # The upstream ahead cache samples at video_fps. A display-paced secondary
    # track must render its requested timestamp, not reuse a video-frame sample.
    # UPDATE_SUB_HARD recreates decoders when this mode is toggled.
    edit('sub/dec_sub.c', '    struct m_config_cache *shared_opts_cache;\n',
         '    struct m_config_cache *shared_opts_cache;\n'
         '    struct m_config_cache *render_opts_cache;\n')
    edit('sub/dec_sub.c', '    int depth = sub->opts->sub_render_ahead_frames;\n',
         '    int depth = sub->opts->sub_render_ahead_frames;\n'
         '    struct mp_osd_render_opts *render_opts = sub->render_opts_cache->opts;\n'
         '    if (sub->order == 1 && render_opts->secondary_sub_display_sync)\n'
         '        return;\n')
    edit('sub/dec_sub.c',
         '        .shared_opts_cache = m_config_cache_alloc(sub, global, &mp_subtitle_shared_sub_opts),',
         '        .shared_opts_cache = m_config_cache_alloc(sub, global, &mp_subtitle_shared_sub_opts),\n'
         '        .render_opts_cache = m_config_cache_alloc(sub, global, &mp_osd_render_sub_opts),')

if __name__ == "__main__":
    apply(Path(sys.argv[1]).resolve())
