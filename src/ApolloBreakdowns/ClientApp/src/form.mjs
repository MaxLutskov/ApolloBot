export function localNow() {
  const d = new Date()
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16)
}
export function available(options, kind, parentId) {
  return options.filter(x => x.active && x.kind === kind && (parentId === undefined || x.parentId === Number(parentId)))
}
export function changeField(form, name, value, requestId, options = []) {
  const next = { ...form, [name]: value, requestId }
  if (name === 'partId') { next.nodeId = ''; next.faultId = '' }
  if (name === 'nodeId') next.faultId = ''
  if (name === 'requestType') next.hasReserveApollo = ''
  return cascadeOther(next, options)
}
export function durationText(seconds) {
  if (seconds < 60) return 'менше 1 хв'
  const minutes = Math.floor(seconds / 60)
  return minutes < 60 ? `${minutes} хв` : `${Math.floor(minutes / 60)} год ${minutes % 60} хв`
}

export function cascadeOther(form, options) {
  const next = { ...form }
  if (options.some(x => x.id === Number(next.partId) && x.isOther))
    next.nodeId = String(options.find(x => x.kind === 'node' && x.parentId === Number(next.partId) && x.isOther && x.active)?.id ?? '')
  if (options.some(x => x.id === Number(next.nodeId) && x.isOther))
    next.faultId = String(options.find(x => x.kind === 'fault' && x.parentId === Number(next.nodeId) && x.isOther && x.active)?.id ?? '')
  return next.nodeId === form.nodeId && next.faultId === form.faultId ? form : next
}

export function missingFields(form, options) {
  const fields = [['siteId', 'Дільниця', 'site'], ['shiftId', 'Зміна', 'shift'], ['machineId', 'Комбайн', 'machine'],
    ['partId', 'Частина обладнання', 'part'], ['nodeId', 'Вузол', 'node'], ['faultId', 'Несправність / вид робіт', 'fault'], ['repairMethodId', 'Виконання ремонту', 'repairMethod']]
  const result = fields.filter(([key, , kind]) => !options.some(x => x.id === Number(form[key]) && x.kind === kind && x.active)).map(([field, label]) => ({ field, label }))
  if (!['emergency', 'productivity'].includes(form.requestType)) result.unshift({ field: 'requestType', label: 'Тип заявки' })
  if (!options.some(x => x.kind === 'person' && x.active && x.label === form.person)) result.unshift({ field: 'person', label: 'Відповідальний працівник' })
  if (options.some(x => [form.partId, form.nodeId, form.faultId].includes(String(x.id)) && x.requiresDescription) && !form.description.trim())
    result.push({ field: 'description', label: 'Додатковий опис' })
  if (form.requestType === 'emergency' && !['yes', 'no'].includes(form.hasReserveApollo))
    result.push({ field: 'hasReserveApollo', label: 'Чи є резервне APOLLO?' })
  return result
}

export const downtimeText = seconds => durationText(Math.max(10, Math.round(seconds / 60)) * 60)

export function reportDowntimeText(report, elapsed = 0, process = false) {
  if (report.requestType === 'productivity') return '0 хв'
  if (process && report.hasReserveApollo === true) return '20 хв'
  const seconds = process ? report.processDowntimeSeconds : report.downtimeSeconds
  const running = process ? report.processDowntimeRunning : (report.machineDowntimeRunning ?? !report.restoredAt)
  return downtimeText(seconds + (running ? elapsed : 0))
}
