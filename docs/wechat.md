# 微信通道（WeChat Channel）

> 手机端方案：**微信本身就是客户端**。桌面端/TUI 常驻运行 Agent，微信收发消息，无需开发、签名、上架任何 App。
> 状态：协议层 + 绑定 + 长轮询驱动 Agent + 裁决回路 + TUI/Desktop 两个入口均已实现，105 个单测全绿。
> 最后更新：2026-10-16

---

## 目录

1. [一句话架构](#1-一句话架构)
2. [代码地图](#2-代码地图)
3. [协议来源与许可证](#3-协议来源与许可证)
4. [绑定流程](#4-绑定流程)
5. [消息流](#5-消息流)
6. [裁决回路（完整创作的关键）](#6-裁决回路完整创作的关键)
7. [单实例：一个 bot 只允许一个进程轮询](#7-单实例一个-bot-只允许一个进程轮询)
8. [排障手册](#8-排障手册)
9. [已知边界与后续任务](#9-已知边界与后续任务)

---

## 1. 一句话架构

```
手机微信 ⇄ iLink 长轮询 ⇄ WeChatChannelService（传输层）
                              ↕ OnAgentEvent / runTurn
                        WeChatAgentChannel（编排层）
                              ↕
                    Agent（Core，跑在桌面/TUI 进程内）
```

**通信发生在桌面端**：微信只是收发管道，Agent、小说数据、检索内核全在本地。
代价是**桌面进程必须开着**——关掉桌面，微信里发消息不会有任何响应。

## 2. 代码地图

```
src/Channels/WeChat/                     ← 与宿主无关，可独立编译与测试
├── WeChatProtocol.cs                    常量 + DTO（零依赖）
├── WeChatCredential.cs                  绑定凭证（长期有效的身份）
├── WeChatIlinkClient.cs                 6 个端点的 HTTP 客户端（internal）
├── WeChatBindingStore.cs                落盘：凭证 + 游标 + 上下文令牌 + local_token_list
├── WeChatChannelLock.cs                 单实例独占锁（FileShare.None）
├── WeChatChannelStatus.cs               相位状态机 + 对外快照
├── WeChatBindingFlow.cs                 扫码编排（internal）
├── WeChatChannelService.cs              宿主门面：绑定面
├── WeChatChannelService.Messaging.cs    宿主门面：消息面
├── WeChatMarkdown.cs                    Markdown → 微信纯文本 + 分段（不截断）
└── WeChatAgentChannel.cs                长轮询 ↔ Agent 桥接

src/Tui/
├── Controls/WeChatBindDialog.cs         /wechat bind 对话框（半块字符二维码）
├── Controls/TerminalQr.cs               终端二维码渲染（CloudLoginDialog 共用）
├── Controls/UiDispatch.cs               UI 线程投递守卫
└── Services/SlashCommandParser.cs       /wechat [/wx] bind|status|unbind

src/Desktop/
├── ViewModels/WeChatChannelViewModel.cs 设置页「账号」Tab 的绑定卡片
└── Views/SettingsView.axaml             卡片 UI（PNG 二维码）
```

两个入口共用**同一个** `WeChatChannelService`：状态源唯一，不可能显示不一致的绑定态。
差异只在二维码渲染（TUI 半块字符 / Desktop PNG 位图）与回合落在哪个会话（都是当前会话）。

## 3. 协议来源与许可证

| 来源 | 许可证 | 用途 |
|---|---|---|
| `@tencent-weixin/openclaw-weixin` | **MIT**（Copyright (C) 2026 Tencent） | 协议端点、请求头、`local_token_list` 语义、`LOSSLESS_ID_FIELDS` |
| `LittleKingKnows/Services/WeChat/*.cs` | **AGPL-3.0**（私有仓） | **仅作踩坑笔记参考，代码未拷贝** |

> ⚠️  Mythology 以 MIT 发布。**不要把 LKK 的文件拷进来**——AGPL 代码进 MIT 公开仓是许可证违规，
> 即使两仓同主体也不例外。协议层的正确来源是腾讯那份 MIT 实现（`npm i @tencent-weixin/openclaw-weixin`）。

从 LKK 的注释里继承下来的**三条易错点**（都是知识，不是代码）：

1. 请求必须带 `local_token_list`（本地存过的 bot token 列表）。空列表会让服务端视本机为全新客户端，**重新签发并解绑旧绑定** = 每次都要重扫码。
2. `bot_type=3` 必须与官方微信渠道构建一字不差。
3. `message_id` / `msg_id` / `svr_id` / `seq` 是 uint64，JSON 里是裸数字，**必须先加引号再反序列化**——19 位整数超出 double 精度，会被静默四舍五入（`WeChatIlinkClient.ProtectUInt64`）。

## 4. 绑定流程

```
/wechat bind                          Desktop：设置 → 账号 → 微信 → 扫码绑定
   ↓
FetchQrCodeAsync(local_token_list)    → qrcode + qrcode_img_content（liteapp 链接）
   ↓
ShowQr(content)                       TUI 画半块字符 / Desktop 画 PNG
   ↓
PollQrStatusAsync（每轮 35s 预算）     服务端实测挂起 ~30.1s
   ↓
confirmed → 落盘凭证 + local_token_list 追加 + 声明独占权 → Polling
```

扫码后**必须在手机上点确认**，光扫不动会一直停在 `已扫码，请在手机上点确认`。

二维码过期会自动刷新，最多 `MaxQrRefresh`(3) 次；超过或 5 分钟未确认 → `Failed`，重新 `/wechat bind` 即可。

**重启免扫码**：凭证落在 `~/.mythology/wechat/binding.json`（Windows 上 DPAPI 加密，其它平台明文 + `chmod 600`），启动时 `TryRestore()` 直接恢复。

## 5. 消息流

**入站**（手机 → Agent）：

```
getupdates 长轮询
  → 过滤 message_type != 1（自己的回声）与群聊
  → 文本 / 语音（服务端转写文本）→ runTurn
  → 图片 / 文件 → 回一句"暂不支持"
```

**出站**（Agent → 手机）：

```
agent.OnEvent
  → turn 门闩：只有"我们这一轮"的事件进缓冲（否则会把终端里的对话也发到手机上）
  → StreamChunk(Text) 累积；Thinking 不上屏
  → settle 窗 1.2s 无新内容 → 发一条；MessageEnd / AgentEnd 立即冲刷
  → Markdown 降级 → 超 4000 字分段连发（不截断）
  → 串行出站队列（顺序与生成顺序一致）
```

**typing 指示器**：回合开始发一次，进行中每 5s 重发（微信的指示器几秒后自动消失）。取不到票据就静默跳过——typing 只是装饰，任何失败都不影响正文。

## 6. 裁决回路（完整创作的关键）

Agent 的 `ask_user`（以及云端裁决 Hook）会创建问题并阻塞等答案。手机上呈现为：

```
【需要你拍板】剧情走向
主角要放过仇人吗？
1) 放过他
2) 当场复仇
回复编号
```

- 回复编号即答案；多选用逗号（`1,3`）；无选项的问题接受任意文字。
- **单选给了多个编号 → 拒绝并重发题目**（静默取第一个会猜错意图）。
- 一组多个问题时，答完一条自动发下一条（`AskUserTool` 用 `Task.WhenAll` 等全部，任意顺序答都行）。
- 回合期间**宿主自己的弹窗被抑制**（`IsTurnActive`），避免两边抢答同一题。

## 7. 单实例：一个 bot 只允许一个进程轮询

两个入口同时长轮询同一个 bot = **消息重复或丢失**。

用 `FileShare.None` 独占文件锁（`~/.mythology/wechat/channel.lock`）而不是 PID 检查：进程崩了 OS 自动释放，没有 PID 复用、心跳过期、跨用户无权读进程这些分支。锁文件内容仅用于诊断。

第二个入口的表现：
- `TryRestore()` 抢不到锁 → 相位 `Failed` + "已被另一个入口占用"，但仍显示**绑定的是哪个号**（用户才知道该去关哪一个）。
- `/wechat bind` 抢不到锁 → 拒绝绑定。
- 解绑需要持有锁；没有锁时拒绝（防止把另一个入口留在"没有凭证却还在轮询"的悬挂态）。

## 8. 排障手册

| 现象 | 原因 | 处理 |
|---|---|---|
| 回复发不出去，日志说已发送 | 回复窗口关闭（errcode **-14**）：微信要求用户先发一条 | 已自动暂存，用户下次发消息时补发。若一直没补上，检查 `binding.json` 是否被清 |
| 长轮询报 -14 | bot token 失效 | 通道自动停并提示重新扫码；不会静默重试一万次 |
| 每轮轮询 30 秒没反应 | **正常**。服务端挂起 ~30.1s，客户端预算 35s（服务端先返回，不会被掐断） | 别把客户端超时调短 |
| 通道 CPU 100% | 服务端不再挂起（自身异常或被代理吞掉长轮询） | 已加 500ms 最小轮询间隔兜底；若仍出现，查网络中间层 |
| 二维码扫不了 | 二维码内容过期（5 分钟） | 重新 `/wechat bind`；终端里确认是 33×33 半块字符图案 |
| 手机上没反应 | 桌面进程没开 / 另一个入口持有锁 / Agent 正忙 | 桌面端 `/wechat status` 看相位；忙时会回一句"桌面正忙" |
| 对话串了 | 微信回合跑在**当前会话**上（TUI 与 Desktop 一致的设计） | 见 §9 |

日志：`~/.mythology/logs/`（TUI/Desktop 共用），搜 `微信` 关键词。

## 9. 已知边界与后续任务

| 项 | 现状 | 说明 |
|---|---|---|
| 微信回合的会话归属 | **跑在当前会话**（与终端对话共用） | 用户确认"基本不会同时用、同一本小说"后的刻意选择。要分开需抽"切会话"原语（TUI 的会话切换绑在 `OpenNovelAsync` 上，Desktop 有现成的 `SessionEngine.SwitchToAsync`），是独立任务 |
| 媒体 | 只入站语音转写；出站只有文本 | 未移植 CDN 上传（AES + 约 300 行）。超长回复用分段连发替代"转 .md 文件" |
| 多微信 / 多 bot | 一个 bot 绑一个微信用户 | iLink 单绑定语义 |
| 安全验证码 | `need_verifycode` 直接失败并提示 | 需要用户输入验证码的流程未实现 |
| 群聊 | 不过滤进来但也不处理（只处理单聊） | v1 明确不做 |
| 无 GUI 常驻 | 需要桌面/TUI 进程开着 | 要 24 小时在线可考虑 `mythology --wechat` 无终端守护进程（复用 `HostBootstrapper.InitializeAsync`，不建 TUI） |

---

## 附：命令速查

```
/wechat              查看绑定状态（等价 /wechat status）
/wechat bind         扫码绑定 / 重新扫码
/wechat unbind       解绑并清除凭证
/wx                  以上命令的简写
```
