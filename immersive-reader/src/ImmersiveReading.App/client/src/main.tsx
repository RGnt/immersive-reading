import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { exposeHostRuntime } from './sdk/exposeRuntime'

exposeHostRuntime()

// macOS keeps its native overlay scroll bars (index.css styles the others)
if (/Mac|iPhone|iPad/i.test(navigator.userAgent)) document.documentElement.classList.add('ir-mac')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
