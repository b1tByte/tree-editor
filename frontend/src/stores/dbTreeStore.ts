import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { treeApi, type NodeDto } from '../api/treeApi'

/** View model of a database node, shaped for the PrimeVue Tree. */
export interface DbTreeItem {
  key: string
  label: string
  leaf: boolean
  loading: boolean
  data: NodeDto
  children?: DbTreeItem[]
}

/**
 * State of DBTreeView. The tree is loaded lazily: roots first, then the children
 * of a node only when the user expands it.
 */
export const useDbTreeStore = defineStore('dbTree', () => {
  const nodes = ref(new Map<number, NodeDto>())
  const rootIds = ref<number[]>([])
  /** Children ids of nodes whose children were already loaded. */
  const childIds = ref(new Map<number, number[]>())
  const loadingIds = ref(new Set<number>())
  const expandedKeys = ref<Record<string, boolean>>({})
  const selectedId = ref<number | null>(null)
  const loadingRoots = ref(false)

  const selectedNode = computed(() =>
    selectedId.value === null ? null : (nodes.value.get(selectedId.value) ?? null),
  )

  const tree = computed<DbTreeItem[]>(() => rootIds.value.map(toItem))

  function toItem(id: number): DbTreeItem {
    const node = nodes.value.get(id)!
    const children = childIds.value.get(id)
    return {
      key: String(id),
      label: node.value,
      leaf: !node.hasChildren,
      loading: loadingIds.value.has(id),
      data: node,
      children: children?.map(toItem),
    }
  }

  function store(list: NodeDto[]) {
    for (const node of list) nodes.value.set(node.id, node)
  }

  async function loadRoots() {
    loadingRoots.value = true
    try {
      const roots = await treeApi.getRoots()
      store(roots)
      rootIds.value = roots.map((r) => r.id)
    } finally {
      loadingRoots.value = false
    }
  }

  /** Loads the children of a node once; called when the node is expanded. */
  async function loadChildren(id: number, force = false) {
    if (!force && childIds.value.has(id)) return
    loadingIds.value.add(id)
    try {
      const children = await treeApi.getChildren(id)
      store(children)
      childIds.value.set(
        id,
        children.map((c) => c.id),
      )
    } finally {
      loadingIds.value.delete(id)
    }
  }

  /**
   * Reloads everything that is currently visible (roots and expanded nodes),
   * e.g. after Apply or Reset. Expanded nodes that no longer exist are collapsed.
   */
  async function refresh() {
    const expanded = new Set(
      Object.keys(expandedKeys.value).filter((k) => expandedKeys.value[k]).map(Number),
    )

    // Build the new state aside and swap it in at once, so the view never sees a half-loaded tree.
    const freshNodes = new Map<number, NodeDto>()
    const freshChildIds = new Map<number, number[]>()
    const roots = await treeApi.getRoots()
    roots.forEach((r) => freshNodes.set(r.id, r))

    // Reload level by level so that a node is known before its children are requested.
    let level = roots.map((r) => r.id).filter((id) => expanded.has(id))
    while (level.length > 0) {
      const results = await Promise.all(level.map((id) => treeApi.getChildren(id)))
      level.forEach((id, i) => {
        const children = results[i]!
        children.forEach((c) => freshNodes.set(c.id, c))
        freshChildIds.set(
          id,
          children.map((c) => c.id),
        )
      })
      level = level.flatMap((id) => freshChildIds.get(id) ?? []).filter((id) => expanded.has(id))
    }

    nodes.value = freshNodes
    childIds.value = freshChildIds
    rootIds.value = roots.map((r) => r.id)
    expandedKeys.value = Object.fromEntries([...freshChildIds.keys()].map((id) => [String(id), true]))
    if (selectedId.value !== null && !freshNodes.has(selectedId.value)) selectedId.value = null
  }

  function select(id: number | null) {
    selectedId.value = id
  }

  return {
    tree,
    expandedKeys,
    selectedId,
    selectedNode,
    loadingRoots,
    loadRoots,
    loadChildren,
    refresh,
    select,
  }
})
