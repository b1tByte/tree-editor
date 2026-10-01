<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Button from 'primevue/button'
import Tree from 'primevue/tree'
import type { TreeNode } from 'primevue/treenode'
import { useToast } from 'primevue/usetoast'
import { treeApi } from '../api/treeApi'
import { useCacheStore } from '../stores/cacheStore'
import { useDbTreeStore } from '../stores/dbTreeStore'

const db = useDbTreeStore()
const cache = useCacheStore()
const toast = useToast()
const loadingIntoCache = ref(false)

const selectionKeys = computed({
  get: () => (db.selectedId === null ? {} : { [String(db.selectedId)]: true }),
  set: (keys: Record<string, boolean>) => {
    // Clicking the selected node again keeps it selected.
    const key = Object.keys(keys).find((k) => keys[k])
    if (key !== undefined) db.select(Number(key))
  },
})

const selectedInCache = computed(() => db.selectedId !== null && cache.has(db.selectedId))

onMounted(async () => {
  try {
    await db.loadRoots()
  } catch (error) {
    showError(error)
  }
})

async function onExpand(node: TreeNode) {
  try {
    await db.loadChildren(Number(node.key))
  } catch (error) {
    showError(error)
  }
}

/** Loads the selected element from the database into the cache. */
async function loadIntoCache(id = db.selectedId) {
  if (id === null) return
  if (cache.has(id)) {
    cache.select(id)
    toast.add({ severity: 'info', summary: 'Already cached', detail: 'This element is already in the cache.', life: 2500 })
    return
  }

  loadingIntoCache.value = true
  try {
    cache.addLoaded(await treeApi.getNode(id))
    cache.select(id)
  } catch (error) {
    showError(error)
  } finally {
    loadingIntoCache.value = false
  }
}

function showError(error: unknown) {
  toast.add({ severity: 'error', summary: 'Error', detail: (error as Error).message, life: 5000 })
}
</script>

<template>
  <div class="card h-100 shadow-sm">
    <div class="card-header d-flex align-items-center gap-2 flex-wrap">
      <i class="pi pi-database text-secondary" aria-hidden="true" />
      <h2 class="h6 mb-0 me-auto">DBTreeView</h2>
      <Button
        label="Load into cache"
        icon="pi pi-arrow-right"
        icon-pos="right"
        size="small"
        :disabled="db.selectedId === null || selectedInCache"
        :loading="loadingIntoCache"
        data-testid="load-into-cache"
        @click="loadIntoCache()"
      />
    </div>
    <div class="card-body p-2 tree-scroll">
      <Tree
        v-model:selection-keys="selectionKeys"
        v-model:expanded-keys="db.expandedKeys"
        :value="db.tree"
        selection-mode="single"
        :meta-key-selection="false"
        :loading="db.loadingRoots"
        loading-mode="icon"
        class="p-0 border-0"
        @node-expand="onExpand"
      >
        <template #default="{ node }">
          <span
            class="node-label"
            :class="{ 'node-deleted': node.data.isDeleted }"
            :data-testid="`db-node-${node.key}`"
            @dblclick="loadIntoCache(Number(node.key))"
          >
            <span class="node-text">{{ node.label }}</span>
            <span class="node-id">#{{ node.key }}</span>
            <span v-if="node.data.isDeleted" class="badge text-bg-danger ms-1">deleted</span>
            <i v-if="cache.has(Number(node.key))" class="pi pi-check-circle text-success ms-1" title="In cache" />
          </span>
        </template>
        <template #empty>
          <span class="text-muted small">The database is empty.</span>
        </template>
      </Tree>
    </div>
    <div class="card-footer small text-muted">
      Expand nodes to load them on demand. Double-click a node to load it into the cache.
    </div>
  </div>
</template>
