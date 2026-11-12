export type Role = 'Viewer' | 'Approver'

export interface Session {
  token: string
  userName: string
  displayName: string
  role: Role
  expiresAtUtc: string
}

export interface DashboardSummary {
  total: number
  success: number
  errors: number
  gaveUp: number
  pendingProposals: number
  errorsByCode: { code: string; count: number }[]
  last7Days: { day: string; success: number; errors: number }[]
}

export interface ErrorItem {
  id: number
  externalId: string
  status: string
  attempts: number
  errorCode: string | null
  errorDetail: string | null
  message: string | null
  updatedAtUtc: string
}

export interface Page<T> {
  items: T[]
  total: number
  pageNumber: number
  pageSize: number
}

export interface Proposal {
  id: number
  erpStatus: string
  suggestedStoreStatus: string
  reason: string
  source: string
  affectedOrders: number
  state: 'Pending' | 'Approved' | 'Rejected'
  decisionNote: string | null
  decidedBy: string | null
  createdAtUtc: string
}

export interface AssistantAnswer {
  answer: string
  toolsUsed: string[]
  modelCalls: number
  budgetExhausted: boolean
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message)
  }
}

let token: string | null = null
export const setToken = (value: string | null) => {
  token = value
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init.headers,
    },
  })

  const body = response.headers.get('content-type')?.includes('json') ? await response.json() : null

  if (!response.ok && response.status !== 429) {
    const message =
      body?.detail ??
      (response.status === 401 ? 'Sessão expirada. Entre de novo.' : response.status === 403 ? 'Seu perfil não pode fazer isso.' : 'Algo deu errado.')
    throw new ApiError(response.status, message)
  }

  return body as T
}

const post = <T>(path: string, data?: unknown) => request<T>(path, { method: 'POST', body: data === undefined ? undefined : JSON.stringify(data) })

export const api = {
  login: (userName: string, password: string) => post<Session>('/api/auth/login', { userName, password }),
  dashboard: () => request<DashboardSummary>('/api/dashboard'),
  errors: (code: string, page: number) =>
    request<Page<ErrorItem>>(`/api/errors?page=${page}&pageSize=10${code ? `&code=${encodeURIComponent(code)}` : ''}`),
  reprocess: (id: number) => post<{ message: string }>(`/api/errors/${id}/reprocess`),
  proposals: () => request<Proposal[]>('/api/proposals'),
  storeStatuses: () => request<string[]>('/api/store-statuses'),
  approve: (id: number, storeStatus: string, note: string) =>
    post<{ message: string; reopenedOrders: number }>(`/api/proposals/${id}/approve`, { storeStatus, note }),
  reject: (id: number, note: string) => post<{ message: string }>(`/api/proposals/${id}/reject`, { note }),
  ask: (question: string) => post<AssistantAnswer>('/api/assistant/ask', { question }),
}
