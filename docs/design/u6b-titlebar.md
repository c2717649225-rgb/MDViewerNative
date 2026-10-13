# U6b 合并标题栏：小设计

> 范围：只在 `chrome=desk` 下生效；classic 保持系统标题栏，一行不改。单独提交，回滚 = revert 这一个提交。

## 目标（规格 §3）

顶栏 44 高：标签 → 空白拖动区 → 工具 → 窗口按钮（46 宽 × 3）。系统标题栏去掉，标题只留在任务栏和 Alt+Tab。

## 必须保住的系统行为

| 行为 | 做法 |
|---|---|
| 拖动窗口、双击最大化/还原、右键系统菜单 | 空白处 `WM_NCHITTEST` 返回 `HTCAPTION`，全部交给 DefWindowProc |
| Alt+Space 系统菜单 | 保留 `WS_SYSMENU`，不动 |
| Win11 贴靠布局（悬停最大化按钮） | 最大化按钮返回 `HTMAXBUTTON`；最小化 `HTMINBUTTON`、关闭 `HTCLOSE` |
| 四边和四角调整大小 | 左右下沿用系统非客户区；顶边在客户区内返回 `HTTOP/HTTOPLEFT/HTTOPRIGHT`（高 = 边框厚度，最大化时没有） |
| 最大化时不溢出屏幕 | `WM_NCCALCSIZE` 最大化时顶部内缩边框厚度（`SM_CYFRAME + SM_CXPADDEDBORDER`，按窗口 DPI） |
| 多显示器 DPI | 边框厚度用 `GetSystemMetricsForDpi(GetDpiForWindow)`；DPI 变化系统会重算非客户区 |
| 阴影与 Win11 边框 | 保留 `WS_THICKFRAME`，DWM 照常画 |

## 做法

- `WM_NCCALCSIZE`（wParam=TRUE）：记下原 top → DefWindowProc → 恢复 top（最大化再加边框厚度）。创建后 `SWP_FRAMECHANGED` 触发一次。
- 系统画的按钮：`HTMINBUTTON` 等落在客户区，DefWindowProc 收到 `WM_NCLBUTTONDOWN` 会画出旧式按钮，所以这三种命中的 `WM_NCLBUTTONDOWN/UP` 自己处理（按下记住、同一按钮上抬起才执行），`WM_NCMOUSEMOVE` + `TrackMouseEvent(TME_NONCLIENT)` 维护悬停，`WM_NCMOUSELEAVE` 清除。
- 命中判定抽成纯函数 `DeskFrame.Hit`（窗口尺寸、是否最大化、边框厚度、点在不在工具/标签上 → 命中码），单测覆盖；宿主只负责把系统消息翻译过来。
- `RenderResources.ToolH` 由常量改为启动时确定的值：desk 44、classic 36（开关只在启动时读取，运行中不变）。标签贴底（离顶 8）、工具垂直居中。
- 窗口按钮由 `ToolbarView` 画（演示 `.caption`）：悬停 desk-2；关闭悬停 #C42B1C 底白字；最大化时画“还原”图标；窗口失去激活时图标用 muted。工具区右缘让出 138。
- 提示：最小化、关闭给提示；最大化不给（Win11 自己弹贴靠布局，两者会叠在一起）。
- 沉浸模式（F11）：工具照旧隐藏，窗口按钮保留可点；隐藏时顶栏其余部分返回 `HTCLIENT`，保留“指针到顶召回”。

## 不做

- Win10 顶边 1px 边框（DWM 在 Win10 不画，接受）。
- Mica / 半透明标题栏：纸张色才是设计的一部分。

## 验收

- 纯函数单测；quick、Verify。
- 截图：宣纸 / 灯下，普通与最大化。
- 里程碑 3 由用户实机试：拖动、双击、贴靠布局、Alt+Space、多屏。
