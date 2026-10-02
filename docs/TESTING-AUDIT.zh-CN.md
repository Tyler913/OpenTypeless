# 测试覆盖审计与改进方案

审计日期：2026-10-02。基于工作区提交 `c3c5c48`、现有源码/测试依赖关系、GitHub Actions 历史日志，以及本次本地测试结果。本文区分已有能力、本次已写入的改动和后续 proposal；新工作流仍须在 GitHub 的四种原生运行器上验证。

## 结论

当前 CI 不能支持“绿灯意味着整个软件基本没有问题”的判断。核心算法和模拟网络故障的测试基础较好，但实际应用的状态编排、系统集成和页面交互仍有明显缺口，而且存在已经发生过的 UI 崩溃漏报。

可以在 CI 阶段发现大量 UI 和功能问题，原生 Swift / WinUI 3 不妨碍自动化。需要分别验证算法、应用服务、真实页面操作、视觉回归、分发与升级、硬件/操作系统集成。覆盖率只是其中一种证据，不能证明所有使用方式正确。

本次进行了全源码树的测试边界盘点，并重点检查 UI 渲染、会话编排、音频输入、权限、快捷键、剪贴板/粘贴、历史记录、凭据、更新器及发布链路。它不是对每条业务分支无缺陷的证明，也不是已完成全面人工体验测试。

## 现有覆盖范围

源码盘点不含构建生成物：macOS 56 个 Swift 文件，约 12,000 行；Windows 77 个 C# 文件，约 17,100 行，另有 XAML。两个测试项目都只依赖 `TypelessCore`，并不引用各自的原生应用目标。

| 层次 / 模块 | macOS | Windows | 实际保证与空缺 |
|---|---|---|---|
| WAV、切块、静音、预录缓存、进度、文本拼接 | Swift Testing | xUnit | 有正常/边界/共享 JSON 用例；不经过实际声卡驱动 |
| 转写、重试、并发备援、流式整理、预连接 | URLProtocol 模拟网络 | HttpMessageHandler 模拟网络 | 130 秒合成录音、503 后重试、永久失败保留部分文字、401 不重试、推测尾段、备援竞速等已有测试；不是实时外部服务测试 |
| 费用、词数、用量账本、词汇学习、编辑识别 | 核心测试 | 核心测试 | 单独模块有断言；没有验证真实会话重试后落盘、粘贴、计费的整条组合链路 |
| 更新检查 | 版本/资源选择、digest 解析测试 | 同左 | 没有执行下载验证、覆盖安装、回滚或更新后重启 |
| 性能 | ARM64 的 Release 测试 | x64 的 Release 测试 | 合成音频、切块、学习和历史统计预算；不是实际 UI 帧率、长时间内存或设备性能测试 |
| SessionController | 765 行原生编排，无专门测试目标 | 856 行原生编排，无专门测试目标 | 按住/点按/免手持、取消、恢复、历史重试、粘贴和清理等组合行为缺少端到端断言 |
| AudioRecorder / LivePreview / OutputMute | AVFoundation / 系统语音识别 / 音频设备 | WASAPI / Windows 语音识别 / 音量控制 | 构建覆盖；无真实设备插拔、麦克风占用、采样率变化、崩溃后解除静音测试 |
| Hotkey / FocusProbe / TextInserter / EditWatcher | CGEvent / Accessibility / Pasteboard | hooks / UI Automation / Win32 clipboard | 核心 TypedEdit 测试不等于真实焦点、快捷键或剪贴板集成测试 |
| Settings / History / Credentials / UsageStore | UserDefaults、文件、旧钥匙串迁移 | JSON、Credential Manager、注册表 | 缺少重启持久化、损坏文件、磁盘失败、迁移等集成测试 |
| 页面、引导、菜单、HUD | 生成截图，只验证一张 popover 文件非空 | 仅 x64 生成截图，可忽略失败 | 没有实际按钮点击、输入/保存、导航、窗口关闭重开、布局基准比较 |
| 安装与分发 | 两架构 DMG/ZIP、签名、架构、可执行文件一致性、截图启动 | 两架构安装、启动存活、快捷方式/注册表、卸载 | 有价值的 smoke tests；ZIP 原先没有解压启动，安装后存活 15 秒不证明可操作 |
| 翻译 / 评测 / 包管理器 | 翻译字符串静态检查 | 同左 | 原先没有评测数据结构、模板渲染或 CI 自身门禁的回归测试；eval/ 不触发平台测试 |

