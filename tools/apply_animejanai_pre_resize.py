"""Keep source resizing active while the RIFE engine builds asynchronously."""
from pathlib import Path


def apply(root):
    path = Path(root) / 'video/filter/vf_animejanai.c'
    source = path.read_text(encoding='utf-8')
    replacements = [
        ('p->depth = (p->aji_active && !p->rife_on && !p->temporal_r) ? depth : 1;',
         'p->depth = (p->aji_active && !p->rife_on && !p->temporal_r && !p->has_pre_resize) ? depth : 1;'),
        ('''    // Pre-RIFE downscale: when rife-first, ask the engine whether the first
    // model's "resize before upscale" was hoisted ahead of RIFE. If so, source
    // frames are downscaled to (work_w, work_h) via api.resize before being
    // interpolated and upscaled, so the RIFE + upscale path runs at work res.
    if (p->rife_first && p->api.pre_resize && p->api.resize) {''',
         '''    // The upscale engine already expects work-resolution input while
    // RIFE is still building. Honor the shim's hoisted source resize even
    // before interpolation becomes active.
    if (p->api.pre_resize && p->api.resize) {'''),
        ('''        if (p->rife_first || (p->temporal_r && rel >= 0)) {
            // rife-first frames (the downscaled source / interp temps) are''',
         '''        if (p->rife_first || p->has_pre_resize || (p->temporal_r && rel >= 0)) {
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
    replacements.extend([
        ("    int (*rife_factor)(aji_ctx *c, int *num, int *den);",
         """    int (*rife_factor)(aji_ctx *c, int *num, int *den);
    int (*rife_factor_for_config)(const aji_create_params *, int, int, double,
                                  int *, int *);"""),
        ('        p->api.rife_factor = aji_lib_sym(p->api.handle, "aji_rife_factor");',
         '''        p->api.rife_factor = aji_lib_sym(p->api.handle, "aji_rife_factor");
        p->api.rife_factor_for_config = aji_lib_sym(p->api.handle, "aji_rife_factor_for_config");'''),
        ('static bool configure_aji(struct mp_filter *vf)\n',
         '''// Only the chain selected for these dimensions, frame rate and slot needs
// its RIFE models. Use the dispatcher's selector, including its model checks.
static void warn_missing_rife(struct mp_filter *vf)
{
    struct priv *p = vf->priv;
    if (p->cur_slot <= 0 || !p->path.rife_model_dir || !p->api.rife_factor_for_config)
        return;
    DIR *dir = opendir(p->path.rife_model_dir);
    if (dir) {
        struct dirent *entry;
        bool found = false;
        while ((entry = readdir(dir))) {
            bstr name = bstr0(entry->d_name);
            if (bstr_case_startswith(name, bstr0("rife_v")) &&
                bstr_case_endswith(name, bstr0(".onnx"))) {
                found = true;
                break;
            }
        }
        closedir(dir);
        if (found)
            return;
    }
    aji_create_params params = {
        .api_version = AJI_API_VERSION,
        .conf_path = p->path.conf,
        .model_dir = p->path.model_dir,
        .rife_model_dir = p->path.rife_model_dir,
        .slot = p->cur_slot,
    };
    int num = 0, den = 0;
    if (p->api.rife_factor_for_config(&params, p->params.w, p->params.h, p->fps,
                                      &num, &den) && den > 0 && num > den)
        MP_WARN(vf, "RIFE models are missing for slot %d\\n", p->cur_slot);
}

static bool configure_aji(struct mp_filter *vf)
'''),
        ('    p->cur_slot = p->pending_slot;\n\n    int ow = 0, oh = 0;',
         '    p->cur_slot = p->pending_slot;\n    warn_missing_rife(vf);\n\n    int ow = 0, oh = 0;'),
    ])
    for before, after in replacements:
        if source.count(before) != 1:
            raise RuntimeError('Unexpected animejanai source: ' + before[:100])
        source = source.replace(before, after)
    path.write_text(source, encoding='utf-8', newline='\r\n')
    print('Applied native AI source resize during asynchronous RIFE builds')


if __name__ == '__main__':
    import sys
    apply(Path(sys.argv[1]))
