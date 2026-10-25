namespace Desktop.Models;

/// <summary>消息发送者。</summary>
public enum MessageSender
{
    /// <summary>用户。</summary>
    User,

    /// <summary>Agent（AI 助手）。</summary>
    Agent,

    /// <summary>系统提示（错误/中断等）。</summary>
    System,
}

/// <summary>Agent 步骤类型。</summary>
public enum StepKind
{
    /// <summary>思考。</summary>
    Thinking,

    /// <summary>阶段正文（工具/思考之间的助手文本，按发生位置插入时间线）。</summary>
    Content,

    /// <summary>工具调用。</summary>
    ToolCall,

    /// <summary>子步骤（复合工具内部）。</summary>
    SubStep,
}

/// <summary>Agent 步骤状态。</summary>
public enum AgentStepStatus
{
    /// <summary>处理中。</summary>
    Processing,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>失败/错误。</summary>
    Failed,
}
