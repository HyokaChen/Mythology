namespace Core.Shared;

/// <summary>内容块抽象基类，表示消息中的内容单元。</summary>
public abstract record ContentBlock;

/// <summary>文本内容块，包含纯文本内容。</summary>
/// <param name="Text">文本内容字符串。</param>
public sealed record TextContent(string Text) : ContentBlock;

/// <summary>图片内容块，包含图片 URL 和可选的替代文本。</summary>
/// <param name="Url">图片的 URL 地址。</param>
/// <param name="AltText">图片的替代文本（可选）。</param>
public sealed record ImageContent(string Url, string? AltText = null) : ContentBlock;
