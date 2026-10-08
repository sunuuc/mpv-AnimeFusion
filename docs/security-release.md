# 安全发布说明

## 2026-10-01 发布暂停

360 云查杀报告 `Trojan.Generic`；用户提供的 360 静态分析报告给出的规则为 `Win64/Heur.Generic.H8oAbrkA`。

受影响文件为 `animejanai/danmaku/DanmakuFactory.exe`，524.50 KiB：

- SHA-256：`db3734fb45118ba08c4929214d4aef000875c2e80ac04130651bc482e5d9b58f`
- MD5：`bbbfb71e4f670c9d9fec22dcbeb40f4f`

已将正式版 1.1.9 改为草稿，暂停公开下载。原副本维持隔离。不会将改名、加壳、修改无关字节或要求用户关闭杀毒作为解决办法。

该文件由固定来源的 DanmakuFactory 源码和固定哈希的工具链构建，未进行 Authenticode 签名。源码审计未发现新增联网、进程注入或自启动代码。ClamAV 1.5.4 独立扫描发布副本和本地重建副本均未检出，但单一引擎扫描通过不能推翻 360 的报告。重建副本未逐字节复现发布副本，不能将其用作原副本安全的证明。

尚未确认真阳性或误报，也未取得 360 官方复核结论。此前功能测试没有覆盖杀毒检测。

## 1.2.0 新构建

用户已测试并接受重新构建的转换器，随后要求替换本机程序并重新公开发布。采用该新构建继续发布；没有取得或冒称取得 360 官方复核。旧哈希的阻断继续保留，旧包不再分发。

- 新转换器 SHA-256：`f6d3f9d5cd99fda0784ef35525f077387ad479f97b8ed4538d870810bb30fca1`
- 固定上游源码、项目修改与工具链重新编译得到相同字节；转换与密集弹幕回归通过。
- 用户本机确认无问题只是本机反馈，不是所有杀毒引擎的认可，也不等同于针对旧文件的官方复核。

## 发布检查

1. 已报告的旧 SHA-256 出现在任何文件名下都阻断发布。
2. 保留来源、完整源码、构建参数与 SHA-256；不修改杀毒设置、不恢复隔离、不添加信任、不使用加壳或无关字节修改。
3. 最终可执行文件、原生库和脚本使用固定来源的 ClamAV 与官方更新数据库完整扫描；规则过期、扫描不完整、检出或扫描器失败均阻断发布。
4. 压缩、解压、上传后分别核对 SHA-256。策略或文件变化、扫描超过 24 小时均重新检查。
5. 对任何后续实际检出继续记录并处理，不宣称所有杀毒软件永远不会报警。

扫描记录和构建来源随版本提供。代码签名确认发行者身份和完整性，本版没有伪造或自签可信签名。

```powershell
python tools/standalone/test_security_verify.py
python tools/standalone/security_verify.py preflight
python tools/standalone/security_verify.py scan stage complete-evidence/security
```

官方说明：[360 软件复核入口](https://open.soft.360.cn/report.php)、[ClamAV 扫描说明](https://docs.clamav.net/manual/Usage/Scanning.html)、[Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)。

## 1.2.0 .NET 发布方式

播放器、管理器和下载器使用微软标准的 self-contained 文件夹发布，运行库与应用分别保存，仍然无需用户安装 .NET。最终发布对所有 EXE、DLL 和脚本逐个扫描，不排除运行库，也不修改检测规则。

本地单文件候选包的 ClamAV 1.5.4 检测命中 `Win.Malware.Aotera-10060486-0`。其数据库规则同时查找五段 .NET 通用运行库字符串；该命中尚未获得厂商复核。候选单文件包不发布，改为标准独立运行库文件并重新完整扫描。

转换器固定使用 `x86_64-windows-gnu` 与 `-mcpu=baseline`，避免默认本机 CPU 优化使发布包依赖构建机器的 AVX 等指令。`-ffile-prefix-map` 固定源文件路径，从不同检出目录生成相同文件。此前未固定目标的云端构建曾重新生成旧报告哈希，安全门已拒绝该候选包；旧哈希仍然阻断。
