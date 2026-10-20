import { useCallback, useEffect, useState } from 'react'
import { api, dateText, post } from './api'

type BotUser = { id: number; name: string; isAdmin: boolean; firstSeenAt: string; lastSeenAt: string; version: string }
type Reminder = { id: number; time: string; active: boolean; version: string }

export default function AdminPanel({ currentUserId }: { currentUserId: number }) {
  const [users, setUsers] = useState<BotUser[]>([])
  const [reminders, setReminders] = useState<Reminder[]>([])
  const [time, setTime] = useState('07:00')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [search, setSearch] = useState('')
  const load = useCallback(async () => {
    const [people, schedule] = await Promise.all([api<BotUser[]>('/admin/users'), api<Reminder[]>('/admin/reminders')])
    setUsers(people); setReminders(schedule)
  }, [])
  useEffect(() => { void load().catch(e => setError(e.message)) }, [load])
  async function perform(action: () => Promise<unknown>) {
    if (busy) return
    setBusy(true); setError('')
    try { await action(); await load() } catch (e) { setError((e as Error).message) }
    finally { setBusy(false) }
  }
  return <>
    <section className="card admin-section"><div className="card-heading"><h2>Користувачі бота <span className="count">{users.length}</span></h2><button className="secondary" disabled={busy} onClick={() => void perform(load)}>Оновити</button></div>
      <p className="muted">Користувачі, які відкрили форму, написали боту або натиснули кнопку заявки. Зміна прав діє для наступних запитів.</p>
      <label className="field">Пошук користувача<input value={search} onChange={e => setSearch(e.target.value)} placeholder="Ім’я або Telegram ID" /></label>
      <div className="option-list">{users.filter(u => `${u.name} ${u.id}`.toLowerCase().includes(search.toLowerCase())).map(u => <div className="admin-row" key={u.id}><div><strong>{u.name || u.id}</strong><small>ID: {u.id} · {u.isAdmin ? 'Адміністратор' : 'Користувач'}</small><small>Перший вхід: {dateText(u.firstSeenAt)} · Остання активність: {dateText(u.lastSeenAt)}</small></div><button disabled={busy || u.id === currentUserId} className="secondary" onClick={() => void perform(() => api(`/admin/users/${u.id}/admin`, { ...post({ isAdmin: !u.isAdmin, version: u.version }), method: 'PUT' }))}>{u.id === currentUserId ? 'Ваш обліковий запис' : u.isAdmin ? 'Забрати адмін-права' : 'Надати адмін-права'}</button></div>)}</div>
    </section>
    <section className="card admin-section"><h2>Нагадування у групу</h2><p className="muted">Щодня за часом Києва. Можна додати декілька розсилок. Якщо відкритих заявок немає — повідомлення не надсилається. Потрібно налаштувати групу APOLLO.</p>
      <form className="actions" onSubmit={e => { e.preventDefault(); void perform(() => api('/admin/reminders', post({ time, active: true }))) }}><label className="field">Час (Київ)<input type="time" required value={time} onChange={e => setTime(e.target.value)} /></label><button disabled={busy}>Додати розсилку</button></form>
      <div className="option-list">{reminders.map(r => <div className="admin-row" key={r.id}><label className="field">Час (Київ)<input aria-label={`Час розсилки ${r.id}`} type="time" value={r.time} onChange={e => setReminders(current => current.map(item => item.id === r.id ? { ...item, time: e.target.value } : item))} /></label><span>{r.active ? 'Увімкнено' : 'Вимкнено'}</span><div className="actions compact"><button disabled={busy} className="secondary" onClick={() => void perform(() => api(`/admin/reminders/${r.id}`, { ...post(r), method: 'PUT' }))}>Зберегти час</button><button disabled={busy} className="secondary" onClick={() => void perform(() => api(`/admin/reminders/${r.id}`, { ...post({ ...r, active: !r.active }), method: 'PUT' }))}>{r.active ? 'Вимкнути' : 'Увімкнути'}</button><button disabled={busy} className="danger" onClick={() => void perform(() => api(`/admin/reminders/${r.id}?version=${r.version}`, { method: 'DELETE' }))}>Видалити</button></div></div>)}</div>
      {!reminders.length && <p className="muted">Розсилок ще немає.</p>}
    </section>
    {error && <div role="alert" className="error-box">{error}</div>}
  </>
}
