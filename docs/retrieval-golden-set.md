# 检索 Golden Set（起点版）

> 用途：比较「精确事实车道」与「跨章关联车道」的检索质量，决定是否值得上 1 跳深召回。
> 语料：本机小说 `22b30e6861e5`（《恋爱吧！折花君》）当前索引。
> 说明：这是 **起点版**，gold chunk 由当前实体/关系/情节线文档生成；上线前请人工复核 10 条。

## 指标

| 指标 | 说明 |
|---|---|
| R@5 / R@10 | gold chunk 是否进入前 5/10 |
| MRR | 第一个 gold chunk 的排名倒数 |
| Precision@5 | 人工评“这条结果对写作有用”的比例 |
| p95 latency | 交互路径 ≤5s；后台 ≤10s |
| 护栏 | 直接事实类 R@5 不得比当前基线下降 >2 个点 |

## 对比组

1. `dense`：`DenseTopK=20, LexicalTopK=0`
2. `hybrid`：`DenseTopK=20, LexicalTopK=20`
3. `hybrid+lexical`：上面的词法层（jieba 预分词 + OR）
4. 仅在 3 仍不足时，再测 1 跳深召回

## 查询集

| id | class | query | gold chunk ids | 备注 |
|---|---|---|---|---|
| q01 | fact | 陈折花的性格、弱点和角色定位是什么？ | doc_908230bbdeeb_c0 | 角色档案直查 |
| q02 | fact | 千山秋依的外冷内热具体表现在哪些性格描述里？ | doc_a0da5ec075a7_c0 | 属性词命中 |
| q03 | fact | 皮皮龙的外形、性格和定位是什么？ | doc_9afc18664b2c_c0 | 角色档案 |
| q04 | fact | 松雪彩子的性格和弱点是什么？ | doc_d99f5bf723d4_c0 | 角色档案 |
| q05 | fact | 松雪美乃的性格和与姐姐的关系设定？ | doc_7960d3e85076_c0, doc_6763f2fcaa43_c0 | 档案 + 关系 |
| q06 | fact | 恶我的动机和角色定位是什么？ | doc_4df21ece6cec_c0 | 反派档案 |
| q07 | fact | 善我的性格和定位是什么？ | doc_13ed924269ae_c0 | 暗线角色 |
| q08 | fact | 青木立一的年龄、身份和性格是什么？ | doc_89d25b20ce8b_c0 | 角色档案 |
| q09 | alias | 那个调皮捣蛋、爱玩爱吃的龙族幼崽是谁？ | doc_9afc18664b2c_c0 | 只给描述，不给名字 |
| q10 | alias | 那个外冷内热、高二的女生是谁？ | doc_a0da5ec075a7_c0 | 只给描述 |
| q11 | alias | 那个心狠手辣、想夺走男主肉体的恶念化身是谁？ | doc_4df21ece6cec_c0 | 只给描述 |
| q12 | alias | 那个默默守护、愿意为爱放弃自身的善念化身是谁？ | doc_13ed924269ae_c0 | 只给描述 |
| q13 | alias | 那个温柔外放、被幽灵困住的松雪家姐姐是谁？ | doc_d99f5bf723d4_c0 | 只给描述 |
| q14 | alias | 那个活泼率直、嫉妒姐姐的松雪家妹妹是谁？ | doc_7960d3e85076_c0 | 只给描述 |
| q15 | alias | 那个退休的 62 岁老人是谁？ | doc_89d25b20ce8b_c0 | 年龄/身份 |
| q16 | alias | 那个 22 岁的大学生摄影师是谁？ | doc_2e7be17875b4_c0 | 年龄/身份 |
| q17 | relation | 皮皮龙和陈折花是什么关系？ | doc_091a044bbd9d_c0 | 关系陈述 |
| q18 | relation | 陈折花和善我是什么关系？ | doc_21cd14c29b8c_c0 | 关系陈述 |
| q19 | relation | 善我和恶我是什么关系？ | doc_2254334c99fa_c0 | 关系陈述 |
| q20 | relation | 松雪彩子和松雪美乃是什么关系？ | doc_6763f2fcaa43_c0 | 关系陈述 |
| q21 | relation | 千山秋依和善我是什么关系？ | doc_cfd10df130ec_c0 | 关系陈述 |
| q22 | relation | 宫无姬子和千山秋依是什么关系？ | doc_5eef8e475b27_c0 | 关系陈述 |
| q23 | relation | 陈折花和恶我是什么关系？ | doc_b25256b66275_c0 | 关系陈述 |
| q24 | relation | 青木立一和陈折花是什么关系？ | doc_86c5a3f75ab2_c0 | 关系陈述 |
| q25 | association | 姐姐因嫉妒化为幽灵、妹妹的怨念化成真相，这条主线是什么？ | doc_a8f925cf92b5_c0, doc_6795c06a7ee9_c0, doc_ebd8856a28d4_c0 | 情节线 + 章节 |
| q26 | association | 皮皮龙为什么会被召唤到男主身边，它的来历是什么？ | doc_12f9c2aa2040_c0, doc_5d95671eaa26_c0, doc_091a044bbd9d_c0 | 隐秘线 + 卷四 |
| q27 | association | 谁在暗中守护秋依，甚至为她放弃自身？ | doc_b34697f6ae89_c0, doc_13ed924269ae_c0, doc_cfd10df130ec_c0 | 善我暗线 |
| q28 | association | 恶我想夺走男主的什么，最终对决在哪一卷？ | doc_7c8fe565ac75_c0, doc_2dc586316ea5_c0, doc_b25256b66275_c0 | 恶我暗线 |
| q29 | association | 男主前世为救女主付出了什么代价？ | doc_5d95671eaa26_c0, doc_12f9c2aa2040_c0 | 卷四 + 皮皮龙线 |
| q30 | association | 青木老人为什么放不下过去，和男主的失忆有什么镜像关系？ | doc_000614fee035_c0, doc_89d25b20ce8b_c0 | 卷三 + 角色 |
| q31 | association | 萤火虫与湿地生态的环境主题在哪条线，谁发起抗争？ | doc_b9efe3da032e_c0, doc_ff6232be50ab_c0, doc_27b6b78cb391_c0 | 环境 + 卷三 + 姬子 |
| q32 | association | 男女主第五卷如何收束各卷线索并重新相遇？ | doc_2dc586316ea5_c0, doc_d4afdb1087b5_c0, doc_3a7499b510b5_c0 | 卷五 + 情感线 |
| q33 | negative | 赛博朋克城市的机械义肢维修记录 | — | 应低分/无结果 |
| q34 | negative | 星际战舰的曲率引擎参数 | — | 应低分/无结果 |
| q35 | negative | 中世纪欧洲的封建税制 | — | 应低分/无结果 |
| q36 | negative | 量子计算机的纠错码 | — | 应低分/无结果 |
| q37 | negative | 海洋馆的鲸鱼饲养手册 | — | 应低分/无结果 |
| q38 | negative | 唐代科举的录取比例 | — | 应低分/无结果 |
| q39 | negative | 火山岩的分类 | — | 应低分/无结果 |
| q40 | negative | 咖啡豆的烘焙曲线 | — | 应低分/无结果 |

