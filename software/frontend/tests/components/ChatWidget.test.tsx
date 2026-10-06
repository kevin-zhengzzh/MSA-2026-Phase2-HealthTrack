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
  streamChat.mockImplementationOnce(async (_h: ChatTurn[], _m: string, onEvent: (e: ChatStreamEvent) => void) => {
    events.forEach(onEvent)
  })
}

async function openWidget() {
  const user = userEvent.setup()
  render(<ChatWidget />)
  await user.click(screen.getByRole('button', { name: 'Open AI assistant' }))
  return user
}

describe('ChatWidget', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    getChatQuota.mockResolvedValue({ used: 2, limit: 30, remaining: 28 })
  })

  it('opens with suggested questions and the remaining quota', async () => {
    await openWidget()

    expect(screen.getByRole('dialog', { name: 'HealthTrack assistant' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'How did I do this week?' })).toBeInTheDocument()
    expect(await screen.findByText('28 / 30 messages left today')).toBeInTheDocument()
  })

  it('sends a suggestion, renders the streamed Markdown answer and updates the quota', async () => {
    streams(
      { type: 'tool', name: 'get_workout_summary' },
      { type: 'text', text: 'You did **4 workouts** ' },
      { type: 'text', text: 'this week.' },
      { type: 'done', remaining: 27 },
    )
    const user = await openWidget()
    await screen.findByText('28 / 30 messages left today')

    await user.click(screen.getByRole('button', { name: 'How did I do this week?' }))

    expect(streamChat).toHaveBeenCalledWith([], 'How did I do this week?', expect.any(Function), expect.any(AbortSignal))
    expect(await screen.findByText('4 workouts')).toContainHTML('') // rendered inside <strong>
    expect(screen.getByText('4 workouts').tagName).toBe('STRONG')
    expect(screen.getByText('27 / 30 messages left today')).toBeInTheDocument()
  })

  it('sends earlier turns as history on the next message', async () => {
    streams({ type: 'text', text: 'Streak is 3.' }, { type: 'done', remaining: 27 })
    streams({ type: 'text', text: '4 more.' }, { type: 'done', remaining: 26 })
    const user = await openWidget()

    await user.type(screen.getByRole('textbox', { name: 'Message' }), "What's my streak?{Enter}")
    await screen.findByText('Streak is 3.')
    await user.type(screen.getByRole('textbox', { name: 'Message' }), 'And until the skin?{Enter}')
    await screen.findByText('4 more.')

    expect(streamChat).toHaveBeenLastCalledWith(
      [
        { role: 'user', text: "What's my streak?" },
        { role: 'assistant', text: 'Streak is 3.' },
      ],
      'And until the skin?',
      expect.any(Function),
      expect.any(AbortSignal),
    )
  })

  it('shows an error with Retry, and retrying resends without the failed exchange in history', async () => {
    streams({ type: 'error', message: 'The AI assistant is unavailable right now. Please try again.' })
    streams({ type: 'text', text: 'All good now.' }, { type: 'done', remaining: 27 })
    const user = await openWidget()

    await user.type(screen.getByRole('textbox', { name: 'Message' }), 'hello{Enter}')
    expect(await screen.findByRole('alert')).toHaveTextContent('unavailable right now')

    await user.click(screen.getByRole('button', { name: 'Retry' }))

    expect(await screen.findByText('All good now.')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(streamChat).toHaveBeenLastCalledWith([], 'hello', expect.any(Function), expect.any(AbortSignal))
  })

  it('surfaces request errors thrown before the stream starts (e.g. quota 429)', async () => {
    streamChat.mockRejectedValueOnce(new Error("You've used all 30 AI messages for today. Try again tomorrow."))
    const user = await openWidget()

    await user.type(screen.getByRole('textbox', { name: 'Message' }), 'hello{Enter}')

    expect(await screen.findByRole('alert')).toHaveTextContent('used all 30 AI messages')
  })

  it('disables input when the daily quota is used up', async () => {
    getChatQuota.mockResolvedValue({ used: 30, limit: 30, remaining: 0 })
    await openWidget()

    expect(await screen.findByText(/used all 30 messages for today/)).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Message' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'How did I do this week?' })).toBeDisabled()
  })

  it('closes with Escape', async () => {
    const user = await openWidget()

    await user.keyboard('{Escape}')

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Open AI assistant' })).toBeInTheDocument()
  })
})
