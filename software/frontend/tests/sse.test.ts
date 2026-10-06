import { describe, expect, it } from 'vitest'
import { createSseParser } from '../src/sse'

function collect(chunks: string[]) {
  const events: string[] = []
  const parse = createSseParser((data) => events.push(data))
  chunks.forEach(parse)
  return events
}

describe('createSseParser', () => {
  it('emits one payload per event', () => {
    expect(collect(['data: {"a":1}\n\ndata: {"b":2}\n\n'])).toEqual(['{"a":1}', '{"b":2}'])
  })

  it('buffers an event split across network chunks', () => {
    expect(collect(['data: {"type":"te', 'xt","text":"hi"}', '\n', '\n'])).toEqual(['{"type":"text","text":"hi"}'])
  })

  it('waits for the blank line before emitting', () => {
    expect(collect(['data: {"a":1}\n'])).toEqual([])
  })

  it('ignores comment lines and handles CRLF', () => {
    expect(collect([': keep-alive\r\n\r\ndata: {"a":1}\r\n\r\n'])).toEqual(['{"a":1}'])
  })
})
