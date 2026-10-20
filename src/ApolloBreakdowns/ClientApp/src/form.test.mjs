import { test } from 'node:test'
import assert from 'node:assert/strict'
import { available, changeField, durationText, missingFields, cascadeOther } from './form.mjs'

test('changing parent clears dependent values and changes idempotency key', () => {
  const next = changeField({ partId: '1', nodeId: '2', faultId: '3' }, 'partId', '4', 'new-id')
  assert.equal(next.nodeId, ''); assert.equal(next.faultId, ''); assert.equal(next.requestId, 'new-id')
})
test('changing repair method clears hidden reserve answer', () => {
  assert.equal(changeField({ hasReserveApollo: 'yes' }, 'repairMethodId', '1', 'new').hasReserveApollo, '')
})
test('catalog hides disabled entries and isolates parent nodes', () => {
  const options = [{ id: 1, kind: 'node', active: true, parentId: 1 }, { id: 2, kind: 'node', active: false, parentId: 1 }, { id: 3, kind: 'node', active: true, parentId: 2 }]
  assert.deepEqual(available(options, 'node', '1').map(x => x.id), [1])
})
test('duration formatting covers first minute and hour boundary', () => {
  assert.equal(durationText(5), 'менше 1 хв'); assert.equal(durationText(900), '15 хв'); assert.equal(durationText(3660), '1 год 1 хв')
})

test('Other part selects Other node and fault automatically', () => {
  const options = [{ id: 10, kind: 'part', isOther: true, active: true }, { id: 11, kind: 'node', parentId: 10, isOther: true, active: true }, { id: 12, kind: 'fault', parentId: 11, isOther: true, active: true }]
  const next = changeField({ partId: '', nodeId: '', faultId: '' }, 'partId', '10', 'new', options)
  assert.equal(next.nodeId, '11'); assert.equal(next.faultId, '12')
})
test('Other node selects only its own Other fault', () => {
  const options = [{ id: 11, kind: 'node', parentId: 10, isOther: true }, { id: 12, kind: 'fault', parentId: 11, isOther: true, active: true }, { id: 13, kind: 'fault', parentId: 99, isOther: true, active: true }]
  assert.equal(cascadeOther({ partId: '10', nodeId: '11', faultId: '' }, options).faultId, '12')
})
test('empty form reports every missing field with its label', () => {
  const form = { person: '', description: '', hasReserveApollo: '' }
  const missing = missingFields(form, [])
  assert.equal(missing.length, 8)
  assert.ok(missing.some(x => x.field === 'person' && x.label.includes('працівник')))
  assert.ok(missing.some(x => x.field === 'nodeId' && x.label === 'Вузол'))
})
test('validation names required description and reserve answer', () => {
  const form = { person: 'Працівник', partId: '1', repairMethodId: '2', description: ' ', hasReserveApollo: '' }
  const options = [{ id: 1, kind: 'part', requiresDescription: true, active: true }, { id: 2, kind: 'repairMethod', requiresReserve: true, active: true }]
  const missing = missingFields(form, options)
  assert.ok(missing.some(x => x.field === 'description'))
  assert.ok(missing.some(x => x.field === 'hasReserveApollo'))
})
