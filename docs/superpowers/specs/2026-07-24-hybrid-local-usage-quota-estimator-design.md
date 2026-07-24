# 混合本地用量额度估算器设计

## 目标

修复 Weekly 或 Monthly 已产生用量、但界面仍显示“估算单次额度：暂不可用”的问题。

服务器 `/wham/usage` 继续提供权威的周期、已用百分比和重置时间；当
Analytics 返回有效 Credits 时沿用服务器数据，当 Analytics 成功但
`data` 为空时，改用本机 Codex 会话中的 token 记录估算本次周期额度。
每次手动刷新都保存一个观测点，后续观测点用于缩小估算区间。

界面必须明确区分“服务器 Analytics 估算”和“本机用量估算”。估算值不是
OpenAI 公布的套餐固定额度，也不能被显示为精确的官方额度。

## 已确认根因

当前账号的 Analytics 请求返回 HTTP 200，但 `data` 是空数组。现有实现把
空数组视为无法估算，因此即使 `/wham/usage` 已显示 Weekly 使用 25%，额度
估算仍为空。相同情况也会影响 Monthly。

这不是使用量不足，也不是额度缓存丢失。修复不能继续依赖 Analytics 必须
有数据这一前提。

## 数据源与信任顺序

1. `/backend-api/wham/usage`
   - 权威提供周期类型、`used_percent`、`reset_at` 和窗口长度。
2. `/backend-api/wham/rate-limit-reset-credits`
   - Monthly 用于识别最近一次已兑换主动重置，确定当前估算片段起点。
3. `/backend-api/wham/analytics/daily-workspace-usage-counts`
   - 返回非空数据时优先使用，保持现有 Analytics 估算能力。
4. `%USERPROFILE%\.codex\sessions\**\*.jsonl`
   - Analytics 数据为空时使用，只统计能够可靠归属到账号和当前片段的
     本机 token 事件。

Analytics 请求失败与 Analytics 成功但数据为空必须区分。失败时保留服务器
百分比并显示具体的简短状态；空数据时自动尝试本机兜底。

## 本地 token 采集与 Credits 计算

解析会话 JSONL 中每次请求的 `event_msg.token_count.info.last_token_usage`，
并从相邻上下文读取实际模型和速度模式。每条事件使用事件自身的 UTC 时间。

对于官方费率表中受支持的模型：

```text
uncached_input = max(0, input_tokens - cached_input_tokens)

credits =
  uncached_input × input_rate
  + cached_input_tokens × cached_input_rate
  + output_tokens × output_rate
```

费率按每一百万 token 的 Credits 换算。`reasoning_output_tokens` 已包含在
`output_tokens` 中，不得再次相加。若输入字段异常，例如缓存输入大于输入
总量，该事件无效，不以静默修正后的数据参与估算。

Fast 模式只按 OpenAI 当前文档中明确公布的倍率处理。不得沿用来源脚本的
统一 `1.5×` 倍率，也不得加入缺少可靠来源的超长上下文 `2×` 规则。

费率表以显式版本保存在代码中，并在测试中覆盖已支持模型。遇到未知模型或
没有官方费率的预览模型时，不猜测价格；跳过该事件并显示“部分用量无法计价”
或在全部事件均无法计价时显示“当前模型暂无官方费率”。

美元显示继续使用现有换算口径 `1000 Credits = US$40`，并在详情中标注为
“按 Credits 购买价格换算的估算”，而不是套餐官方标价。

参考：

