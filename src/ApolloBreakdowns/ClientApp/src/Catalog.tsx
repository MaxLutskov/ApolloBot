import { useEffect, useState } from 'react'
import { api, post, type Option } from './api'

const kinds: Record<string, string> = { site: 'Дільниці', shift: 'Зміни', person: 'Працівники', machine: 'Комбайни', part: 'Частини обладнання', node: 'Вузли', fault: 'Несправності', repairMethod: 'Виконання ремонту' }
type Editor = { id?: number; label: string; active: boolean; requiresDescription: boolean; requiresReserve: boolean; requiresRepairStart: boolean; isOther?: boolean; sortOrder: number; version?: string }
const empty = (): Editor => ({ label: '', active: true, requiresDescription: false, requiresReserve: false, requiresRepairStart: false, sortOrder: 0 })

export default function Catalog({ options, reload }: { options: Option[]; reload(): Promise<void> }) {
  const [kind, setKind] = useState('site')
  const [parent, setParent] = useState('')
  const [editor, setEditor] = useState<Editor>(empty)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [queue, setQueue] = useState<{ pending: number; delayed: number } | null>(null)
  const parentKind = kind === 'node' ? 'part' : kind === 'fault' ? 'node' : null
  const parents = options.filter(x => x.kind === parentKind)
  const visible = options.filter(x => x.kind === kind && (!parentKind || x.parentId === Number(parent))).sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id)
  const parentLabel = (x: Option) => {
    const part = options.find(o => o.id === x.parentId)
    return (part ? part.label + ' / ' : '') + x.label + (x.active ? '' : ' (вимкнено)')
  }
  useEffect(() => { void api<{ pending: number; delayed: number }>('/admin/notifications').then(setQueue).catch(() => {}) }, [])
  const clear = () => { setEditor(empty()); setError(''); setMessage(''); setConfirmDelete(false) }
  async function refresh() {
    setBusy(true); setError('')
    try { await reload(); clear(); setQueue(await api('/admin/notifications')) }
    catch (e) { setError((e as Error).message) }
    finally { setBusy(false) }
  }
  async function save(event: React.FormEvent) {
    event.preventDefault(); if (busy) return
    setBusy(true); setError(''); setMessage('')
    try {
      await api('/admin/options' + (editor.id ? `/${editor.id}` : ''), {
        ...post({ ...editor, kind, parentId: parentKind ? +parent : null }), method: editor.id ? 'PUT' : 'POST'
      })
      await reload(); setEditor(empty()); setMessage('Варіант збережено. Зміни доступні у формі.')
    } catch (e) { setError((e as Error).message) }
    finally { setBusy(false) }
  }
  async function remove() {
    if (!editor.id || busy) return
    setBusy(true); setError(''); setMessage('')
    try {
      await api(`/admin/options/${editor.id}?version=${editor.version}`, { method: 'DELETE' })
      await reload(); setEditor(empty()); setConfirmDelete(false); setMessage('Варіант і вкладені пункти видалено з довідника. Історію збережено.')
    } catch (e) { setError((e as Error).message) }
    finally { setBusy(false) }
  }
  return <section className="card"><div className="card-heading"><div><div className="eyebrow">АДМІНІСТРУВАННЯ</div><h2>Варіанти полів</h2></div><button className="secondary" disabled={busy} onClick={() => void refresh()}>Оновити</button></div>
    <p className="muted">Додавайте та змінюйте значення списків. Вимкнені варіанти зникнуть із нових форм. Назви у відправлених заявках зберігаються.</p>
    {queue && <div className="queue-status">Підтвердження Telegram: у черзі {queue.pending}, з повторними спробами {queue.delayed}.</div>}
    <div className="grid catalog-filters"><label className="field">Поле<select disabled={busy} value={kind} onChange={e => { setKind(e.target.value); setParent(''); clear() }}>{Object.entries(kinds).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></label>{parentKind && <label className="field">{parentKind === 'part' ? 'Частина обладнання' : 'Частина / вузол'}<select disabled={busy} value={parent} required onChange={e => { setParent(e.target.value); clear() }}><option value="">Оберіть батьківський елемент</option>{parents.map(x => <option key={x.id} value={x.id}>{parentLabel(x)}</option>)}</select></label>}</div>
    {!parentKind || parent ? <div className="catalog-layout"><div className="option-list">{visible.length === 0 ? <p className="muted">Ще немає варіантів. Додайте перший.</p> : visible.map(o => <button key={o.id} disabled={busy} className={'option-row ' + (editor.id === o.id ? 'selected' : '')} onClick={() => { setEditor({ ...o }); setConfirmDelete(false); setError(''); setMessage(''); setConfirmDelete(false) }}><span>{o.label}<small>{o.requiresDescription ? 'Потребує опису · ' : ''}{o.active ? 'Активний' : 'Вимкнений'}</small></span><span aria-hidden="true">↗</span></button>)}</div><form className="option-editor" onSubmit={save}><h3>{editor.id ? 'Редагування варіанта' : 'Новий варіант'}</h3><fieldset disabled={busy}><label className="field">Назва<input required maxLength={300} readOnly={editor.isOther} value={editor.label} onChange={e => setEditor(v => ({ ...v, label: e.target.value }))} placeholder="Введіть назву" /></label><label className="field">Порядок у списку<input type="number" required step={1} min={-100000} max={100000} value={editor.sortOrder} onChange={e => setEditor(v => ({ ...v, sortOrder: +e.target.value }))} /></label><label className="checkbox"><input type="checkbox" disabled={editor.isOther} checked={editor.active} onChange={e => setEditor(v => ({ ...v, active: e.target.checked }))} /> Доступний у формі</label>{['part', 'node', 'fault'].includes(kind) && <label className="checkbox"><input type="checkbox" checked={editor.requiresDescription} onChange={e => setEditor(v => ({ ...v, requiresDescription: e.target.checked }))} /> Обов’язковий опис</label>}{kind === 'repairMethod' && <label className="checkbox"><input type="checkbox" checked={editor.requiresReserve} onChange={e => setEditor(v => ({ ...v, requiresReserve: e.target.checked }))} /> Запитувати про резервне APOLLO</label>}{kind === 'repairMethod' && <label className="checkbox"><input type="checkbox" checked={editor.requiresRepairStart} onChange={e => setEditor(v => ({ ...v, requiresRepairStart: e.target.checked }))} /> Показувати кнопку «Почато ремонт» перед завершенням</label>}</fieldset><div className="actions"><button disabled={busy} type="submit">{busy ? 'Зберігаємо…' : 'Зберегти'}</button>{editor.id && !editor.isOther && <button type="button" className="danger" disabled={busy} onClick={() => setConfirmDelete(true)}>Видалити</button>}{editor.id && <button type="button" className="secondary" disabled={busy} onClick={clear}>Новий варіант</button>}</div>{confirmDelete && <div className="error-box" role="alert"><p>Видалити «{editor.label}» та всі вкладені пункти? Історія заявок збережеться.</p><div className="actions"><button type="button" className="danger" disabled={busy} onClick={() => void remove()}>Підтвердити видалення</button><button type="button" className="secondary" onClick={() => setConfirmDelete(false)}>Скасувати</button></div></div>}</form></div> : <p className="empty">Оберіть батьківський елемент для перегляду й додавання варіантів.</p>}
    {error && <div className="error-box" role="alert">{error}</div>}{message && <div className="success-box" role="status">{message}</div>}
  </section>
}
