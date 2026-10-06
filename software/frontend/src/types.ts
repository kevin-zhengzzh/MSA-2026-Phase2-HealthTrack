export interface User {
  id: number
  username: string
  email: string
  points: number
  streak: number
  lastCheckIn: string | null
  createdAt: string
  equippedSkinId: number | null
  equippedTheme: string
  avatarUrl: string | null
  weeklyCalorieGoal: number
}

export interface CheckIn {
  id: number
  date: string
  note: string | null
  createdAt: string
}

export const WORKOUT_TYPES = ['Running', 'Cycling', 'Swimming', 'Gym', 'Yoga', 'Other'] as const

// Fixed categorical color per workout type (never reassigned/cycled), so a
// type keeps the same identity color across charts regardless of which
// types have data in a given period.
export const WORKOUT_TYPE_COLORS: Record<string, string> = {
  Running: '#2a78d6',
  Cycling: '#eb6834',
  Swimming: '#1baf7a',
  Gym: '#eda100',
  Yoga: '#e87ba4',
  Other: '#008300',
}

export interface WorkoutRecord {
  id: number
  workoutType: string
  calories: number
  date: string
  createdAt: string
}

export interface WorkoutSubmitResult extends WorkoutRecord {
  pointsEarned: number
  totalPoints: number
}

export interface CheckInResult {
  id: number
  date: string
  pointsEarned: number
  totalPoints: number
  streak: number
  createdAt: string
  percentSurpassed: number
}

export interface Skin {
  id: number
  name: string
  description: string
  pointCost: number
  theme: string
  isOwned: boolean
  isEquipped: boolean
  isReward: boolean
  isNew: boolean
}

export interface RewardStatus {
  checkIn: { pointsEarned: number; claimed: boolean } | null
  workout: { pointsEarned: number; claimed: boolean } | null
}

export interface PointTransaction {
  id: number
  amount: number
  reason: string
  createdAt: string
}

export interface LeaderboardEntry {
  rank: number
  id: number
  username: string
  points: number
  isMe: boolean
  avatarUrl: string | null
}

export interface StreakLeaderboardEntry {
  rank: number
  id: number
  username: string
  streak: number
  isMe: boolean
  avatarUrl: string | null
}

export interface CaloriesLeaderboardEntry {
  rank: number
  id: number
  username: string
  calories: number
  isMe: boolean
  avatarUrl: string | null
}

export interface CheckinTodayLeaderboardEntry {
  rank: number
  id: number
  username: string
  checkedInAt: string
  isMe: boolean
  avatarUrl: string | null
}

export interface AuthResponse {
  token: string
  userId: number
  username: string
}

// AI chat assistant — see specs/06-ai-features-spec.md §7
export interface ChatTurn {
  role: 'user' | 'assistant'
  text: string
}

export interface ChatQuota {
  used: number
  limit: number
  remaining: number
}

export type ChatStreamEvent =
  | { type: 'text'; text: string }
  | { type: 'tool'; name: string }
  | { type: 'done'; remaining: number }
  | { type: 'error'; message: string }

// Maps theme name → preview hex color
export const THEME_COLORS: Record<string, { primary: string; light: string }> = {
  default:  { primary: '#16a34a', light: '#dcfce7' },
  ocean:    { primary: '#2563eb', light: '#dbeafe' },
  sunset:   { primary: '#ea580c', light: '#ffedd5' },
  midnight: { primary: '#7c3aed', light: '#ede9fe' },
  cherry:   { primary: '#db2777', light: '#fce7f3' },
  dark:     { primary: '#6366f1', light: '#312e81' },
}
