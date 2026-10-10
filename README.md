# ZCode Classic

让 **Windows 7** 也能用上 [ZCode](https://github.com/zai-org/ZCode) 官方 AI 编程助手的桌面客户端。

官方 ZCode 桌面版要求 Node.js 24（不支持 Win7）。ZCode Classic 用 .NET Framework 4.8 + WebView2 实现协议兼容的本地后端，承载官方 Web UI，使 Win7/Win8/老 Win10 机器获得与官方一致的使用体验。

```
┌──────────────────────────────────────────────┐
│  ZCode Classic (C# WinForms, .NET 4.8)       │
│                                              │
│  ┌────────────────────────────────────────┐  │
│  │  WebView2                              │  │
│  │  官方 ZCode Web UI（本地构建 out/web2）│  │
│  └──────────────┬─────────────────────────┘  │
│                 │ WebSocket ws://127.0.0.1:8347/ws │
│  ┌──────────────┴─────────────────────────┐  │
│  │  RPC 后端（V4 协议，替代 Node.js）     │  │
│  │  agent 循环 · 会话 · 工具 · 审批 · MCP │  │
│  └────────────────────────────────────────┘  │
└──────────────────────────────────────────────┘
```

## 功能

- **官方 Web UI**：会话侧栏、流式对话、工具调用卡片、审批交互、模型/思考档位切换、主题（深/浅/浅绿/灰/自定义取色）、分组视图、归档等，全部来自官方前端
- **Agent 工具集**：Bash / Read / Write / Edit / Glob / Grep / Todo / Skill / WebFetch / SystemInfo
- **电脑操作（CUA）**：UIA 控件树（tree/find/invoke/click/value）、鼠标、键盘、窗口管理、截屏（`UIA`/`Mouse`/`Key`/`Window`/`Screen` 五工具，Win7 可用）
- **内置浏览器**：右侧面板（悬浮按钮或 Ctrl+U），网址/本地文件/搜索；Markdown 渲染（离线 marked.js）、HTML/图片/PDF 预览
- **MCP**：stdio MCP server（官方 `~/.zcode/cli/config.json` 的 `mcp.servers` 格式兼容），设置页可视化管理
- **Skills**：双目录加载（应用目录 + 官方 `~/.zcode/skills`），启停持久化
- **会话管理**：本地持久化、重命名/删除/归档/置顶、消息排队、fork、compact
- **额度显示**：智谱 5 小时/周用量悬浮条
- **权限门**：智能确认（读自动/写审批）/ 手动确认 / 完全访问 三档

## 安装

### 依赖

| 组件 | 说明 |
|---|---|
| .NET Framework 4.8 | Win7 SP1 需手动安装（[离线安装器](https://dotnet.microsoft.com/download/dotnet-framework/net48)） |
| WebView2 Runtime | 缺失时安装器会打开官方下载页；Win7 请用 [109 版 fixed runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) |

### 方式一：安装包（推荐）

下载 [`installer/ZCodeClassic_Setup_1.0.0.exe`](installer/ZCodeClassic_Setup_1.0.0.exe)（33 MB，自包含），双击安装。默认装到 `Program Files\ZCode Classic`（弹 UAC）；也可指定目录静默装到用户空间（无需管理员）：

```
ZCodeClassic_Setup_1.0.0.exe "%LOCALAPPDATA%\ZCode Classic"
```

### 方式二：便携

解压 `ZCodeClassic_Setup_1.0.0.zip`（Release 页）直接运行 `ZCode7.exe`，或按 [build.bat](build.bat) 从源码构建。

### 配置

首次启动在设置页填写 API 地址与 Key（保存在 `%APPDATA%\ZCode7\config.json`，仅存本机）。兼容智谱 Coding Plan（Anthropic 端点）及任意 OpenAI/Anthropic 兼容网关；模型列表可在线拉取或手填。

## 从源码构建

```cmd
build.bat
```

需要 .NET 4.8 Developer Pack（脚本用 Roslyn csc 直接编译，无需 VS）。产物为 `out\ZCode7.exe`。

官方 Web UI 构建见 [ui-patches/README.md](ui-patches/README.md)：clone 官方仓库 → 应用补丁 → `pnpm build` → 产物拷到 `out/web2/`。没有 web2 时应用回退到内置精简 UI（`web/`）。

## 项目结构

```
src/            C# 源码（agent/RPC/WebView2 宿主/CUA/内置浏览器）
  Agent.cs        agent 主循环、审批门、系统提示词
  ApiClient.cs    Anthropic/OpenAI 兼容 API 客户端（流式）
  Rpc/            V4 协议：帧编码/序列化/通道分发
  RpcServicesRegistry.cs  RPC 服务路由（会话/设置/模型/MCP/skills）
  V4AgentService.cs       会话投影（snapshot/delta/审批/排队）
  ChatServer.cs   本地 HTTP+WS（静态资源/preview 路由）
  ChatHost.cs     WebView2 宿主（快捷键/外链保护/额度条）
  ComputerUse.cs  电脑操作：UIA/鼠标/键盘/窗口/截屏
  BrowserPane.cs  内置浏览器面板
  Tools.cs        内置工具集
  McpClient.cs    stdio MCP 客户端
  Skills.cs       技能加载（双目录）
web/            回退精简 UI（无 web2 时使用）
installer/      自解压安装器与安装脚本
ui-patches/     官方 Web UI 本地修改补丁
docs/           验收测试报告
deploy-win7.ps1 Win7 局域网部署脚本（admin share）
```

## 安全说明

- API Key 只保存在本机 `%APPDATA%\ZCode7\config.json`，不参与任何上传
- WebView2 仅监听 `127.0.0.1`；外部链接一律转系统浏览器
- CUA 工具：只读动作（看树/列窗口/截屏）不审批，点击/键盘/关窗走权限门；拒绝操作宿主自身窗口
- 自签名代码签名证书（CN=ZCode7 Dev），仅用于开发期标识

## License

MIT
