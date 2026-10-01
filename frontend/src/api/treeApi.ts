/** A tree node as returned by the API. */
export interface NodeDto {
  id: number
  parentId: number | null
  value: string
  isDeleted: boolean
  /** Ancestor ids ordered from the root down to the parent. */
  ancestorIds: number[]
  hasChildren: boolean
}

export interface AddedNode {
  /** Negative temporary id assigned by the cache. */
  tempId: number
  /** Database id (positive) or temporary id of another new node (negative). */
  parentId: number
  value: string
}

export interface UpdatedNode {
  id: number
  value: string
}

export interface ApplyRequest {
  added: AddedNode[]
  updated: UpdatedNode[]
  deleted: number[]
  cachedIds: number[]
}

export interface ApplyResponse {
  idMap: { tempId: number; id: number }[]
  skipped: number[]
  nodes: NodeDto[]
  insertedCount: number
  updatedCount: number
  deletedCount: number
}

export class ApiError extends Error {}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(url, {
      ...init,
      headers: { 'Content-Type': 'application/json', ...init?.headers },
    })
  } catch {
    throw new ApiError('The API is not reachable.')
  }

  if (!response.ok) {
    let message = `Request failed with status ${response.status}.`
    try {
      const problem = await response.json()
      message = problem.detail ?? problem.title ?? message
    } catch {
      // Not a JSON problem response; keep the generic message.
    }
    throw new ApiError(message)
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

export const treeApi = {
  getRoots: () => request<NodeDto[]>('/api/nodes/roots'),
  getChildren: (id: number) => request<NodeDto[]>(`/api/nodes/${id}/children`),
  getNode: (id: number) => request<NodeDto>(`/api/nodes/${id}`),
  apply: (body: ApplyRequest) =>
    request<ApplyResponse>('/api/nodes/apply', { method: 'POST', body: JSON.stringify(body) }),
  reset: () => request<void>('/api/reset', { method: 'POST' }),
}
