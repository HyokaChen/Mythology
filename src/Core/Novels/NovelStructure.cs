using Core.Data;

namespace Core.Novels;

/// <summary>
/// 结构索引快照（大纲树 / 设定面板共用）：总纲（<see cref="Novel"/>）+ 卷纲 + 章节细纲元数据。
///
/// 章节头不含正文——<see cref="Chapter.Content"/> 恒为空串，字符数由
/// <see cref="Chapter.CharCount"/> 携带（结构读取路径只读 frontmatter，不加载正文）。
/// 由 <see cref="INovelStore.GetStructure"/> 按目录时间戳缓存构建；
/// <see cref="Unassigned"/> 为未分卷桶（VolumeId 为空或指向不存在卷的章节）。
///
/// <para>层级约定（四层大纲）：全书大纲 = <see cref="Novel"/>.Outline；
/// 卷 + 卷级大纲 = <see cref="Volumes"/>；章节细纲 + 章节 = <see cref="Chapters"/>（frontmatter 元数据）；
/// 正文不入此快照，按序单文件读（<c>GetChapterByOrder</c>）。</para>
///
/// <para>取证备注：本类型原文件位于本目录（<c>src/Core/Novels/</c>，命名空间 Core.Novels），
/// 2026-09-27 与 OutlineRows 同期新建时被 .gitignore 未锚定的 `novels/` 规则静默吞掉、从未入库
/// （见 .gitignore 178-183 行事故注释）。若原作者的原版补回，请删除本重建文件。</para>
/// </summary>
/// <param name="Novel">小说总纲与元数据（Title / Outline / CompletionStatus 等）。</param>
/// <param name="Volumes">全部卷（按 Order 升序）。</param>
/// <param name="Chapters">全部章节头（不含正文，按 Order 升序）。</param>
/// <param name="Unassigned">未分卷桶：VolumeId 为空或指向不存在卷的章节。</param>
public sealed record NovelStructure(
    Novel Novel,
    List<Volume> Volumes,
    List<Chapter> Chapters,
    List<Chapter> Unassigned
);
