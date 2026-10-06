import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const { getWeeklySummary } = vi.hoisted(() => ({ getWeeklySummary: vi.fn() }))
vi.mock('../../src/api', () => ({ getWeeklySummary }))

import WeeklySummaryCard from '../../src/components/WeeklySummaryCard'

const week = { weekStart: '2026-09-28', weekEnd: '2026-10-04' }

describe('WeeklySummaryCard', () => {
  beforeEach(() => vi.clearAllMocks())

  it('shows a loading state, then the AI summary with the week range', async () => {
    getWeeklySummary.mockResolvedValue({ ...week, summary: 'You burned 1,870 kcal — 94% of your goal.', source: 'ai' })
    render(<WeeklySummaryCard />)

    expect(screen.getByLabelText('Loading summary')).toBeInTheDocument()
    expect(await screen.findByText('You burned 1,870 kcal — 94% of your goal.')).toBeInTheDocument()
    // Parsed as local dates — "2026-09-28" must not slip back to Sep 27
    expect(screen.getByText('Sep 28 – Oct 4')).toBeInTheDocument()
    expect(screen.getByText('AI summary')).toBeInTheDocument()
  })

  it('labels cached summaries as AI too, but not the no-data fallback', async () => {
    getWeeklySummary.mockResolvedValue({ ...week, summary: 'No workouts were logged last week.', source: 'fallback' })
    render(<WeeklySummaryCard />)

    expect(await screen.findByText('No workouts were logged last week.')).toBeInTheDocument()
    expect(screen.queryByText('AI summary')).not.toBeInTheDocument()
  })

  it('shows an error with Retry that refetches', async () => {
    getWeeklySummary
      .mockRejectedValueOnce(new Error('Summary unavailable right now.'))
      .mockResolvedValueOnce({ ...week, summary: 'Back online.', source: 'cache' })
    const user = userEvent.setup()
    render(<WeeklySummaryCard />)

    await user.click(await screen.findByRole('button', { name: 'Retry' }))

    expect(await screen.findByText('Back online.')).toBeInTheDocument()
    expect(getWeeklySummary).toHaveBeenCalledTimes(2)
  })
})
