// Incremental Server-Sent Events parser. Network chunks don't line up with
// events — one chunk can hold half an event, or several — so text is buffered
// until a blank line ("\n\n") closes an event, then each "data:" payload is
// handed to onData. Comment lines (":keep-alive") and other fields are ignored.
export function createSseParser(onData: (data: string) => void) {
  let buffer = ''
  return (chunk: string) => {
    buffer += chunk.replace(/\r\n/g, '\n')
    let boundary: number
    while ((boundary = buffer.indexOf('\n\n')) !== -1) {
      const rawEvent = buffer.slice(0, boundary)
      buffer = buffer.slice(boundary + 2)
      const data = rawEvent
        .split('\n')
        .filter((line) => line.startsWith('data:'))
        .map((line) => line.slice(5).trimStart())
        .join('\n')
      if (data) onData(data)
    }
  }
}
