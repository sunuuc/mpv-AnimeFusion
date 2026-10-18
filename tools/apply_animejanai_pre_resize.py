"""Keep source resizing active while the RIFE engine builds asynchronously."""
from pathlib import Path


def apply(root):
    path = Path(root) / 'video/filter/vf_animejanai.c'
    source = path.read_text(encoding='utf-8')
    replacements = [
        ('p->depth = (p->aji_active && !p->rife_on) ? depth : 1;',
         'p->depth = (p->aji_active && !p->rife_on && !p->has_pre_resize) ? depth : 1;'),
        ('''    // Pre-RIFE downscale: when rife-first, ask the engine whether the first
    // model's "resize before upscale" was hoisted ahead of RIFE. If so, source
    // frames are downscaled to (work_w, work_h) via api.resize before being
    // interpolated and upscaled, so the RIFE + upscale path runs at work res.
    if (p->rife_first && p->api.pre_resize && p->api.resize) {''',
         '''    // The upscale engine already expects work-resolution input while
    // RIFE is still building. Honor the shim's hoisted source resize even
    // before interpolation becomes active.
    if (p->api.pre_resize && p->api.resize) {'''),
        ('''        if (p->rife_first) {
            // rife-first frames (the downscaled source / interp temps) are''',
         '''        if (p->rife_first || p->has_pre_resize) {
            // Preprocessed frames (the downscaled source / interp temps) are'''),
        ('''    if (p->rife_first) {
        // RIFE-first:''',
         '''    if (p->rife_first || p->has_pre_resize) {
        // Source resizing also runs while the RIFE engine is building.
        // RIFE-first:'''),
        ('''        if (p->rife_prev && p->rife_prev->pts != MP_NOPTS_VALUE &&
            src->pts != MP_NOPTS_VALUE && src->pts > p->rife_prev->pts) {''',
         '''        if (p->rife_on && p->rife_prev &&
            p->rife_prev->pts != MP_NOPTS_VALUE &&
            src->pts != MP_NOPTS_VALUE && src->pts > p->rife_prev->pts) {'''),
    ]
    for before, after in replacements:
        if source.count(before) != 1:
            raise RuntimeError('Unexpected animejanai source: ' + before[:100])
        source = source.replace(before, after)
    path.write_text(source, encoding='utf-8', newline='\r\n')
    print('Applied native AI source resize during asynchronous RIFE builds')


if __name__ == '__main__':
    import sys
    apply(Path(sys.argv[1]))
