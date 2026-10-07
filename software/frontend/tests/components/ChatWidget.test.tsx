import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ChatStreamEvent, ChatTurn } from '../../src/types'

const { getChatQuota, streamChat } = vi.hoisted(() => ({
  getChatQuota: vi.fn(),
  streamChat: vi.fn(),
}))
vi.mock('../../src/api', () => ({ getChatQuota, streamChat }))

import ChatWidget from '../../src/components/ChatWidget'

// Makes streamChat replay the given events, like the server's SSE stream
function streams(...events: ChatStreamEvent[]) {
  streamChat.mockImplementationOnce(
    async (_h: ChatTurn[], _m: string, _mode: string | null, onEvent: (e: ChatStreamEvent) => void) => {
      events.forEach(onEvent)
    },
  )
}

async function openWidget() {
  const user = userEvent.setup()
  render(<ChatWidget />)
  await user.click(screen.getByRole('button', { name: 'Open AI assistant' }))
  return user
}

const messageBox = () => screen.getByRole('textbox', { name: 'Message' })

describe('ChatWidget', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    getChatQuota.mockResolvedValue({ used: 2, limit: 30, remaining: 28 })
  })

  it('opens with topic pills and the remaining quota', async () => {
    await openWidget()

    expect(screen.getByRole('dialog', { name: 'HealthTrack assistant' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Rank/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Workout advice/ })).toBeInTheDocument()
    expect(await screen.findByText('28 / 30 messages left today')).toBeInTheDocument()
  })

  it('sends a typed message, renders the streamed Markdown answer and updates the quota', async () => {
    streams(
      { type: 'tool', name: 'get_workout_summary' },
      { type: 'text', text: 'You did **4 workouts** ' },
      { type: 'text', text: 'this week.' },
      { type: 'done', remaining: 27 },
    )
    const user = await openWidget()
    await screen.findByText('28 / 30 messages left today')

    await user.type(messageBox(), 'How did I do this week?{Enter}')

    expect(streamChat).toHaveBeenCalledWith([], 'How did I do this week?', null, expect.any(Function), expect.any(AbortSignal))
    expect((await screen.findByText('4 workouts')).tagName).toBe('STRONG')
    expect(screen.getByText('27 / 30 messages left today')).toBeInTheDocument()
  })

  it('one click on a pill sends its prompt with the mode; the next typed message has no mode', async () => {
    streams({ type: 'text', text: "You're #2 on points." }, { type: 'done', remaining: 27 })
    streams({ type: 'text', text: '30 points behind.' }, { type: 'done', remaining: 26 })
    const user = await openWidget()

    await user.click(screen.getByRole('button', { name: /Rank/ }))

    expect(streamChat).toHaveBeenLastCalledWith([], 'Where do I stand on the leaderboards?', 'rank', expect.any(Function), expect.any(AbortSignal))
    expect(await screen.findByText("You're #2 on points.")).toBeInTheDocument()
    // The user bubble carries the topic badge (the pill itself has the same label)
    expect(screen.getAllByText('🏆 Rank').length).toBe(2)

    await user.type(messageBox(), 'How far behind am I?{Enter}')

    expect(streamChat).toHaveBeenLastCalledWith(
      [
        { role: 'user', text: 'Where do I stand on the leaderboards?' },
        { role: 'assistant', text: "You're #2 on points." },
      ],
      'How far behind am I?',
      null,
      expect.any(Function),
      expect.any(AbortSignal),
    )
  })

  it('shows an error with Retry, and retrying resends with the same mode and no failed history', async () => {
    streams({ type: 'error', message: 'The AI assistant is unavailable right now. Please try again.' })
    streams({ type: 'text', text: 'All good now.' }, { type: 'done', remaining: 27 })
    const user = await openWidget()

    await user.click(screen.getByRole('button', { name: /Goal/ }))
    expect(await screen.findByRole('alert')).toHaveTextContent('unavailable right now')

    await user.click(screen.getByRole('button', { name: 'Retry' }))

    expect(await screen.findByText('All good now.')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(streamChat).toHaveBeenLastCalledWith([], 'How close am I to my weekly goal?', 'goal', expect.any(Function), expect.any(AbortSignal))
  })

  it('surfaces request errors thrown before the stream starts (e.g. quota 429)', async () => {
    streamChat.mockRejectedValueOnce(new Error("You've used all 30 AI messages for today. Try again tomorrow."))
    const user = await openWidget()

    await user.type(messageBox(), 'hello{Enter}')

    expect(await screen.findByRole('alert')).toHaveTextContent('used all 30 AI messages')
  })

  it('disables input when the daily quota is used up', async () => {
    getChatQuota.mockResolvedValue({ used: 30, limit: 30, remaining: 0 })
    await openWidget()

    expect(await screen.findByText(/used all 30 messages for today/)).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Message' })).not.toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Topics' })).not.toBeInTheDocument()
  })

  it('closes with Escape', async () => {
    const user = await openWidget()

    await user.keyboard('{Escape}')

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Open AI assistant' })).toBeInTheDocument()
  })
})
