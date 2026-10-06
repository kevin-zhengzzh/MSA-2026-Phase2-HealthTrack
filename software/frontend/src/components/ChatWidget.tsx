import { useEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import { getChatQuota, streamChat } from '../api'
import type { ChatQuota, ChatTurn } from '../types'

// AI chat assistant (specs/06-ai-features-spec.md §5.1). A floating button in
// the same right-hand column as RecordButton (bottom-6) and BackToTopButton
// (bottom-24), one step above them so it never overlaps either (CA-1).

const MAX_MESSAGE_LENGTH = 1000 // matches ChatAssistantService.MaxMessageLength
const MAX_HISTORY_TURNS = 20 // CA-7; the server caps it too

// Demo-friendly starters (CA-2), one per tool/range so each path gets exercised
const SUGGESTIONS = [
  'How did I do this week?',
  'How close am I to my weekly goal?',
  'How many check-ins until the reward skin?',
  'How many workouts did I do last month?',
]

const TOOL_LABELS: Record<string, string> = {
  get_workout_summary: 'Checking your workouts…',
  get_checkin_status: 'Checking your check-in streak…',
  get_weekly_goal_progress: 'Checking your weekly goal…',
}

interface Message {
  role: 'user' | 'assistant'
  text: string
  // Failed exchanges stay visible but are left out of the history sent back
  failed?: boolean
}

function ChatIcon({ className }: { className?: string }) {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className={className}>
      <path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12Z" />
      <path d="M8.5 12h.01M12 12h.01M15.5 12h.01" />
    </svg>
  )
}

// After a failure: keep the user's message (marked failed so it's not sent as
// history) and drop the assistant bubble unless some text had already streamed.
function markLastExchangeFailed(messages: Message[]): Message[] {
  const next = messages.slice()
  const last = next[next.length - 1]
  if (last?.role === 'assistant') {
    if (last.text) next[next.length - 1] = { ...last, failed: true }
    else next.pop()
  }
  const userIndex = next.map((m) => m.role).lastIndexOf('user')
  if (userIndex !== -1) next[userIndex] = { ...next[userIndex], failed: true }
  return next
}

