import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError, setTestModeHeader, type Option, type User } from './api'
import ReportForm from './ReportForm'
import History from './History'
import Catalog from './Catalog'
import AdminPanel from './AdminPanel'

type Profile = { user: User; demo: boolean; temporaryStorage: boolean; testChatConfigured: boolean }

export default function App() {
  const [me, setMe] = useState<Profile | null>(null)
  const [testMode, setTestMode] = useState(false)
  const [formBusy, setFormBusy] = useState(false)
  const generation = useRef(0)
  const [options, setOptions] = useState<Option[]>([])
  const [tab, setTab] = useState('form')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const [retry, setRetry] = useState(false)
  const loadOptions = useCallback(async () => { setOptions(await api<Option[]>('/options')) }, [])
  const load = useCallback(async () => {
    const current = ++generation.current
    setLoading(true); setError(''); setRetry(false)
    try {
      const [user, catalog] = await Promise.all([api<Profile>('/me'), api<Option[]>('/options')])
      if (current === generation.current) { setMe(user); setOptions(catalog) }
    } catch (e) { if (current === generation.current) { if (testMode && e instanceof ApiError && e.status === 403) { setTestModeHeader(false); setTestMode(false) } else { setError((e as Error).message); setRetry(e instanceof ApiError && e.status === 503) } } }
    finally { if (current === generation.current) setLoading(false) }
  }, [testMode])
  useEffect(() => { window.Telegram?.WebApp.ready(); window.Telegram?.WebApp.expand(); void load() }, [load])
  useEffect(() => {
    if (!retry || loading) return
    const timer = window.setTimeout(() => void load(), 3000)
    return () => window.clearTimeout(timer)
  }, [retry, loading, load])

  useEffect(() => {
    const timer = window.setInterval(() => { void api<Profile>('/me').then(setMe).catch(e => { if (testMode && e instanceof ApiError && e.status === 403) { setTestModeHeader(false); setTestMode(false) } }) }, 30000)
    return () => window.clearInterval(timer)
  }, [testMode])

  return <div className="app-shell">
    <header className="topbar"><div className="brand"><span className="brand-mark">A<span>•</span></span><div>APOLLO<small>СЕРВІСНИЙ ЖУРНАЛ</small></div></div><span className="topbar-meta">{me?.user.isAdmin ? 'Адміністратор' : 'Облік обладнання'}</span></header>
    <main>
      {(me?.demo || me?.temporaryStorage) && <div className="demo-banner">{me.demo ? 'Локальний деморежим · Повідомлення не надсилаються' : 'Тимчасове сховище'}{me.temporaryStorage && ' · Дані зникнуть після перезапуску'}</div>}
      {me?.user.isAdmin && <label className="field mode-selector">Режим Mini App<select aria-label="Режим Mini App" disabled={formBusy || loading} value={testMode ? 'test' : 'live'} onChange={e => { const enabled = e.target.value === 'test'; setTestModeHeader(enabled); setTestMode(enabled); setTab('form') }}><option value="live">Робочий</option><option value="test">Тестовий</option></select></label>}
      {testMode && <div className="demo-banner" role="status"><strong>ТЕСТОВИЙ РЕЖИМ</strong> · Окремі заявки, історія, CSV та нагадування. {me?.demo ? 'У деморежимі Telegram-повідомлення не надсилаються.' : me?.testChatConfigured ? 'Повідомлення надходитимуть лише в тестову групу.' : 'Для надсилання тестових заявок налаштуйте Telegram__TestGroupChatId в Azure.'}</div>}
      <section className="intro"><div className="eyebrow"><span className="status-dot" /> ОБЛІК НЕСПРАВНОСТЕЙ</div><h1>Техніка під контролем.</h1><p>Фіксуйте поломки, щоб швидше повертати обладнання до роботи.</p></section>
      {loading ? <div className="card empty" role="status">Завантаження форми…</div> : error ? <div className="card empty"><h2>Не вдалося відкрити форму</h2><p role="alert">{error}</p><button onClick={() => void load()}>Спробувати ще раз</button></div> : me && <>
        <nav className="tabs" aria-label="Розділи"><button className={tab === 'form' ? 'active' : ''} onClick={() => setTab('form')}><span>＋</span> Нова заявка</button><button className={tab === 'history' ? 'active' : ''} onClick={() => setTab('history')}>Усі заявки</button>{me.user.isAdmin && <button className={tab === 'catalog' ? 'active' : ''} onClick={() => setTab('catalog')}>Адміністрування</button>}</nav>
        <div hidden={tab !== 'form'}><ReportForm key={testMode ? "test" : "live"} testMode={testMode} onBusyChange={setFormBusy} user={me.user} options={options} reloadOptions={loadOptions} onHistory={() => setTab('history')} /></div>
        {tab === 'history' && <History key={testMode ? "test" : "live"} testMode={testMode} demo={me.demo} admin={me.user.isAdmin} />}
        {tab === 'catalog' && me.user.isAdmin && <><AdminPanel key={testMode ? "test" : "live"} testMode={testMode} currentUserId={me.user.id} />{!testMode && <Catalog options={options} reload={loadOptions} />}</>}
      </>}
      <footer><strong>APOLLO</strong><span>Єдиний журнал. Кожна поломка має значення.</span><span>Час журналу: Київ</span></footer>
    </main>
  </div>
}
