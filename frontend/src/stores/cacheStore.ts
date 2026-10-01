import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type { ApplyRequest, ApplyResponse, NodeDto } from '../api/treeApi'

/** An element held in the local cache. */
export interface CacheNode {
  /** Database id (positive) or temporary id of a new node (negative). */
  id: number
  parentId: number | null
  /** All ancestor ids from the root down to the parent (may contain temporary ids). */
  ancestorIds: number[]
  value: string
  /** Value last read from the database; null for new nodes. */
  originalValue: string | null
  /** The node is deleted in the database. */
  deletedInDb: boolean
  /** The user deleted this node in the cache; not applied yet. */
  pendingDelete: boolean
}

/** View model of a cached node, shaped for the PrimeVue Tree. */
export interface CachedTreeItem {
  key: string
  label: string
  leaf: boolean
  data: {
    id: number
    isNew: boolean
    isModified: boolean
    isDeleted: boolean
    /** Number of ancestor levels between this node and its displayed parent that are not cached. */
    hiddenLevels: number
  }
  children: CachedTreeItem[]
}

export type AddResult = 'added' | 'exists'

export const useCacheStore = defineStore('cache', () => {
  const nodes = ref(new Map<number, CacheNode>())
  const selectedId = ref<number | null>(null)
  let nextTempId = -1

  // ----- derived state ------------------------------------------------------

  const pendingDeleteIds = computed(
    () => new Set([...nodes.value.values()].filter((n) => n.pendingDelete).map((n) => n.id)),
  )

  /** A node is deleted if it, or any of its ancestors, is deleted (in the DB or pending). */
  function isDeleted(node: CacheNode): boolean {
    if (node.deletedInDb || node.pendingDelete) return true
    const pending = pendingDeleteIds.value
    return node.ancestorIds.some((id) => pending.has(id))
  }

  function isNew(node: CacheNode): boolean {
    return node.id < 0
  }

  function isModified(node: CacheNode): boolean {
    return !isNew(node) && node.originalValue !== node.value
  }

  const selectedNode = computed(() =>
    selectedId.value === null ? null : (nodes.value.get(selectedId.value) ?? null),
  )

  const selectedIsDeleted = computed(() => (selectedNode.value ? isDeleted(selectedNode.value) : false))

  /**
   * Cached nodes arranged into a hierarchy. Each node is placed under its nearest cached
   * ancestor, so nodes loaded separately and in any order end up in the correct place.
   * Nodes without a cached ancestor are shown as top-level items.
   */
  const tree = computed<CachedTreeItem[]>(() => {
    const items = new Map<number, CachedTreeItem>()
    for (const node of nodes.value.values()) {
      items.set(node.id, {
        key: String(node.id),
        label: node.value,
        leaf: true,
        data: {
          id: node.id,
          isNew: isNew(node),
          isModified: isModified(node),
          isDeleted: isDeleted(node),
          hiddenLevels: 0,
        },
        children: [],
      })
    }

    const roots: CachedTreeItem[] = []
    for (const node of nodes.value.values()) {
      const item = items.get(node.id)!
      let parent: CachedTreeItem | undefined
      for (let i = node.ancestorIds.length - 1; i >= 0; i--) {
        parent = items.get(node.ancestorIds[i]!)
        if (parent) {
          item.data.hiddenLevels = node.ancestorIds.length - 1 - i
          break
        }
      }
      if (parent) {
        parent.children.push(item)
        parent.leaf = false
      } else {
        roots.push(item)
      }
    }

    const sortTree = (list: CachedTreeItem[]) => {
      list.sort(compareItems)
      list.forEach((i) => sortTree(i.children))
    }
    sortTree(roots)
    return roots
  })

  const allKeys = computed(() => [...nodes.value.keys()].map(String))

  const pendingChanges = computed(() => {
    let added = 0
    let updated = 0
    let deleted = 0
    for (const node of nodes.value.values()) {
      const deletedNow = isDeleted(node)
      if (isNew(node)) {
        if (!deletedNow) added++
      } else if (node.pendingDelete && !node.deletedInDb) {
        deleted++
      } else if (!deletedNow && isModified(node)) {
        updated++
      }
    }
    return { added, updated, deleted, total: added + updated + deleted }
  })

  // ----- actions ------------------------------------------------------------

  /** Puts an element loaded from the database into the cache. */
  function addLoaded(dto: NodeDto): AddResult {
    if (nodes.value.has(dto.id)) return 'exists'
    nodes.value.set(dto.id, {
      id: dto.id,
      parentId: dto.parentId,
      ancestorIds: [...dto.ancestorIds],
      value: dto.value,
      originalValue: dto.value,
      deletedInDb: dto.isDeleted,
      pendingDelete: false,
    })
    return 'added'
  }

  function has(id: number): boolean {
    return nodes.value.has(id)
  }

  function select(id: number | null) {
    selectedId.value = id !== null && nodes.value.has(id) ? id : null
  }

  function requireEditable(id: number): CacheNode {
    const node = nodes.value.get(id)
    if (!node) throw new Error(`Node ${id} is not in the cache.`)
    if (isDeleted(node)) throw new Error('Deleted elements cannot be changed.')
    return node
  }

  function updateValue(id: number, value: string) {
    const node = requireEditable(id)
    node.value = normalizeValue(value)
  }

  /** Creates a new child in the cache and returns its temporary id. */
  function addChild(parentId: number, value: string): number {
    const parent = requireEditable(parentId)
    const id = nextTempId--
    nodes.value.set(id, {
      id,
      parentId,
      ancestorIds: [...parent.ancestorIds, parentId],
      value: normalizeValue(value),
      originalValue: null,
      deletedInDb: false,
      pendingDelete: false,
    })
    return id
  }

  /** Marks the node (and therefore its whole subtree) as deleted. */
  function remove(id: number) {
    const node = requireEditable(id)
    node.pendingDelete = true
  }

  /** Collects pending changes for the Apply request. */
  function buildApplyRequest(): ApplyRequest {
    const all = [...nodes.value.values()]
    const pending = pendingDeleteIds.value

    const added = all
      .filter((n) => isNew(n) && !isDeleted(n))
      // Parents first: a parent always has a shorter ancestor chain than its children.
      .sort((a, b) => a.ancestorIds.length - b.ancestorIds.length || b.id - a.id)
      .map((n) => ({ tempId: n.id, parentId: n.parentId!, value: n.value }))

    const updated = all
      .filter((n) => !isNew(n) && !isDeleted(n) && isModified(n))
      .map((n) => ({ id: n.id, value: n.value }))

    // Only the topmost deleted nodes are needed: the server deletes whole subtrees.
    const deleted = all
      .filter(
        (n) => !isNew(n) && n.pendingDelete && !n.deletedInDb && !n.ancestorIds.some((a) => pending.has(a)),
      )
      .map((n) => n.id)

    const cachedIds = all.filter((n) => !isNew(n)).map((n) => n.id)

    return { added, updated, deleted, cachedIds }
  }

  /** Synchronizes the cache with the result of a successful Apply. */
  function applyResult(response: ApplyResponse) {
    const idMap = new Map(response.idMap.map((m) => [m.tempId, m.id]))
    const remap = (id: number) => idMap.get(id) ?? id
    const fresh = new Map(response.nodes.map((n) => [n.id, n]))

    const next = new Map<number, CacheNode>()
    for (const node of nodes.value.values()) {
      // New nodes that were deleted before Apply, or skipped by the server, are dropped.
      if (isNew(node) && !idMap.has(node.id)) continue

      const id = remap(node.id)
      const dto = fresh.get(id)
      if (!dto) continue

      next.set(id, {
        id,
        parentId: dto.parentId,
        ancestorIds: [...dto.ancestorIds],
        value: dto.value,
        originalValue: dto.value,
        deletedInDb: dto.isDeleted,
        pendingDelete: false,
      })
    }

    nodes.value = next
    selectedId.value = selectedId.value === null ? null : remap(selectedId.value)
    if (selectedId.value !== null && !next.has(selectedId.value)) selectedId.value = null
  }

  function clear() {
    nodes.value = new Map()
    selectedId.value = null
    nextTempId = -1
  }

  return {
    nodes,
    selectedId,
    selectedNode,
    selectedIsDeleted,
    tree,
    allKeys,
    pendingChanges,
    has,
    select,
    addLoaded,
    updateValue,
    addChild,
    remove,
    buildApplyRequest,
    applyResult,
    clear,
  }
})

export const MAX_VALUE_LENGTH = 200

function normalizeValue(value: string): string {
  const trimmed = value.trim()
  if (!trimmed) throw new Error('Value must not be empty.')
  if (trimmed.length > MAX_VALUE_LENGTH) throw new Error(`Value must not be longer than ${MAX_VALUE_LENGTH} characters.`)
  return trimmed
}

/** Existing nodes by id, then new nodes in creation order. */
function compareItems(a: CachedTreeItem, b: CachedTreeItem): number {
  const x = a.data.id
  const y = b.data.id
  if (x > 0 && y > 0) return x - y
  if (x < 0 && y < 0) return y - x
  return x > 0 ? -1 : 1
}
