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
         '    int64_t secondary_end_ns;\n    int64_t secondary_next_ns;\n'
         '    int64_t secondary_sample_ns;\n    double secondary_last_pts;\n'
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

static bool secondary_redraw_due(struct vo *vo, int64_t now)
{
    struct vo_internal *in = vo->in;
    struct vo_frame *frame = in->current_frame;
    if (!frame || frame->playback_speed <= 0 || frame->display_synced ||
        in->paused ||
        !in->hasframe || !in->visible || in->vsync_interval <= 1 ||
        now >= in->secondary_end_ns || !osd_secondary_needs_redraw(vo->osd))
        return false;
    // Video at or above the ASS cadence already supplies enough presentations.
    // Extra swaps compete with those frames; only fill low-cadence gaps.
    int64_t duration = frame->duration >= 0 ? frame->duration :
        MP_TIME_S_TO_NS(frame->approx_duration / frame->playback_speed);
    return duration > secondary_interval(vo);
}

static void update_secondary_clock(struct vo *vo, bool new_frame)
{
    struct vo_internal *in = vo->in;
    struct vo_frame *frame = in->current_frame;
    int64_t now = mp_time_ns();
    int64_t sample_time = now;
    double pts = MP_NOPTS_VALUE;
    if (frame && frame->current && frame->playback_speed > 0) {
        if (new_frame) {
            in->secondary_anchor_pts = frame->current->pts;
            if (frame->display_synced)
                in->secondary_anchor_pts += frame->ideal_frame_vsync;
            in->secondary_anchor_ns = frame->display_synced ? now : frame->pts;
            double duration = frame->duration >= 0 ? MP_TIME_NS_TO_S(frame->duration)
                : frame->approx_duration / frame->playback_speed;
            in->secondary_end_ns = in->secondary_anchor_ns + MP_TIME_S_TO_NS(MPMAX(0, duration));
            // Video may be prepared early; publish ASS for its presentation,
            // not for the earlier render call's wall-clock bucket.
            sample_time = MPMAX(now, in->secondary_anchor_ns);
        }
        if (new_frame && in->paused)
            pts = frame->current->pts;
        if (!in->paused && in->secondary_anchor_ns) {
            int64_t sample = MPMIN(sample_time, in->secondary_end_ns);
            pts = in->secondary_anchor_pts +
                MP_TIME_NS_TO_S(MPMAX(0, sample - in->secondary_anchor_ns)) * frame->playback_speed;
            // Small cadence corrections must not move rolling text backwards.
            // A seek clears validity in forget_frames; paused seek frames are
            // also allowed to publish their exact requested timestamp.
            if (in->secondary_clock_valid)
                pts = MPMAX(pts, in->secondary_last_pts);
        }
    }
    bool sample_due = !in->secondary_sample_ns ||
        sample_time / secondary_interval(vo) >
            in->secondary_sample_ns / secondary_interval(vo);
    if (!frame || (new_frame && in->paused) || (!in->paused && sample_due)) {
        osd_set_secondary_pts(vo->osd, pts);
        in->secondary_sample_ns = sample_time - sample_time % secondary_interval(vo);
        in->secondary_last_pts = pts;
        in->secondary_clock_valid = pts != MP_NOPTS_VALUE;
    }
}

static bool render_frame(struct vo *vo)
''')
    edit('video/out/vo.c', '    if (in->frame_queued) {\n        talloc_free(in->current_frame);',
         '''    // Keep the displayed frame available for ASS redraws until the next video
    // presentation is due. Consuming it early would block this thread in
    // wait_until() and starve the secondary subtitle deadlines.
    if (in->frame_queued && !in->frame_queued->display_synced &&
        secondary_redraw_due(vo, mp_time_ns()) &&
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
        bool secondary_active = secondary_redraw_due(vo, now);
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
         '    in->secondary_anchor_ns = 0;\n    in->secondary_end_ns = 0;\n'
         '    in->secondary_next_ns = 0;\n    in->secondary_sample_ns = 0;\n'
         '    osd_set_secondary_pts(vo->osd, MP_NOPTS_VALUE);')

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
