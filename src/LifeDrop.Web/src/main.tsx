import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { ApiClient } from './api/client.ts'

const api = new ApiClient(import.meta.env.VITE_API_BASE_URL ?? '')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App api={api} />
  </StrictMode>,
)
