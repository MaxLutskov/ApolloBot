import { test } from 'node:test'
import assert from 'node:assert/strict'
import { available, changeField, durationText } from './form.mjs'

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
