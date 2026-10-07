import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from './App.tsx'
import { ApiClient } from './api/client.ts'

function client(handler: (url: string) => Response | Promise<Response>) {
  sessionStorage.clear()
  return new ApiClient('https://api.test', (async (input: RequestInfo | URL) => handler(String(input))) as typeof fetch, sessionStorage)
}

const ok = () => new Response(JSON.stringify({ accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' }), { status: 200 })

async function fill(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Company code'), 'T1')
  await user.type(screen.getByLabelText('Email'), 'manager@t1.iq')
  await user.type(screen.getByLabelText('Password'), 'secret')
}

describe('App', () => {
  it('asks for every field before calling the server', async () => {
    const handler = vi.fn(ok)
    const user = userEvent.setup()
    render(<App api={client(handler)} />)
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Fill in all fields.')
    expect(handler).not.toHaveBeenCalled()
  })

  it('fills the company code from a company link and keeps it editable', async () => {
    window.history.pushState({}, '', '/?company=acme')
    try {
      const user = userEvent.setup()
      render(<App api={client(ok)} />)
      const code = screen.getByLabelText('Company code')
      expect(code).toHaveValue('acme')
      await user.clear(code)
      await user.type(code, 'other')
      expect(code).toHaveValue('other')
    } finally {
      window.history.pushState({}, '', '/')
    }
  })

  it('signs in, shows the account and signs out', async () => {
    const user = userEvent.setup()
    const me = { id: 'm', email: 'manager@t1.iq', roles: [], permissions: [] }
    render(<App api={client((url) => (url.endsWith('/auth/login') ? ok() : url.endsWith('/me') ? new Response(JSON.stringify(me)) : new Response(null, { status: 204 })))} />)
    await fill(user)
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByText(/Signed in as manager@t1.iq \(T1\)/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Sign out' }))
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('explains a refused login and disables the button while waiting', async () => {
    let release!: (r: Response) => void
    const user = userEvent.setup()
    render(<App api={client(() => new Promise<Response>((r) => (release = r)))} />)
    await fill(user)
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(screen.getByRole('button', { name: 'Signing in…' })).toBeDisabled()
    release(new Response(null, { status: 403 }))
    expect(await screen.findByRole('alert')).toHaveTextContent('This account can sign in only from its approved phone.')
  })
})
