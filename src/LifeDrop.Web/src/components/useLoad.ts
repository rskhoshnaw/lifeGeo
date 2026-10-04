import { useCallback, useEffect, useState } from 'react'
import { errorMessage } from '../api/admin.ts'

// Loads data on mount and on reload(); keeps the last error as a readable message.
export function useLoad<T>(load: () => Promise<T>) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let active = true
    load().then(
      (value) => active && (setData(value), setError(null)),
      (e: unknown) => active && setError(errorMessage(e)),
    )
    return () => {
      active = false
    }
  }, [load, version])

  const reload = useCallback(() => setVersion((v) => v + 1), [])
  return { data, error, reload }
}

// Runs one action at a time; returns its error message, if any, for display.
export function useAction(after: () => void) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const run = async (action: () => Promise<void>) => {
    if (busy) return
    setBusy(true)
    setError(null)
    try {
      await action()
      after()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  return { busy, error, run }
}
