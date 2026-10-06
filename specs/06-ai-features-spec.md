# AI Features Spec — Chat Assistant & Weekly Summary

**Status:** In progress — P0 backend + frontend done; manual UI check and deployment remaining
**Created:** 2026-10-06
**Deadline:** 1 day (P0 must ship; P1/P2 as time allows)

This is the single source of truth for the AI extension to HealthTrack. All AI-related code, prompts, tests, and deployment changes should trace back to an ID in this document. When a decision changes, update this file first (see [Changelog](#13-changelog)).

---

## 1. Goals

The primary purpose is a **portfolio piece for interviews**. The features should demonstrate, and be easy to explain:

1. **Tool calling / agent loop** — the model decides which tool to call, the backend executes it, the loop continues until an answer is produced
2. **Streaming output** — responses render token by token
3. **Per-user data isolation** — the model can never read another user's data
4. **Rate limiting and usage tracking** — cost is bounded and observable
5. **Swappable model layer** — business code is not coupled to one AI provider

Non-goal: maximizing the number of AI features.

## 2. Scope and Priority

| Priority | Item | IDs |
|---|---|---|
| **P0 — must ship** | Chat assistant with streaming | CA-* |
| | 3 read-only tools | T-1, T-2, T-3 |
| | Usage log + 30 messages/user/day limit | NF-4 |
| | Deployed to Azure via existing CI/CD | NF-7 |
| **P1 — should ship** | Demo data script | DM-* |
| | AI weekly summary | WS-* |
| **P2 — if time allows** | 2 more tools | T-4, T-5 |
| **Out of scope** | 👍/👎 feedback, Langfuse tracing, persisted chat history, natural-language workout logging, write-capable tools, in-app admin/monitoring UI | — |

## 3. Key Decisions

| # | Decision | Rationale |
|---|---|---|
| D-1 | **Dropped natural-language workout logging** | The existing form is one dropdown + one number; typing a sentence is slower. AI should go where manual work is genuinely tedious. |
| D-2 | **Provider: DeepSeek** (OpenAI-compatible API) | Low cost; supports tool calling and streaming. |
| D-3 | **All model calls go through an `IChatModel` interface** | Provider can be swapped (e.g. to Claude/OpenAI) by adding one implementation; also makes the AI mockable in tests. |
| D-4 | **Only aggregates leave the system** | DeepSeek servers are in China. Tool results and prompts contain stats and workout types only — never email, username, avatar, or password hash. |
| D-5 | **`userId` is never a tool parameter** | Every tool reads the user from the JWT. A prompt like "show me user 1's data" has no way to reach another user. |
| D-6 | **Numbers are computed in C#, words by the model** | Models are unreliable at arithmetic; all totals/percentages are calculated before the model sees them. |
| D-7 | **Chat history lives in the frontend only** | Cleared on refresh. Avoids a new table and keeps conversation content out of the database. Client-sent history is safe to trust because tools are scoped to the JWT user regardless. |
| D-8 | **Monitoring is not exposed in-app** | The app has no admin role, so any monitoring endpoint would be visible to every user. Usage is queried in the Neon console; logs in Azure Log Stream; spend in the DeepSeek dashboard. |
| D-9 | **Weekly summary covers the last complete week** | Complete data, generated once per week, simplest caching, lowest cost. |
| D-10 | **Language** — chat follows the user's input; weekly summary follows the browser language | The summary has no user input to follow, so `navigator.language` is passed from the frontend. |
| D-11 | **Model: `deepseek-flash` with thinking disabled** (`"thinking": { "type": "disabled" }`) | Thinking is on by default. In thinking mode, any request that carries `tools` must replay every earlier `reasoning_content`, across user turns too, or the API returns 400. That conflicts with D-7 (the frontend keeps only text), and the extra tokens add latency on an Azure-Australia → DeepSeek round trip. Looking up and summarizing a few numbers doesn't need deep reasoning. |
| D-12 | **Client: a thin typed `HttpClient` wrapper over DeepSeek's OpenAI-format `/chat/completions`**, not a third-party SDK | We need DeepSeek-specific fields (`thinking`) that the OpenAI .NET SDK doesn't model; a ~150-line client gives full control over the request body, SSE parsing, tool-call delta accumulation, and `usage`. It sits behind `IChatModel` (D-3), so swapping to an SDK later is local. |

## 4. Architecture

```
Frontend (React)
  ├─ ChatWidget ──── POST /api/ai/chat (SSE) ──┐
  └─ WeeklySummaryCard ── GET /api/ai/weekly-summary ──┐
                                                │     │
Backend (ASP.NET Core)                          ▼     ▼
  AiController ([Authorize], UserId from JWT)
    ├─ ChatQuotaService ─────── AiUsageLog (count today's chat rows)
    ├─ ChatAssistantService ─── agent loop (model ↔ tools), HTTP-agnostic
    ├─ ChatToolExecutor ─────── EF Core queries, always filtered by UserId
    ├─ WeeklySummaryService ─── WeeklySummary cache table
    ├─ AiUsageTracker (scoped) ── writes one AiUsageLog row per request
    └─ IChatModel
         └─ UsageTrackingChatModel (decorator: adds each call's tokens to the tracker)
              └─ DeepSeekChatModel ──── HTTPS ──── DeepSeek API
```

- `UsageTrackingChatModel` wraps the real implementation and feeds every model call's token usage into the request-scoped `AiUsageTracker`; the tool executor adds tool names to the same tracker; the endpoint calls `SaveAsync` once. This keeps tracking out of business code while still producing **one row per user request** (§8), even though the decorator itself sees individual model calls.
- The exact `IChatModel` signature (non-streaming call + `IAsyncEnumerable` streaming events, with tool-call support) is finalized during implementation. The DeepSeek API has been verified (see [§12](#12-open-items)).

## 5. Functional Requirements

### 5.1 Chat Assistant (P0)

**User story:** As a user, I want to ask about my own activity in plain language ("how did I do this month?", "how many days until the reward skin?") instead of navigating between pages.

| ID | Requirement | Acceptance criteria |
|---|---|---|
| CA-1 | A chat button is visible bottom-right on every logged-in page | Opens/closes a chat panel; does not overlap `BackToTopButton` on mobile |
| CA-2 | The panel shows 3–4 suggested questions when empty | Clicking one sends it as a message |
| CA-3 | The assistant answers using the tools in [§6](#6-tools) | Asking "how many workouts this week?" triggers T-1 and the answer matches the DB |
| CA-4 | Responses stream | First text appears ≤ 3 s after sending (NF-3); a "looking up your data…" indicator shows while a tool runs |
| CA-5 | Replies in the language of the user's message | A Chinese question gets a Chinese answer; English gets English |
| CA-6 | Stays on topic | Off-topic requests are politely declined; no medical diagnosis; health questions include a "consult a professional" note |
| CA-7 | History kept in frontend state only | Refresh clears it; each request sends at most the last 20 turns |
| CA-8 | Shows remaining daily quota | e.g. "25 / 30 left today"; input disabled at 0 with an explanation |
| CA-9 | Graceful failure | On API error/timeout: error message + retry button; the rest of the app is unaffected |

### 5.2 Weekly Summary (P1)

**User story:** As a user, when I open the Dashboard I want a short summary of last week with one concrete suggestion, so I don't have to interpret the charts myself.

| ID | Requirement | Acceptance criteria |
|---|---|---|
| WS-1 | Dashboard card shows a 3–4 sentence summary of the last complete week (Mon–Sun) | Mentions active days, total calories, goal completion; ends with one suggestion |
| WS-2 | Stats are computed in C#; the model only writes prose | Every number in the summary matches the DB (D-6) |
| WS-3 | Language follows the browser | Frontend sends `lang` (from `navigator.language`) |
| WS-4 | Cached per user per week | Second load of the same week makes no model call |
| WS-5 | No data → no model call | If last week has no workouts (incl. new users), a fixed encouraging message is returned with `source: "fallback"` |
| WS-6 | Graceful failure | On model error, the card shows "Summary unavailable right now"; the Dashboard still loads |

## 6. Tools

All tools are **read-only**, take **no `userId`** (D-5), and return **aggregates only** (D-4). The backend resolves "today" with the same `ResolveToday` clamp used by existing controllers.

| ID | Priority | Name | Input | Returns |
|---|---|---|---|---|
| T-1 | P0 | `get_workout_summary` | `range`: `"this_week"` \| `"last_week"` \| `"this_month"` \| `"last_month"` | `{ range, from, to, workoutCount, activeDays, totalCalories, byType: { Running: { count, calories }, … } }` |
| T-2 | P0 | `get_checkin_status` | — | `{ streak, checkedInToday, rewardSkinStreak, ownsRewardSkin, checkInsUntilRewardSkin }` |
| T-3 | P0 | `get_weekly_goal_progress` | — | `{ weekStart, goal, caloriesSoFar, remainingCalories, percent, daysLeftInWeek }` |
| T-4 | P2 | `get_rank` | — | `{ rank, totalUsers, points }` (no other users' names) |
| T-5 | P2 | `get_points_summary` | — | `{ balance, earnedLast7Days, recentSources: [{ reason, amount, date }] }` |

Unknown tool names or invalid arguments return `{ "error": "..." }` as the tool result rather than throwing, so the model can recover.

Implementation notes (`backend/Services/Ai/ChatToolExecutor.cs`):
- `ExecuteAsync(userId, today, call)` — the controller passes `userId` from the JWT; the model's arguments are only ever read for `range`, so an injected `"userId": 2` is ignored (tested).
- Weeks start on **Monday**, matching the frontend's `WeeklyGoalDonut`.
- **T-2 streak:** `User.Streak` is only recalculated on the next check-in, so after missed days it still holds the old run. The tool reports `streak = 0` unless the last check-in was today or yesterday. The reward threshold comes from `CheckInController.RewardSkinStreak` (extracted from a hard-coded `7`) so the tool and the check-in logic can't drift.

## 7. API

All endpoints are under `[Authorize]`.

### `POST /api/ai/chat` — Server-Sent Events

Request:
```json
{ "history": [{ "role": "user", "text": "..." }, { "role": "assistant", "text": "..." }], "message": "How did I do this week?" }
```

Response: `Content-Type: text/event-stream`, one JSON object per `data:` line:

| Event | Meaning |
|---|---|
| `{ "type": "tool", "name": "get_workout_summary" }` | A tool started (drives the "looking up…" indicator) |
| `{ "type": "text", "text": "This week you…" }` | Text delta to append |
| `{ "type": "done", "remaining": 24 }` | Finished; updated quota |
| `{ "type": "error", "message": "..." }` | Failed; stream ends |

Before streaming starts: `429` with `{ "message": "..." }` if the daily quota is used up; `400` if `message` is empty or > 1000 characters.

Errors after the stream has started (model failure, timeout) arrive as an `error` event on a `200` response, because headers are already sent. Pass `?localDate=YYYY-MM-DD` like other endpoints; the server clamps it with `ResolveToday`. History turns with any role other than `assistant` are treated as `user` — a client can't inject a system message.

The frontend must read this with `fetch` + `ReadableStream` (not `EventSource`, which cannot send POST bodies or the `Authorization` header).

### `GET /api/ai/chat/quota`

```json
{ "used": 6, "limit": 30, "remaining": 24 }
```

### `GET /api/ai/weekly-summary?lang=zh-CN&localDate=2026-10-06` (P1)

```json
{ "weekStart": "2026-09-28", "weekEnd": "2026-10-04", "summary": "...", "source": "ai" }
```
`source` is `"ai"`, `"cache"`, or `"fallback"`.

## 8. Data Model

Both tables are added via EF Core migrations and applied automatically on startup (`Database.Migrate()`).

### `AiUsageLog` (P0) — one row per user request (not per model call)

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | |
| `UserId` | int | Indexed with `CreatedAt` |
| `Feature` | string | `"chat"` \| `"weekly_summary"` |
| `InputTokens` / `OutputTokens` | int | Summed across every model call in the agent loop |
| `LatencyMs` | int | Whole request |
| `ToolsCalled` | string? | Comma-separated, e.g. `"get_workout_summary,get_checkin_status"` |
| `Success` | bool | |
| `Error` | string? | Short message only |
| `CreatedAt` | DateTime (UTC) | |

**No message content is stored.** Quota = count of `Feature = "chat" AND Success = true` rows for the user since 00:00 UTC; failed requests don't count against the user.

**Known limitation:** the quota is checked before a request starts but the row is written when it ends, so two messages sent at the same moment with 1 left can both pass. Acceptable for a 30/day soft limit; a strict limit would need a reservation row or a DB-level counter.

### `WeeklySummary` (P1)

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | |
| `UserId` | int | Unique index on `(UserId, WeekStart, Language)` |
| `WeekStart` | DateOnly | Monday |
| `Language` | string | Normalized, e.g. `"zh"` / `"en"` |
| `Content` | string | |
| `CreatedAt` | DateTime (UTC) | |

## 9. Prompts (drafts)

**Chat system prompt:**
> You are the HealthTrack assistant. You help the signed-in user understand their own workouts, check-ins, streaks, and goals in this app. Whenever an answer depends on the user's data, call the relevant tool first — even for follow-up questions and even if earlier messages mention numbers, because data can change and earlier replies may be incomplete. Never guess or invent numbers. Call tools directly without announcing that you are about to look something up. Reply in the same language as the user's latest message. Keep answers short and encouraging. Only discuss fitness and this app; politely decline anything else. Do not diagnose medical conditions; for health concerns, suggest consulting a professional.
>
> Today is {Weekday, yyyy-MM-dd} in the user's time zone.

**Weekly summary system prompt:**
> You are an encouraging fitness coach. Write a 3–4 sentence summary of the user's week from the JSON stats provided. Use only the numbers given; never invent data. End with one concrete, achievable suggestion for next week. Write in {language}. No medical advice.

## 10. Non-Functional Requirements

| ID | Category | Requirement |
|---|---|---|
| NF-1 | Security | `userId` only from JWT (D-5); at least one test proves tools never return another user's data |
| NF-2 | Privacy | Only aggregates sent to the provider (D-4); no message content persisted (D-7, §8) |
| NF-3 | Performance | First streamed text ≤ 3 s; agent loop capped at 5 model calls per request |
| NF-4 | Cost | 30 chat messages/user/day; ≤ 20 turns of history per request; weekly summaries cached |
| NF-5 | Swappability | Business code depends only on `IChatModel` (D-3) |
| NF-6 | Testability | Unit tests use a fake `IChatModel`; **CI never calls a real AI API** |
| NF-7 | Deployment | API key in Azure as a secret referenced by env var `DeepSeek__ApiKey`; local dev uses `dotnet user-secrets` (`DeepSeek:ApiKey`); no CI/CD workflow changes |
| NF-8 | Observability | `AiUsageLog` + `ILogger` errors visible in Azure Log Stream; no in-app monitoring UI (D-8) |

## 11. Demo Data (P1)

| ID | Requirement |
|---|---|
| DM-1 | `software/scripts/seed-demo.sql` inserts ~4 weeks of varied workouts and check-ins for a given `UserId` |
| DM-2 | Includes a full "last week" so the weekly summary has content, and a current streak of 5 so "2 days until the reward skin" is demoable |
| DM-3 | Usage: register a demo account through the UI, look up its `Id`, run the script in the Neon SQL console. No seed endpoint in the app |

## 12. Open Items

- [x] Verify against current DeepSeek docs (2026-10-06) — see [DeepSeek API notes](#deepseek-api-notes-verified-2026-10-06)
- [x] Choose the C# client → D-12
- [x] Finalize the `IChatModel` signature → `backend/Services/Ai/IChatModel.cs`, `ChatTypes.cs`
- [ ] Developer: create a DeepSeek account, top up a small balance, create **separate dev and prod API keys**, set the dev key with `dotnet user-secrets` (do not paste it into chat or commit it)

### DeepSeek API notes (verified 2026-10-06)

Source: [api-docs.deepseek.com](https://api-docs.deepseek.com/) — re-check before relying on these if much time has passed.

| Item | Value |
|---|---|
| Base URL | `https://api.deepseek.com` (OpenAI format), auth `Authorization: Bearer <key>` |
| Endpoint | `POST /chat/completions` |
| Models | `deepseek-flash` (chosen), `deepseek-v4-pro`; both 1M context, tool calls, JSON output |
| Thinking | On by default; disable with `"thinking": { "type": "disabled" }` (D-11) |
| Tools | `tools: [{ type: "function", function: { name, description, parameters } }]`; `tool_choice`: `none` / `auto` / `required` / named |
| Tool calls out | `choices[0].message.tool_calls[]`: `{ id, type: "function", function: { name, arguments } }` (`arguments` is a JSON string); `finish_reason: "tool_calls"` |
| Tool results in | `{ role: "tool", tool_call_id, content }`, after the assistant message that carried the `tool_calls` |
| Streaming | `stream: true`; chunks carry `choices[0].delta.content` / `delta.tool_calls[]` (incremental, accumulate by index); `stream_options: { include_usage: true }` puts `usage` on the final chunk |
| Usage | `usage.prompt_tokens`, `completion_tokens`, `prompt_tokens_details.prompt_cache_hit_tokens` / `prompt_cache_miss_tokens` |
| `finish_reason` | `stop`, `length`, `tool_calls`, `content_filter`, `insufficient_system_resource`, `aborted` |
| Price (`deepseek-flash`, per 1M tokens, off-peak / peak) | input cache miss $0.15 / $0.30, cache hit $0.003 / $0.006, output $0.60 / $1.20. Peak = 01:00–04:00 and 06:00–10:00 UTC, Mon–Fri |

### Smoke test against real DeepSeek (2026-10-07, local)

`deepseek-flash`, thinking disabled, test user with 2 workouts + 1 check-in this week. Four questions × 3 runs:

| Question | Expected | Result |
|---|---|---|
| 这周我运动得怎么样？离周目标还差多少？ | T-1 + T-3, Chinese | 3/3 correct tools and numbers |
| 那我还要签到几天才能拿到奖励皮肤？ (follow-up) | T-2, uses history for context | 3/3 after prompt fix (see below) |
| How many workouts did I do last month? | T-1 `last_month`, English | 3/3 |
| Can you write me a Python script…? | no tool, decline | 3/3 |

First streamed text: 0.6–2.1 s (meets NF-3). Two issues found and fixed in the system prompt / loop:
1. **Hallucinated follow-up.** With text-only history the model answered a follow-up from context instead of calling T-2, inventing a wrong streak. Fixed by requiring a tool call whenever the answer depends on user data, including follow-ups.
2. **Pre-tool preamble.** The model sometimes says "I'll check…" before a tool call, which ran straight into the answer. The prompt now discourages it, and `ChatAssistantService` inserts a paragraph break between rounds as a fallback (tested).

Replies use light Markdown (`**bold**`, `-` lists) and emoji — the frontend renders them with `react-markdown` (raw HTML disabled by default, so model output can't inject markup).

**Frontend notes:** the chat button sits at `bottom-42` in the same right-hand column as `RecordButton` (`bottom-6`) and `BackToTopButton` (`bottom-24`). SSE is parsed by `src/sse.ts`, which buffers partial network chunks. `react-markdown` grows the JS bundle from ~322 kB to ~448 kB (gzip ~95 → ~134 kB); lazy-loading the widget is a possible later optimization.

## Task Checklist

**P0**
- [x] DeepSeek client + `IChatModel` + `DeepSeekChatModel` + DI registration + config (8 tests, stubbed HTTP)
- [x] `AiUsageLog` entity + migration + `AiUsageTracker` + `UsageTrackingChatModel` decorator (5 tests, shared `FakeChatModel`)
- [x] `ChatQuotaService` + `GET /api/ai/chat/quota` (3 tests)
- [x] `ChatToolExecutor` with T-1, T-2, T-3 (17 tests incl. 3 isolation tests for NF-1)
- [x] `POST /api/ai/chat`: agent loop (`ChatAssistantService`) + SSE streaming (6 tests; local end-to-end run verified auth, validation, SSE error path, quota not charged on failure)
- [x] Backend tests: tool user isolation, tool outputs, quota, loop cap (fake `IChatModel`) — 70 backend tests total
- [x] Verify a real DeepSeek round trip locally — see [smoke test](#smoke-test-against-real-deepseek-2026-10-07-local)
- [x] Frontend `ChatWidget`: panel, suggested questions, streaming render, tool indicator, quota, errors (11 tests: 4 SSE parser + 7 widget)
- [x] Manual UI check in a browser (desktop + mobile width), incl. CA-1 overlap
- [ ] Azure secret `DeepSeek__ApiKey`; push; CI/CD green; smoke test on production

**P1**
- [ ] `seed-demo.sql` + demo account
- [ ] `WeeklySummary` entity + migration + service + endpoint + fallback + tests
- [ ] Dashboard `WeeklySummaryCard`

**P2**
- [ ] T-4 `get_rank`, T-5 `get_points_summary`

## 13. Changelog

| Date | Change |
|---|---|
| 2026-10-06 | Initial spec from requirements discussion |
| 2026-10-06 | Verified DeepSeek API; added D-11 (thinking disabled), D-12 (HttpClient wrapper), API notes; separate dev/prod keys |
| 2026-10-06 | P0 step 1 implemented: `IChatModel`, `DeepSeekChatModel`, `DeepSeekOptions`, DI + config |
| 2026-10-06 | Added Chinese translation ([06-ai-features-spec.zh-CN.md](./06-ai-features-spec.zh-CN.md)) |
| 2026-10-06 | P0 step 2: `AiUsageLog` + migration; decorator renamed `LoggingChatModel` → `UsageTrackingChatModel` and paired with a scoped `AiUsageTracker`, because the decorator sees per-call usage but §8 requires one row per request |
| 2026-10-06 | P0 step 3: `ChatQuotaService` (UTC day, uses `TimeProvider` for testability) + `AiController` with `GET /api/ai/chat/quota`; documented the check-then-write race as a known limitation |
| 2026-10-06 | P0 step 4: `ChatToolExecutor` (T-1..T-3); return shapes extended (`range`, `rewardSkinStreak`, `checkInsUntilRewardSkin`, `remainingCalories`); stale-streak handling; `CheckInController.RewardSkinStreak` constant |
| 2026-10-06 | P0 step 5: `ChatAssistantService` (agent loop, max 5 model calls, history capped to 20 turns and stripped of system roles) + `POST /api/ai/chat` SSE endpoint; backend P0 complete |
| 2026-10-07 | Real DeepSeek smoke test; chat prompt now requires a tool call for any data-dependent answer (fixes a hallucinated follow-up) and discourages pre-tool preambles; loop inserts a paragraph break between rounds |
| 2026-10-07 | P0 step 6: `ChatWidget` + `streamChat` (fetch + `ReadableStream`) + incremental SSE parser; `react-markdown` for replies |
