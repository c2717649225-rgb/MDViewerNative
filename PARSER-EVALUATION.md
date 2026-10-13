# Markdown 解析器复用评估

2026-09-14：Markdig 值得作为后续迁移目标。本轮保留现有生产解析器，未添加生产 NuGet 依赖。独立解析已验证 AOT、规范和性能收益；原生布局需要的块模型及项目扩展仍须完成适配，尚不能将直接替换视为功能等价。

## 方法与来源

- 候选为官方 NuGet [Markdig 1.3.2](https://www.nuget.org/packages/Markdig/1.3.2)，[源码仓库](https://github.com/xoofx/markdig)，BSD-2-Clause。包内记录源码 commit `fc705234fa211d179ee1d5e7656b51ab99f70ca9`；使用其 net8.0 资产，该目标没有额外包依赖。
- 两份独立程序均使用 .NET 9.0.301、win-x64 NativeAOT、`InvariantGlobalization=true`。现有实现链接当前解析器与 HTML 导出代码，候选使用 Markdig；均成功发布。候选无 AOT 警告。实验基准程序因未链接布局器而有一个 `Block.H` 未赋值警告，生产构建没有该警告。
- 使用现有 655 例规范文本；严格、宽松归一化函数与 `selftest/Program.cs` 相同。它们会忽略部分 HTML 差异，分数不代表完整 CommonMark 兼容。
- 另观察 12 组项目扩展的 HTML 和 AST，包括表格、任务、脚注、强调扩展、公式、折叠块、原始 HTML、Mermaid、emoji、图片链接、嵌套引用。它们是差异观察，不是新增的 12 项通过测试。
- 纯解析测量预热 8 次、运行 15 次取中位数；分配量为当前线程每次平均字节数。未包含 AST 适配、DirectWrite 布局、绘制或 HTML 输出，不与应用整体性能门槛混用。

## 实测

| 配置 | HTML 精确一致 | 项目严格口径 | 项目宽松口径 |
|---|---:|---:|---:|
| 当前解析器和导出 | 354/655 | 554/655 | 609/655 |
| Markdig 标准模式 | 621/655 | 654/655 | 654/655 |
| Markdig 项目扩展候选 | 619/655 | 652/655 | 652/655 |

扩展候选显式启用 PipeTables、TaskLists、Footnotes、EmphasisExtras、Mathematics、Emoji、AutoLinks；`UseEmojiAndSmiley(enableSmileys: false)` 保留短码表情并关闭额外字符替换。最初启用 smileys 时只有 650 例严格一致，`<33>` 和 `-->` 会被改写，因此未将该默认设置作为推荐配置。

标准模式唯一未达严格口径的是 #542：`[ẞ]` 无法匹配 `[SS]: /url`。扩展候选另外因自动链接改变 #610、#613 的规范输出。按 strict/loose/fail 逐例排序，与当前实现相比，标准模式改善 101 例、退化 1 例（#542）；扩展候选改善 100 例、退化 2 例（#542、#610）。不能仅凭总分增加接受这些差异。

| 纯解析指标 | 当前实现 | Markdig 标准 | Markdig 扩展候选 |
|---|---:|---:|---:|
| big20k 中位耗时 | 47.129 ms | 20.819 ms | 23.798 ms |
| big20k 每次分配 | 39,912,135 B | 15,588,579 B | 16,689,920 B |
| 全格式文档中位耗时 | 0.181 ms | 0.082 ms | 0.117 ms |
| 全格式文档每次分配 | 345,387 B | 98,163 B | 146,979 B |

各配置的 240 次并行输出比较均无差异。独立 AOT 实验程序：当前实现 2,889,216 B，Markdig 4,034,048 B，相差约 1.09 MiB。该数包含实验程序自身且未集成原生渲染器，不能当作正式应用的最终体积增量。耗时是本机观察，尚未测量适配后的整体性能。

## 迁移需要解决的实际差异

| 边界 | 观察与处理要求 |
|---|---|
| 原生块模型 | Markdig 提供树形列表、引用和内联节点；当前 `DocView` 使用平铺 `Block` / `Run` 及 `XOff`、`QSeq` 等字段。需要一个 AST 到现有模型的适配入口，不能直接传入布局器。 |
| 公式、Mermaid | Markdig 已识别 MathInline、MathBlock 和 FencedCodeBlock，但默认 HTML 的公式需要外部渲染。应继续连接现有 `MathRender` / `MathUnicode` 和 `MermaidLite`，保持本机预览与独立 HTML 导出行为。 |
| 折叠块 | 当前 `HtmlDetails` 是可折叠单元；Markdig 将示例拆成 HtmlBlock 与段落。需要明确收集边界，保持折叠和点击行为。 |
| 原始 HTML | 候选默认输出原始 script 和 javascript 链接；当前导出会转义示例 script、替换该链接。迁移必须保留项目既有 HTML 处理策略，不能直接将 `Markdown.ToHtml` 用作导出入口。 |
| 脚注与强调 | Markdig 正确收集了多段脚注，当前实现把第二段当成代码；脚注 ID、回跳链接和嵌套强调模型也不同。应明确哪些是接受的修复，并逐例审查。 |
| 表格、任务、图片、emoji | 观察样例均有可用 AST；HTML class、对齐属性等不同。仍需验证原生布局、点击和导出，不能仅比较默认 HTML 字符串。 |

迁移验收应同时做到：只保留一套生产解析入口；删除被替代的 `ParserContext` 及不再使用的查表逻辑；审查上述逐例差异；通过现有语料、401 项自测和 49 项 AOT 窗口检查；测量包含适配的整体性能与正式包体积。适配代码的实际大小尚未测得，不预先宣称能净删多少行。

本机实验代码、输入、逐例 JSON、AST 类型和构建日志位于被忽略的 `work/markdig-evaluation/`；其中 `current-results`、`markdig-standard-results`、`markdig-configured-results` 对应表中三列。实验项目不进入业务工程或默认 CI。
