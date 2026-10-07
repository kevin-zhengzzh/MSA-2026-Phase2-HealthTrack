import type { AuthResponse, CaloriesLeaderboardEntry, ChatQuota, ChatStreamEvent, ChatTurn, CheckIn, CheckinTodayLeaderboardEntry, CheckInResult, LeaderboardEntry, PointTransaction, RewardStatus, Skin, StreakLeaderboardEntry, User, WeeklySummary, WorkoutRecord, WorkoutSubmitResult } from './types'
import { useStore } from './store'
import { createSseParser } from './sse'

const ORIGIN = import.meta.env.VITE_API_URL ?? 'http://localhost:5000'
const BASE = `${ORIGIN}/api`

// Resolves a relative path (e.g. from User.avatarUrl) to a fetchable URL
export const assetUrl = (path: string) => `${BASE}${path}`

function authHeaders() {
  const token = localStorage.getItem('token')
  return {
    'Content-Type': 'application/json',
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
  }
}

// Error responses come in three different shapes depending on where they
// failed: FluentValidation failures are a plain array of strings; our own
// controller errors are { message }; and a raw model-binding failure (e.g.
// sending "1e44" for an int field) never reaches FluentValidation at all —
// [ApiController] short-circuits it into its own { title, errors } shape.
// Without unwrapping that last one, the user just sees "Bad Request".
function extractErrorMessage(err: unknown, fallback: string): string {
  if (Array.isArray(err)) return err.join(' ')
  if (err && typeof err === 'object') {
    const obj = err as Record<string, unknown>
    if (typeof obj.message === 'string') return obj.message
    if (obj.errors && typeof obj.errors === 'object') {
      const messages = Object.values(obj.errors as Record<string, unknown>).flat()
      if (messages.length > 0) return messages.join(' ')
    }
    if (typeof obj.title === 'string') return obj.title
  }
  return fallback
}

// A 401 on an authenticated request means the token expired or was revoked
// (as opposed to e.g. /auth/login rejecting bad credentials, which never had
// a token to send in the first place) — bounce back to signed-out state
// instead of leaving the page stuck rendering with data that'll never load.
function handleUnauthorized(hadToken: boolean) {
  if (!hadToken) return
  // App load fires several authenticated requests in parallel, so they can
  // all 401 around the same time — react to the first one only. By the time
  // the rest get here, clearAuth() below has already nulled the live token,
  // so this check (not the hadToken snapshot from when each request started)
  // is what actually de-dupes them.
  if (!useStore.getState().token) return
  useStore.getState().clearAuth()
  useStore.getState().pushToast('Your session has expired — please sign in again.')
}

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const hadToken = !!localStorage.getItem('token')
  const res = await fetch(`${BASE}${path}`, {
    ...options,
    headers: { ...authHeaders(), ...(options?.headers ?? {}) },
  })
  if (!res.ok) {
    if (res.status === 401) handleUnauthorized(hadToken)
    const err = await res.json().catch(() => ({ message: res.statusText }))
    throw new Error(extractErrorMessage(err, res.statusText))
  }
  if (res.status === 204) return undefined as T
  return res.json()
}

// Auth
export const register = (username: string, email: string, password: string) =>
  request<AuthResponse>('/auth/register', {
    method: 'POST',
    body: JSON.stringify({ username, email, password }),
  })

