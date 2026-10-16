import { useCallback, useEffect, useState } from 'react'
import { api, dateText, headers, type Page } from './api'
import { durationText } from './form.mjs'

export default function History({ admin }: { admin: boolean }) {
  const [data, setData] = useState<Page | null>(null)
  const [page, setPage] = useState(1)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [exporting, setExporting] = useState(false)
  const [restoring, setRestoring] = useState<number | null>(null)
  const [elapsed, setElapsed] = useState(0)
  const load = useCallback(async () => {
    setBusy(true); setError('')
    try { setData(await api<Page>(`/breakdowns?page=${page}`)); setElapsed(0) }
    catch (e) { setError((e as Error).message) }
    finally { setBusy(false) }
  }, [page])
  useEffect(() => { void load() }, [load])
  useEffect(() => { const timer = setInterval(() => setElapsed(s => s + 1), 1000); return () => clearInterval(timer) }, [])
  useEffect(() => { const timer = setInterval(() => { void load() }, 30000); return () => clearInterval(timer) }, [load])
  async function restore(id: number) {
    if (restoring !== null) return
    setRestoring(id); setError('')
    try { await api(`/breakdowns/${id}/restore`, { method: 'POST' }); await load() }
    catch (e) { setError((e as Error).message) }
    finally { setRestoring(null) }
  }
  async function download() {
    setExporting(true); setError('')
    try {
      const response = await fetch('/api/app/admin/export', { headers: headers() })
      if (!response.ok) throw new Error('Не вдалося експортувати журнал. Перевірте доступ та повторіть спробу.')
      const url = URL.createObjectURL(await response.blob())
      const link = document.createElement('a'); link.href = url; link.download = 'Поломки_APOLLO.csv'; link.click()
      setTimeout(() => URL.revokeObjectURL(url), 60000)
    } catch (e) { setError((e as Error).message) }
    finally { setExporting(false) }
  }
  return <section className="card history"><div className="card-heading"><div><div className="eyebrow">ІСТОРІЯ РЕЄСТРАЦІЙ</div><h2>{admin ? 'Журнал заявок' : 'Мої заявки'} {data && <span className="count">{data.total}</span>}</h2></div><div className="actions compact"><button disabled={busy} className="secondary" onClick={() => void load()}>Оновити</button>{admin && <button disabled={exporting} className="secondary" onClick={() => void download()}>{exporting ? 'Експорт…' : 'CSV ↓'}</button>}</div></div>
    {error && <div role="alert" className="error-box">{error}</div>}
    {busy && <p role="status">Завантаження заявок…</p>}
    {!busy && data?.items.length === 0 && <div className="empty"><div className="empty-icon">≡</div><h3>Журнал поки порожній</h3><p>Відправлені заявки з’являться тут разом із номером і датою.</p></div>}
    <div className="report-list" aria-busy={busy}>{data?.items.map(r => <article key={r.id} className="report-row"><div className="report-top"><strong>#{r.id} <span>· {r.machine}</span></strong><time>{dateText(r.createdAt)}</time></div><div className={"report-state " + (r.restoredAt ? "closed" : "")}>{r.restoredAt ? "Роботу відновлено" : "Простій триває"}</div><h3>{r.fault}</h3><p>{r.part} <span className="muted">/</span> {r.node}</p><div className="report-meta"><span>{r.site}</span><span>Зміна {r.shift}</span><span>{r.person}</span><span className="downtime">◷ {durationText(r.downtimeSeconds + (r.restoredAt ? 0 : elapsed))} простою</span></div><p>Ремонт: {r.repairMethod}{r.hasReserveApollo !== null && <> · Резервне APOLLO: {r.hasReserveApollo ? "Так" : "Ні"}</>}</p>{r.description && <p className="description">{r.description}</p>}<small className="muted">Зареєстровано: {dateText(r.createdAt)}</small>{r.restoredAt && <p>Відновлено: {dateText(r.restoredAt)}</p>}{r.canRestore && <div><button className="restore-button" disabled={restoring !== null} onClick={() => void restore(r.id)}>{restoring === r.id ? "Зберігаємо…" : "✓ Роботу відновлено"}</button></div>}</article>)}</div>
    {data && data.total > 20 && <div className="pagination"><button className="secondary" disabled={page === 1 || busy} onClick={() => setPage(p => p - 1)}>← Назад</button><span>{page} / {Math.ceil(data.total / 20)}</span><button className="secondary" disabled={page * 20 >= data.total || busy} onClick={() => setPage(p => p + 1)}>Далі →</button></div>}
  </section>
}
