# 云端诊断链路（客户端侧）

> 本仓库只包含**本地客户端**。云端 Harness 的实现、私有参数、计费契约与运营配置均在独立私有仓库，
> 不随本仓库开源；本文只记录客户端如何接入。

## 客户端契约

- 入口：`Core.Cloud.IFictionHarnessClient`（`ExecuteAsync` / `GetContextAsync`）。
- 传输：`ClientPlatform` 的 `CloudRestTransport` + `HttpFictionHarnessClient`（SSE 优先、轮询回退）。
- 调度：`CloudDiagnosis.DiagnosisScheduler` → `SignalBuilder` → `Agent.InjectQueue`。
- 降级：云端不可达、鉴权失败或额度不足时自动降级到本地创作；本地创作不依赖云端。
- 错误：服务端 `code`/`detail` 为权威，经 `CloudErrorMapper` 映射；额度类失败返回
  `usage_limit_exceeded`，客户端做一次性降级提示。

## 边界

- 云端能力列表、深度档、计费单价、月度额度与私有算法不在本仓库文档中维护。
- 客户端本地诊断只做确定性结构检查（正文冲突、设定一致性、时间线、规则约束），
  不做近似深度诊断，不携带任何云端私有阈值/基准/词表。