export const login = (email: string, password: string) =>
  request<AuthResponse>('/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  })

// User
export const getMe = () => request<User>('/user/me')

export const updateWeeklyGoal = (weeklyCalorieGoal: number) =>
  request<{ weeklyCalorieGoal: number }>('/user/weekly-goal', {
    method: 'PUT',
    body: JSON.stringify({ weeklyCalorieGoal }),
  })

export const updateUsername = (username: string) =>
  request<{ username: string }>('/user/username', {
    method: 'PUT',
    body: JSON.stringify({ username }),
  })

export async function uploadAvatar(file: File) {
  const token = localStorage.getItem('token')
  const formData = new FormData()
  formData.append('file', file)

  const res = await fetch(`${BASE}/user/avatar`, {
    method: 'POST',
    headers: token ? { Authorization: `Bearer ${token}` } : {},
    body: formData,
  })
  if (!res.ok) {
    if (res.status === 401) handleUnauthorized(!!token)
    const err = await res.json().catch(() => ({ message: res.statusText }))
    throw new Error(extractErrorMessage(err, res.statusText))
  }
  return res.json() as Promise<{ avatarUrl: string }>
}

// Check-in
function localDateStr() {
  const d = new Date()
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

export const checkInToday = (note?: string) =>
  request<CheckInResult>(`/checkin?localDate=${localDateStr()}`, {
    method: 'POST',
    body: JSON.stringify(note ?? null),
  })

export const getTodayStatus = () =>
  request<{ checkedIn: boolean; result: CheckInResult | null }>(`/checkin/today?localDate=${localDateStr()}`)

export const getCheckInHistory = () => request<CheckIn[]>('/checkin/history')

// Leaderboard
export const getLeaderboard = () => request<LeaderboardEntry[]>('/leaderboard')

// Consecutive check-in streak, in days — not today's check-in time
export const getStreakLeaderboard = () => request<StreakLeaderboardEntry[]>('/leaderboard/streak')

// Calories burned today only
export const getCaloriesLeaderboard = () => request<CaloriesLeaderboardEntry[]>(`/leaderboard/calories?localDate=${localDateStr()}`)

// Who checked in earliest today — only users who've checked in today appear
export const getCheckinTodayLeaderboard = () =>
  request<CheckinTodayLeaderboardEntry[]>(`/leaderboard/checkin-today?localDate=${localDateStr()}`)

// Points
export const getPointHistory = () => request<PointTransaction[]>('/points/history')

// Rewards — check-in/workout points are earned but not credited until claimed
export const getTodayRewards = () =>
  request<RewardStatus>(`/rewards/today?localDate=${localDateStr()}`)

export const claimRewards = () =>
  request<{ claimedPoints: number; totalPoints: number }>(`/rewards/claim?localDate=${localDateStr()}`, {
    method: 'POST',
  })

// Skins
export const getSkins = () => request<Skin[]>('/skin')

export const purchaseSkin = (id: number) =>
  request<{ message: string; remainingPoints: number }>(`/skin/${id}/purchase`, { method: 'POST' })

// Clears the "new skin" red-dot notification — call when the Store opens
export const markSkinsSeen = () => request<void>('/skin/mark-seen', { method: 'PUT' })

export const equipSkin = (id: number) =>
  request<{ theme: string }>(`/skin/${id}/equip`, { method: 'PUT' })

export const unequipSkin = () =>
  request<{ theme: string }>('/skin/equip', { method: 'DELETE' })

// Workout records
export const recordWorkout = (workoutType: string, calories: number) =>
  request<WorkoutSubmitResult>(`/workout?localDate=${localDateStr()}`, {
    method: 'POST',
    body: JSON.stringify({ workoutType, calories }),
  })

export const getWorkoutHistory = () => request<WorkoutRecord[]>('/workout')

export const getWorkoutTodayStatus = () =>
  request<{ recordedToday: boolean }>(`/workout/today?localDate=${localDateStr()}`)

export const updateWorkout = (id: number, workoutType: string, calories: number) =>
  request<WorkoutRecord>(`/workout/${id}`, {
    method: 'PUT',
    body: JSON.stringify({ workoutType, calories }),
  })

export const deleteWorkout = (id: number) =>
  request<void>(`/workout/${id}`, { method: 'DELETE' })

// AI weekly summary of the last complete week
export const getWeeklySummary = () =>
  request<WeeklySummary>(`/ai/weekly-summary?localDate=${localDateStr()}`)

// AI chat assistant
export const getChatQuota = () => request<ChatQuota>('/ai/chat/quota')

// POST /ai/chat answers with Server-Sent Events, which EventSource can't
// consume (no POST body, no Authorization header) — so the response body is
// read as a stream and parsed incrementally. Validation/quota failures arrive
// as a normal JSON error before the stream starts and are thrown like any
// other request error; failures after that arrive as an "error" event.
export async function streamChat(
  history: ChatTurn[],
  message: string,
  mode: string | null,
  onEvent: (event: ChatStreamEvent) => void,
  signal?: AbortSignal,
) {
  const hadToken = !!localStorage.getItem('token')
  const res = await fetch(`${BASE}/ai/chat?localDate=${localDateStr()}`, {
    method: 'POST',
    headers: authHeaders(),
    body: JSON.stringify({ history, message, mode }),
    signal,
  })
  if (!res.ok || !res.body) {
    if (res.status === 401) handleUnauthorized(hadToken)
    const err = await res.json().catch(() => ({ message: res.statusText }))
    throw new Error(extractErrorMessage(err, 'The AI assistant is unavailable right now.'))
  }

  const parse = createSseParser((data) => onEvent(JSON.parse(data) as ChatStreamEvent))
  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  while (true) {
    const { done, value } = await reader.read()
    if (done) break
    parse(decoder.decode(value, { stream: true }))
  }
}
