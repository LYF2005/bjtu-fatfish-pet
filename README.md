# 北交大肥鱼 · youkeding-pet

> 一只住在桌面上的鲸鱼娘学习助手：作业 DDL、实验栏对账、课表、课件资料、空教室，点课表里的课程还能直接跳到对应课件。
> **账号只存本机、数据不上传、课件不入库。** 与桌面壁纸项目完全独立，可单独克隆运行。

> 美术素材来源于开源项目 `dsh-pet`（PC2005-cloud，MIT）等，详见 [THIRD-PARTY.md](THIRD-PARTY.md) 与素材目录声明。

---

## 功能

- **作业 DDL**：汇总各门课的作业 / 实验 / 课程报告，按截止时间排序，标注已交 / 逾期 / 即将截止。
- **实验栏对账**：老师常把作业藏进「课程考核 → 实验」，本工具把「平台自报任务数」与「作业栏 + 实验栏 + 报告栏」逐课对账，减少漏做。
- **课表**：以「星期一～星期日为列、节次/时间为行」的表格一屏呈现；今天列高亮、本周课程突出；点课程直达对应课件。
- **课件资料**：按学期 / 课程 / 目录浏览并下载本人有权限访问的资料，本地缓存。
- **空教室**：按教学周查询空闲教室网格。
- **提醒**：作业临近截止、未读邮件通过桌宠气泡提醒。

## 隐私与安全

账号密码仅用 Windows DPAPI（绑定当前 Windows 账户）加密保存在本机，明文不落盘、不上传、不进版本库。详见 [DISCLAIMER.md](DISCLAIMER.md)。

---

## 项目结构

```text
youkeding-pet/
├── BigBlueFish.Windows/     # C# WPF 桌宠（学习中心三页：作业 / 课件 / 课表）
├── BigBlueFish.Control/     # 共享协议代码
├── BigBlueFish.Fate/        # 命理/黄历等趣味模块
├── BigBlueFish.Mcp/         # MCP stdio 桥接
├── deployment/              # 部署辅助（安装包 / 模型管理）
├── server/                  # 独立学业后端（Node.js）
│   ├── studyserver.js       #   HTTP 服务：/study/* + 桌宠大脑端点
│   ├── notify/              #   学校采集器（可配置端点）
│   │   ├── bjtu.mjs         #     北交大 MIS / 课程平台 / 邮箱 / 教务
│   │   ├── endpoints.json   #     学校端点配置（改版时只改这里）
│   │   ├── credentials.mjs  #     DPAPI / AES 凭证存储
│   │   └── config.example.json
│   ├── config.example.json  #   后端端口配置模板
│   └── package.json
├── bridge-config.example.json  # C# 端桥接地址模板
├── docs/DEV.md              # 深度开发 / 构建 / 发布说明
└── DISCLAIMER.md            # 免责声明与隐私承诺
```

两个可独立启动的部分：

1. **后端**（`server/`）：Node.js，默认监听 `127.0.0.1:23336`。
2. **桌宠**（C# WPF）：通过 `bridge-config.json` 指向后端地址。

---

## 快速开始

### 1. 启动后端

```bash
cd server
npm install
cp config.example.json config.json                 # 可选：改端口
cp notify/config.example.json notify/config.json   # 学业偏好配置
node studyserver.js
```

看到 `[北交大肥鱼] 桌宠后端已启动 http://127.0.0.1:23336` 即成功。
验证：`curl http://127.0.0.1:23336/health`

### 2. 配置桌宠桥接地址

把 `bridge-config.example.json` 复制为构建输出目录（`BigBlueFish.Windows/bin/x64/Release/net10.0-windows10.0.26100.0/`）里的 `bridge-config.json`：

```json
{ "bridgeBase": "http://127.0.0.1:23336" }
```

也可用环境变量 `YOUKEDING_BRIDGE_BASE` / `YOUKEDING_BRIDGE_PORT` 覆盖（优先级最高）。默认即 23336，不改也能用。

### 3. 编译 / 运行桌宠

需要 Windows x64 + .NET 10 SDK：

```powershell
./build.ps1 -Locked -InputMode Ocr
```

运行后右键托盘图标 → 「北交大肥鱼 / 学习中心」→ 账号设置里填自己的学号与密码即可。
（更完整的模型准备、Linux / 无 MCP 版、发布说明见 [docs/DEV.md](docs/DEV.md)。）

---

## 学校适配

采集器已把端点外置到 `server/notify/endpoints.json`。校方改版或适配其他学校时，优先改这里的 URL；采集逻辑集中在 `bjtu.mjs`，按 `login / fetchCourses / fetchMail / fetchSchedule` 钩子扩展即可，上层 UI 无需改动。

---

## 常见问题

- **首次点学习中心很慢？** 后端开机后会串行预热课件会话（需 SSO 登录学校系统），完成后首次点击即秒开。未配置账号时跳过预热。
- **换账号后看到旧数据？** 换号会清除课件的 25 分钟单例会话与空教室 10 分钟缓存，不会串号。
- **凭证解不开？** DPAPI 与 Windows 账户绑定，换电脑 / 换账户后需重新填写密码。
- **端口冲突？** 后端默认 23336，与壁纸项目 23334 不冲突；可用 `config.json` 或环境变量改。
- **想连回旧壁纸服务？** 把 `bridge-config.json` 的 `bridgeBase` 改成 `http://127.0.0.1:23334` 即可，无需改代码。

---

## 许可

代码 Apache-2.0（见 [LICENSE](LICENSE)）；第三方组件与素材的许可见 [THIRD-PARTY.md](THIRD-PARTY.md)。使用前请阅读 [DISCLAIMER.md](DISCLAIMER.md)。
