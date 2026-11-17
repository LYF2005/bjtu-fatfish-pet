# 邮课叮桌宠 · 免安装绿色包

一键打包，解压即用。不写注册表、不装系统服务、不需要用户预装 .NET 或 Node.js。

## 产物形态

| 平台 | 产物 | 入口 |
| --- | --- | --- |
| Windows x64 | `邮课叮桌宠-Windows-x64.zip` | `启动邮课叮.exe`（无黑窗） |
| Linux x64 | `邮课叮桌宠-Linux-x64.tar.gz` | `启动邮课叮.sh` |
| macOS Apple Silicon | `邮课叮桌宠-macOS-arm64.tar.gz` | `启动邮课叮.command` |
| macOS Intel | `邮课叮桌宠-macOS-x64.tar.gz` | `启动邮课叮.command` |

## 包内目录结构

```
邮课叮桌宠/
├── 启动邮课叮.exe / .sh / .command   # 一键启动器：拉起后端→等端口→拉起桌宠
├── app/                              # 桌宠本体（.NET 自包含发布，含运行时）
├── server/                           # Node 后端（纯 JS 依赖，零原生模块）
│   ├── studyserver.js
│   ├── knowledge.js
│   ├── notify/
│   └── node_modules/                 # 仅 sm-crypto / jsbn 等纯 JS 包
├── runtime/node/                     # 内嵌 Node 运行时（官方二进制）
├── bridge-config.json                # 桥接地址（默认 127.0.0.1:23336）
└── 使用说明.txt
```

## 核心设计决策

1. **后端零原生依赖**：`better-sqlite3` 是可选依赖，缺失时 `studyserver.js`
   自动降级为内存存储（事件/审批缓存不落盘，知识库接口返回 503）。
   打包时主动剔除该原生模块，从而三端共用同一份后端源码，无需为每个平台编译 C++ 扩展。
2. **内嵌 Node 运行时**：直接携带 Node 官方二进制，用户无需自行安装。
3. **.NET 自包含发布**：桌宠以 `--self-contained` 发布，运行时随包，避免用户装 .NET 10。
4. **启动器无窗口**：Windows 用 WinExe（GUI 子系统）编译，双击不弹控制台；
   Linux/macOS 用 shell 脚本后台拉起后端。后端已在运行时复用，不重复启动。

## 打包

```powershell
# 全部平台
powershell -ExecutionPolicy Bypass -File .\build-portable.ps1

# 指定平台
powershell -ExecutionPolicy Bypass -File .\build-portable.ps1 -Platform win-x64

# 自定义 Node 版本 / 输出目录
powershell -ExecutionPolicy Bypass -File .\build-portable.ps1 -NodeVersion v22.22.2 -OutDir D:\dist
```

产物输出到 `packaging/dist/`。首次打包会下载 Node 官方二进制并缓存到
`packaging/.cache/`，后续复用。全部平台约 680 MB（四份包合计），可单独构建。

## 已验证产物

| 包 | 大小 | 架构校验 | 状态 |
| --- | --- | --- | --- |
| `邮课叮桌宠-Windows-x64.zip` | 235 MB | exe = x64 | 通过 |
| `邮课叮桌宠-Linux-x64.tar.gz` | 152 MB | PE = x64 | 通过 |
| `邮课叮桌宠-macOS-arm64.tar.gz` | 145 MB | DLL/CoreCLR/Host = ARM64 | 通过 |
| `邮课叮桌宠-macOS-x64.tar.gz` | 149 MB | DLL/CoreCLR = x86_64 | 通过 |

功能验证：从 Windows 包提取后端，用其内嵌 `node.exe` 实际启动，
`better-sqlite3` 缺失时正确打印降级日志并完成内存初始化，HTTP `/health` 返回 `ok:true`。
四端包均已确认不含 `better-sqlite3` 原生模块、含完整启动器/配置/说明。

## 已知限制

- **macOS 签名/公证**：交叉编译产物未经 Apple 公证，首次打开需右键「打开」
  或在「系统设置 → 隐私与安全性」中允许。正式分发需在 Mac 上用开发者证书签名。
- **Linux 依赖**：Avalonia 需要系统 X11/字体库，主流桌面发行版默认具备。
- **SQLite 持久化**：绿色包默认内存存储，重启后缓存清空（不影响账号、作业、课表等实时采集）。
