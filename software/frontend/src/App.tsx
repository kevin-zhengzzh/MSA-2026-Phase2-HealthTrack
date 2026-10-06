import { useEffect, useState } from 'react'
import { BrowserRouter, Link, Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { useStore } from './store'
import { claimRewards, getMe, getSkins, getTodayRewards, getTodayStatus, updateUsername, uploadAvatar } from './api'
import Sidebar from './components/Sidebar'
import RecordButton from './components/RecordButton'
import UserMenu from './components/UserMenu'
import DailyTasksMenu from './components/DailyTasksMenu'
import StoreModal from './components/StoreModal'
import PointsHistoryModal from './components/PointsHistoryModal'
import ToastContainer from './components/ToastContainer'
import ChatWidget from './components/ChatWidget'
import Dashboard from './pages/Dashboard'
import Login from './pages/Login'
import Register from './pages/Register'
import History from './pages/History'
import RecordHistory from './pages/RecordHistory'
import Rank from './pages/Rank'
import Landing from './pages/Landing'

// Home / Check-in History / Record History share the sidebar and are
// treated as one "Dashboard" section in the header nav's active state
const DASHBOARD_SECTION_PATHS = ['/', '/history', '/record-history']

function HomeIcon({ className }: { className?: string }) {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className={className}>
      <path d="M3 11.5 12 4l9 7.5" />
      <path d="M5 10v9a1 1 0 0 0 1 1h4v-6h4v6h4a1 1 0 0 0 1-1v-9" />
    </svg>
  )
}

function RankIcon({ className }: { className?: string }) {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className={className}>
      <path d="M8 21h8" />
      <path d="M12 17v4" />
      <path d="M7 4h10v5a5 5 0 0 1-10 0V4Z" />
      <path d="M17 5h2.5a1.5 1.5 0 0 1 1.5 1.5A3.5 3.5 0 0 1 17.5 10H17" />
      <path d="M7 5H4.5A1.5 1.5 0 0 0 3 6.5 3.5 3.5 0 0 0 6.5 10H7" />
    </svg>
  )
}

// The two top-level sections shown as a sliding-rail switcher in the header
const NAV_TABS = [
  { to: '/', label: 'Home', icon: HomeIcon, isActive: (p: string) => DASHBOARD_SECTION_PATHS.includes(p) },
  { to: '/rank', label: 'Rank', icon: RankIcon, isActive: (p: string) => p === '/rank' },
]

function NavBar({ onOpenStore, onOpenPoints }: { onOpenStore: () => void; onOpenPoints: () => void }) {
  const { username, user, cachedAvatarUrl, skins, rewardStatus, setUser, setUsername, setRewardStatus, pushToast, clearAuth, bumpLeaderboardRefresh } = useStore()
  const location = useLocation()
  const activeTabIndex = NAV_TABS.findIndex((t) => t.isActive(location.pathname))
  const hasAffordableSkin = !!user && skins.some((s) => !s.isReward && !s.isOwned && s.pointCost <= user.points)
  const hasNewSkin = skins.some((s) => s.isNew)
  const hasStoreNotification = hasAffordableSkin || hasNewSkin

  async function handleAvatarUpload(file: File) {
    try {
      await uploadAvatar(file)
      setUser(await getMe())
    } catch (err: unknown) {
      pushToast(err instanceof Error ? err.message : 'Failed to upload image')
    }
  }

  async function handleUsernameChange(newUsername: string) {
    try {
      const res = await updateUsername(newUsername)
      setUsername(res.username)
      pushToast('Username updated!', 'success')
      // Leaderboard entries come from their own DB query, not from
      // user.username — bump so Rank refetches and shows the new name.
      bumpLeaderboardRefresh()
    } catch (err: unknown) {
      const error = err instanceof Error ? err : new Error('Failed to update username')
      pushToast(error.message)
      throw error
    }
  }

  async function handleClaim() {
    try {
      const res = await claimRewards()
      pushToast(`+${res.claimedPoints} points claimed!`, 'success')
      const [updatedUser, updatedRewards] = await Promise.all([getMe(), getTodayRewards()])
      setUser(updatedUser)
      setRewardStatus(updatedRewards)
      bumpLeaderboardRefresh()
    } catch (err: unknown) {
      pushToast(err instanceof Error ? err.message : 'Nothing to claim right now.')
    }
  }

  return (
    <header className="fixed top-0 inset-x-0 z-40 h-14 bg-[var(--bg-surface)] border-b border-[var(--border)] shadow" style={{ color: 'var(--primary)' }}>
      {/* Same max-width as the page body below, so the logo and right-side
          controls line up with the sidebar/content edges on large screens.
          Below lg, flex justify-between (not the 3-equal-column grid) so the
          nav switcher doesn't get squeezed into overlapping the side items. */}
      <div className="max-w-7xl mx-auto px-3 sm:px-4 lg:pr-4 lg:pl-4 h-full flex lg:grid lg:grid-cols-3 items-center justify-between">
        <Link
          to="/"
          className="font-bold text-base sm:text-lg tracking-tight justify-self-start flex-shrink-0"
          style={{ marginLeft: 'clamp(-24px, calc((1280px - 100vw) / 2), 0px)' }}
        >
          HealthTrack
        </Link>
        <nav className="relative flex text-sm font-medium bg-[var(--bg-inset)] rounded-full p-1 justify-self-center flex-shrink-0">
          <div
            className="absolute top-1 bottom-1 left-1 w-12 sm:w-20 rounded-full transition-transform duration-200 ease-out"
            style={{
              backgroundColor: 'var(--primary-light)',
              transform: `translateX(${Math.max(activeTabIndex, 0) * 100}%)`,
              opacity: activeTabIndex === -1 ? 0 : 1,
            }}
          />
          {NAV_TABS.map((tab) => {
            const active = tab.isActive(location.pathname)
            return (
              <Link
                key={tab.to}
                to={tab.to}
                aria-label={tab.label}
                title={tab.label}
                className={`relative z-10 w-12 sm:w-20 flex items-center justify-center px-2 sm:px-3 py-1.5 rounded-full transition ${active ? '' : 'opacity-80 hover:opacity-100'}`}
                style={active ? { color: 'var(--primary-text)' } : undefined}
              >
                <tab.icon className="w-5 h-5" />
              </Link>
            )
          })}
        </nav>
        <div className="flex items-center gap-1.5 sm:gap-3 justify-self-end flex-shrink-0">
          <DailyTasksMenu rewardStatus={rewardStatus} onClaim={handleClaim} />
          <UserMenu
            username={username}
            points={user?.points ?? null}
            avatarUrl={user?.avatarUrl ?? cachedAvatarUrl}
            hasStoreNotification={hasStoreNotification}
            onOpenStore={onOpenStore}
            onOpenPoints={onOpenPoints}
            onAvatarUpload={handleAvatarUpload}
            onUpdateUsername={handleUsernameChange}
            onSignOut={clearAuth}
          />
        </div>
      </div>
    </header>
  )
}

