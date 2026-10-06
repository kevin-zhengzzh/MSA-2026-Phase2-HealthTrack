# AI 功能需求文档：对话助手与每周总结

> 本文件是 [06-ai-features-spec.md](./06-ai-features-spec.md) 的中文版。两个版本需要同步修改；内容不一致时，以英文版为准。

**状态：** P0 已上线（2026-10-07，commit `45fce57`）；P1 和 P2（演示数据、每周总结、T-4/T-5、话题按钮）已在本地完成，尚未部署
**创建日期：** 2026-10-06
**截止时间：** 1 天（P0 必须上线，P1 / P2 视时间而定）

本文档是 HealthTrack AI 扩展功能的唯一依据。所有和 AI 相关的代码、提示词、测试和部署改动，都应该能对应到本文档里的某个编号。决定有变化时，先更新本文档（见 [变更记录](#13-变更记录)）。

---

## 1. 目标

主要目的是作为**面试作品集**。功能需要展示以下能力，并且容易讲清楚：

1. **工具调用 / Agent 循环**：模型决定调用哪个工具，后端执行，循环进行直到得出答案
2. **流式输出**：回复逐字显示
3. **用户数据隔离**：模型永远读不到其他用户的数据
4. **频率限制和用量记录**：成本有上限，并且可以观测
5. **可替换的模型层**：业务代码不和某一家 AI 服务商绑定

非目标：追求 AI 功能的数量。

## 2. 范围和优先级

| 优先级 | 内容 | 编号 |
|---|---|---|
| **P0 必须上线** | 带流式输出的对话助手 | CA-* |
| | 3 个只读工具 | T-1、T-2、T-3 |
| | 用量记录 + 每人每天 30 条上限 | NF-4 |
| | 通过现有 CI/CD 部署到 Azure | NF-7 |
| **P1 尽量上线** | 演示数据脚本 | DM-* |
| | AI 每周总结 | WS-* |
| **P2 有时间再做** | 再加 2 个工具 + 话题按钮（对话模式） | T-4、T-5、CM-* |
| **不在范围内** | 👍/👎 反馈、Langfuse 链路追踪、对话历史存数据库、自然语言记录运动、会修改数据的工具、应用内的管理或监测界面 | — |

## 3. 关键决策

| 编号 | 决策 | 原因 |
|---|---|---|
| D-1 | **去掉自然语言记录运动** | 现有表单只有一个下拉框加一个数字，打一句话反而更慢。AI 应该用在人工操作真正麻烦的地方。 |
| D-2 | **服务商：DeepSeek**（兼容 OpenAI 的接口） | 成本低，支持工具调用和流式输出。 |
| D-3 | **所有模型调用都通过 `IChatModel` 接口** | 新增一个实现类就能换服务商（比如换成 Claude 或 OpenAI）；测试时也能 mock 掉 AI。 |
| D-4 | **只有统计数据会离开系统** | DeepSeek 的服务器在中国。工具结果和提示词里只包含统计数字和运动类型，绝不包含邮箱、用户名、头像或密码哈希。 |
| D-5 | **`userId` 永远不作为工具参数** | 所有工具都从 JWT 中读取当前用户。即使用户说"给我看用户 1 的数据"，也没有任何途径查到别人的数据。 |
| D-6 | **数字由 C# 计算，文字由模型组织** | 模型做算术不可靠，所有总数和百分比都在交给模型之前算好。 |
| D-7 | **对话历史只保存在前端** | 刷新后清空。不需要新建表，对话内容也不会进数据库。前端发来的历史可以信任，因为不管历史怎么写，工具都只能查到 JWT 对应用户的数据。 |
| D-8 | **不在应用内开放监测数据** | 应用没有管理员角色，任何监测接口都会对所有用户可见。用量在 Neon 控制台查询，日志在 Azure Log Stream 查看，花费在 DeepSeek 后台查看。 |
| D-9 | **每周总结覆盖上一个完整的周** | 数据完整，每周只生成一次，缓存最简单，成本最低。 |
| D-10 | **语言**：对话跟随用户输入；每周总结跟随浏览器语言 | 每周总结没有用户输入可以参考，所以由前端传入 `navigator.language`。 |
| D-11 | **模型：`deepseek-flash`，并关闭思考模式**（`"thinking": { "type": "disabled" }`） | 思考模式默认开启。在思考模式下，只要请求带了 `tools`，就必须回传之前每一轮的 `reasoning_content`（包括跨用户提问的轮次），否则 API 返回 400。这和 D-7（前端只保存文字）冲突；而且额外的 token 会增加从 Azure 澳大利亚到 DeepSeek 的延迟。查几个数字再总结一下，不需要深度推理。 |
| D-12 | **客户端：自己写一个轻量的 `HttpClient` 封装，调用 DeepSeek 的 OpenAI 格式接口 `/chat/completions`**，不用第三方 SDK | 需要发送 DeepSeek 特有的字段（`thinking`），OpenAI 的 .NET SDK 不支持。大约 150 行代码就能完全控制请求内容、SSE 解析、工具调用的增量拼接和 `usage`。它位于 `IChatModel` 后面（D-3），以后换成 SDK 只需要改这一处。 |
| D-13 | **话题按钮是模式，而不是预设问题**：每个按钮发送 `mode`，后端据此加上话题说明并缩小工具范围（§5.3） | 像 AI 产品里的"工具"按钮一样方便用户发现功能，同时减少模型选错工具的机会。按钮绝不绕过模型直接调用接口，否则只是和现有按钮重复。 |

## 4. 架构

```
前端（React）
  ├─ ChatWidget ──── POST /api/ai/chat（SSE）──┐
  └─ WeeklySummaryCard ── GET /api/ai/weekly-summary ──┐
                                                │     │
后端（ASP.NET Core）                            ▼     ▼
  AiController（[Authorize]，UserId 从 JWT 获取）
    ├─ ChatQuotaService ─────── AiUsageLog（统计今天的对话次数）
    ├─ ChatAssistantService ─── Agent 循环（模型 ↔ 工具），不涉及 HTTP
    ├─ ChatToolExecutor ─────── EF Core 查询，一律按 UserId 过滤
    ├─ WeeklySummaryService ─── WeeklySummary 缓存表
    ├─ AiUsageTracker（每个请求一个实例）── 每次请求写一行 AiUsageLog
    └─ IChatModel
         └─ UsageTrackingChatModel（装饰器：把每次调用的 token 累加到 tracker）
              └─ DeepSeekChatModel ──── HTTPS ──── DeepSeek API
```

- `UsageTrackingChatModel` 包在真正的实现外面，把每次模型调用的 token 用量累加到按请求创建的 `AiUsageTracker` 里；工具执行时把工具名记到同一个 tracker；请求结束时由接口调用一次 `SaveAsync`。这样业务代码不用关心记录逻辑，同时满足**每次用户请求只记一行**（§8），尽管装饰器本身看到的是一次次单独的模型调用。
- `IChatModel` 的具体方法签名（普通调用 + 用 `IAsyncEnumerable` 返回的流式事件，都支持工具调用）在实现阶段确定。DeepSeek API 已经确认过了，见 [§12](#12-待确认事项)。

## 5. 功能需求

### 5.1 对话助手（P0）

**用户故事：** 作为用户，我希望用自然语言询问自己的运动情况（比如"我这个月运动得怎么样？""还差几天拿到奖励皮肤？"），而不用在多个页面之间来回找。

| 编号 | 需求 | 验收标准 |
|---|---|---|
| CA-1 | 所有登录后的页面右下角都有聊天按钮 | 点击能打开 / 关闭对话面板；在手机上不能挡住 `BackToTopButton` |
| CA-2 | 对话面板为空时显示 3 到 4 个推荐问题 | 点击后直接作为消息发送 |
| CA-3 | 助手使用 [§6](#6-工具) 里的工具回答问题 | 问"这周运动了几次？"会触发 T-1，回答和数据库里的数据一致 |
| CA-4 | 回复流式显示 | 发送后 3 秒内出现第一个字（NF-3）；工具运行期间显示"正在查询你的数据…" |
| CA-5 | 用用户提问的语言回复 | 用中文问就用中文答，用英文问就用英文答 |
| CA-6 | 不偏离主题 | 无关的请求礼貌拒绝；不做医疗诊断；涉及健康问题时提示咨询专业人士 |
| CA-7 | 历史只保存在前端 | 刷新后清空；每次请求最多带最近 20 轮 |
| CA-8 | 显示今日剩余次数 | 比如"今日剩余 25 / 30 条"；用完后禁用输入框并说明原因 |
| CA-9 | 出错时正常降级 | API 出错或超时：显示错误提示和重试按钮；应用其他部分不受影响 |

### 5.2 每周总结（P1）

**用户故事：** 作为用户，我打开 Dashboard 时，希望看到上周情况的简短总结和一条具体建议，这样不用自己去分析图表。

| 编号 | 需求 | 验收标准 |
|---|---|---|
| WS-1 | Dashboard 卡片显示上一个完整周（周一到周日）的总结，3 到 4 句话 | 提到运动天数、总消耗、目标完成度；最后给一条建议 |
| WS-2 | 统计数据由 C# 计算，模型只负责写文字 | 总结里的每个数字都和数据库一致（D-6） |
| WS-3 | 语言跟随浏览器 | 前端传入 `lang`（来自 `navigator.language`） |
| WS-4 | 按用户和周缓存 | 同一周第二次加载时不调用模型 |
| WS-5 | 没有数据就不调用模型 | 上周没有运动记录时（包括新用户），返回固定的鼓励文案，`source: "fallback"` |
| WS-6 | 出错时正常降级 | 模型出错时卡片显示"暂时无法生成总结"；Dashboard 照常加载 |

实现说明（`backend/Services/Ai/WeeklySummaryService.cs`、`frontend/src/components/WeeklySummaryCard.tsx`）：
- 支持的语言是一个白名单：`en` 和 `zh`（`zh-CN`、`zh_TW` 都归为 `zh`），其他语言一律按 `en` 处理。这样每周缓存的版本数和模型调用次数都有上限。
- 发给模型的统计数据：`weekStart`、`weekEnd`、`workoutCount`、`activeDays`、`totalCalories`、`weeklyCalorieGoal`、`goalPercent`、`previousWeekCalories`、`checkInDays`、`byType`。不包含名字和备注（D-4）。
- 只有真正调用模型时才写入一条 `AiUsageLog`（`weekly_summary`）；命中缓存和返回固定文案时不记录。模型失败时返回 `503`，并记为失败。
- 并发请求导致重复插入时（唯一索引），会捕获异常并返回已保存的那一份。
- 卡片放在 Dashboard 四宫格上方，单独占一整行；带有"AI summary"标签（固定文案时不显示），出错时有重试按钮。
- 真实 DeepSeek 验证（2026-10-07，本地）：英文和中文的数字都正确；命中缓存约 6 毫秒；新用户返回固定文案，没有调用模型。第一次生成中文时，模型把时间说成了"本周"，提示词现在明确要求称为"上周"，之后连续生成 3 次都正确。

### 5.3 话题按钮 / 对话模式（P2）

**用户故事：** 作为用户，我希望输入框上方有一排可以一键点击的话题（"排行""运动建议"……），这样不用自己组织问题，就能知道助手能做什么，并得到有针对性的回答。

| 编号 | 需求 | 验收标准 |
|---|---|---|
| CM-1 | 输入框上方的按钮：📊 Weekly review、💡 Workout advice、🏆 Rank、🪙 Points、🎯 Goal | 一直显示（不只在对话为空时）；屏幕窄时可以横向滚动；取代原来的推荐问题（CA-2） |
| CM-2 | 点击按钮选中这个模式；再点一次取消 | 选中的按钮高亮（`aria-pressed`）并显示 ×；后续追问时保持选中 |
| CM-3 | 选中模式后，输入框为空也能发送 | 发送这个按钮的默认问题；输入了文字时发送输入的文字 |
| CM-4 | 请求里带上 `mode`；后端给系统提示词加上这个话题的说明，并缩小工具范围 | 例如 `rank` 只开放 T-4；不存在的模式返回 `400` |
| CM-5 | 带模式发送的用户消息，气泡上显示一个小标签 | |
| CM-6 | 运动建议只给一般性的健身建议 | 提到疼痛、受伤或身体状况时，建议咨询专业人士，不给建议 |

模式（`backend/Services/Ai/ChatModes.cs`）和对应的工具：

| 模式 | 工具 | 默认问题（前端） |
|---|---|---|
| `review` | T-1、T-3、T-2 | How is my week going? |
| `advice` | T-1、T-3 | Based on my recent workouts, what should I do next? |
| `rank` | T-4 | Where do I stand on the leaderboards? |
| `points` | T-5 | Summarize my points and any unclaimed rewards. |
| `goal` | T-3、T-1 | How close am I to my weekly goal? |

不选模式、直接打字提问时，仍然可以使用全部 5 个工具。

真实 DeepSeek 验证（2026-10-07，本地，演示账号）：每个模式的数字都正确；`rank` 和 `points` 只调用了各自的工具；在运动建议模式下问"跑步时膝盖疼怎么办"，没有调用工具，并建议看医生或理疗师；`rank` 没有提到任何其他用户；`mode: "hack"` 返回 400。

## 6. 工具

所有工具都是**只读的**，**不接受 `userId`**（D-5），**只返回统计数据**（D-4）。后端判断"今天"时，使用和现有 Controller 一样的 `ResolveToday` 限制逻辑。

| 编号 | 优先级 | 名称 | 输入 | 返回 |
|---|---|---|---|---|
| T-1 | P0 | `get_workout_summary` | `range`：`"this_week"` \| `"last_week"` \| `"this_month"` \| `"last_month"` | `{ range, from, to, workoutCount, activeDays, totalCalories, byType: { Running: { count, calories }, … } }` |
| T-2 | P0 | `get_checkin_status` | — | `{ streak, checkedInToday, rewardSkinStreak, ownsRewardSkin, checkInsUntilRewardSkin }` |
| T-3 | P0 | `get_weekly_goal_progress` | — | `{ weekStart, goal, caloriesSoFar, remainingCalories, percent, daysLeftInWeek }` |
| T-4 | P2 ✅ | `get_rank` | — | `{ totalUsers, leaderboardShowsTop, points/streak/caloriesToday: { rank, value, behindNextRank }, earliestCheckInToday: { rank, checkedInToday, usersCheckedInToday } }`，排序规则和 `LeaderboardController` 一致（包括使用存储的 `Streak`）；只返回自己的位置 |
| T-5 | P2 ✅ | `get_points_summary` | — | `{ balance, earnedLast7Days, spentLast7Days, unclaimedToday: { checkIn, workout, total }, recentTransactions: [{ reason, amount, date }] }`，未领取奖励的判断规则和 `RewardsController.GetToday` 一致 |

工具名称不存在或参数无效时，不抛出异常，而是把 `{ "error": "..." }` 作为工具结果返回，让模型自己处理。

实现说明（`backend/Services/Ai/ChatToolExecutor.cs`）：
- `ExecuteAsync(userId, today, call)`：`userId` 由 Controller 从 JWT 中取出传入；模型的参数只会读取 `range`，所以即使被注入了 `"userId": 2` 也会被忽略（有测试覆盖）。
- 一周从**周一**开始，和前端的 `WeeklyGoalDonut` 一致。
- **T-2 的连续签到天数：** `User.Streak` 只在下次签到时才重新计算，所以中断几天后它还保留着旧值。工具只有在最近一次签到（`CheckIns.Date`，本地日期）是今天或昨天时才返回这个值，否则返回 `streak = 0`。奖励皮肤所需的天数来自 `CheckInController.RewardSkinStreak`（原来是写死的 `7`，现在提成了常量），保证工具和签到逻辑使用同一个值。

## 7. API

所有接口都需要登录（`[Authorize]`）。

### `POST /api/ai/chat`：Server-Sent Events

请求：
```json
{ "history": [{ "role": "user", "text": "..." }, { "role": "assistant", "text": "..." }], "message": "How did I do this week?", "mode": "review" }
```

响应：`Content-Type: text/event-stream`，每个 `data:` 行是一个 JSON 对象：

| 事件 | 含义 |
|---|---|
| `{ "type": "tool", "name": "get_workout_summary" }` | 开始执行某个工具（前端据此显示"正在查询…"） |
| `{ "type": "text", "text": "This week you…" }` | 要追加显示的文字片段 |
| `{ "type": "done", "remaining": 24 }` | 完成，并返回更新后的剩余次数 |
| `{ "type": "error", "message": "..." }` | 失败，流结束 |

开始流式输出之前：今日额度用完时返回 `429` 和 `{ "message": "..." }`；`message` 为空或超过 1000 个字符时返回 `400`。

流式输出开始后发生的错误（模型失败、超时）会以 `error` 事件的形式出现在 `200` 响应里，因为响应头已经发出去了。`mode` 是可选的（§5.3），值不存在时返回 `400`。和其他接口一样传入 `?localDate=YYYY-MM-DD`，服务器会用 `ResolveToday` 做限制。历史记录里角色不是 `assistant` 的轮次一律当作 `user` 处理，前端无法注入系统消息。

前端必须用 `fetch` + `ReadableStream` 读取，不能用 `EventSource`，因为它不能发送 POST 请求体，也不能带 `Authorization` 请求头。

### `GET /api/ai/chat/quota`

```json
{ "used": 6, "limit": 30, "remaining": 24 }
```

### `GET /api/ai/weekly-summary?lang=zh-CN&localDate=2026-10-06`（P1）

```json
{ "weekStart": "2026-09-28", "weekEnd": "2026-10-04", "summary": "...", "source": "ai" }
```
`source` 的取值为 `"ai"`、`"cache"` 或 `"fallback"`。

## 8. 数据模型

两张表都通过 EF Core 迁移添加，后端启动时自动执行（`Database.Migrate()`）。

### `AiUsageLog`（P0）：每次用户请求记一行（不是每次模型调用）

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | int 主键 | |
| `UserId` | int | 和 `CreatedAt` 一起建索引 |
| `Feature` | string | `"chat"` \| `"weekly_summary"` |
| `InputTokens` / `OutputTokens` | int | Agent 循环里所有模型调用的总和 |
| `LatencyMs` | int | 整个请求的耗时 |
| `ToolsCalled` | string? | 用逗号分隔，比如 `"get_workout_summary,get_checkin_status"` |
| `Success` | bool | |
| `Error` | string? | 只记录简短的错误信息 |
| `CreatedAt` | DateTime（UTC） | |

**不保存任何对话内容。** 额度 = 该用户从 UTC 0 点起 `Feature = "chat" AND Success = true` 的记录数；失败的请求不计入额度。

**已知限制：** 额度在请求开始前检查，但记录在请求结束时才写入。所以只剩 1 条额度时，如果同时发出两条消息，两条都可能通过。对于每天 30 条的软性限制，这可以接受；如果要求严格限制，需要先写一条预占记录，或者在数据库层面计数。

### `WeeklySummary`（P1）

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | int 主键 | |
| `UserId` | int | `(UserId, WeekStart, Language)` 建唯一索引 |
| `WeekStart` | DateOnly | 周一 |
| `Language` | string | 规范化后的值，比如 `"zh"` / `"en"` |
| `Content` | string | |
| `CreatedAt` | DateTime（UTC） | |

## 9. 提示词（草稿）

实际发送给模型的提示词用英文，下面附上中文翻译方便理解。

**对话的系统提示词：**
> You are the HealthTrack assistant. You help the signed-in user understand their own workouts, check-ins, streaks, and goals in this app. Whenever an answer depends on the user's data, call the relevant tool first — even for follow-up questions and even if earlier messages mention numbers, because data can change and earlier replies may be incomplete. Never guess or invent numbers. Call tools directly without announcing that you are about to look something up. Reply in the same language as the user's latest message. Keep answers short and encouraging. Only discuss fitness and this app; politely decline anything else. Do not diagnose medical conditions; for health concerns, suggest consulting a professional.
>
> Today is {Weekday, yyyy-MM-dd} in the user's time zone.

中文翻译：你是 HealthTrack 助手，帮助已登录的用户了解他们在这个应用里的运动、签到、连续签到和目标情况。只要回答依赖用户的数据，就先调用相应的工具，追问也不例外，即使之前的消息里提到过数字，因为数据可能已经变化，之前的回答也可能不完整。绝不猜测或编造数字。直接调用工具，不要预告你要去查数据。用用户最近一条消息的语言回复。回答要简短、带鼓励性。只讨论健身和这个应用，其他话题礼貌拒绝。不诊断疾病；涉及健康问题时，建议咨询专业人士。

**每周总结的系统提示词：**
> You are an encouraging fitness coach. Write a 3–4 sentence summary of the user's previous week (the Monday–Sunday range in the JSON stats provided). The user reads this during the following week, so always call it "last week" (never "this week"). Use only the numbers given; never invent data. End with one concrete, achievable suggestion for next week. Write in {language}. No medical advice. Plain text only — no Markdown, headings or lists.

中文翻译：你是一位善于鼓励的健身教练。根据提供的 JSON 统计数据（其中周一到周日的日期范围），写 3 到 4 句话总结用户上一周的情况。用户会在下一周看到这段话，所以始终称为"上周"，不要说"本周"。只使用给出的数字，绝不编造数据。最后给出一条具体、可以做到的下周建议。用 {language} 书写。不提供医疗建议。只输出纯文本，不要 Markdown、标题或列表。

## 10. 非功能需求

| 编号 | 类别 | 需求 |
|---|---|---|
| NF-1 | 安全 | `userId` 只从 JWT 获取（D-5）；至少有一个测试证明工具不会返回其他用户的数据 |
| NF-2 | 隐私 | 只把统计数据发给服务商（D-4）；不保存对话内容（D-7、§8） |
| NF-3 | 性能 | 第一个字在 3 秒内出现；每次请求的 Agent 循环最多调用 5 次模型 |
| NF-4 | 成本 | 每人每天 30 条对话；每次请求最多带 20 轮历史；每周总结有缓存 |
| NF-5 | 可替换性 | 业务代码只依赖 `IChatModel`（D-3） |
| NF-6 | 可测试性 | 单元测试使用假的 `IChatModel`；**CI 永远不调用真实的 AI API** |
| NF-7 | 部署 | API Key 作为 Secret 存在 Azure，通过环境变量 `DeepSeek__ApiKey` 引用；本地开发用 `dotnet user-secrets`（`DeepSeek:ApiKey`）；不需要修改 CI/CD workflow |
| NF-8 | 可观测性 | `AiUsageLog` 记录用量；`ILogger` 输出的错误可以在 Azure Log Stream 查看；不提供应用内的监测界面（D-8） |

## 11. 演示数据（P1）

| 编号 | 需求 |
|---|---|
| DM-1 | `software/scripts/seed-demo.sql` 为指定的 `UserId` 插入大约 4 周、类型多样的运动和签到记录 |
| DM-2 | 包含一个完整的"上周"，让每周总结有内容；当前连续签到 5 天，用来演示"还差 2 天拿到奖励皮肤" |
| DM-3 | 用法：在网站上注册一个用户名以 `demo` 开头的演示账号，在脚本开头设置 `demo_username`（和 `local_tz`），再到 Neon 的 SQL Editor 运行。应用里不提供生成数据的接口 |

实现说明（`software/scripts/seed-demo.sql`）：整个脚本是一个 `DO` 代码块；按用户名查找用户，用户名不以 `demo` 开头就拒绝执行，避免误改真实账号的数据。可以重复运行：每次都会替换该用户最近 35 天（不含今天）的运动和签到记录。日期都相对于用户本地的"今天"（`local_tz`）计算，所以任何时候运行都能用。本地已验证：两个安全检查都会生效；生成数据后，AI 回答上周 1870 千卡 / 5 次运动，连续签到 5 天、还差 2 次拿到奖励皮肤，本周 740 / 2000（37%）。

**写脚本时发现的原有 bug，已修复（2026-10-07）：** `User.LastCheckIn` 以 UTC 存储，但 `CheckInController` 原来拿它的 **UTC** 日期和用户**本地**的"昨天"比较。在新西兰（UTC+12/13），上午签到时的 UTC 日期是前一天，所以第二天上午再签到时，连续签到会被重置成 1（反过来，已经中断的连续签到也可能被错误地延续）。现在 `CheckInController` 和 T-2 都改为根据 `CheckIns.Date`（本身就是用户的本地日期）判断，`LastCheckIn` 不再参与任何日期判断。测试见 `Tests/Controllers/CheckInStreakTests.cs`（3 个测试，修复前两个方向的错误都复现了）。

## 12. 待确认事项

- [x] 对照 DeepSeek 当前的文档确认用法（2026-10-06），见 [DeepSeek API 说明](#deepseek-api-说明2026-10-06-已确认)
- [x] 选择 C# 客户端，见 D-12
- [x] 确定 `IChatModel` 的方法签名，见 `backend/Services/Ai/IChatModel.cs`、`ChatTypes.cs`
- [ ] 开发者：注册 DeepSeek 账号，充一点余额，**分别创建开发用和线上用的 API Key**，用 `dotnet user-secrets` 配置开发用的 Key（不要发到聊天里，也不要提交到代码库）

### DeepSeek API 说明（2026-10-06 已确认）

来源：[api-docs.deepseek.com](https://api-docs.deepseek.com/)。如果距离确认时间较久，使用前请重新核对。

| 项目 | 内容 |
|---|---|
| 地址 | `https://api.deepseek.com`（OpenAI 格式），认证方式 `Authorization: Bearer <key>` |
| 接口 | `POST /chat/completions` |
| 模型 | `deepseek-flash`（选用）、`deepseek-v4-pro`；都是 1M 上下文，支持工具调用和 JSON 输出 |
| 思考模式 | 默认开启；用 `"thinking": { "type": "disabled" }` 关闭（D-11） |
| 工具定义 | `tools: [{ type: "function", function: { name, description, parameters } }]`；`tool_choice`：`none` / `auto` / `required` / 指定名称 |
| 模型请求调用工具 | `choices[0].message.tool_calls[]`：`{ id, type: "function", function: { name, arguments } }`（`arguments` 是 JSON 字符串）；`finish_reason: "tool_calls"` |
| 回传工具结果 | `{ role: "tool", tool_call_id, content }`，放在带有 `tool_calls` 的那条 assistant 消息后面 |
| 流式输出 | `stream: true`；每个数据块包含 `choices[0].delta.content` / `delta.tool_calls[]`（增量数据，按 index 拼接）；设置 `stream_options: { include_usage: true }` 后，最后一个数据块会带上 `usage` |
| 用量 | `usage.prompt_tokens`、`completion_tokens`、`prompt_tokens_details.prompt_cache_hit_tokens` / `prompt_cache_miss_tokens` |
| `finish_reason` | `stop`、`length`、`tool_calls`、`content_filter`、`insufficient_system_resource`、`aborted` |
| 价格（`deepseek-flash`，每百万 token，非高峰 / 高峰） | 输入未命中缓存 $0.15 / $0.30，命中缓存 $0.003 / $0.006，输出 $0.60 / $1.20。高峰时段为周一到周五 UTC 01:00–04:00 和 06:00–10:00 |

### 真实 DeepSeek 冒烟测试（2026-10-07，本地）

`deepseek-flash`，关闭思考模式，测试账号本周有 2 条运动记录和 1 次签到。4 个问题，各跑 3 轮：

| 问题 | 预期 | 结果 |
|---|---|---|
| 这周我运动得怎么样？离周目标还差多少？ | 调用 T-1 + T-3，用中文回答 | 3/3 工具和数字都正确 |
| 那我还要签到几天才能拿到奖励皮肤？（追问） | 调用 T-2，借助历史理解上下文 | 修改提示词后 3/3（见下文） |
| How many workouts did I do last month? | 调用 T-1 `last_month`，用英文回答 | 3/3 |
| Can you write me a Python script…? | 不调用工具，礼貌拒绝 | 3/3 |

第一个字出现时间：0.6 到 2.1 秒（满足 NF-3）。发现并修复了两个问题：
1. **追问时编造数据。** 历史里只有文字，模型直接根据上下文回答追问，没有调用 T-2，编出了错误的连续签到天数。修复方法：提示词要求只要回答依赖用户数据，就必须先调用工具，追问也不例外。
2. **调用工具前的预告。** 模型有时会在调用工具前先说"我来查一下……"，和后面的回答连在一起。提示词现在不鼓励这样做；`ChatAssistantService` 还会在两轮之间插入空行作为兜底（有测试覆盖）。

回复会使用简单的 Markdown（`**加粗**`、`-` 列表）和 emoji，前端用 `react-markdown` 渲染（默认不渲染原始 HTML，所以模型的输出无法注入标签）。

**前端说明：** 聊天按钮放在 `bottom-42`，和 `RecordButton`（`bottom-6`）、`BackToTopButton`（`bottom-24`）在右侧同一列。SSE 由 `src/sse.ts` 解析，能处理被拆开的网络数据块。`react-markdown` 让 JS 包从约 322 kB 增加到约 448 kB（gzip 后约 95 → 134 kB）；以后可以考虑懒加载这个组件来优化。

## 任务清单

**P0**
- [x] DeepSeek 客户端 + `IChatModel` + `DeepSeekChatModel` + 依赖注入注册 + 配置（8 个测试，模拟 HTTP）
- [x] `AiUsageLog` 实体 + 迁移 + `AiUsageTracker` + `UsageTrackingChatModel` 装饰器（5 个测试，共用 `FakeChatModel`）
- [x] `ChatQuotaService` + `GET /api/ai/chat/quota`（3 个测试）
- [x] `ChatToolExecutor`，实现 T-1、T-2、T-3（17 个测试，其中 3 个是 NF-1 的用户隔离测试）
- [x] `POST /api/ai/chat`：Agent 循环（`ChatAssistantService`）+ SSE 流式输出（6 个测试；本地端到端验证了登录、参数校验、SSE 错误事件、失败不扣额度）
- [x] 后端测试：工具的用户隔离、工具输出、额度、循环次数上限（使用假的 `IChatModel`），后端共 70 个测试
- [x] 在本地验证真实的 DeepSeek 调用，见 [冒烟测试](#真实-deepseek-冒烟测试2026-10-07本地)
- [x] 前端 `ChatWidget`：对话面板、推荐问题、流式显示、工具运行提示、剩余次数、错误处理（11 个测试：SSE 解析器 4 个 + 组件 7 个）
- [x] 在浏览器里手动检查界面（桌面和手机宽度），包括 CA-1 的按钮遮挡问题
- [x] Azure 配置 Secret `DeepSeek__ApiKey`；push；CI/CD 通过；在线上做冒烟测试

**P1**
- [x] `seed-demo.sql`（本地已验证）；线上的演示账号还需要开发者自己注册并运行脚本
- [x] `WeeklySummary` 实体 + 迁移 + 服务 + 接口 + 无数据时的固定文案 + 测试（后端 14 个测试）
- [x] Dashboard 的 `WeeklySummaryCard`（前端 3 个测试）

**P2**
- [x] T-4 `get_rank`、T-5 `get_points_summary`（3 个测试）
- [x] 话题按钮 / 对话模式（§5.3）：`ChatModes`、`POST /api/ai/chat` 的 `mode` 参数、`ChatWidget` 里的按钮（后端 4 个测试，组件测试已重写）

## 13. 变更记录

| 日期 | 改动 |
|---|---|
| 2026-10-06 | 根据需求讨论创建初版 |
| 2026-10-06 | 确认 DeepSeek API；新增 D-11（关闭思考模式）、D-12（HttpClient 封装）和 API 说明；开发和线上使用不同的 Key |
| 2026-10-06 | 新增中文版 |
| 2026-10-06 | 完成 P0 第 1 步：`IChatModel`、`DeepSeekChatModel`、`DeepSeekOptions`、依赖注入和配置 |
| 2026-10-06 | P0 第 2 步：`AiUsageLog` + 迁移；装饰器从 `LoggingChatModel` 改名为 `UsageTrackingChatModel`，并配合按请求创建的 `AiUsageTracker`，因为装饰器看到的是每次模型调用，而 §8 要求每次请求只记一行 |
| 2026-10-06 | P0 第 3 步：`ChatQuotaService`（按 UTC 日计算，使用 `TimeProvider` 方便测试）+ `AiController` 和 `GET /api/ai/chat/quota`；把"先检查、后写入"的竞态问题记为已知限制 |
| 2026-10-06 | P0 第 4 步：`ChatToolExecutor`（T-1 到 T-3）；返回字段扩展（`range`、`rewardSkinStreak`、`checkInsUntilRewardSkin`、`remainingCalories`）；处理过期的连续签到天数；新增常量 `CheckInController.RewardSkinStreak` |
| 2026-10-06 | P0 第 5 步：`ChatAssistantService`（Agent 循环，最多调用 5 次模型，历史最多 20 轮并过滤掉 system 角色）+ `POST /api/ai/chat` SSE 接口；P0 后端完成 |
| 2026-10-07 | 真实 DeepSeek 冒烟测试；对话提示词改为：只要回答依赖用户数据就必须调用工具（修复追问时编造数据的问题），并且不鼓励调用工具前的预告；Agent 循环在两轮之间插入空行 |
| 2026-10-07 | P0 第 6 步：`ChatWidget` + `streamChat`（fetch + `ReadableStream`）+ 增量 SSE 解析器；用 `react-markdown` 渲染回复 |
| 2026-10-07 | P0 已部署（`45fce57`）：CI 和 Deploy 通过，在线上界面验证了对话功能（工具回答、拒绝无关问题、额度） |
| 2026-10-07 | P1 演示数据：`software/scripts/seed-demo.sql`（按用户名查找，有 `demo` 前缀检查，按本地时区计算日期，可重复运行）；记录了原有的"UTC 和本地日期比较"导致连续签到中断的问题 |
| 2026-10-07 | 修复原有的连续签到 bug：`CheckInController` 和 T-2 改为根据 `CheckIns.Date`（本地日期）判断是否连续，不再用 `LastCheckIn` 的 UTC 日期；演示脚本不再需要"固定在 UTC 中午"的特殊处理 |
| 2026-10-07 | P1 每周总结：`WeeklySummary` 表 + 迁移、`WeeklySummaryService`、`GET /api/ai/weekly-summary`、Dashboard 卡片；语言白名单 `en`/`zh`；用真实模型验证后，提示词明确要求称为"上周" |
| 2026-10-07 | P2：T-4 `get_rank`、T-5 `get_points_summary`；话题按钮作为对话模式（§5.3、D-13），取代原来的推荐问题 |
