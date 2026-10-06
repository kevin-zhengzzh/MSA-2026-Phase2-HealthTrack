import { useEffect, useState } from 'react'
import { getWeeklySummary } from '../api'
import type { WeeklySummary } from '../types'

// AI summary of the last complete Monday–Sunday week (specs/06-ai-features-spec.md §5.2).
// The backend caches it per week, so loading the Dashboard repeatedly is cheap.

// "2026-09-28" → local Date (new Date("yyyy-MM-dd") would parse as UTC midnight)
function parseLocalDate(dateStr: string) {
  const [y, m, d] = dateStr.split('-').map(Number)
  return new Date(y, m - 1, d)
}

function formatRange(start: string, end: string) {
  const fmt = (s: string) => parseLocalDate(s).toLocaleDateString('en-US', { month: 'short', day: 'numeric' })
  return `${fmt(start)} – ${fmt(end)}`
}

export default function WeeklySummaryCard() {
  const [data, setData] = useState<WeeklySummary | null>(null)
  const [failed, setFailed] = useState(false)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    let cancelled = false
    setFailed(false)
    getWeeklySummary()
      .then((d) => { if (!cancelled) setData(d) })
      .catch(() => { if (!cancelled) setFailed(true) })
    return () => { cancelled = true }
  }, [attempt])

  return (
    <section className="bg-[var(--bg-surface)] rounded-2xl shadow p-6" aria-label="Last week summary">
      <div className="flex items-center justify-between gap-3 mb-3">
        <h2 className="font-semibold text-[var(--text-secondary)]">
          Last week
          {data && <span className="ml-2 text-sm font-normal text-[var(--text-muted)]">{formatRange(data.weekStart, data.weekEnd)}</span>}
        </h2>
        {data && data.source !== 'fallback' && (
          <span
            className="text-xs font-medium px-2 py-0.5 rounded-full"
            style={{ backgroundColor: 'var(--primary-light)', color: 'var(--primary-text)' }}
          >
            AI summary
          </span>
        )}
      </div>

      {failed ? (
        // WS-6: a failure only affects this card, never the rest of the Dashboard
        <p className="text-sm text-[var(--text-muted)]">
          Summary unavailable right now.{' '}
          <button type="button" onClick={() => setAttempt((a) => a + 1)} className="underline font-medium cursor-pointer">
            Retry
          </button>
        </p>
      ) : !data ? (
        <div className="space-y-2" aria-label="Loading summary">
          <div className="h-4 w-full rounded bg-[var(--bg-inset)] animate-pulse" />
          <div className="h-4 w-11/12 rounded bg-[var(--bg-inset)] animate-pulse" />
          <div className="h-4 w-2/3 rounded bg-[var(--bg-inset)] animate-pulse" />
        </div>
      ) : (
        <p className={`text-sm leading-relaxed whitespace-pre-line ${data.source === 'fallback' ? 'text-[var(--text-muted)]' : 'text-[var(--text-primary)]'}`}>
          {data.summary}
        </p>
      )}
    </section>
  )
}
