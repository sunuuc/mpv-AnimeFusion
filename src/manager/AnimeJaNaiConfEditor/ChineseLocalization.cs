using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AnimeJaNaiConfEditor;

/// <summary>
/// Simplified Chinese localization overlay for the personal zh-CN build.
///
/// This deliberately lives outside the upstream XAML/view-model strings so the fork can
/// keep pulling upstream changes with minimal conflicts.  SetCurrentValue is used so
/// bindings stay alive: dynamic status text and dialog content can be translated again
/// whenever their source changes.
/// </summary>
public static class ChineseLocalization
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
    {
        ["mpv-AnimeFusion Manager"] = "mpv-AnimeFusion 管理器",
        ["Profiles"] = "配置方案",
        ["Global Settings"] = "全局设置",
        ["Default Profiles (Read-only)"] = "默认配置（只读）",
        ["Custom Profiles"] = "自定义配置",
        ["Tools"] = "工具",
        ["Run Benchmarks"] = "运行性能测试",
        ["Submit to Catalog"] = "提交到性能目录",
        ["Enable Logging"] = "启用日志",
        ["Upscaling Backend"] = "超分后端",
        ["Switching the backend takes effect after you restart the player."] = "切换后端后，需要重启播放器才能生效。",
        ["TensorRT Engine Settings"] = "TensorRT 引擎设置",
        ["Engine Type"] = "引擎类型",
        ["Static ONNX"] = "静态 ONNX",
        ["Static"] = "静态",
        ["Dynamic"] = "动态",
        ["Min Resolution"] = "最低分辨率",
        ["Opt Resolution"] = "最佳分辨率",
        ["Max Resolution"] = "最高分辨率",
        ["Builder Optimization Level"] = "引擎构建优化等级",
        ["GPU Subtitle Rendering (Experimental)"] = "GPU 字幕渲染（实验性）",
        ["Subtitle rendering changes take effect after you restart the player."] = "字幕渲染设置更改后，需要重启播放器才能生效。",
        ["Import Full Config From File"] = "从文件导入完整配置",
        ["Export Full Config To File"] = "将完整配置导出到文件",
        ["Profile Name"] = "配置名称",
        ["Model Preset"] = "模型预设",
        ["Standard"] = "标准",
        ["Sharp"] = "锐化",
        ["Set as Default Profile in mpv"] = "设为 mpv 默认配置",
        ["Use this profile by default when launching mpv."] = "启动 mpv 时默认使用此配置。",
        ["Profile edits apply when you switch profiles in the player, or after you restart it."] = "修改配置后，在播放器中切换配置或重启播放器即可生效。",
        ["Activation Condition"] = "启用条件",
        ["Resolution Range"] = "分辨率范围",
        ["FPS Range"] = "帧率范围",
        ["Upscale Settings"] = "超分设置",
        ["Resize Height Before Upscale"] = "超分前缩放高度",
        ["Resize Factor Before Upscale"] = "超分前缩放比例",
        ["Model"] = "模型",
        ["Open Models Directory"] = "打开模型目录",
        ["Enable RIFE Interpolation"] = "启用 RIFE 补帧",
        ["Whether or not to run RIFE video frame interpolation."] = "是否启用 RIFE 视频补帧。",
        ["Copy Selected Profile To Current Profile"] = "将选中配置复制到当前配置",
        ["Import Profile Config From File"] = "从文件导入配置",
        ["Export Profile Config To File"] = "将配置导出到文件",
        ["Add Model"] = "添加模型",
        ["RIFE models are not installed. Install them from the Components tab to use interpolation."] = "尚未安装 RIFE 模型。请在“组件”选项卡中安装后再使用补帧。",
        ["Interpolation Factor"] = "补帧倍数",
        ["Ensemble"] = "集成模式",
        ["Scene Detection Threshold"] = "转场检测阈值",
        ["Interpolate before upscaling"] = "先补帧，再超分",
        ["Add Chain"] = "添加处理链",
        ["Components"] = "组件",
        ["Components recommended for this PC are not installed. Check the highlighted items below, then click Apply changes to install them."] = "尚未安装此电脑推荐的组件。请查看下方高亮项目，然后点击“应用更改”进行安装。",
        ["recommended for this PC"] = "推荐用于此电脑",
        ["Apply changes"] = "应用更改",
        ["Refresh"] = "刷新",
        ["installed"] = "已安装",
        ["optional"] = "可选",
        ["Detecting hardware..."] = "正在检测硬件……",
        ["Checking installed components..."] = "正在检查已安装组件……",
        ["Nothing to change."] = "没有需要更改的内容。",
        ["Done."] = "完成。",
        ["unknown error"] = "未知错误",
        ["Could not load component information."] = "无法读取组件信息。",
        ["mpv-AnimeFusionUpdater.exe not found next to the install - component management unavailable."] = "安装目录中未找到 mpv-AnimeFusionUpdater.exe，无法管理组件。",
        ["GPU: no NVIDIA device detected — the built-in DirectML engine covers AMD and Intel GPUs"] = "GPU：未检测到 NVIDIA 设备；内置 DirectML 后端可用于 AMD 和 Intel GPU",
        ["TensorRT runtime"] = "TensorRT 运行库",
        ["RIFE interpolation models"] = "RIFE 补帧模型",
        ["Update first, then manage components."] = "请先更新软件，再管理组件。",

        ["Measure your real playback fps (the speed you will actually get) at several resolutions. mpv windows will open and close on their own during the test; leave them alone or the results will be invalid. Can take 10+ minutes depending on your hardware."] = "测试多个分辨率下的实际播放帧率（也就是你真正能获得的速度）。测试期间 mpv 窗口会自动打开和关闭，请不要操作，否则结果会失效。根据硬件不同，测试可能需要 10 分钟以上。",
        ["Share your latest benchmark results with the community catalog. You'll see exactly what is sent before anything leaves your machine. No account required."] = "把最新性能测试结果分享到社区目录。发送前会明确显示上传内容，不需要账号。",
        ["Enable logging to view which models are being used and which resolutions they are scaling from.\n                The log file is created at"] = "启用日志后可以查看正在使用的模型以及从什么分辨率进行超分。\n                日志文件会创建在",
        ["Fastest option but NVIDIA only. Supports fp16 and fp32 ONNX models. fp16 models are recommended for faster performance and reduced VRAM usage without any loss in quality. The initial engine generation may require several minutes but only needs to be done once."] = "速度最快，但仅支持 NVIDIA。支持 fp16 和 fp32 ONNX 模型。推荐使用 fp16，可在不损失画质的情况下提升速度并减少显存占用。首次生成引擎可能需要几分钟，但只需进行一次。",
        ["Compatible with NVIDIA, AMD, and Intel GPUs but slower than TensorRT."] = "兼容 NVIDIA、AMD 和 Intel GPU，但速度慢于 TensorRT。",
        ["Which input resolutions the engine is built for. Static ONNX uses the ONNX model's shape. Static builds for a single resolution and rebuilds when it changes. Dynamic builds one engine for the resolution range specified below."] = "决定引擎针对哪些输入分辨率构建。“静态 ONNX”使用 ONNX 模型自身的尺寸；“静态”针对单一分辨率构建，分辨率变化时重新构建；“动态”针对下方指定的分辨率范围构建一个通用引擎。",
        ["Higher levels (0-5) take longer to build the engine but can produce a faster engine. Default is 5."] = "等级越高（0–5），引擎构建时间越长，但生成的引擎可能更快。默认值为 5。",
        ["Switches this profile between the standard and sharper V3.1 models."] = "在标准 V3.1 模型与更锐利的 V3.1 模型之间切换此配置。",
        ["Range of video resolutions to activate this chain. Select a common resolution from the drop down or type a custom resolution. Larger resolutions require more processing power to upscale. A maximum resolution of 0x0 means that the chain will run on all video resolutions."] = "用于启用此处理链的视频分辨率范围。可以从下拉菜单选择常见分辨率，也可以手动输入。分辨率越高，超分所需算力越大。最高分辨率设为 0x0 表示对所有视频分辨率启用此处理链。",
        ["Range of video fps (frames per second) to activate this chain. Higher fps videos require more processing power to upscale. A maximum fps of 0 means the chain will run on all video fps."] = "用于启用此处理链的视频帧率范围。帧率越高，超分所需算力越大。最高帧率设为 0 表示对所有视频帧率启用此处理链。",
        ["Resize the video to this height before running this model, set to 0 to disable. Downscaling the video improves performance; for most sources downscaling reduces quality but some blurry sources may see improved sharpness with the right amount of downscaling."] = "运行此模型前先把视频缩放到该高度，设为 0 表示关闭。降低分辨率可以提升性能；通常会降低画质，但对部分较模糊的片源，适当降采样反而可能改善锐度。",
        ["Resize the video by this factor before running this model. The video width and height are scaled by this percentage, so a value of 100% performs no resize, and a value of 50% cuts the width and height of the video in half. This setting is ignored if Resize Height Before Upscale is specified."] = "运行此模型前按该比例缩放视频。宽和高都会按此百分比缩放：100% 表示不缩放，50% 表示宽高都减半。如果已经指定“超分前缩放高度”，则忽略此设置。",
        ["The upscaling model to run. To choose from more models, add ONNX model files to the models directory."] = "选择要运行的超分模型。如需更多模型，请把 ONNX 模型文件放入模型目录。",
        ["Interpolation factor represented as a fraction. For example, 2 / 1 will double the framerate."] = "补帧倍数使用分数表示。例如 2 / 1 会把帧率提升为 2 倍。",
        ["Interpolation model to run. Higher RIFE version is newer and usually better quality."] = "选择补帧模型。通常 RIFE 版本越高越新，画质也更好。",
        ["Whether or not to use the ensemble version of the model, which improves quality but runs slower."] = "是否使用模型的集成版本。开启后可提高质量，但运行速度更慢。",
        ["Scene detection is used to prevent interpolating frames during hard transitions between scenes. The threshold adjusts the sensitivity for what is considered a scene change. If the threshold is too high, more interpolation artifacts may be visible during scene transitions. If the threshold is too low, the video may stutter more."] = "转场检测用于避免在镜头硬切时生成中间帧。该阈值控制判定镜头切换的灵敏度：阈值过高可能在转场时出现更多补帧伪影；阈值过低则可能造成更多卡顿。",
        ["Run RIFE at the source resolution before upscaling (faster, recommended). Uncheck to interpolate the upscaled frames instead."] = "先在源分辨率下运行 RIFE，再进行超分（更快，推荐）。取消勾选则先超分，再对超分后的画面补帧。",
        ["A chain is a a set of upscale settings which can be activated based on conditions such as the video resolution and fps. Videos with higher resolution and fps are more demanding to upscale, so setting up multiple chains allows you to select the most suitable model for each video type based on the capabilities of your hardware."] = "处理链是一组超分设置，可根据视频分辨率、帧率等条件自动启用。分辨率和帧率越高，对性能要求越高，因此可以建立多个处理链，让不同类型的视频自动使用最适合你硬件性能的模型。",

        ["On"] = "开启",
        ["Off"] = "关闭",
        ["OK"] = "确定",
        ["Cancel"] = "取消",
        ["Close"] = "关闭",
        ["Yes"] = "是",
        ["No"] = "否",
        ["Start benchmark"] = "开始性能测试",
        ["Submit"] = "提交",
        ["Data to submit:"] = "将提交的数据：",
        ["Submitted"] = "已提交",
        ["Submission failed"] = "提交失败",
        ["No benchmark results yet"] = "还没有性能测试结果",
        ["No benchmark results found"] = "未找到性能测试结果",
        ["Couldn't read benchmark results"] = "无法读取性能测试结果",
        ["Run playback benchmark"] = "运行播放性能测试",
        ["Submit benchmark to community catalog"] = "将性能测试提交到社区目录",
        ["Optional: a name or handle to credit you (blank = anonymous)"] = "可选：填写昵称或名称（留空则匿名）",
        ["Optional note: anything notable not already captured above (e.g. undervolt, cooling, laptop on battery)"] = "可选备注：填写上方未记录的重要信息（例如降压、散热方式、笔记本电池供电等）",
        ["Import Profile Conf File"] = "导入配置文件",
        ["Import Full Conf File"] = "导入完整配置文件",
        ["Export Full Conf File"] = "导出完整配置文件",
        ["Export Current Profile Conf File"] = "导出当前配置文件",
        ["Confirm Full Conf Import"] = "确认导入完整配置",
        ["Confirm Profile Conf Import"] = "确认导入配置",
        ["mpv-AnimeFusion Conf File"] = "mpv-AnimeFusion 配置文件",
        ["mpv-AnimeFusion Profile Conf File"] = "mpv-AnimeFusion 配置方案文件",
        ["mpv-AnimeFusion Conf File (*.conf)"] = "mpv-AnimeFusion 配置文件 (*.conf)",
        ["mpv-AnimeFusion Profile Conf File (*.pconf)"] = "mpv-AnimeFusion 配置方案文件 (*.pconf)",
    };

    private static readonly (string English, string Chinese)[] Phrases =
    [
        (" (Default)", "（默认）"),
        ("Default Profile", "默认配置"),
        ("Custom Profile", "自定义配置"),
        ("Remove Model", "移除模型"),
        ("Remove Chain", "移除处理链"),
        ("Chain ", "处理链 "),
        ("Model ", "模型 "),
        ("GPU: ", "GPU："),
        ("Installing ", "正在安装 "),
        ("Removing ", "正在移除 "),
        (" failed: ", " 失败："),
        ("Update available", "有可用更新"),
        ("Update first", "请先更新"),
        ("up to date", "已是最新版本"),
        ("not installed", "未安装"),
        ("not found", "未找到"),
        ("recommended", "推荐"),
        ("optional", "可选"),
        ("installed", "已安装"),
        ("Checking ", "正在检查 "),
        ("Loading ", "正在加载 "),
        ("Downloading ", "正在下载 "),
        ("Installing...", "正在安装……"),
        ("Please wait", "请稍候"),
        ("Error", "错误"),
        ("Warning", "警告"),
    ];

    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        int start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        int end = text.Length - 1;
        while (end >= start && char.IsWhiteSpace(text[end])) end--;

        string core = start <= end ? text[start..(end + 1)] : text;
        if (Exact.TryGetValue(core, out var exact))
            return text[..start] + exact + text[(end + 1)..];

        // Common dynamic labels generated by StringFormat or view-model status text.
        var m = Regex.Match(core, @"^Chain (\d+)$");
        if (m.Success) return text[..start] + $"处理链 {m.Groups[1].Value}" + text[(end + 1)..];
        m = Regex.Match(core, @"^Model (\d+)$");
        if (m.Success) return text[..start] + $"模型 {m.Groups[1].Value}" + text[(end + 1)..];
        m = Regex.Match(core, @"^Remove Chain (\d+)$");
        if (m.Success) return text[..start] + $"移除处理链 {m.Groups[1].Value}" + text[(end + 1)..];
        m = Regex.Match(core, @"^Remove Model (\d+)$");
        if (m.Success) return text[..start] + $"移除模型 {m.Groups[1].Value}" + text[(end + 1)..];
        m = Regex.Match(core, @"^GPU: (.+)$");
        if (m.Success) return text[..start] + $"GPU：{m.Groups[1].Value}" + text[(end + 1)..];
        m = Regex.Match(core, @"^Installing (.+) failed: (.+)$");
        if (m.Success) return text[..start] + $"安装 {m.Groups[1].Value} 失败：{m.Groups[2].Value}" + text[(end + 1)..];
        m = Regex.Match(core, @"^Removing (.+) failed: (.+)$");
        if (m.Success) return text[..start] + $"移除 {m.Groups[1].Value} 失败：{m.Groups[2].Value}" + text[(end + 1)..];
        m = Regex.Match(core, @"^Removing (.+)\.\.\.$");
        if (m.Success) return text[..start] + $"正在移除 {m.Groups[1].Value}……" + text[(end + 1)..];

        string result = text;
        foreach (var (english, chinese) in Phrases)
            result = result.Replace(english, chinese, StringComparison.Ordinal);
        return result;
    }

    public static void Apply(Window window)
    {
        TranslateVisual(window);
        foreach (var visual in window.GetVisualDescendants())
            TranslateVisual(visual);
    }

    private static void TranslateVisual(object value)
    {
        if (value is Window window && window.Title is { Length: > 0 } title)
            window.SetCurrentValue(Window.TitleProperty, Translate(title));

        if (value is HeaderedContentControl headered && headered.Header is string header)
            headered.SetCurrentValue(HeaderedContentControl.HeaderProperty, Translate(header));

        if (value is ContentControl contentControl && contentControl.Content is string content)
            contentControl.SetCurrentValue(ContentControl.ContentProperty, Translate(content));

        if (value is TextBox textBox && textBox.Watermark is string watermark)
            textBox.SetCurrentValue(TextBox.WatermarkProperty, Translate(watermark));

        if (value is TextBlock textBlock)
        {
            if (textBlock.Inlines is { Count: > 0 })
            {
                foreach (var inline in textBlock.Inlines)
                    TranslateInline(inline);
            }
            else if (textBlock.Text is { Length: > 0 } text)
            {
                textBlock.SetCurrentValue(TextBlock.TextProperty, Translate(text));
            }
        }

        if (value is Control control && ToolTip.GetTip(control) is string tip)
            ToolTip.SetTip(control, Translate(tip));
    }

    private static void TranslateInline(Inline inline)
    {
        if (inline is Run run && run.Text is { Length: > 0 } text)
            run.SetCurrentValue(Run.TextProperty, Translate(text));

        if (inline is Span span)
        {
            foreach (var child in span.Inlines)
                TranslateInline(child);
        }
    }
}
