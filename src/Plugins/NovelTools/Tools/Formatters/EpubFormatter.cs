using System.IO.Compression;
using System.Text;
using Core.Data;
using Core.Shared;

namespace Plugins.NovelTools.Tools.Formatters;

/// <summary>
/// EPUB 格式生成器 — 手动拼接 XHTML + 打包 ZIP，零第三方依赖。
/// 生成符合 EPUB 2.0 规范的最小电子书。
/// </summary>
/// <remarks>
/// 核心方法 Generate 负责按顺序生成 EPUB 内部结构：
/// 1. mimetype（无压缩）
/// 2. META-INF/container.xml（容器描述）
/// 3. OEBPS/content.opf（内容清单）
/// 4. OEBPS/toc.ncx（目录导航）
/// 5. OEBPS/style.css（样式表）
/// 6. 各章节 XHTML
/// 7. 附录（可选）
/// </remarks>
public sealed class EpubFormatter : IFormatter
{
    /// <summary>
    /// 生成 EPUB 格式的 Base64 编码字符串。
    /// </summary>
    /// <param name="novel">小说信息。</param>
    /// <param name="chapters">章节列表。</param>
    /// <param name="settings">格式化设置。</param>
    /// <returns>Base64 编码的 EPUB 数据。</returns>
    public string Generate(Novel novel, List<Chapter> chapters, FormatterSettings settings)
    {
        var ordered = chapters.OrderBy(c => c.Order).ToList();

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // mimetype — 必须第一个条目，无压缩
            var mimeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimeEntry.Open()))
                writer.Write("application/epub+zip");

            // META-INF/container.xml
            WriteTextEntry(zip, "META-INF/container.xml", BuildContainerXml());

            // OEBPS/content.opf
            WriteTextEntry(zip, "OEBPS/content.opf", BuildOpf(novel, ordered));

            // OEBPS/toc.ncx
            WriteTextEntry(zip, "OEBPS/toc.ncx", BuildNcx(novel, ordered));

            // OEBPS/style.css
            WriteTextEntry(zip, "OEBPS/style.css", BuildCss());

            // 各章节 XHTML
            foreach (var ch in ordered)
                WriteTextEntry(zip, $"OEBPS/chapter_{ch.Order}.xhtml", BuildChapterXhtml(ch));

            // 附录
            if (settings.IncludeAppendix)
                WriteTextEntry(zip, "OEBPS/appendix.xhtml", BuildAppendixXhtml(settings));
        }

        var bytes = ms.ToArray();
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// 向 ZIP 归档中写入文本条目。
    /// </summary>
    /// <param name="zip">ZIP 归档。</param>
    /// <param name="path">条目路径。</param>
    /// <param name="content">文本内容。</param>
    private static void WriteTextEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    /// <summary>
    /// 构建 META-INF/container.xml 内容。
    /// </summary>
    /// <returns>XML 字符串。</returns>
    private static string BuildContainerXml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<!DOCTYPE container>");
        sb.AppendLine(
            "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
        );
        sb.AppendLine("  <rootfiles>");
        sb.AppendLine(
            "    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oasis-package+xml\"/>"
        );
        sb.AppendLine("  </rootfiles>");
        sb.AppendLine("</container>");
        return sb.ToString();
    }

    /// <summary>
    /// 构建 CSS 样式表内容。
    /// </summary>
    /// <returns>CSS 字符串。</returns>
    private static string BuildCss()
    {
        var sb = new StringBuilder();
        sb.AppendLine("body { font-family: serif; margin: 1em; line-height: 1.8; }");
        sb.AppendLine("h1 { text-align: center; margin-top: 2em; }");
        sb.AppendLine("h2 { text-align: center; margin-top: 2em; }");
        sb.AppendLine("p { text-indent: 2em; margin: 0.5em 0; }");
        return sb.ToString();
    }

    /// <summary>
    /// 构建 OEBPS/content.opf 内容（内容清单）。
    /// </summary>
    /// <param name="novel">小说信息。</param>
    /// <param name="chapters">章节列表。</param>
    /// <returns>OPF XML 字符串。</returns>
    private static string BuildOpf(Novel novel, List<Chapter> chapters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine(
            "<package xmlns=\"http://www.idpf.org/2007/opf\" unique-identifier=\"bookid\" version=\"2.0\">"
        );
        sb.AppendLine("  <metadata>");
        sb.AppendLine(
            "    <dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
                + EscapeXml(novel.Title)
                + "</dc:title>"
        );
        sb.AppendLine(
            "    <dc:language xmlns:dc=\"http://purl.org/dc/elements/1.1/\">zh-CN</dc:language>"
        );
        sb.AppendLine(
            "    <dc:identifier xmlns:dc=\"http://purl.org/dc/elements/1.1/\" id=\"bookid\">urn:uuid:"
                + novel.Id
                + "</dc:identifier>"
        );
        sb.AppendLine("  </metadata>");
        sb.AppendLine("  <manifest>");
        sb.AppendLine(
            "    <item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>"
        );
        sb.AppendLine("    <item id=\"style\" href=\"style.css\" media-type=\"text/css\"/>");
        foreach (var ch in chapters)
            sb.AppendLine(
                "    <item id=\"chapter_"
                    + ch.Order
                    + "\" href=\"chapter_"
                    + ch.Order
                    + ".xhtml\" media-type=\"application/xhtml+xml\"/>"
            );
        sb.AppendLine(
            "    <item id=\"appendix\" href=\"appendix.xhtml\" media-type=\"application/xhtml+xml\"/>"
        );
        sb.AppendLine("  </manifest>");
        sb.AppendLine("  <spine toc=\"ncx\">");
        foreach (var ch in chapters)
            sb.AppendLine("    <itemref idref=\"chapter_" + ch.Order + "\"/>");
        sb.AppendLine("    <itemref idref=\"appendix\"/>");
        sb.AppendLine("  </spine>");
        sb.AppendLine("</package>");
        return sb.ToString();
    }

    /// <summary>
    /// 构建 OEBPS/toc.ncx 内容（目录导航）。
    /// </summary>
    /// <param name="novel">小说信息。</param>
    /// <param name="chapters">章节列表。</param>
    /// <returns>NCX XML 字符串。</returns>
    private static string BuildNcx(Novel novel, List<Chapter> chapters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine(
            "<!DOCTYPE ncx PUBLIC \"-//NISO//DTD ncx 2005-1//EN\" \"http://www.daisy.org/z3986/2005/ncx-2005-1.dtd\">"
        );
        sb.AppendLine("<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">");
        sb.AppendLine("  <head>");
        sb.AppendLine("    <meta name=\"dtb:uid\" content=\"urn:uuid:" + novel.Id + "\"/>");
        sb.AppendLine("  </head>");
        sb.AppendLine("  <docTitle><text>" + EscapeXml(novel.Title) + "</text></docTitle>");
        sb.AppendLine("  <navMap>");
        var order = 1;
        foreach (var ch in chapters)
        {
            sb.AppendLine("    <navPoint id=\"nav_" + ch.Order + "\" playOrder=\"" + order + "\">");
            sb.AppendLine(
                "      <navLabel><text>第"
                    + ch.Order
                    + "章 "
                    + EscapeXml(ch.Title)
                    + "</text></navLabel>"
            );
            sb.AppendLine("      <content src=\"chapter_" + ch.Order + ".xhtml\"/>");
            sb.AppendLine("    </navPoint>");
            order++;
        }
        sb.AppendLine("  </navMap>");
        sb.AppendLine("</ncx>");
        return sb.ToString();
    }

    /// <summary>
    /// 构建单章 XHTML 内容。
    /// </summary>
    /// <param name="chapter">章节信息。</param>
    /// <returns>XHTML 字符串。</returns>
    private static string BuildChapterXhtml(Chapter chapter)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
        sb.AppendLine("  <head>");
        sb.AppendLine("    <title>" + EscapeXml(chapter.Title) + "</title>");
        sb.AppendLine("    <link rel=\"stylesheet\" href=\"style.css\"/>");
        sb.AppendLine("  </head>");
        sb.AppendLine("  <body>");
        sb.AppendLine("    <h2>第" + chapter.Order + "章 " + EscapeXml(chapter.Title) + "</h2>");
        var paragraphs = chapter.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in paragraphs)
        {
            var trimmed = p.Trim();
            if (trimmed.Length > 0)
                sb.AppendLine("    <p>" + EscapeXml(trimmed) + "</p>");
        }
        sb.AppendLine("  </body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// 构建附录 XHTML 内容（角色、地点、组织）。
    /// </summary>
    /// <param name="settings">格式化设置。</param>
    /// <returns>附录 XHTML 字符串。</returns>
    private static string BuildAppendixXhtml(FormatterSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
        sb.AppendLine("  <head>");
        sb.AppendLine("    <title>附录</title>");
        sb.AppendLine("    <link rel=\"stylesheet\" href=\"style.css\"/>");
        sb.AppendLine("  </head>");
        sb.AppendLine("  <body>");
        sb.AppendLine("    <h1>附录</h1>");

        if (settings.Characters.Count > 0)
        {
            sb.AppendLine("    <h2>角色</h2>");
            foreach (var c in settings.Characters)
            {
                var role = c.Role switch
                {
                    CharacterRole.Protagonist => "主角",
                    CharacterRole.Supporting => "配角",
                    CharacterRole.Minor => "龙套",
                    CharacterRole.Antagonist => "反派",
                    _ => c.Role.ToString(),
                };
                var gender = string.IsNullOrEmpty(c.Gender) ? "" : $" ({c.Gender})";
                var age = string.IsNullOrEmpty(c.Age) ? "" : $"，{c.Age}";
                sb.AppendLine(
                    "    <p><b>"
                        + EscapeXml(c.Name)
                        + "</b> "
                        + $"<span style=\"font-size:0.85em;color:#888\">{role}{gender}{age}</span>: "
                        + EscapeXml(Truncate(c.Personality, 60))
                        + "</p>"
                );
            }
        }

        if (settings.Locations.Count > 0)
        {
            sb.AppendLine("    <h2>地点</h2>");
            foreach (var l in settings.Locations)
                sb.AppendLine(
                    "    <p><b>"
                        + EscapeXml(l.Name)
                        + "</b>: "
                        + EscapeXml(Truncate(l.Description, 60))
                        + "</p>"
                );
        }

        if (settings.Factions.Count > 0)
        {
            sb.AppendLine("    <h2>组织</h2>");
            foreach (var f in settings.Factions)
                sb.AppendLine(
                    "    <p><b>"
                        + EscapeXml(f.Name)
                        + "</b>: "
                        + EscapeXml(Truncate(f.Description, 60))
                        + "</p>"
                );
        }

        sb.AppendLine("  </body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// XML 转义特殊字符。
    /// </summary>
    /// <param name="s">原始字符串。</param>
    /// <returns>转义后的字符串。</returns>
    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

    /// <summary>
    /// 截断字符串到指定长度。
    /// </summary>
    /// <param name="s">原始字符串。</param>
    /// <param name="max">最大长度。</param>
    /// <returns>截断后的字符串。</returns>
    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "-" : s.Truncate(max);
}
