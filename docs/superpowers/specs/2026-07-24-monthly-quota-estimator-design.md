# 月额度估算器设计

## 目标

为服务器识别为 `Monthly` 的账号自动估算当前基础月额度美元区间，同时排除赠送重置次数产生的历史周期用量。

显示格式：

`估算单次月额度 US$160–200`

估算值与用户手动记录的“单次月额度”及服务器可选的“官方月度上限”分开显示，互不覆盖。

## 数据来源

仅在用户点击“刷新额度”时调用以下非公开接口：

1. `GET /backend-api/wham/usage`
   - 读取 Monthly 窗口的 `used_percent`、`reset_at`、`reset_after_seconds` 和窗口长度。
2. `GET /backend-api/wham/rate-limit-reset-credits`
   - 读取重置记录的 `status` 和 `redeemed_at`。
3. `GET /backend-api/wham/analytics/daily-workspace-usage-counts`
   - 读取当前有效估算片段的每日 `totals.credits`。

请求继续使用账号认证快照中的令牌与账号 ID。令牌、完整响应和认证 JSON 不写入日志或本机元数据。

## 估算片段

先计算 Monthly 自然窗口起点：

`naturalStart = reset_at - limit_window_seconds`

在成功读取重置历史后，选择满足以下条件的最新 `redeemed_at`：

- 状态表示已经兑换；
- 时间大于或等于 `naturalStart`；
- 时间小于或等于服务器当前时间。

若存在该记录：

`segmentStart = latest redeemed_at`

否则：

`segmentStart = naturalStart`

只统计 `segmentStart` 至服务器当前时间的 Analytics Credits。不得乘以可用重置次数、用户记录的已用重置次数或历史兑换次数。

## 区间算法

Analytics 只提供日期桶，无法精确切开 `segmentStart` 当天重置前后的用量：

- 下限：排除 `segmentStart` 所在日期桶。
- 上限：包含 `segmentStart` 所在日期桶。
- 若 `segmentStart` 恰好为 UTC `00:00:00`，不存在起始日混入问题，上下限都包含该日期桶。

将上下界 Credits 分别除以当前 `used_percent / 100`，再按现有估算口径以 `1000 Credits = US$40` 折算美元并保留两位小数。

该换算是估算口径，不是 OpenAI 官方套餐价格。

## 可用条件与失败策略

Monthly 自动估算必须同时满足：

- `used_percent` 大于 0；
- `reset_at`、窗口长度和服务器当前时间有效；
- 重置历史请求成功且响应结构有效；
- Analytics 请求成功且响应结构有效。

如果使用量为 0，显示：

`估算单次月额度：产生用量后可计算`

如果重置历史不可读取、响应无效，或 Analytics 无法读取，显示：

`估算单次月额度：暂不可用`

不得在重置历史未知时假设“没有使用过重置”。任何估算失败都不得覆盖已经成功刷新的 `Monthly` 剩余百分比、重置时间、手动额度记录或官方月度上限。

## 现有周额度行为

Weekly 估算继续使用当前逻辑和当前接口，不新增重置历史依赖，不改变现有周额度计算结果或显示文案。

共享的 Credits 区间计算可以抽取为周期无关的估算器，但不得改变周额度的既有输出。

## 验证

- Monthly 使用量大于 0、无兑换记录时，从自然窗口起点估算。
- Monthly 使用量大于 0、存在一个或多个兑换记录时，只使用最近一次 `redeemed_at` 之后的用量。
- 自然窗口之外及服务器当前时间之后的兑换记录被忽略。
- `segmentStart` 位于 UTC 零点时上下限相等；非零点时仅由起始日期桶形成区间。
- 重置历史接口失败或结构无效时不请求或不应用月额度估算。
- Analytics 失败时保留 Monthly 百分比。
- Monthly 使用量为 0 时不请求估算所需接口，并显示需产生用量。
- Weekly 估算测试保持原样通过。
- 完整测试套件、发布合同和认证文件哈希检查通过。

## 非目标

- 不自动消费重置次数。
- 不自动修改用户记录的累计已用重置次数。
- 不根据 `available_count` 的变化推测历史兑换。
- 不估算尚未产生用量的月额度。
- 不增加自动账号切换或代理模式。
