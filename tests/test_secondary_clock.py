"""Exercise the production VO clock with deterministic media and wall times.

The C functions are extracted verbatim from the maintained native patch. Only
the surrounding VO/OSD dependencies are stubbed; no clock logic is duplicated.
"""
import ast
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def clock_source():
    source = os.environ.get('MPV_CLOCK_SOURCE')
    if source:
        return Path(source).read_text(encoding='utf-8')
    tree = ast.parse((ROOT/'tools/apply_secondary_sub_sync.py').read_text(encoding='utf-8'))
    return '\n'.join(node.value for node in ast.walk(tree)
                     if isinstance(node, ast.Constant) and isinstance(node.value, str))


def function(source, name):
    match = re.search(r'(?:static )?(?:double|void) ' + name + r'\([^)]*\)\s*\{', source)
    if not match:
        raise ValueError('Missing native function: ' + name)
    depth = 1
    end = match.end()
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[match.start():end]


STUBS = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <math.h>
#include <stdio.h>
#define MP_NOPTS_VALUE (-1e20)
#define MPMAX(a,b) ((a) > (b) ? (a) : (b))
#define MPCLAMP(x,a,b) ((x) < (a) ? (a) : (x) > (b) ? (b) : (x))
#define MP_TIME_NS_TO_S(t) ((t) / 1e9)
struct image { double pts; };
struct vo_frame {
    struct image *current;
    double playback_speed, ideal_frame_vsync;
    int64_t pts;
    bool display_synced, secondary_sub_paused;
};
struct vo_internal {
    struct vo_frame *current_frame;
    double secondary_anchor_pts, secondary_clock_speed;
    int64_t secondary_anchor_ns, secondary_next_ns;
    bool secondary_clock_valid, secondary_clock_paused, paused, hasframe;
    bool dropped_frame, request_redraw;
    int lock;
};
struct vo { struct vo_internal *in; double *osd; };
static int64_t now_ns;
static int64_t mp_time_ns(void) { return now_ns; }
static void osd_set_secondary_pts(double *out, double pts) { *out = pts; }
static void mp_mutex_lock(int *lock) {}
static void mp_mutex_unlock(int *lock) {}
static void wakeup_core(struct vo *vo) {}
static void reset_vsync_timings(struct vo *vo) {}
static void wakeup_locked(struct vo *vo) {}
'''

CASES = r'''
static int checks;
static void near(double actual, double expected) {
    if (fabs(actual - expected) > 1e-6) {
        fprintf(stderr, "clock %.9f expected %.9f\n", actual, expected);
        assert(false);
    }
    checks++;
}
int main(void) {
    struct image image = { .pts = 8 };
    struct vo_frame frame = { .current = &image, .playback_speed = 1 };
    struct vo_internal in = { .current_frame = &frame, .hasframe = true };
    double pts;
    struct vo vo = { .in = &in, .osd = &pts };
    now_ns = 10000000000LL;
    update_secondary_clock(&vo, true);
    now_ns += 300000000;
    update_secondary_clock(&vo, false);
    near(pts, 8.3);

    // A seek retains its old image. Redraw must not initialize a new clock.
    in.hasframe = false;
    in.secondary_clock_valid = false;
    in.secondary_clock_paused = true;
    now_ns += 5000000000LL;
    update_secondary_clock(&vo, false);
    near(pts, MP_NOPTS_VALUE);
    assert(!in.secondary_clock_valid);
    in.hasframe = true;
    update_secondary_clock(&vo, false);
    near(pts, MP_NOPTS_VALUE);
    assert(!in.secondary_clock_valid);

    // The first target picture precedes core playback restart completion.
    image.pts = 26;
    frame.secondary_sub_paused = true;
    update_secondary_clock(&vo, true);
    near(pts, 26);
    now_ns += 10000000000LL;
    update_secondary_clock(&vo, false);
    near(pts, 26);
    frame.secondary_sub_paused = false;
    image.pts = 26.0 + 1.0 / 24;
    update_secondary_clock(&vo, true);
    near(pts, image.pts);
    now_ns += 20000000;
    update_secondary_clock(&vo, false);
    near(pts, image.pts + .02);

    // User and cache pause share the native pause transition. A fresh frame
    // after resume re-anchors even when the old clock has a large offset.
    for (int pass = 0; pass < 3; pass++) {
        vo_set_paused(&vo, true);
        double frozen = pts;
        now_ns += 7000000000LL;
        update_secondary_clock(&vo, false);
        near(pts, frozen);
        vo_set_paused(&vo, false);
        now_ns += 500000000;
        update_secondary_clock(&vo, false);
        near(pts, frozen);
        image.pts += .125;
        update_secondary_clock(&vo, true);
        near(pts, image.pts);
    }

    // Normal playback remains continuous between frames at every speed.
    double speeds[] = { .5, 1, 2 };
    for (int speed = 0; speed < 3; speed++) {
        frame.playback_speed = speeds[speed];
        in.secondary_clock_valid = false;
        image.pts = 2;
        update_secondary_clock(&vo, true);
        for (int sample = 1; sample <= 120; sample++) {
            now_ns += 10000000;
            image.pts = 2 + sample * .01 * speeds[speed];
            update_secondary_clock(&vo, sample % 4 == 0);
            near(pts, image.pts);
        }
    }

    frame.playback_speed = 1;
    frame.display_synced = true;
    frame.ideal_frame_vsync = .016;
    frame.secondary_sub_paused = true;
    image.pts = 4;
    update_secondary_clock(&vo, true);
    near(pts, 4.016);
    now_ns += 5000000000LL;
    update_secondary_clock(&vo, false);
    near(pts, 4.016);
    frame.secondary_sub_paused = false;
    image.pts = 4.042;
    update_secondary_clock(&vo, true);
    near(pts, 4.058);
    near(image.pts, 4.042); // Video/AI frame PTS is immutable.
    printf("Passed %d native clock checks\n", checks);
}
'''


class SecondaryClock(unittest.TestCase):
    def test_seek_cache_restart_pause_and_speeds(self):
        source = clock_source()
        # vo_set_paused is edited through an existing source seam. Reconstruct
        # its unmodified tail when testing the generator rather than a checkout.
        if not re.search(r'void vo_set_paused\(', source):
            start = source.index('    if (in->paused != paused) {\n        // The core')
            end = source.index('        in->paused = paused;', start) + len('        in->paused = paused;')
            source += '\nvoid vo_set_paused(struct vo *vo, bool paused) {\nstruct vo_internal *in = vo->in;\n' + source[start:end] + '\n}}\n'
        code = STUBS + '\n'.join(function(source, name) for name in
                                ('secondary_clock_pts', 'update_secondary_clock', 'vo_set_paused')) + CASES
        compiler = os.environ.get('CC', str(ROOT/'_probe/build-tools/msys/ucrt64/bin/gcc.exe'))
        with tempfile.TemporaryDirectory(prefix='secondary-clock-') as tmp:
            c = Path(tmp)/'clock.c'
            exe = Path(tmp)/'clock.exe'
            c.write_text(code, encoding='utf-8')
            env = dict(os.environ)
            env['PATH'] = str(Path(compiler).parent) + os.pathsep + env.get('PATH', '')
            build = subprocess.run([compiler, '-std=c11', str(c), '-o', str(exe)],
                                   capture_output=True, text=True, env=env)
            self.assertEqual(build.returncode, 0, build.stdout + build.stderr)
            run = subprocess.run([str(exe)], capture_output=True, text=True)
            self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
            print(run.stdout.strip())


if __name__ == '__main__':
    unittest.main()
