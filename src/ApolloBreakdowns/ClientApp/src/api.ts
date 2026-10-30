export type Option = { id: number; kind: string; parentId: number | null; label: string; active: boolean; requiresDescription: boolean; requiresReserve: boolean; requiresRepairStart: boolean; isOther: boolean; sortOrder: number; version: string }
export type User = { id: number; name: string; isAdmin: boolean }
export type Report = { machineDowntimeGroupId: string | null; machineDowntimeStartedAt: string | null; machineRestoredAt: string | null; machineRestoredByBreakdownId: number | null; machineDowntimeRunning: boolean; isTest: boolean; requestType: string; processRestoredAt: string | null; processRestoredByBreakdownId: number | null; processDowntimeSeconds: number; processDowntimeRunning: boolean; id: number; site: string; shift: string; person: string; machine: string; part: string; node: string; fault: string; description: string; repairMethod: string; hasReserveApollo: boolean | null; createdAt: string; requiresRepairStart: boolean; repairStartedAt: string | null; repairSeconds: number | null; canStartRepair: boolean; restoredAt: string | null; restoredByUserId: number | null; downtimeSeconds: number; canRestore: boolean }
export type Page = { items: Report[]; total: number; page: number; pageSize: number }
type TelegramWebApp = { initData: string; ready(): void; expand(): void; openTelegramLink?(url: string): void; requestWriteAccess?(callback: (granted: boolean) => void): void; HapticFeedback?: { notificationOccurred(type: string): void } }
declare global { interface Window { Telegram?: { WebApp: TelegramWebApp } } }

let testModeHeader = false
export function setTestModeHeader(enabled: boolean) { testModeHeader = enabled }

export class ApiError extends Error { constructor(message: string, public status: number) { super(message) } }
export function headers() {
  const result: Record<string, string> = { 'X-Telegram-Init-Data': window.Telegram?.WebApp.initData ?? '' }
  if (testModeHeader) result['X-Apollo-Test-Mode'] = 'true'
  const demo = new URLSearchParams(location.search).get('demoUser')
  if (demo) result['X-Demo-User-Id'] = demo
  return result
}
export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try { response = await fetch('/api/app' + path, { ...init, headers: { ...headers(), ...(init?.body ? { 'Content-Type': 'application/json' } : {}), ...init?.headers } }) }
  catch { throw new ApiError('Немає зв’язку із сервером. Дані форми збережено. Спробуйте ще раз.', 0) }
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new ApiError(body?.message ?? (response.status === 403 ? 'Ця дія доступна лише адміністратору.' : response.status === 429 ? 'Забагато запитів. Зачекайте хвилину.' : 'Не вдалося виконати запит.'), response.status)
  }
  return response.json()
}
export const post = (body: unknown) => ({ method: 'POST', body: JSON.stringify(body) })
export const dateText = (value: string) => new Date(value).toLocaleString('uk-UA', { timeZone: 'Europe/Kyiv', dateStyle: 'medium', timeStyle: 'short' })
