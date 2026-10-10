# 官方 Web UI 本地补丁

ZCode Classic 承载的是 ZCode 官方 Web UI 的本地构建（`packages/web` 产物，拷贝为 `out/web2/`）。本目录保存全部本地修改，形式为对官方仓库 `packages/ui/src` 的统一 diff。

## 修改内容

- `useTheme.ts` — 新增 浅绿/灰/自定义取色 主题（HSL 派生 20 个变量，跨实例同步，切换时清理内联变量）
- `styles.css` — `.theme-light-green` / `.theme-gray` 配色块
- `WorkspaceSidebar.tsx` / `SettingsPage.tsx` — 主题白名单补齐 light-green/gray
- `WorkspaceSidebarFooter.tsx` — 主题菜单加"自定义颜色…"取色器入口；移除登录墙统计项
- `WorkspaceHeaderActionSection.tsx` — 精简头部（去 share/终端）
- `ModelProviderSection.tsx` / `useModelProviderNavigation.ts` — 移除内置供应商卡片（纯自定义供应商模式）
- `useZCodeAgentService.ts` / `useZCodeSessionService.ts` / `useZCodeTaskService.ts` — 适配本地后端的会话/任务行为
- `settingsNavigation.ts`、i18n 词条（zh-CN/en-US）

## 构建步骤

```bash
# 1. 获取官方源码（需 Node 24 + pnpm）
git clone https://github.com/zai-org/ZCode.git
cd ZCode
pnpm install

# 2. 应用补丁
git apply /path/to/zcode-classic/ui-patches/zcode-ui.patch

# 3. 构建官方 Web UI
cd packages/web
pnpm build

# 4. 拷贝产物
#    将 packages/web/dist 整体拷贝为 ZCode Classic 的 out/web2/
#    （与 ZCode7.exe 同级的 web2/ 目录）

# 5. 附带离线 Markdown 渲染
mkdir -p out/web2/preview
curl -o out/web2/preview/marked.min.js https://cdn.jsdelivr.net/npm/marked/marked.min.js
```

补丁基于官方 `feat: update v3.14.3`（commit 29628c9）。官方更新后 rebase 或重新套用即可，冲突点主要集中在 footer/theme 白名单两处。
