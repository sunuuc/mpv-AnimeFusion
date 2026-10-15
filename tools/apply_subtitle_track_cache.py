"""Preserve embedded subtitle tracks in the pinned mpv demuxer cache."""
from pathlib import Path
import sys


def apply(root):
    def edit(name, old, new):
        path = root / name
        text = path.read_text(encoding='utf-8')
        if text.count(old) != 1:
            raise RuntimeError('Native source context mismatch: ' + name + ' / ' + old[:80])
        path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

    edit('demux/demux.h', '    int seekable_cache;\n',
         '    int seekable_cache;\n    bool cache_unselected_subs;\n')
    edit('demux/demux.h', '    bool no_cache_seeking;\n',
         '    bool no_cache_seeking;\n'
         '    // Unselected subtitles share the current input, without opening\n'
         '    // additional streams (unlike separate HLS/DASH renditions).\n'
         '    bool can_cache_unselected_subs;\n')
    edit('demux/demux_mkv.c', 'static int demux_mkv_open(demuxer_t *demuxer, enum demux_check check)\n{\n',
         'static int demux_mkv_open(demuxer_t *demuxer, enum demux_check check)\n{\n'
         '    demuxer->can_cache_unselected_subs = true;\n')
    edit('demux/demux_lavf.c', '    priv->avfc = avfc;\n',
         '    priv->avfc = avfc;\n'
         '    demuxer->can_cache_unselected_subs = priv->pb && avfc->pb == priv->pb;\n')
    edit('demux/demux.c', '        {"index", OPT_CHOICE(index_mode, {"default", 1}, {"recreate", 0})},',
         '        {"demuxer-cache-unselected-subs", OPT_BOOL(cache_unselected_subs)},\n'
         '        {"index", OPT_CHOICE(index_mode, {"default", 1}, {"recreate", 0})},')
    edit('demux/demux.c', 'static uint64_t get_forward_buffered_bytes(struct demux_stream *ds)\n', '''// Cache subtitle packets from the existing demuxer read, without selecting a
// decoder or requesting additional input. Keep using the normal cache budget.
static bool cache_unselected_subs(struct demux_stream *ds)
{
    struct demux_internal *in = ds->in;
    return ds->type == STREAM_SUB && in->can_cache && in->seekable_cache &&
           in->d_thread->can_cache_unselected_subs &&
           !in->back_demuxing && in->d_user->opts->cache_unselected_subs;
}

static bool stream_is_enabled(struct demux_stream *ds)
{
    return ds->selected || cache_unselected_subs(ds);
}

// Include overlapping subtitle events, not just the last event before pts.
// The native seek target still handles missing durations and seek preroll.
static struct demux_packet *find_subtitle_target(struct demux_queue *queue,
                                                 double pts, int flags)
{
    struct demux_packet *target = find_seek_target(queue, pts, flags);
    double start = pts - queue->ds->sh->seek_preroll;
    for (struct demux_packet *dp = queue->head; dp && dp != target; dp = dp->next) {
        if (dp->keyframe && dp->pts != MP_NOPTS_VALUE && dp->duration > 0 &&
            dp->pts <= pts && dp->pts + dp->duration >= start)
            return dp;
    }
    return target;
}

// An unselected subtitle track has no decoder to advance its reader position.
// Follow the existing playback update so future packets stay in the forward
// budget and expired packets can be pruned normally, including image subtitles.
static void advance_subtitle_cache(struct demux_stream *ds, double pts)
{
    if (ds->selected || !cache_unselected_subs(ds) || pts == MP_NOPTS_VALUE)
        return;
    if (!ds->reader_head)
        ds->reader_head = ds->queue->head;
    double start = pts - ds->sh->seek_preroll;
    while (ds->reader_head && ds->reader_head->next) {
        struct demux_packet *dp = ds->reader_head;
        struct demux_packet *next = dp->next;
        if (dp->pts == MP_NOPTS_VALUE || next->pts == MP_NOPTS_VALUE ||
            dp->pts + MPMAX(dp->duration, 0) >= start || next->pts > start)
            break;
        ds->reader_head = next;
    }
}

static uint64_t get_forward_buffered_bytes(struct demux_stream *ds)
''')
    edit('demux/demux.c', '''    ds->eof = false;
    ds->refreshing = false;

    update_stream_eager_state(in);''', '''    ds->eof = false;
    if (!cache_unselected_subs(ds))
        ds->refreshing = false;

    update_stream_eager_state(in);''')
    edit('demux/demux.c', '''        if (!ds->selected)
            clear_queue(range->streams[ds->index]);''', '''        if (!stream_is_enabled(ds))
            clear_queue(range->streams[ds->index]);''')
    edit('demux/demux.c', '''    free_empty_cached_ranges(in);

    wakeup_ds(ds);
}''', '''    free_empty_cached_ranges(in);

    if (!ds->selected && cache_unselected_subs(ds)) {
        ds->reader_head = find_subtitle_target(ds->queue, in->last_playback_pts, 0);
        advance_subtitle_cache(ds, in->last_playback_pts);
    }

    wakeup_ds(ds);
}''')
    edit('demux/demux.c', '    bool drop = !ds->selected || in->seeking || ds->sh->attached_picture;',
         '    bool drop = !stream_is_enabled(ds) || in->seeking || ds->sh->attached_picture;')
    edit('demux/demux.c', '''    adjust_seek_range_on_packet(ds, dp);

    // May need to reduce backward cache.''', '''    adjust_seek_range_on_packet(ds, dp);
    advance_subtitle_cache(ds, in->last_playback_pts);

    // May need to reduce backward cache.''')
    edit('demux/demux.c', '                ds->selected && !ds->refreshing;',
         '                stream_is_enabled(ds) && !ds->refreshing;')
    edit('demux/demux.c', '''    in->last_playback_pts = pts;

    demuxer->events |= in->events;''', '''    in->last_playback_pts = pts;
    for (int n = 0; n < in->num_streams; n++)
        advance_subtitle_cache(in->streams[n]->ds, pts);

    demuxer->events |= in->events;''')
    edit('demux/demux.c', '''        struct demux_packet *target = find_seek_target(queue, pts, flags);
        ds->reader_head = target;''', '''        struct demux_packet *target = cache_unselected_subs(ds)
            ? find_subtitle_target(queue, pts, flags)
            : find_seek_target(queue, pts, flags);
        ds->reader_head = target;''')
    # Both range joining and resuming a cached range must skip duplicate packets
    # for cached subtitle tracks, even when those tracks have no selected decoder.
    path = root / 'demux/demux.c'
    text = path.read_text(encoding='utf-8')
    old = 'ds->refreshing = ds->selected;'
    if text.count(old) != 2:
        raise RuntimeError('Native source context mismatch: cached range refresh')
    text = text.replace(old, 'ds->refreshing = stream_is_enabled(ds) && ds->queue->tail;')
    path.write_text(text, encoding='utf-8', newline='\n')
    edit('demux/demux.c', '        refresh_more |= ds->refreshing;',
         '        refresh_more |= ds->refreshing && ds->selected;')
    edit('demux/demux.c', '''    ref_pts = MP_ADD_PTS(ref_pts, -in->ts_offset);

    if (in->back_demuxing)''', '''    ref_pts = MP_ADD_PTS(ref_pts, -in->ts_offset);

    if (cache_unselected_subs(ds)) {
        // Only move the subtitle reader. A refresh seek would discard every
        // audio/video queue and re-read input that is already buffered.
        ds->reader_head = find_subtitle_target(ds->queue, ref_pts, 0);
        ds->skip_to_keyframe = !ds->reader_head;
        ds->base_ts = ref_pts;
        MP_VERBOSE(in, "select cached subtitle track %d at %f\\n",
                   stream->index, ref_pts);
        return;
    }

    if (in->back_demuxing)''')
    edit('demux/demux.c', '''// logical state, while this function returns the actual state (in case the
// demuxer attempts to cache even unselected packets for track switching - this
// will potentially be done in the future).''', '''// logical state, while this function returns the actual state. Unselected
// subtitle tracks can remain enabled to cache packets for track switching.''')
    edit('demux/demux.c', '    r = stream->ds->selected;',
         '    r = stream_is_enabled(stream->ds);')
    edit('DOCS/man/options.rst', '``--demuxer-thread=<yes|no>``\n', '''``--demuxer-cache-unselected-subs=<yes|no>``
    Keep unselected subtitle tracks in the seekable demuxer cache (default: no).
    Packets are read by the existing demuxer, within the normal forward and
    backward cache limits. Switching subtitles uses those packets without a
    refresh seek, preserving the audio/video cache and playback position.
    This does not open another input or read the entire file before playback.

    Only packets encountered during normal reading are available. This option
    requires the seekable cache and does not apply to backward playback or
    demuxers with independently fetched renditions, such as HLS and DASH.

``--demuxer-thread=<yes|no>``
''')


if __name__ == '__main__':
    apply(Path(sys.argv[1]).resolve())
    print('Applied native subtitle track caching')