export default function ChatWidget() {
  const [open, setOpen] = useState(false)
  const [messages, setMessages] = useState<Message[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [toolStatus, setToolStatus] = useState<string | null>(null)
  const [quota, setQuota] = useState<ChatQuota | null>(null)
  const [error, setError] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    if (!open) return
    getChatQuota().then(setQuota).catch(() => {})
    inputRef.current?.focus()
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  // Stop an in-flight stream if the widget unmounts (e.g. on sign-out)
  useEffect(() => () => abortRef.current?.abort(), [])

  // Keep the newest text in view as it streams in
  useEffect(() => {
    const list = listRef.current
    if (list) list.scrollTop = list.scrollHeight
  }, [messages, toolStatus, error])

  const outOfQuota = quota?.remaining === 0

  async function send(text: string, base: Message[]) {
    const message = text.trim()
    if (!message || busy || outOfQuota) return

    const history: ChatTurn[] = base
      .filter((m) => !m.failed && m.text)
      .slice(-MAX_HISTORY_TURNS)
      .map(({ role, text }) => ({ role, text }))

    setMessages([...base, { role: 'user', text: message }, { role: 'assistant', text: '' }])
    setInput('')
    setError(null)
    setBusy(true)
    setToolStatus(null)

    const controller = new AbortController()
    abortRef.current = controller
    const appendToAnswer = (chunk: string) =>
      setMessages((prev) => {
        const next = prev.slice()
        const last = next[next.length - 1]
        next[next.length - 1] = { ...last, text: last.text + chunk }
        return next
      })

    try {
      const outcome: { error: string | null } = { error: null }
      await streamChat(history, message, (event) => {
        switch (event.type) {
          case 'text':
            setToolStatus(null)
            appendToAnswer(event.text)
            break
          case 'tool':
            setToolStatus(TOOL_LABELS[event.name] ?? 'Looking up your data…')
            break
          case 'done':
            setQuota((q) => (q ? { ...q, used: q.limit - event.remaining, remaining: event.remaining } : q))
            break
          case 'error':
            outcome.error = event.message
            break
        }
      }, controller.signal)
      if (outcome.error) throw new Error(outcome.error)
    } catch (err) {
      if (controller.signal.aborted) return
      setMessages(markLastExchangeFailed)
      setError(err instanceof Error ? err.message : 'Something went wrong. Please try again.')
    } finally {
      if (abortRef.current === controller) {
        setBusy(false)
        setToolStatus(null)
      }
    }
  }

  // Re-send the last user message, discarding the failed exchange (CA-9)
  function retry() {
    const index = messages.map((m) => m.role).lastIndexOf('user')
    if (index !== -1) send(messages[index].text, messages.slice(0, index))
  }

  const last = messages[messages.length - 1]
  const waitingForFirstText = busy && last?.role === 'assistant' && !last.text

  if (!open) {
    return (
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label="Open AI assistant"
        title="Ask the AI assistant"
        className="fixed bottom-42 right-4 sm:right-16 w-12 h-12 flex items-center justify-center text-white rounded-full shadow-lg transition-transform active:scale-95 cursor-pointer z-30"
        style={{ backgroundColor: 'var(--primary)' }}
        onMouseEnter={(e) => (e.currentTarget.style.backgroundColor = 'var(--primary-hover)')}
        onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'var(--primary)')}
      >
        <ChatIcon className="w-6 h-6" />
      </button>
    )
  }

  return (
    <section
      role="dialog"
      aria-label="HealthTrack assistant"
      className="fixed z-40 inset-x-3 bottom-3 top-17 sm:top-auto sm:inset-x-auto sm:right-16 sm:bottom-6 sm:w-96 sm:h-[34rem] flex flex-col rounded-2xl shadow-xl border border-[var(--border)] bg-[var(--bg-surface)] overflow-hidden"
    >
      <header className="flex items-center justify-between gap-3 px-4 py-3 border-b border-[var(--border)]">
        <div className="min-w-0">
          <h2 className="font-semibold text-[var(--text-primary)] text-sm">HealthTrack Assistant</h2>
          {quota && (
            <p className="text-xs text-[var(--text-secondary)]">
              {quota.remaining} / {quota.limit} messages left today
            </p>
          )}
        </div>
        <button
          type="button"
          onClick={() => setOpen(false)}
          aria-label="Close AI assistant"
          className="p-1.5 rounded-full text-[var(--text-secondary)] hover:bg-[var(--bg-inset)] cursor-pointer"
        >
          <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className="w-5 h-5">
            <path d="M6 6l12 12M18 6 6 18" />
          </svg>
        </button>
      </header>

      <div ref={listRef} className="flex-1 overflow-y-auto px-4 py-3 space-y-3" aria-live="polite">
        {messages.length === 0 && (
          <div className="space-y-3">
            <p className="text-sm text-[var(--text-secondary)]">
              Ask me about your workouts, check-in streak, or weekly goal.
            </p>
            <div className="flex flex-col gap-2">
              {SUGGESTIONS.map((s) => (
                <button
                  key={s}
                  type="button"
                  onClick={() => send(s, [])}
                  disabled={outOfQuota}
                  className="text-left text-sm px-3 py-2 rounded-xl border border-[var(--border)] text-[var(--text-primary)] hover:bg-[var(--bg-inset)] disabled:opacity-50 cursor-pointer disabled:cursor-not-allowed"
                >
                  {s}
                </button>
              ))}
            </div>
          </div>
        )}

        {messages.map((m, i) =>
          m.role === 'user' ? (
            <div key={i} className="flex justify-end">
              <p
                className={`max-w-[85%] px-3 py-2 rounded-2xl rounded-br-sm text-sm whitespace-pre-wrap break-words ${m.failed ? 'opacity-60' : ''}`}
                style={{ backgroundColor: 'var(--primary)', color: 'white' }}
              >
                {m.text}
              </p>
            </div>
          ) : m.text ? (
            <div key={i} className="flex justify-start">
              {/* react-markdown never renders raw HTML, so model output can't inject markup */}
              <div className="max-w-[85%] px-3 py-2 rounded-2xl rounded-bl-sm text-sm bg-[var(--bg-inset)] text-[var(--text-primary)] break-words">
                <ReactMarkdown
                  components={{
                    p: ({ children }) => <p className="mb-2 last:mb-0">{children}</p>,
                    ul: ({ children }) => <ul className="list-disc pl-5 mb-2 last:mb-0 space-y-0.5">{children}</ul>,
                    ol: ({ children }) => <ol className="list-decimal pl-5 mb-2 last:mb-0 space-y-0.5">{children}</ol>,
                    strong: ({ children }) => <strong className="font-semibold">{children}</strong>,
                  }}
                >
                  {m.text}
                </ReactMarkdown>
              </div>
            </div>
          ) : null,
        )}

        {waitingForFirstText && (
          <p className="text-xs text-[var(--text-secondary)] italic">{toolStatus ?? 'Thinking…'}</p>
        )}

        {error && (
          <div role="alert" className="text-sm text-red-600 flex items-center gap-2 flex-wrap">
            <span>{error}</span>
            <button type="button" onClick={retry} className="underline font-medium cursor-pointer">
              Retry
            </button>
          </div>
        )}
      </div>

      <form
        className="border-t border-[var(--border)] p-3"
        onSubmit={(e) => {
          e.preventDefault()
          send(input, messages)
        }}
      >
        {outOfQuota ? (
          <p className="text-sm text-[var(--text-secondary)] text-center py-2">
            You've used all {quota?.limit} messages for today. Come back tomorrow!
          </p>
        ) : (
          <div className="flex items-end gap-2">
            <textarea
              ref={inputRef}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => {
                // Enter sends; Shift+Enter adds a newline
                if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                  e.preventDefault()
                  send(input, messages)
                }
              }}
              rows={1}
              maxLength={MAX_MESSAGE_LENGTH}
              placeholder="Ask about your progress…"
              aria-label="Message"
              className="flex-1 resize-none max-h-28 px-3 py-2 text-sm rounded-xl border border-[var(--border)] bg-[var(--bg-page)] text-[var(--text-primary)] focus:outline-none focus:ring-2 focus:ring-[var(--primary-light)]"
            />
            <button
              type="submit"
              disabled={busy || !input.trim()}
              className="px-3 py-2 text-sm font-medium text-white rounded-xl disabled:opacity-50 cursor-pointer disabled:cursor-not-allowed"
              style={{ backgroundColor: 'var(--primary)' }}
            >
              Send
            </button>
          </div>
        )}
        <p className="mt-2 text-[11px] text-[var(--text-secondary)] text-center">
          AI answers can be wrong. Not medical advice.
        </p>
      </form>
    </section>
  )
}
