export function localNow() {
  const d = new Date()
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16)
}
export function available(options, kind, parentId) {
  return options.filter(x => x.active && x.kind === kind && (parentId === undefined || x.parentId === Number(parentId)))
}
export function changeField(form, name, value, requestId) {
  const next = { ...form, [name]: value, requestId }
  if (name === 'partId') { next.nodeId = ''; next.faultId = '' }
  if (name === 'nodeId') next.faultId = ''
  if (name === 'repairMethodId') next.hasReserveApollo = ''
  return next
}
export function durationText(seconds) {
  if (seconds < 60) return 'менше 1 хв'
  const minutes = Math.floor(seconds / 60)
  return minutes < 60 ? `${minutes} хв` : `${Math.floor(minutes / 60)} год ${minutes % 60} хв`
}