## 本机可复现性说明（2026-10）

原 40 条查询指向 `22b30e6861e5`（《恋爱吧！折花君》）的实体库索引，本机 `~/.mythology/vectors.db`
里是另一部小说（`2c2410d54e08`，76 块）的索引，gold chunk id 全部不存在。实测 40 条里只有 5 条能在当前语料上评估，
且两臂都排第 1 —— **无区分度**。

因此把评测固化成一条命令（`src/StabilityHarness`），gold 从**库本身**推导，不依赖外部标注：

```bash
dotnet build src/StabilityHarness -c Release
cp ~/.mythology/vectors.db /tmp/v.db     # 评测只读打开，但建议用副本
./src/StabilityHarness/bin/Release/net10.0/StabilityHarness \
  retrieval-eval --db /tmp/v.db --novel 2c2410d54e08 --gold strict --verbose
```

gold 规则：
- 档案文档正文以 `角色: ` / `地点: ` / `情节线: ` / `场景: ` / `组织: ` / `道具: ` 开头 → 由此拿到实体名与其档案文档；
- **Fact**（`{名}的性格和设定是什么？`）：Strict = 该档案文档；Loose = 所有提到该名的文档；
- **Partial**（名字前 2 字）：Loose gold = 提到该名的文档；
- **Relation**（`和{名}相关的关系是什么？`）：gold = 提到该名的关系陈述。

三个方案：A 基线（默认词典 + 最佳路径）；B 现行（实体注册 + 索引侧 2/3 字片段）；
C 现行 + 二轮词项扩展（首轮 BM25 挖词项 → 二轮 → 交错合并，SAG2 式、不加 LLM）。

实测（54 条查询，候选上限 100）：

| 口径 | 方案 | R@5 | R@10 | AE@5（找齐全部证据） | MRR |
|---|---|---|---|---|---|
| strict | A 基线 | 74% | 76% | 72% | 0.605 |
| strict | **B 现行** | **94%** | **98%** | **98%** | **0.778** |
| strict | C 二轮扩展 | 91% | 98% | 98% | 0.774 |
| loose | A 基线 | 76% | 76% | 70% | 0.665 |
| loose | **B 现行** | **96%** | **98%** | **98%** | **0.835** |
| loose | C 二轮扩展 | 94% | 98% | 98% | 0.837 |

结论：B 相对 A 把 R@5 从 ~75% 提到 ~95%、AE@5 从 ~70% 提到 98%；
**C（SAG2 式二轮扩展）在本语料上没有收益**（单小说、首轮已覆盖证据），它是为多跳/大语料设计的，故不接入产品链路。

## 判定规则

- `fact` / `alias` / `relation` 走 **P 车道**：`ConsistencyCheck` 或 `WriteContext`，不做扩展。
- `association` 走 **A 车道**：`ForeshadowingSearch`，可测 1 跳深召回。
- `negative` 不设 gold；若 top1 分数接近正例 top1，说明检索阈值/噪声有问题。
- 深召回上线门槛：A 车道 R@5 相对 hybrid 提升 ≥10%，P 车道 R@5 不降，p95 ≤5s。