- [OpenAI Codex rate card](https://help.openai.com/en/articles/20001106-codex-rate-card)
- [OpenAI Speed documentation](https://learn.chatgpt.com/docs/agent-configuration/speed)

## 账号归属

本地会话事件本身不包含账号 ID，因此不能根据邮箱、目录名或时间接近程度
猜测账号。

应用维护每个稳定 `account_key` 的已知激活区间：

- 启动和重新加载注册表时，记录当前活动账号及其激活时间；
- 登录成功、账号切换成功时结束旧账号区间并开始新账号区间；
- 仅当事件时间落在一个无歧义的已知激活区间内时，才归属到该账号；
- 区间重叠、缺口或未知历史中的事件不参与任何账号估算。

当前活动 Weekly 账号的注册表激活时间早于本次 Weekly 片段起点，因此只要
本机日志覆盖该片段且存在可计价事件，第一次刷新即可给出初步估算。

此前未被本软件可靠跟踪的非活动账号不能追溯猜测历史用量。它们从首次建立
可靠激活区间和服务器观测点后开始积累，通常需要再次使用并刷新才能估算。

## 周期片段与重置

Weekly 自然片段：

```text
segmentStart = reset_at - limit_window_seconds
```

Monthly 先计算相同的自然起点；若当前自然窗口内存在已兑换的主动重置：

```text
segmentStart = latest redeemed_at
```

否则使用自然起点。片段身份由以下字段共同组成：

- 稳定账号 key；
- `Weekly` 或 `Monthly`；
- `segmentStart`；
- `reset_at`。

自然重置或主动重置使片段身份变化。新片段不得使用旧片段的 Credits、百分比
或估算区间，旧数据只作为历史保留。

## 单个观测点

每次成功的手动“刷新额度”保存服务器观测时间、已用百分比和本地或 Analytics
Credits。服务器只返回整数百分比时，将真实百分比视为：

```text
[max(0, displayedPercent - 0.5), min(100, displayedPercent + 0.5)]
```

若将来接口提供更高精度，则按实际精度生成相应误差范围。

### 完整片段观测

当账号的可靠激活覆盖从 `segmentStart` 到观测时间的整个片段时，累计该片段
中的可计价 Credits。设累计值为 `C`，百分比范围为 `[pLow, pHigh]`：

```text
quotaLowerCredits = C / (pHigh / 100)
quotaUpperCredits = C / (pLow / 100)
```

只有 `C > 0` 且 `pLow > 0` 时才有有限区间。第一次有效观测显示“初步估算”。

### 增量观测

当可靠激活从片段中途开始时，第一次刷新只建立基线。之后同一账号、同一片段
且激活连续时，使用两个观测点之间的 `deltaCredits` 和
`deltaUsedPercent` 估算：

```text
deltaPercentLow  = laterLow - earlierHigh
deltaPercentHigh = laterHigh - earlierLow

quotaLowerCredits = deltaCredits / (deltaPercentHigh / 100)
quotaUpperCredits = deltaCredits / (deltaPercentLow / 100)
```

要求 `deltaCredits > 0` 且 `deltaPercentLow > 0`。否则只提示“继续使用后再次
刷新”，不生成没有有限上界或由百分比舍入噪声主导的数值。

本机 Credits 只覆盖本机日志。若同一账号还在其他电脑或客户端产生用量，本机
估算会偏低，因此本地来源始终显示“本机用量估算”，不得伪装成服务器精确值。

## 多观测点收敛

每个有效完整片段观测或增量观测都会产生一个美元区间。展示时从最新区间开始，
向前与同一片段的区间求交集：

```text
finalLower = max(all compatible lowers)
finalUpper = min(all compatible uppers)
```

只采用最近的连续兼容后缀。若加入更早区间会导致交集为空，则在该区间前停止，
保留最近的兼容结果并记录“历史观测不一致”。不对冲突区间取平均，也不输出
虚假的精确值。

- 一个有效区间：`初步估算单次周额度：US$X–Y`
- 两个及以上兼容区间：`多点估算单次月额度：US$X–Y`
- 上下限按现有货币格式显示；只有计算后相等才显示单值。

## 存储与隐私

新增独立版本化文件：

```text
%LOCALAPPDATA%\CodexAccountSwitcher\quota-estimate-ledger.json
```

不扩充现有 `quota-cache.json` 的职责。估算账本只保存：

- 稳定账号 key；
- 已知激活区间；
- 片段身份；
- 观测时间和百分比精度；
- 已可靠归属的 Credits 或 Analytics 估算区间；
- 数据来源和不含敏感内容的状态；
- 必要的本地扫描检查点。

不得保存访问令牌、邮箱、提示词、回复正文、完整会话事件、请求头或原始接口
响应。写入采用临时文件加原子替换；损坏或不支持版本的账本保留原文件并停止
覆盖，不影响服务器百分比刷新。

一次批量刷新只扫描相关本地会话文件一次，再按账号激活区间聚合，不能为每个
账号重复扫描全目录。只读取修改时间可能与当前片段或已知激活区间重叠的文件。

## 界面与状态文案

账号卡片继续显示服务器余量百分比和重置时间。详情区显示额度估算和来源：

- `初步估算单次周额度：US$X–Y（本机用量）`
- `多点估算单次月额度：US$X–Y（服务器 Analytics）`
- `Analytics 无数据，已改用本机用量估算`
- `已建立估算基线，继续使用后再次刷新`
- `当前片段没有可计价的本机用量`
- `当前模型暂无官方费率`
- `部分用量无法计价，区间可能偏低`
- `账号历史归属不明确，将从本次刷新开始记录`

不得再把 Analytics 空数组笼统显示为“暂不可用”。估算失败不能覆盖已成功
刷新的服务器百分比、重置时间、可用重置次数或用户手动记录。

## 失败处理

- `/usage` 失败：该账号刷新失败，保留上次缓存。
- 重置历史失败：Monthly 不猜测主动重置片段；保留百分比并说明无法确定片段。
- Analytics 请求失败：尝试本地兜底，并保留简短来源状态。
- Analytics 空数据：正常进入本地兜底，不作为异常。
- 本地文件缺失或解析失败：跳过单个无效文件或事件，报告汇总状态，不中止其他
  账号刷新。
- 账本读写失败：不影响服务器额度显示；写失败时保留本次内存结果并提示未保存。
- 账号归属不明确：不使用相关事件。
- 区间冲突：使用最近兼容后缀并显示不一致状态。

## 验证

测试必须覆盖：

- Analytics HTTP 200 且 `data=[]` 时进入本地兜底；
- token 公式正确拆分缓存和非缓存输入；
- reasoning token 不被重复计价；
- 官方模型标准与 Fast 模式费率；
- 未知模型和混合已知/未知模型状态；
- 事件只归属到无歧义的账号激活区间；
- 当前活动账号覆盖完整片段时第一次刷新可生成初步估算；
- 历史归属未知的非活动账号不会被猜测，并可由后续增量观测生成估算；
- Weekly 自然重置和 Monthly 主动重置创建新片段；
- 整数百分比误差传播到完整片段与增量估算区间；
- 多观测区间求交、冲突时选择最近兼容后缀；
- 账本原子保存、恢复、损坏文件保留和账号隔离；
- 缓存恢复后仍显示最近一次估算，手动刷新后更新；
- 本地文件部分损坏不影响其他账号；
- 完整 Release 测试、发布文件合同和认证文件哈希检查通过。

测试和验收不得执行真实登录、账号切换、账号移除、主动重置或自动切换。接口
和本地会话输入使用脱敏 fixture；真实账号只在用户明确操作“刷新额度”时访问
现有只读额度接口。

## 非目标

- 不自动切换账号。
- 不自动消耗主动重置次数。
- 不修改 Codex 的 `auth.json`、会话历史或个性化设置。
- 不追溯猜测无法证明归属的历史会话。
- 不把本机估算宣传为 OpenAI 官方套餐额度。
- 不增加后台自动刷新或持续监控。
- 不扩大到主界面之外的视觉重构。
