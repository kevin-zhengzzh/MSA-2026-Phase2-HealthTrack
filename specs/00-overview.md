# /specs — Planning, Design & AI-Assisted Development Evidence

This folder documents how **HealthTrack** was actually planned and built, for the MSA 2026 Phase 2 (Software Stream) assessment. It is required repository content per the assessment brief: *"Contains evidence of planning, design, and AI-assisted development... Must include prompts used during development (not just final code)."*

## How AI was used (brief summary)

Two different tools were used at two different stages: **Claude (web chat)** for early architecture and scope planning before any code existed (2026-06-23 → 07-06), and **Claude Code** (VS Code agent, Sonnet 5) for the actual implementation, debugging, testing, Dockerization, and deployment (2026-07-21 → 08-07). Usage was consistently conversational and iterative — describe a change, review the result, correct it — rather than one-shot prompting; several entries in the logs below specifically show the developer proposing their own candidate solution and asking for an evaluation, rejecting an AI-proposed patch in favor of a structural fix, or overriding an unrequested AI action. See `03-ai-prompt-log-web.md` and `04-ai-prompt-log-vscode.md` for the detailed, prompt-level record this summary is drawn from.

## Files in this folder

| File | Covers |
|---|---|
| [01-planning-early-design.md](./01-planning-early-design.md) | The developer's own early idea/bug backlog (not AI-generated) — what was planned, what shipped, what was cut |
| [02-planning-refinement-notes.md](./02-planning-refinement-notes.md) | The developer's later cleanup/polish punch-list, mapped to the commits it produced |
| [03-ai-prompt-log-web.md](./03-ai-prompt-log-web.md) | Early architecture & scope decisions from Claude web-chat planning, pre-code |
| [04-ai-prompt-log-vscode.md](./04-ai-prompt-log-vscode.md) | Curated, real, decision-point prompts from Claude Code sessions across the whole build |
| [05-design-decisions.md](./05-design-decisions.md) | Key design decisions synthesized from the logs above, with two state diagrams |
| [06-ai-features-spec.md](./06-ai-features-spec.md) | Spec for the post-submission AI extension (chat assistant with tool calling, weekly summary) — the source of truth for all AI-related work ([中文版](./06-ai-features-spec.zh-CN.md)) |

## Timeline

- **2026-06-23** — first commit; early architecture planning via Claude web chat (`03-ai-prompt-log-web.md`).
- **2026-07-06** — basic check-in and store redemption functions.
- **2026-07-21** — original scope (dual backend, full food logging, real-time leaderboard) simplified down to the shipped HealthTrack concept: check-in + streak + workout records + a cosmetic skin shop.
- **2026-07-21 → 2026-08-07** — the bulk of feature development, debugging, testing, Dockerization, deployment, and documentation, captured prompt-by-prompt in `04-ai-prompt-log-vscode.md`.

## A note on honesty in this folder

Where the original prompt text genuinely isn't available (the four earliest web-chat planning prompts — see `03-ai-prompt-log-web.md`), that's stated explicitly rather than reconstructed. Everything else quoted in `01`–`04` is real, sourced either from the developer's own saved notes/prompts or, for the Claude Code period, extracted directly from local session transcripts — lightly copyedited for spelling and grammar for clarity and rigor, with meaning and intent unchanged from the original.