function PrivateLayout() {
  const { token, user, setUser, setTheme, storeOpen, setStoreOpen, setSkins, setRewardStatus, setCheckedInToday } = useStore()
  const location = useLocation()
  const [pointsOpen, setPointsOpen] = useState(false)

  // Hooks must be called before any conditional return
  useEffect(() => {
    if (token) {
      getMe().then(setUser).catch(console.error)
      getSkins().then(setSkins).catch(console.error)
      getTodayRewards().then(setRewardStatus).catch(console.error)
      // Fetched globally (not just by Dashboard) so pages like Rank that
      // depend on checkedInToday (e.g. StreakTracker) are accurate even
      // when Dashboard never mounted this session.
      getTodayStatus().then((r) => setCheckedInToday(r.checkedIn)).catch(console.error)
    }
  }, [token])

  useEffect(() => {
    if (user?.equippedTheme) setTheme(user.equippedTheme)
  }, [user?.equippedTheme])

  // Root without a token is the marketing landing page, not an auth redirect —
  // every other private path still bounces straight to /login as before.
  if (!token) return location.pathname === '/' ? <Landing /> : <Navigate to="/login" replace />

  const showSidebar = DASHBOARD_SECTION_PATHS.includes(location.pathname)
  // Rank has its own internal sidebar (ranking categories, not routes) — it
  // needs the wider Dashboard-style width, and to hug the left edge the
  // same way, instead of the narrow centered list pages
  const isRank = location.pathname === '/rank'
  const isWide = location.pathname === '/'
  const hugLeft = showSidebar || isRank

  return (
    <div className="min-h-screen bg-[var(--bg-page)]">
      <NavBar onOpenStore={() => setStoreOpen(true)} onOpenPoints={() => setPointsOpen(true)} />
      <div className="pt-14">
        <div className="max-w-7xl mx-auto px-4 py-6 lg:py-8 flex flex-col lg:flex-row gap-6 lg:gap-10 items-stretch lg:items-start">
          {showSidebar && <Sidebar />}
          {showSidebar && <RecordButton />}
          {/* Content hugs the sidebar (no auto-margin) when one is showing —
              mx-auto only re-centers it on sidebar-less pages like History/Record History.
              Only applies at lg+, where the sidebar sits beside the content instead of above it. */}
          {/* Rank has its own internal side columns (RankSidebar +
              StreakTracker, 224px + 192px + two 40px gaps = 496px) eating
              into whatever width it's given — max-w-2xl (672px) + 496px so
              its podium/list column ends up exactly as wide as History's card. */}
          <main className={`flex-1 min-w-0 ${hugLeft ? '' : 'lg:mx-auto'} ${isWide ? 'lg:max-w-5xl' : isRank ? 'lg:max-w-[73rem]' : 'lg:max-w-2xl'}`}>
            <Routes>
              <Route path="/" element={<Dashboard />} />
              <Route path="/history" element={<History />} />
              <Route path="/record-history" element={<RecordHistory />} />
              {/* Keyed by location.key so clicking the header's Rank tab while
                  already on /rank remounts the page — resetting its local
                  tab state back to the Daily Check-in default — instead of
                  leaving whichever tab you'd switched to still showing. */}
              <Route path="/rank" element={<Rank key={location.key} />} />
              <Route path="*" element={<Navigate to="/" replace />} />
            </Routes>
          </main>
        </div>
      </div>
      {storeOpen && <StoreModal onClose={() => setStoreOpen(false)} />}
      {pointsOpen && <PointsHistoryModal onClose={() => setPointsOpen(false)} />}
      <ChatWidget />
    </div>
  )
}

export default function App() {
  const token = useStore((s) => s.token)
  return (
    <BrowserRouter>
      <ToastContainer />
      <Routes>
        <Route path="/login" element={token ? <Navigate to="/" replace /> : <Login />} />
        <Route path="/register" element={token ? <Navigate to="/" replace /> : <Register />} />
        <Route path="/*" element={<PrivateLayout />} />
      </Routes>
    </BrowserRouter>
  )
}
