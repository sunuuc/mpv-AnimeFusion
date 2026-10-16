"""Record completed playback sessions in mpv's existing watch-history file."""
from pathlib import Path
import sys


def apply(root):
    def edit(name, old, new):
        path = root / name
        text = path.read_text(encoding='utf-8')
        if text.count(old) != 1:
            raise RuntimeError('Native source context mismatch: ' + name + ' / ' + old[:80])
        path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

    edit('player/core.h', '    double playback_pts;\n', '''    double playback_pts;
    // First and most recent presented positions in this opened file. Unlike
    // playback_pts, these survive seeking and draining the output chains.
    double watch_history_start;
    double watch_history_end;
    int64_t watch_history_time;
''')
    edit('player/loadfile.c', '#include <inttypes.h>\n',
         '#include <inttypes.h>\n#include <math.h>\n')
    edit('player/loadfile.c', '#include <libavutil/avutil.h>\n',
         '#include <libavutil/avutil.h>\n#include <libavcodec/codec_par.h>\n')
    edit('player/loadfile.c', '#include "misc/json.h"\n',
         '#include "misc/json.h"\n#include "misc/node.h"\n')
    edit('player/loadfile.c', 'static void append_to_watch_history(struct MPContext *mpctx)\n', '''// Snapshot the same in-memory stream headers used by track-list and
// file-format. Do not probe the stream, query its size, or open another input.
static void append_history_media_info(struct MPContext *mpctx, mpv_node *entry)
{
    struct demuxer *demuxer = mpctx->demuxer;
    if (!demuxer)
        return;

    mpv_node *info = node_map_add(entry, "media_info", MPV_FORMAT_NODE_MAP);
    const char *container = demuxer->filetype ? demuxer->filetype : demuxer->desc->name;
    if (container && container[0])
        node_map_add_string(info, "container", container);
    mpv_node *tracks = node_map_add(info, "tracks", MPV_FORMAT_NODE_ARRAY);
    for (int n = 0; n < mpctx->num_tracks; n++) {
        struct track *track = mpctx->tracks[n];
        if (track->is_external || !track->stream)
            continue;
        struct mp_codec_params p = *track->stream->codec;
        mpv_node *item = node_array_add(tracks, MPV_FORMAT_NODE_MAP);
        node_map_add_int64(item, "id", track->user_tid);
        node_map_add_string(item, "type", stream_type_name(track->type));
        node_map_add_flag(item, "selected", track->selected);
        if (track->title && track->title[0])
            node_map_add_string(item, "title", track->title);
        if (track->lang && track->lang[0])
            node_map_add_string(item, "language", track->lang);
        if (p.codec && p.codec[0])
            node_map_add_string(item, "codec", p.codec);
        if (p.codec_desc && p.codec_desc[0])
            node_map_add_string(item, "codec_description", p.codec_desc);
        if (p.codec_profile && p.codec_profile[0])
            node_map_add_string(item, "profile", p.codec_profile);
        if (p.format_name && p.format_name[0])
            node_map_add_string(item, "format", p.format_name);
        if (p.lav_codecpar && p.lav_codecpar->bits_per_raw_sample > 0)
            node_map_add_int64(item, "bit_depth", p.lav_codecpar->bits_per_raw_sample);
        if (p.bitrate > 0)
            node_map_add_int64(item, "bitrate", p.bitrate);
        if (track->type == STREAM_VIDEO) {
            if (p.disp_w > 0)
                node_map_add_int64(item, "width", p.disp_w);
            if (p.disp_h > 0)
                node_map_add_int64(item, "height", p.disp_h);
            if (isfinite(p.fps) && p.fps > 0)
                node_map_add_double(item, "fps", p.fps);
        } else if (track->type == STREAM_AUDIO) {
            if (p.samplerate > 0)
                node_map_add_int64(item, "sample_rate", p.samplerate);
            if (p.channels.num > 0) {
                node_map_add_int64(item, "channels", p.channels.num);
                node_map_add_string(item, "channel_layout", mp_chmap_to_str(&p.channels));
            }
        }
    }
}

static void append_to_watch_history(struct MPContext *mpctx)
''')
    edit('player/loadfile.c', '    bstr dst = {0};\n',
         '    append_history_media_info(mpctx, &node);\n\n    bstr dst = {0};\n')
    edit('player/loadfile.c', '''    if (!mpctx->opts->save_watch_history)
        return;
''', '''    if (!mpctx->opts->save_watch_history ||
        mpctx->watch_history_start == MP_NOPTS_VALUE)
        return;
''')
    edit('player/loadfile.c', '    list->num = title ? 3 : 2;\n',
         '    list->num = title ? 7 : 6;\n')
    edit('player/loadfile.c', '        .u.int64 = time(NULL),\n',
         '        .u.int64 = mpctx->watch_history_time,\n')
    edit('player/loadfile.c', '''    if (title) {
        list->keys[2] = "title";
        list->values[2] = (struct mpv_node) {
''', '''    list->keys[2] = "start";
    list->values[2] = (struct mpv_node) {
        .format = MPV_FORMAT_DOUBLE,
        .u.double_ = mpctx->watch_history_start,
    };
    list->keys[3] = "end";
    list->values[3] = (struct mpv_node) {
        .format = MPV_FORMAT_DOUBLE,
        .u.double_ = mpctx->watch_history_end,
    };
    list->keys[4] = "duration";
    double duration = get_time_length(mpctx);
    list->values[4] = (struct mpv_node) {
        .format = isfinite(duration) && duration >= 0
                ? MPV_FORMAT_DOUBLE : MPV_FORMAT_NONE,
        .u.double_ = duration,
    };
    list->keys[5] = "end_time";
    list->values[5] = (struct mpv_node) {
        .format = MPV_FORMAT_INT64,
        .u.int64 = time(NULL),
    };
    if (title) {
        list->keys[6] = "title";
        list->values[6] = (struct mpv_node) {
''')
    edit('player/loadfile.c', '    mpctx->shown_vframes = 0;\n', '''    mpctx->shown_vframes = 0;
    mpctx->watch_history_start = MP_NOPTS_VALUE;
    mpctx->watch_history_end = MP_NOPTS_VALUE;
    mpctx->watch_history_time = time(NULL);
''')
    edit('player/loadfile.c', '    append_to_watch_history(mpctx);\n\n', '')
    edit('player/loadfile.c', '''    if (mpctx->stop_play != AT_END_OF_FILE)
        clear_audio_output_buffers(mpctx);
''', '''    // Save before clearing the streams and per-file metadata. Failed opens
    // without a presented playback position do not create history entries.
    append_to_watch_history(mpctx);

    if (mpctx->stop_play != AT_END_OF_FILE)
        clear_audio_output_buffers(mpctx);
''')
    edit('player/playloop.c', '''        if (mpts != MP_NOPTS_VALUE)
            mpctx->playback_pts = mpts;
    }
}

// We always make sure audio and video buffers are filled''', '''        if (mpts != MP_NOPTS_VALUE)
            mpctx->playback_pts = mpts;
    }

    // A seek target is not a presented position. Wait for playback restart,
    // then use the same clock as time-pos, including reverse playback.
    if (mpctx->restart_complete && mpctx->playback_pts != MP_NOPTS_VALUE &&
        isfinite(mpctx->playback_pts))
    {
        double position = MPMAX(mpctx->playback_pts * mpctx->play_dir, 0);
        if (mpctx->watch_history_start == MP_NOPTS_VALUE)
            mpctx->watch_history_start = position;
        mpctx->watch_history_end = position;
    }
}

// We always make sure audio and video buffers are filled''')
    edit('DOCS/man/options.rst', '''    path, and its ``title`` field is the title when it was available.
''', '''    path, and its ``title`` field is the title when it was available. Each
    completed playback session appends a separate entry. ``start`` and ``end``
    are the first and last presented playback positions in seconds; ``end``
    can precede ``start`` after seeking. ``duration`` is the full media length
    in seconds, or null if unknown. ``end_time`` is the UNIX timestamp when
    playback ended. ``media_info`` contains the container and the internal
    tracks' known codec parameters from this playback session. Saving it does
    not reopen or probe the media. Files that fail before playback do not
    create an entry.
''')
    print('Applied native playback-session watch history')


if __name__ == '__main__':
    apply(Path(sys.argv[1] if len(sys.argv) > 1 else 'mpv-source'))
