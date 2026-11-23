# 检测方法与版本跟踪

资料核对日期为 2026-11-23。

| 来源 | 公开信息 | TechPaper 中的处理 |
| --- | --- | --- |
| GPTZero 5.0，2026-11-19 | 厂商按领域、文本长度、生成模型和学习者语料报告误报及召回表现。这些结果具有特定数据集和评测设置。 | 在线功能调用官方接口，读取服务返回的版本与三分类概率。厂商成绩不能移用到离线规则。 |
| GPTZero 方法说明 | 官方支持页说明，2023 年秋季已迁移到深度学习架构。 | 本地仅提示行文特征。句长一致、重复短语都可出现在人工写作中。 |
| AACL 2026 中文机器文本检测研究 | 字符级 Transformer 与配对语料在其中文实验中表现良好。消融研究发现标点可能诱导表层过拟合。 | 文风提示显示可检查的具体段落，避免把表层指标转换为作者概率。 |
| OpenAI 模型更新 | 官方于 2026-11-17 更新了 GPT-6.2 Astra 与 Sol 的发布信息。 | 新生成模型带来分布变化。项目没有对该模型家族进行独立的中文论文检出率验证。 |

- [GPTZero 5.0 评测及局限](https://gptzero.me/research/gptzero-5)
- [GPTZero 对困惑度和突发性的说明](https://support.gptzero.me/articles/9585228410-how-do-i-interpret-burstiness-or-perplexity)
- [GPTZero API 返回字段和版本示例](https://docs.gptzero.me/quickstart/)
- [AACL 中文字符级检测论文](https://aclanthology.org/2026.aacl-long.265/)
- [OpenAI 发布记录](https://openai.com/index/introducing-gpt-6-sol-and-luna/)

## 复核一个新模型版本

在获得授权的同学科资料上分别收集人工原稿、纯模型生成、混合修订三组，保留写作过程证据。按作者、主题及生成模型版本划分训练、验证与独立测试集，避免同一原文的近似版本跨组。按语言、长度及领域报告样本数。分别呈现人工误报率、模型召回率、混合类混淆矩阵及置信区间。

调用在线服务时记录响应中的版本、评测日期、文本处理方式。厂商更新后可在相同的冻结测试集上重新运行，并另设未见新模型集。当前发行版完成了软件功能和文档结构回归，尚无独立中文学术语料准确率或跨平台降分率。

## 开源研究带来的取舍

Fast-DetectGPT 以条件概率曲率衡量候选文本，涉及语言模型及采样分布；项目未复刻该方法或移用其指标。DIPPER 的实验说明段落级释义可以改变已有检测器的输出，同一次释义也可能改变事实。M4 将多生成器、多领域和多语言并列作为检测维度。TechPaper 在合成稿中比较清晰度修订前后的分类，同时核查数字和引用。只要改变某条规则就会使本地标签漂移，不能据此宣称第三方检测率下降。

[Fast-DetectGPT](https://github.com/baoguangsheng/fast-detect-gpt) · [DIPPER 论文配套实现](https://github.com/martiansideofthemoon/ai-detection-paraphrases) · [M4](https://github.com/mbzuai-nlp/M4) · [非母语作者误报研究](https://scale.stanford.edu/publications/gpt-detectors-are-biased-against-non-native-english-writers)

GPTZero 将来源重叠和作者类别分别扫描。报告中的文档置信度也不能换算成 AI 字数占比。TechPaper 将可定位的本地语料片段与文风线索分别呈现。[GPTZero 来源比对说明](https://support.gptzero.me/articles/1988519176-what-is-plagiarism-check) · [GPTZero 分类解释](https://support.gptzero.me/articles/4965048546-How-do-I-interpret-a-GPTZero-result)
