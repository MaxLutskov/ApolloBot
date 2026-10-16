import { useCallback, useEffect, useState } from 'react'
import { api, type Option, type User } from './api'
import ReportForm from './ReportForm'
import History from './History'
import Catalog from './Catalog'

export default function App() {
  const [me, setMe] = useState<{ user: User; demo: boolean } | null>(null)
  const [options, setOptions] = useState<Option[]>([])
  const [tab, setTab] = useState('form')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const loadOptions = useCallback(async () => { setOptions(await api<Option[]>('/options')) }, [])
  const load = useCallback(async () => {
    setLoading(true); setError('')
    try {
      const [user, catalog] = await Promise.all([api<{ user: User; demo: boolean }>('/me'), api<Option[]>('/options')])
      setMe(user); setOptions(catalog)
    } catch (e) { setError((e as Error).message) }
    finally { setLoading(false) }
  }, [])
  useEffect(() => { window.Telegram?.WebApp.ready(); window.Telegram?.WebApp.expand(); void load() }, [load])

  return <div className="app-shell">
    <header className="topbar"><div className="brand"><span className="brand-mark">A<span>•</span></span><div>APOLLO<small>СЕРВІСНИЙ ЖУРНАЛ</small></div></div><span className="topbar-meta">{me?.user.isAdmin ? 'Адміністратор' : 'Облік обладнання'}</span></header>
    <main>
      {me?.demo && <div className="demo-banner">Демо-режим · Дані зникнуть після перезапуску · Повідомлення не надсилаються</div>}
      <section className="intro"><div className="eyebrow"><span className="status-dot" /> ОБЛІК НЕСПРАВНОСТЕЙ</div><h1>Техніка під контролем.</h1><p>Фіксуйте поломки, щоб швидше повертати обладнання до роботи.</p></section>
      {loading ? <div className="card empty" role="status">Завантаження форми…</div> : error ? <div className="card empty"><h2>Не вдалося відкрити форму</h2><p role="alert">{error}</p><button onClick={() => void load()}>Спробувати ще раз</button></div> : me && <>
        <nav className="tabs" aria-label="Розділи"><button className={tab === 'form' ? 'active' : ''} onClick={() => setTab('form')}><span>＋</span> Нова заявка</button><button className={tab === 'history' ? 'active' : ''} onClick={() => setTab('history')}>{me.user.isAdmin ? 'Журнал заявок' : 'Мої заявки'}</button>{me.user.isAdmin && <button className={tab === 'catalog' ? 'active' : ''} onClick={() => setTab('catalog')}>Довідники</button>}</nav>
        <div hidden={tab !== 'form'}><ReportForm user={me.user} options={options} reloadOptions={loadOptions} onHistory={() => setTab('history')} /></div>
        {tab === 'history' && <History admin={me.user.isAdmin} />}
        {tab === 'catalog' && me.user.isAdmin && <Catalog options={options} reload={loadOptions} />}
      </>}
      <footer><strong>APOLLO</strong><span>Єдиний журнал. Кожна поломка має значення.</span><span>Час журналу: Київ</span></footer>
    </main>
  </div>
}
