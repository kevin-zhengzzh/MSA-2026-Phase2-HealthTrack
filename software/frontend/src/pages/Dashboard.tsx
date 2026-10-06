import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { checkInToday, getCheckInHistory, getMe, getTodayRewards, getTodayStatus, getWorkoutHistory, updateWeeklyGoal } from '../api'
import { useStore } from '../store'
import type { CheckIn, CheckInResult, WorkoutRecord } from '../types'
import CalorieBarChart from '../components/CalorieBarChart'
import WeeklyGoalDonut from '../components/WeeklyGoalDonut'
import CheckInHeatmap from '../components/CheckInHeatmap'
import WeeklySummaryCard from '../components/WeeklySummaryCard'
import { useLockBodyScroll } from '../hooks/useLockBodyScroll'

// Ticks every second so the header clock stays live while the page is open.
function useNow() {
  const [now, setNow] = useState(new Date())
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(id)
  }, [])
  return now
}

export default function Dashboard() {
  const { user, setUser, checkedInToday, setCheckedInToday, setRewardStatus, pushToast, setStoreOpen, workoutRefreshKey } = useStore()
  const now = useNow()
  const [loading, setLoading] = useState(false)
  const [lastResult, setLastResult] = useState<CheckInResult | null>(null)
  const [workoutHistory, setWorkoutHistory] = useState<WorkoutRecord[]>([])
  const [checkInHistory, setCheckInHistory] = useState<CheckIn[]>([])
  const [initialLoading, setInitialLoading] = useState(true)
  const [showPointsHint, setShowPointsHint] = useState(false)
  useLockBodyScroll(showPointsHint)

  // Re-runs whenever the calendar date changes, not just on mount — a tab
  // left open (or a laptop woken up) past midnight would otherwise keep
  // showing yesterday's checkedInToday/history state indefinitely, since
  // this page never remounts on its own.
  const todayStr = now.toDateString()
  useEffect(() => {
    Promise.allSettled([
      getMe().then(setUser),
      getTodayStatus().then((r) => {
        setCheckedInToday(r.checkedIn)
        setLastResult(r.result ?? null)
      }),
      getWorkoutHistory().then(setWorkoutHistory),
      getCheckInHistory().then(setCheckInHistory),
    ]).then(() => setInitialLoading(false))
  }, [todayStr])

  // A workout recorded from the shared floating Record button (which can be
  // opened from any page) doesn't touch this page's local state directly.
  useEffect(() => {
    if (workoutRefreshKey === 0) return
    getWorkoutHistory().then(setWorkoutHistory).catch(console.error)
  }, [workoutRefreshKey])

  async function handleGoalSave(newGoal: number) {
    try {
      await updateWeeklyGoal(newGoal)
      if (user) setUser({ ...user, weeklyCalorieGoal: newGoal })
      pushToast('Weekly goal updated!', 'success')
    } catch (err: unknown) {
      pushToast(err instanceof Error ? err.message : 'Failed to update goal')
      throw err
    }
  }

  async function handleCheckIn() {
    if (checkedInToday) {
      pushToast('Already checked in today. Come back tomorrow!')
      return
    }
    setLoading(true)
    try {
      const result = await checkInToday()
      setLastResult(result)
      setCheckedInToday(true)
      setCheckInHistory((h) => [{ id: result.id, date: result.date, note: null, createdAt: result.createdAt }, ...h])
      pushToast(`+${result.pointsEarned} pts ready to claim in Daily Tasks · ${result.streak}-day streak`, 'success')
      getTodayRewards().then(setRewardStatus).catch(console.error)
    } catch (err: unknown) {
      pushToast(err instanceof Error ? err.message : 'Check-in failed')
    } finally {
      setLoading(false)
    }
  }

  // The backend only updates streak at check-in time, so it still holds
  // yesterday's value until today's check-in happens — show 0 until then
  // rather than a stale streak that looks "already earned" for today.
  const displayStreak = checkedInToday ? (user?.streak ?? 0) : 0

  return (
    <div className="flex flex-col gap-6">
      {/* items-start (not center) — keeps the "Dashboard" title's top edge
          flush with the row, matching the plain <h1> on History/Record
          History, instead of it centering against the taller stat cards. */}
      <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold text-[var(--text-primary)]">Dashboard</h1>
          <p className="text-sm text-[var(--text-muted)] mt-1">
            {now.toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' })}
            {' · '}
            {now.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', second: '2-digit' })}
          </p>
        </div>
        {user && (
          <div className="grid grid-cols-2 gap-3">
            <StatCard label="Points" value={user.points} onInfoClick={() => setShowPointsHint(true)} />
            <StatCard label="Current streak" value={`${displayStreak} day${displayStreak > 1 ? 's' : ''}`} />
          </div>
        )}
      </div>

      <WeeklySummaryCard />

      {/*
        A true 2x2 grid (not two independent columns) so row 2 — the weekly
        goal donut and the check-in frequency chart — share one row track
        and stretch to the same height, keeping their bottoms aligned. Order
        values control both the mobile stacking sequence (check-in card and
        heatmap first) and the desktop 2-column placement.
      */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <div className="order-3 lg:order-1">
          <CalorieBarChart records={workoutHistory} />
        </div>

        <div className="order-1 lg:order-2">
          {initialLoading ? (
            <div className="w-full h-full bg-[var(--bg-surface)] rounded-2xl shadow p-8 flex flex-col items-center justify-center gap-4">
              <div className="w-16 h-16 rounded-full bg-[var(--bg-inset)] animate-pulse" />
              <div className="h-6 w-56 rounded bg-[var(--bg-inset)] animate-pulse" />
              <div className="h-4 w-72 rounded bg-[var(--bg-inset)] animate-pulse" />
            </div>
          ) : (
            // Big check-in button — same icon/title/subtitle/button structure
            // in both states so the card never resizes; the button stays
            // visible after checking in, just disabled and greyed out
            // instead of disappearing.
            <div className="w-full h-full bg-[var(--bg-surface)] rounded-2xl shadow p-8 flex flex-col items-center justify-center gap-4">
              <div className="w-[60px] h-[60px] flex items-center justify-center">
                {checkedInToday ? (
                  <div
                    className="w-[60px] h-[60px] rounded-full flex items-center justify-center text-white"
                    style={{ backgroundColor: 'var(--primary)' }}
                  >
                    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" className="w-8 h-8">
                      <path d="M20 6 9 17l-5-5" />
                    </svg>
                  </div>
                ) : (
                  <span className="text-6xl leading-none">🏃</span>
                )}
              </div>
              <p className="text-xl font-semibold text-[var(--text-secondary)]">
                {checkedInToday ? 'You checked in today!' : 'Did you exercise today?'}
              </p>
              <p className="text-[var(--text-muted)] text-sm text-center">
                {checkedInToday
                  ? lastResult
                    ? <>You've surpassed <Link to="/rank" className="font-bold hover:underline" style={{ color: 'var(--primary)' }}>{lastResult.percentSurpassed}%</Link> of users today. Come back tomorrow!</>
                    : 'Come back tomorrow to keep your streak going.'
                  : 'Hit the button to log your workout and earn points.'}
              </p>
              <button
                onClick={handleCheckIn}
                disabled={loading}
                className="mt-2 text-white font-bold text-lg px-10 py-4 rounded-2xl shadow-lg transition-transform active:scale-95 disabled:opacity-60 cursor-pointer"
                style={{ backgroundColor: checkedInToday ? 'var(--border-strong)' : 'var(--primary)' }}
                onMouseEnter={(e) => { if (!checkedInToday) e.currentTarget.style.backgroundColor = 'var(--primary-hover)' }}
                onMouseLeave={(e) => { if (!checkedInToday) e.currentTarget.style.backgroundColor = 'var(--primary)' }}
              >
                {checkedInToday ? '✓ Checked In' : loading ? 'Checking in…' : '✓ Check In'}
              </button>
            </div>
          )}
        </div>

        <div className="order-4 lg:order-3">
          {user && <WeeklyGoalDonut records={workoutHistory} goal={user.weeklyCalorieGoal} onSave={handleGoalSave} />}
        </div>

        <div className="order-2 lg:order-4">
          <CheckInHeatmap checkIns={checkInHistory} records={workoutHistory} />
        </div>
      </div>

      {/* Points hint modal */}
      {showPointsHint && (
        <div
          className="fixed inset-0 z-40 flex items-center justify-center bg-black/30 backdrop-blur-sm p-4"
          onClick={() => setShowPointsHint(false)}
        >
          <div
            className="bg-[var(--bg-surface)] rounded-2xl shadow-xl p-6 w-full max-w-sm"
            onClick={(e) => e.stopPropagation()}
          >
            <h2 className="text-xl font-bold text-[var(--text-primary)] mb-4">How points work</h2>
            <ul className="text-sm text-[var(--text-muted)] space-y-2 list-disc list-inside">
              <li>10 base points per check-in</li>
              <li>+streak × 2 bonus (e.g. 5-day streak = +10 bonus)</li>
              <li>Recording a workout also earns +10 points — once per day only</li>
              <li>
                Spend points in the{' '}
                <button
                  onClick={() => { setShowPointsHint(false); setStoreOpen(true) }}
                  className="font-medium hover:underline cursor-pointer"
                  style={{ color: 'var(--primary)' }}
                >
                  Store
                </button>{' '}
                to unlock app skins
              </li>
            </ul>
          </div>
        </div>
      )}
    </div>
  )
}

function StatCard({ label, value, onInfoClick }: { label: string; value: string | number; onInfoClick?: () => void }) {
  return (
    <div className="rounded-xl p-4" style={{ backgroundColor: 'var(--primary-light)', color: 'var(--primary-text)' }}>
      <div className="flex items-center gap-1.5">
        <p className="text-xs font-medium uppercase tracking-wide opacity-70">{label}</p>
        {onInfoClick && (
          <button
            onClick={onInfoClick}
            aria-label={`About ${label}`}
            className="w-4 h-4 rounded-full flex items-center justify-center text-[10px] font-bold leading-none cursor-pointer opacity-70 hover:opacity-100"
            style={{ backgroundColor: 'var(--primary)', color: 'white' }}
          >
            ?
          </button>
        )}
      </div>
      <p className="text-2xl font-bold mt-1">{value}</p>
    </div>
  )
}
