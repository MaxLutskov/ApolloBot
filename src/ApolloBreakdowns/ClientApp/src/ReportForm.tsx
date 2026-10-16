import { useEffect, useRef, useState } from 'react'
import { api, ApiError, post, dateText, type Option, type User } from './api'
import { available, changeField } from './form.mjs'

type Form = { requestId: string; siteId: string; shiftId: string; person: string; machineId: string; partId: string; nodeId: string; faultId: string; description: string; repairMethodId: string; hasReserveApollo: string }
const blank = (): Form => ({ requestId: crypto.randomUUID(), siteId: '', shiftId: '', person: '', machineId: '', partId: '', nodeId: '', faultId: '', description: '', repairMethodId: '', hasReserveApollo: '' })
type Receipt = { id: number; requestId: string; createdAt: string }

export default function ReportForm({ user, options, reloadOptions, onHistory }: { user: User; options: Option[]; reloadOptions(): Promise<void>; onHistory(): void }) {
  const draftKey = `apollo-draft-${user.id}`
  const [form, setForm] = useState<Form>(() => {
    try { const saved = JSON.parse(sessionStorage.getItem(draftKey) ?? 'null'); return saved && typeof saved.requestId === 'string' ? { ...blank(), ...saved } : blank() } catch { return blank() }
  })
  const [busy, setBusy] = useState(false)
  const locked = useRef(false)
  const [error, setError] = useState('')
  const [receipt, setReceipt] = useState<Receipt | null>(null)
  useEffect(() => { try { sessionStorage.setItem(draftKey, JSON.stringify(form)) } catch { /* Storage may be disabled in WebView. */ } }, [form, draftKey])
  const set = (name: keyof Form, value: string) => { setError(''); setForm(f => changeField(f, name, value, crypto.randomUUID())) }
  const needsDescription = options.some(o => [form.partId, form.nodeId, form.faultId].includes(String(o.id)) && o.requiresDescription)
  const needsReserve = options.some(o => o.id === +form.repairMethodId && o.requiresReserve)
  const select = (name: keyof Form, label: string, kind: string, placeholder: string, parent?: string) => {
    const list = available(options, kind, parent) as Option[]
    const stale = form[name] && !list.some(x => String(x.id) === form[name])
    return <label className="field">{label} <span className="required">*</span><select value={form[name]} required disabled={busy || (parent !== undefined && !parent)} onChange={e => set(name, e.target.value)}><option value="">{placeholder}</option>{stale && <option value={form[name]} disabled>Варіант недоступний — оберіть інший</option>}{list.map(x => <option key={x.id} value={x.id}>{x.label}</option>)}</select>{stale && <small className="error">Оберіть доступний варіант.</small>}</label>
  }
  const reset = () => { setForm(blank()); setError(''); setReceipt(null) }
  async function submit(event: React.FormEvent) {
    event.preventDefault()
    if (locked.current) return
    const invalid = ['siteId', 'shiftId', 'machineId', 'partId', 'nodeId', 'faultId', 'repairMethodId'].some(key => !options.some(o => o.id === Number(form[key as keyof Form]) && o.active))
    if (invalid) { setError('Довідник змінився. Перевірте вибрані варіанти.'); return }
    locked.current = true; setBusy(true); setError('')
    try {
      const result = await api<Receipt>('/breakdowns', post({ ...form, siteId: +form.siteId, shiftId: +form.shiftId, machineId: +form.machineId, partId: +form.partId, nodeId: +form.nodeId, faultId: +form.faultId, repairMethodId: +form.repairMethodId, hasReserveApollo: needsReserve && form.hasReserveApollo ? form.hasReserveApollo === 'yes' : null }))
      setReceipt(result); setForm(blank()); window.Telegram?.WebApp.HapticFeedback?.notificationOccurred('success')
    } catch (e) {
      setError((e as Error).message)
      if (e instanceof ApiError && e.status === 409) await reloadOptions().catch(() => {})
    } finally { locked.current = false; setBusy(false) }
  }
  if (receipt) return <section className="card receipt" aria-live="polite"><div className="success-icon">✓</div><div className="eyebrow">ЗБЕРЕЖЕНО В ЖУРНАЛІ</div><h2>Заявку №{receipt.id} отримано</h2><p>Відлік простою розпочато. Повідомлення надійде вам і в групу APOLLO. Після ремонту натисніть у повідомленні «Роботу відновлено».</p><div className="receipt-details"><span>Початок простою<strong>{dateText(receipt.createdAt)}</strong></span><span>Код заявки<strong>{receipt.requestId.slice(0, 8)}</strong></span></div><div className="actions"><button onClick={reset}>Зареєструвати ще одну</button><button className="secondary" onClick={onHistory}>Відкрити журнал</button></div></section>

  return <div className="form-layout"><form className="card report-form" onSubmit={submit}>
    <div className="card-heading"><div><div className="eyebrow">НОВА РЕЄСТРАЦІЯ</div><h2>Повідомити про поломку</h2></div><span className="badge">APOLLO</span></div>
    <fieldset disabled={busy}><legend><span>01</span> Місце та відповідальний</legend><div className="grid">
      {select('siteId', 'Дільниця', 'site', 'Оберіть дільницю')}
      {select('shiftId', 'Зміна', 'shift', 'Оберіть зміну')}
      <label className="field">Відповідальний працівник <span className="required">*</span><input list="people" autoComplete="name" value={form.person} minLength={2} maxLength={300} required placeholder="Прізвище та ім’я" onChange={e => set('person', e.target.value)} /><datalist id="people">{options.filter(o => o.kind === 'person' && o.active).map(o => <option key={o.id} value={o.label} />)}</datalist></label>
      {select('machineId', 'Комбайн', 'machine', 'Оберіть APOLLO')}
    </div></fieldset>
    <fieldset disabled={busy}><legend><span>02</span> Що сталося</legend><div className="grid">
      {select('partId', 'Частина обладнання', 'part', 'Оберіть частину')}
      {select('nodeId', 'Вузол', 'node', form.partId ? 'Оберіть вузол' : 'Спочатку оберіть частину', form.partId)}
      <div className="wide">{select('faultId', 'Несправність / вид робіт', 'fault', form.nodeId ? 'Оберіть несправність' : 'Спочатку оберіть вузол', form.nodeId)}</div>
      <label className="field wide">Додатковий опис {needsDescription ? <span className="required">*</span> : <span className="optional">необов’язково</span>}<textarea value={form.description} required={needsDescription} maxLength={4000} placeholder="Що сталося, за яких обставин, що вже зроблено…" onChange={e => set('description', e.target.value)} /><small>{form.description.length} / 4000</small></label>
    </div></fieldset>
    <fieldset disabled={busy}><legend><span>03</span> Виконання ремонту</legend><div className="grid">
      {select('repairMethodId', 'Виконання ремонту', 'repairMethod', 'Оберіть виконавця ремонту')}
      {needsReserve && <label className="field">Чи є резервне APOLLO? <span className="required">*</span><select required value={form.hasReserveApollo} onChange={e => set('hasReserveApollo', e.target.value)}><option value="">Оберіть відповідь</option><option value="yes">Так</option><option value="no">Ні</option></select></label>}
    </div><div className="timer-note">◷ Простій рахується автоматично: від надсилання заявки до натискання «Роботу відновлено».</div></fieldset>
    {error && <div className="error-box" role="alert">{error}</div>}
    <div className="actions"><button disabled={busy} type="submit">{busy ? 'Зберігаємо…' : 'Надіслати заявку'} <span aria-hidden="true">↗</span></button><button disabled={busy} type="button" className="secondary" onClick={reset}>Очистити</button></div><p className="form-note">Після збереження ви отримаєте номер заявки та підтвердження.</p>
  </form><aside><div className="guide"><div className="guide-icon" aria-hidden="true">↗</div><div className="eyebrow">ШВИДКА РЕЄСТРАЦІЯ</div><h3>Від поломки —<br />до запису в журналі.</h3><p>Оберіть обладнання та несправність зі списку. Кожен наступний список залежить від попереднього.</p><div className="guide-divider" /><strong>Не знайшли несправність?</strong><p>Оберіть «Інша несправність» і коротко опишіть ситуацію.</p><div className="guide-divider" /><small>Ви увійшли як</small><div className="user-name">{user.name || `Користувач ${user.id}`}</div></div><p className="aside-note">Чернетка зберігається в цій вкладці під час заповнення.</p></aside></div>
}
