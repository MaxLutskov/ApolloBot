import { useEffect, useRef, useState } from 'react'
import { api, ApiError, post, dateText, type Option, type User } from './api'
import { available, changeField, cascadeOther, missingFields } from './form.mjs'

type Form = { requestType: string; requestId: string; siteId: string; shiftId: string; person: string; machineId: string; partId: string; nodeId: string; faultId: string; description: string; repairMethodId: string; hasReserveApollo: string }
const blank = (): Form => ({ requestType: '', requestId: crypto.randomUUID(), siteId: '', shiftId: '', person: '', machineId: '', partId: '', nodeId: '', faultId: '', description: '', repairMethodId: '', hasReserveApollo: '' })
type Receipt = { requestType: string; id: number; requestId: string; createdAt: string }

export default function ReportForm({ user, options, reloadOptions, onHistory, testMode, onBusyChange }: { testMode: boolean; onBusyChange(busy: boolean): void; user: User; options: Option[]; reloadOptions(): Promise<void>; onHistory(): void }) {
  const draftKey = `apollo-draft-${user.id}-${testMode ? "test" : "live"}`
  const [form, setForm] = useState<Form>(() => {
    try { const saved = JSON.parse(sessionStorage.getItem(draftKey) ?? (!testMode ? sessionStorage.getItem(`apollo-draft-${user.id}`) : null) ?? 'null'); return saved && typeof saved.requestId === 'string' ? { ...blank(), ...saved } : blank() } catch { return blank() }
  })
  const [busy, setBusy] = useState(false)
  useEffect(() => { onBusyChange(busy) }, [busy, onBusyChange])
  const locked = useRef(false)
  const [error, setError] = useState('')
  const [validated, setValidated] = useState(false)
  const errorSummary = useRef<HTMLDivElement>(null)
  const missing = missingFields(form, options) as { field: string; label: string }[]
  const invalidField = (name: string) => validated && missing.some(x => x.field === name)
  const [receipt, setReceipt] = useState<Receipt | null>(null)
  useEffect(() => { try { sessionStorage.setItem(draftKey, JSON.stringify(form)) } catch { /* Storage may be disabled in WebView. */ } }, [form, draftKey])
  const set = (name: keyof Form, value: string) => { setError(''); setForm(f => changeField(f, name, value, crypto.randomUUID(), options)) }
  useEffect(() => { setForm(f => cascadeOther(f, options)) }, [options])
  const needsDescription = options.some(o => [form.partId, form.nodeId, form.faultId].includes(String(o.id)) && o.requiresDescription)
  const needsReserve = form.requestType === 'emergency'
  const select = (name: keyof Form, label: string, kind: string, placeholder: string, parent?: string) => {
    const list = available(options, kind, parent) as Option[]
    const optionValue = (option: Option) => name === 'person' ? option.label : String(option.id)
    const stale = form[name] && !list.some(x => optionValue(x) === form[name])
    return <label className="field">{label} <span className="required">*</span><select name={name} aria-invalid={invalidField(name)} value={form[name]} required disabled={busy || (parent !== undefined && (!parent || options.some(x => x.id === Number(parent) && x.isOther)))} onChange={e => set(name, e.target.value)}><option value="">{placeholder}</option>{stale && <option value={form[name]} disabled>Варіант недоступний — оберіть інший</option>}{list.map(x => <option key={x.id} value={optionValue(x)}>{x.label}</option>)}</select>{invalidField(name) && <small className="error">Заповніть поле «{label}».</small>}{stale && <small className="error">Оберіть доступний варіант.</small>}</label>
  }
  const reset = () => { setForm(blank()); setError(''); setReceipt(null); setValidated(false) }
  async function submit(event: React.FormEvent) {
    event.preventDefault()
    if (locked.current) return
    setValidated(true)
    if (missing.length) {
      setError('Заповніть обов’язкові поля: ' + missing.map(x => x.label).join('; ') + '.')
      window.setTimeout(() => { errorSummary.current?.scrollIntoView({ behavior: 'smooth', block: 'center' }); errorSummary.current?.focus() }, 0)
      return
    }
    locked.current = true; setBusy(true); setError('')
    try {
      const result = await api<Receipt>('/breakdowns', post({ ...form, siteId: +form.siteId, shiftId: +form.shiftId, machineId: +form.machineId, partId: +form.partId, nodeId: +form.nodeId, faultId: +form.faultId, repairMethodId: +form.repairMethodId, hasReserveApollo: needsReserve && form.hasReserveApollo ? form.hasReserveApollo === 'yes' : null }))
      setReceipt(result); setForm(blank()); window.Telegram?.WebApp.HapticFeedback?.notificationOccurred('success')
    } catch (e) {
      setError((e as Error).message)
      if (e instanceof ApiError && e.status === 409) await reloadOptions().catch(() => {})
    } finally { locked.current = false; setBusy(false) }
  }
  if (receipt) return <section className="card receipt" aria-live="polite"><div className="success-icon">✓</div><div className="eyebrow">{testMode ? 'ЗБЕРЕЖЕНО В ТЕСТОВОМУ ЖУРНАЛІ' : 'ЗБЕРЕЖЕНО В ЖУРНАЛІ'}</div><h2>Заявку №{receipt.id} отримано</h2><p>{receipt.requestType === 'productivity' ? 'Зниження продуктивності зафіксовано без нарахування простою.' : 'Відлік простою машини розпочато.'} {testMode ? 'Тестове повідомлення надійде в окрему групу.' : 'В особистий чат надійде підтвердження.'} Стан заявки можна змінити у спільному журналі або в повідомленні групи APOLLO.</p><div className="receipt-details"><span>Зареєстровано<strong>{dateText(receipt.createdAt)}</strong></span><span>Код заявки<strong>{receipt.requestId.slice(0, 8)}</strong></span></div><div className="next-request"><p>Створіть також заявку в іншому боті:</p><a className="bot-link" href="https://t.me/mhp_sm_bot" target="_blank" rel="noopener noreferrer" onClick={event => { if (window.Telegram?.WebApp.openTelegramLink) { event.preventDefault(); window.Telegram.WebApp.openTelegramLink('https://t.me/mhp_sm_bot') } }}>Перейти до MHP SM — створити іншу заявку ↗</a></div><div className="actions"><button onClick={reset}>Зареєструвати ще одну</button><button className="secondary" onClick={onHistory}>Відкрити журнал</button></div></section>

  return <div className="form-layout"><form className="card report-form" onSubmit={submit} noValidate>
    <div className="card-heading"><div><div className="eyebrow">НОВА РЕЄСТРАЦІЯ</div><h2>Повідомити про поломку</h2></div><span className="badge">APOLLO</span></div>
    {error && <div ref={errorSummary} tabIndex={-1} className="error-box" role="alert">{error}</div>}
    <label className="field request-type">Тип заявки <span className="required">*</span><select name="requestType" aria-invalid={invalidField('requestType')} value={form.requestType} required disabled={busy} onChange={e => set('requestType', e.target.value)}><option value="">Оберіть тип заявки</option><option value="emergency">Аварійна</option><option value="productivity">Зниження продуктивності</option></select>{invalidField('requestType') && <small className="error">Оберіть тип заявки.</small>}</label>
    <fieldset disabled={busy}><legend><span>01</span> Місце та відповідальний</legend><div className="grid">
      {select('siteId', 'Дільниця', 'site', 'Оберіть дільницю')}
      {select('shiftId', 'Зміна', 'shift', 'Оберіть зміну')}
      {select('person', 'Відповідальний працівник', 'person', 'Оберіть працівника')}
      {select('machineId', 'Комбайн', 'machine', 'Оберіть APOLLO')}
    </div></fieldset>
    <fieldset disabled={busy}><legend><span>02</span> Що сталося</legend><div className="grid">
      {select('partId', 'Частина обладнання', 'part', 'Оберіть частину')}
      {select('nodeId', 'Вузол', 'node', form.partId ? 'Оберіть вузол' : 'Спочатку оберіть частину', form.partId)}
      <div className="wide">{select('faultId', 'Несправність / вид робіт', 'fault', form.nodeId ? 'Оберіть несправність' : 'Спочатку оберіть вузол', form.nodeId)}</div>
      <label className="field wide">Додатковий опис {needsDescription ? <span className="required">*</span> : <span className="optional">необов’язково</span>}<textarea name="description" aria-invalid={invalidField("description")} value={form.description} required={needsDescription} maxLength={4000} placeholder="Що сталося, за яких обставин, що вже зроблено…" onChange={e => set('description', e.target.value)} />{invalidField("description") && <small className="error">Додайте опис несправності.</small>}<small>{form.description.length} / 4000</small></label>
    </div></fieldset>
    <fieldset disabled={busy}><legend><span>03</span> Виконання ремонту</legend><div className="grid">
      {select('repairMethodId', 'Виконання ремонту', 'repairMethod', 'Оберіть виконавця ремонту')}
      {needsReserve && <label className="field">Чи є резервне APOLLO? <span className="required">*</span><select name="hasReserveApollo" aria-invalid={invalidField("hasReserveApollo")} required value={form.hasReserveApollo} onChange={e => set('hasReserveApollo', e.target.value)}><option value="">Оберіть відповідь</option><option value="yes">Так</option><option value="no">Ні</option></select>{invalidField("hasReserveApollo") && <small className="error">Оберіть «Так» або «Ні».</small>}</label>}
    </div><div className="timer-note">{form.requestType === 'productivity' ? 'Для зниження продуктивності простій машини та процесу не нараховується.' : 'Простій машини: від першої аварійної заявки до закриття останньої для цього APOLLO, мінімум 10 хв. Простій процесу: за наявності резерву — 20 хв, без резерву — до завершення ремонту будь-якого APOLLO, мінімум 10 хв.'}</div></fieldset>

    <div className="actions"><button disabled={busy} type="submit">{busy ? 'Зберігаємо…' : 'Надіслати заявку'} <span aria-hidden="true">↗</span></button><button disabled={busy} type="button" className="secondary" onClick={reset}>Очистити</button></div><p className="form-note">Після збереження ви отримаєте номер заявки та підтвердження.</p>
  </form><aside><div className="guide"><div className="guide-icon" aria-hidden="true">↗</div><div className="eyebrow">ШВИДКА РЕЄСТРАЦІЯ</div><h3>Від поломки —<br />до запису в журналі.</h3><p>Оберіть обладнання та несправність зі списку. Кожен наступний список залежить від попереднього.</p><div className="guide-divider" /><strong>Не знайшли несправність?</strong><p>Оберіть «Інша несправність» і коротко опишіть ситуацію.</p><div className="guide-divider" /><small>Ви увійшли як</small><div className="user-name">{user.name || `Користувач ${user.id}`}</div></div><p className="aside-note">Чернетка зберігається в цій вкладці під час заповнення.</p></aside></div>
}