macOS 的 `Package.swift` 声明 macOS 26；Windows 应用使用 WinUI 3 / .NET 10，最低版本声明为 Windows 10 build 19041。CI 的 `windows-latest` 环境并不能证明这个最低客户端版本可用。运行器环境随镜像变化，要以每次 job 的系统信息为准。[GitHub 运行器文档](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)

## 已证实的问题

### P1：Windows UI 实际崩溃，但 CI 通过

[PR CI 36994214517](https://github.com/Tyler913/OpenTypeless/actions/runs/36994214517) 整体成功，但 Windows x64 的截图启动日志包含：

```text
exit code -1073741189 after 3 s
Faulting module name: CoreMessagingXP.dll
Exception code: 0xc000027b
No files were found ... snapshots. No artifacts will be uploaded.
```

原工作流的 `continue-on-error: true`、未校验进程退出码、没有检查截图集合，以及应用捕获异常后退出成功，共同造成漏报。这只能确认截图模式曾崩溃，不能据此认定正常用户启动也一定崩溃，或推断具体根因。

后一次主分支 CI 的 x64 截图成功，进一步说明不能把某次成功当作此问题不存在。新门禁会使同类失败变红，并保留进程、事件和图片证据；本次没有声称已经修复 `CoreMessagingXP.dll` 崩溃根因。

### P1：主要用户旅程没有自动化行为断言

两个测试项目的依赖均止于 `TypelessCore`。`SessionController` 自行创建 recorder、settings/history 单例、HUD、clipboard/input 等原生对象，尚无方便注入的测试边界。

例如，当前测试可以证明“管线能保留失败前的文字”，却没有证明“用户从历史点击重试后，只补发失败段、历史状态变成完成、费用没有漏计/重复计、结果正确进入剪贴板、HUD 回到空闲”。

### P2：截图不是交互测试，也不是视觉回归

macOS 原先仅要求 `popover-en.png` 非空，其余页面缺图仍可通过。部分截图写入用了 `try?`，错误被忽略。Windows 的错误也可能只写入 `error.txt`。两端都只在 CI 渲染英文；Windows ARM64 不渲染 UI。

截图模式直接构造页面和 HUD 状态，且 `--demo` 跳过真实权限判断。它不证明权限申请可用、录音按钮有作用或失败状态能通过真实操作到达。macOS 的 offscreen 截图主要是设置详情，不包含完整系统窗口合成；WinUI RenderTargetBitmap 不包含真实系统 backdrop。当前没有可以批准/对比的图片基准。

### P2：卸载判定存在异步完成窗口，缺少诊断

[主分支 CI 36994950873](https://github.com/Tyler913/OpenTypeless/actions/runs/36994950873) 在 ARM64 因 `the desktop shortcut is still there` 失败；[同一提交的 Release 36994991874](https://github.com/Tyler913/OpenTypeless/actions/runs/36994991874) 成功。

原检查只等安装目录消失，随后立即断言快捷方式与注册表也消失。这不足以等待所有异步卸载后置条件。本次改为等待整组条件，仍对实际残留失败，并保存卸载日志和退出码。这是对检查可靠性的修正；仅靠旧日志不能确认此前残留一定是假阳性。

### P2：发布和共享检查、路径过滤未完全对齐

原 Release 只调用平台构建，未执行顶层翻译检查。`eval/` 和发布工作流变更未覆盖对应的平台构建；只改 Swift 提示词不会运行 Windows 中已有的 `PromptIsIdenticalToMacOS` 测试。

`CI passed` 原来只排除 failure/cancelled，不能区分“合理跳过”与“本该运行但被跳过”。新门禁按 change detection 的预期逐项检查。

GitHub ruleset 已要求 `CI passed`；这次没有修改仓库保护设置。现有事件范围是 main 的 push 和面向 main 的 PR，并非任意分支的一次 push 都会自动构建。

## 本次已写入的改动

1. **严格 UI 渲染门禁。** 两平台、两架构都运行中英文 × 明暗主题。每个组合必须生成 7 张设置页、4 张引导页、1 张菜单、5 张 HUD，共 17 张；每个架构合计 68 张。
2. **不能吞掉失败。** 进程非零退出、180 秒超时、缺失/损坏/纯色或全透明图片、`error.txt` 都导致失败。macOS 写图失败抛错；Windows 截图异常与 UI 未处理异常传播为失败退出码。新增 macOS learned HUD 截图。
3. **验证实际分发物。** Windows 从 ZIP 解压后的可执行文件渲染 UI，解压路径特意包含空格；macOS 延续从 DMG 的签名 app 渲染。现有安装与架构检查保留。
4. **保留失败证据。** 图片、每场景进程日志、测试日志/TRX、Windows 事件日志、安装/卸载日志在失败时仍上传。新增 job/step 超时。
5. **覆盖率可见。** Swift 生成 LLVM JSON；.NET 在 Linux 管线任务使用 Coverlet，生成 Cobertura/TRX。摘要明确只统计核心库。暂不凭空设置全软件 80%/90% 门槛。
6. **共享检查复用。** PR/main CI、Release 和包管理器发布都执行翻译检查、actionlint、CI 脚本回归测试、包管理器模板渲染测试、评测数据结构检查。结构检查不会请求付费模型，也不评估实际模型输出质量。
7. **修正过滤与最终门禁。** 测试工具、eval 和 release 工作流改动触发两端；Swift 提示词变更触发 Windows 一致性测试；所需 job 被跳过也不允许绿灯。
8. **卸载检查保留原标准。** 等待目录、快捷方式、注册表、登录项和进程全部清理完成；不删除残留来使测试通过。

新截图检查只能发现整图无内容等明显问题。有背景但缺少控件、文字截断、遮挡、错误页面内容仍可能通过。它不包含已批准基准图，也没有本次新建原生交互测试目标。

## 实测结果与验证边界

| 检查 | 本次结果 |
|---|---|
| Swift `swift test --enable-code-coverage` | runner 报告 143 tests 通过；其中独立性能 suite 的 4 项显示 skipped，由 CI 的 perf.sh 另跑 |
| Swift 核心库行覆盖率 | 3,012 / 3,355 = **89.8%**；不计应用目标和测试代码 |
| .NET `dotnet test --collect "XPlat Code Coverage"` | **141 passed、4 skipped、0 failed**；跳过项为独立性能预算 |
| .NET 核心库行覆盖率 | **87.0%**；在本机 macOS ARM64 临时 .NET 10 SDK 测得，云端 Linux 数值可能略有差异 |
| 新增 Python CI 回归测试 | 16 项通过，含缺图、损坏/透明图、异常文件、崩溃/超时、必需 job 跳过、覆盖率聚合、模板与评测数据 |
| macOS 应用构建 | 修改截图代码后 `swift build` 成功；存在原有编译器警告 |
| 工作流静态验证 | actionlint 1.7.12 通过（YAML、表达式、可复用工作流契约；没有把它当作 PowerShell 执行验证） |
| 翻译检查 | 358 strings、7 个附加翻译语言、0 problems |
| 已有 CI 图片校验 | 下载主分支成功生成的 Windows 截图，17 张完整通过新解码检查 |

本次没有在本机执行新的四架构云端工作流、WinUI 应用编译/交互、Windows 安装/卸载或新的 68 图原生矩阵，也没有重跑未改动的 Release 性能预算。不能把这些未运行的项目写成“全部验证通过”。

核心低覆盖文件可优先审查：Swift `Providers` 36.6%、`APIClient` 73.7%、`Localization` 73.7%；C# `Providers` 24.1%、`UpdateCheck` 71.0%、`Localization` 71.6%、`ApiClient` 73.3%。其中包含配置/元数据分支，不能只按百分比排序风险；会话取消、错误恢复和更新回滚仍更重要。

## 第二轮复核与补充

以上为第一轮审计。第二轮复核了第一轮的全部改动，并在同一 PR 中补充了两层测试。

### 复核结论

- **本地复核通过**：用 `build-app.sh --package` 打出 macOS arm64 包，对包内程序执行 `run_snapshots.py`，4 个场景 68 张图全部通过；抽查图片确认 `--light`/`--dark` 与 `--lang` 实际生效。CI 脚本测试、actionlint 1.7.12 通过。
- **保留**：严格截图门禁、失败证据上传、卸载后置条件整体等待、fail-closed 的 `CI passed`、共享检查、覆盖率报告。
- **调整**：actionlint 改为下载发布版二进制并按官方 checksums 校验，不再每次安装 Go 并编译（约省 1 分钟）。
- **预期风险**：Windows ARM64 首次进入截图门禁，x64 曾出现 `CoreMessagingXP.dll` 崩溃。如果因此变红，说明门禁发现了真实问题，应修应用，而不是放宽门禁。

### 新增一：打包产物的端到端听写（不花 API 费用）

两端的打包产物都自带 `--transcribe-file` 命令行模式（macOS 是应用本体，Windows 是 zip 里的 `OpenTypeless.Cli.exe`），与应用使用同一套 `TranscriptionPipeline`、`HedgedPolish` 和设置读取。CI 以 `--realtime` 按说话速度送入一分钟合成录音，对接本地假服务商：

- **合成录音**（`scripts/ci/dictation_audio.py`）：每个词是一段不同音高的音调，词间有短停顿，短语间有长停顿，中英文词混合。内容不涉及隐私，每次生成的字节都相同。
- **假服务商**（`scripts/ci/fake_provider.py`）：OpenRouter 形状的接口。它按收到的音频测音高来"转写"，所以分块切在词中间、重复发送或丢块，都会直接反映在转写结果里。第一个转写请求返回 503；整理结果用 SSE + chunked 编码每次只发 5 字节，中文的 UTF-8 字节会被拆到两次读取里。
- **断言**（`scripts/ci/run_dictation.py`）：去掉空白后，原始转写必须与"说出"的词序列完全一致，整理结果必须与服务端流出的完全一致；请求必须带 API Key，单块不超过 29 秒，至少有 2 块，503 确实发生过并已重试，整理收到的正是打印出来的转写。
- **路径**：录音放在带空格和中文的目录；Windows zip 解压到 `OpenTypeless 便携版`。

这一层能发现单元测试发现不了的发布构建问题：自包含发布或链接裁剪导致的运行时差异、ATS/HTTP 栈、真实 socket 上的分块与 SSE 解析、重试，以及非 ASCII 路径。它仍不覆盖热键、HUD、麦克风、粘贴和会话取消，前文 P1 的 `SessionController` 可注入测试依然是下一步最重要的工作。本地对 macOS arm64 包实测：63 秒录音分成 3 块，共 18 个转写请求（含推测尾段和那次 503），结果逐字一致。

### 新增二：区域设置矩阵

Linux 上的 .NET 核心测试改为分别在 en-US/UTC（带覆盖率）、de-DE/Europe/Berlin（小数逗号）、tr-TR/Europe/Istanbul（土耳其语 i 的大小写规则）、zh-CN/Asia/Shanghai 下各跑一遍。本地在 de-DE、tr-TR、zh-CN、ar-SA 下实测均通过，所以目前是防回归的护栏，不是在修已知 bug。

### 对第一轮 proposal 的补充意见

- **同意优先级**：可注入的 `SessionController` 测试，比继续提高核心库覆盖率更有价值。
- **像素基准图要谨慎**：托管运行器的字体、系统版本和渲染会随镜像漂移，像素对比容易误报，维护成本高。建议先做结构断言（无障碍树里控件存在、可见、文本未截断），再对少量稳定页面引入像素基准。
- **低成本的下一步**：
  1. macOS 运行器上用 `defaults write -g AppleLocale` 切换到 zh_CN/de_DE 后再跑一遍 Swift 测试（运行器是一次性的，不影响开发机）。
  2. Windows 运行器上用 `Set-Culture zh-CN` 后再跑截图和听写。
  3. 升级测试：下载上一版 GitHub Release 的 zip，安装后用本次构建的 zip 执行应用内更新器的安装路径，验证升级后能启动、数据保留。

## Proposal：下一阶段如何在 CI 提前发现用户可见问题

### P1 — 应用服务与会话测试（优先于追求更高核心库覆盖率）

从两个 `SessionController` 提取可注入的 audio source、clock、permissions、history/settings、text delivery 接口。保留生产实现；测试替换设备/时钟/网络，不复制一份业务逻辑到测试中。将可独立运行的服务放到可被测试项目引用的 library；WinUI UI 对象本身需要运行于 XAML UI 线程。[Microsoft 原生测试指南](https://learn.microsoft.com/en-us/windows/apps/develop/testing/)

最低用例与通过标准：

| 用例 | 必须断言 |
|---|---|
| 按住松开、快速点按、免手持二次按键、处理中再次按键 | 状态转移正确；只有一个活动会话，不重复开始/完成 |
| 短录音取消、长录音保留后取消、整理中取消 | **取消后绝不粘贴/发送**；按策略保留音频；迟到回调不污染下一会话 |
| 无麦克风、拒绝权限、录音中设备失效 | 用户能看到错误；停止录音/计时；解除静音；可以再次开始 |
| 503/429、401、断流、格式错误、超时、备援同时失败 | 重试次数/路由符合策略；保留可恢复文字；HUD 与历史状态一致 |
| 历史失败记录重试、连续重试 | 只发送必要段落；可计费的请求全部入账；词数不会重复统计 |
| 设置保存与重启、损坏旧文件、迁移、写盘失败 | 值可恢复；失败明确可见；不能静默丢失已有录音或凭据 |
| 快速连续会话、上一会话回调晚到 | 文本/账本/历史各自归属正确；资源最终释放 |

这组测试应是每个 PR 的 required check，不依赖真实网络或实体麦克风。

### P1 — 原生 UI 交互测试

macOS 增加独立 Xcode UI test harness，启动编译后的 `.app`，用 XCTest/XCUIAutomation 查询与操作控件；当前 SwiftPM 的 core test target 本身不是 UI test target。为关键按钮和输入控件设置稳定的 accessibility identifier。[Apple XCUIAutomation](https://developer.apple.com/documentation/XCUIAutomation)

Windows 优先用 C# + UI Automation 的小型 harness（例如 FlaUI/UIA3）验证可访问性树，给控件加 AutomationId；先验证实际 WinUI 控件支持情况和 hosted desktop session，再扩大套件。[FlaUI 项目说明](https://github.com/FlaUI/FlaUI)

Appium Windows 是另一条路径，但其当前驱动仍代理 WinAppDriver；上游明确说明 WinAppDriver 长期缺少维护。因此不能把“安装 Appium”当作已消除这个依赖风险。[Appium Windows Driver](https://github.com/appium/appium-windows-driver)

第一批旅程：打开/关闭/重开设置；逐页导航；切换语言；保存并重启验证；服务商/自定义 URL 校验；选择主/备模型；新增/删除词汇；搜索/复制/删除/重试历史；完成首次引导；关闭窗口后托盘常驻；第二次启动激活已有实例。

通过标准应是控件值、持久化文件和应用状态一致，不能只检查“点击 API 没报错”。失败上传控件树、截图、应用日志和最后操作。以条件等待替代任意固定 sleep；不对失败整套盲目重跑使其变绿。

### P1 — 不花 API 费用的完整听写旅程

在 CI 启动本地 HTTP stub，生成无隐私的 16 kHz mono WAV，运行**真正应用的会话入口**。Windows 已有 `OPENTYPELESS_TEST_AUDIO`，可直接利用；macOS 需提供等价可注入音频源。两端已有 custom endpoint，是连接本地 stub 的基础。

用专用测试目标窗口接收粘贴（AppKit NSTextView / Windows 标准 EDIT 控件），先填充剪贴板，再触发听写，检查目标文字精确匹配、剪贴板按设置恢复、历史/费用/HUD 正确。另测选区替换、多行/emoji/中英混合、不接收粘贴时保留可手动粘贴文本、用户中途修改剪贴板时不覆盖新内容。

这一层可以没有真实麦克风/付费模型，但仍经过真实会话、UI 与跨进程输入。不要用仅运行 CLI 转写代替它；CLI 不覆盖 global hotkey、应用 HUD、权限和跨应用粘贴。真实全局按键和 OS 权限部分若托管运行器不稳定，则放到专用交互式 runner，并明确区分两层。

### P2 — 视觉回归与可访问性

在现在的截图矩阵上增加固定数据：空历史、上千条历史、很长模型名/URL、错误提示、词汇长列表、处理中、网络失败。固定时间、动画帧、数据和窗口尺寸，避免随机波形与实时模型价格造成误报。

使用经人工批准的基准图、像素/结构差异和差异图 artifacts；关键控件同时检查边界、可见性、可访问名称及 Tab 顺序。基准更新单独审查，禁止每次跑自动覆盖基准。

PR 保留中英文和明暗主题；定期全量测试扩展到所有 9 种界面语言、Windows 100/150/200% DPI、小窗口、长文本、macOS 不同缩放、多显示器菜单/HUD 定位。这一阶段才可以开始系统捕获截断、布局偏移和控件遮挡。

### P1/P2 — 更新、安装与恢复

通过临时 app 目录与本地 release fixture 执行旧版本 → 新版本升级，而不只测 `UpdateCheck` 的版本字符串。覆盖 digest 错误、错误架构、macOS 签名身份不一致、缺文件、文件被占用、复制中途失败、回滚后重启、保留卸载器与用户数据。测试更新过程中 app 正在听写时会延迟安装。

Windows 另测从 ZIP 启动 CLI、重复安装、更新后卸载、带空格/非 ASCII 的路径、取消安装；macOS 测从磁盘映像复制到 Applications 后启动、更新后权限身份保留。下载文件的 quarantine/Gatekeeper 与 SmartScreen 体验，不能由 CI 内部生成并直接启动的包完全代表。

### P2 — 定期真实服务评测和设备/系统矩阵

真实 STT/LLM 测试应独立于 PR 的确定性门禁：使用合成/明确授权数据、单独密钥、费用上限、固定 holdout，并记录供应商、模型、时间、请求/响应结构、质量指标和延迟分位数。现有 `eval/polish-holdout.json` 与 CLI 是基础；`EvaluationTests` 测的是评测工具，不代表模型 holdout 已经被实际执行。

设备/系统测试建议用专用自托管 Mac/Windows 测试机（可以远程放在云端或测试实验室），有登录桌面会话和可控音频设备：权限首次拒绝/撤销、USB/蓝牙插拔、默认输入切换、睡眠唤醒、输出静音恢复、浏览器/Electron/原生输入框、管理员窗口、输入法组合键、Windows 最低支持版本。GitHub 通用 VM 不能代表全部硬件状态。

### 建议验收顺序

1. 先让本次严格门禁在四个原生运行器稳定通过；真实 UI 崩溃必须定位，不能恢复 `continue-on-error`。
2. 加会话取消/恢复与持久化测试，再加一条确定性的“听写 → stub → 目标窗口粘贴”旅程。
3. 加设置/引导/历史真实交互与升级回滚测试。
4. 固定截图数据后引入视觉基准、额外语言/DPI，以及定期真实 API/设备矩阵。
5. 用真实基线设置分模块覆盖率不倒退策略，并记录每种关键用户旅程的通过率与 flaky 次数；不把达到一个总覆盖率百分比作为全部软件的完成标准。

完成前 3 步后，CI 对核心用户旅程的可信度会明显提高；仍需少量发布前设备/系统体验检查。用户下载试用可以作为额外反馈来源，而不必承担发现基础错误的主要责任。
