namespace Tui.Data;

/// <summary>
/// TUI 层 Agent 展示状态（三态）。
/// 与 Core.Agent.AgentStateEnum（Agent 状态机的真实状态：Idle/Running）分离——
/// Compact 是纯 UI 展示态：上下文压缩期间 Agent 内部仍是 Running，
/// 仅状态栏/任务面板需要单独的提示文案与配色。
/// 用 enum 取代散落在事件/状态栏中的 "idle"/"run"/"compact" 魔数。
/// </summary>
public enum TuiState
{
    /// <summary>空闲，等待用户输入。</summary>
    Idle,

    /// <summary>运行中（LLM 回复 / 工具执行）。</summary>
    Run,

    /// <summary>上下文压缩中（UI 专属展示态）。</summary>
    Compact,
}
