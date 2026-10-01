<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Tree from 'primevue/tree'
import { useToast } from 'primevue/usetoast'
import { treeApi } from '../api/treeApi'
import { MAX_VALUE_LENGTH, useCacheStore } from '../stores/cacheStore'
import { useDbTreeStore } from '../stores/dbTreeStore'

const cache = useCacheStore()
const db = useDbTreeStore()
const toast = useToast()
const applying = ref(false)

// All cached nodes are expanded unless the user collapses them.
const collapsedKeys = ref(new Set<string>())
const expandedKeys = computed({
  get: () => Object.fromEntries(cache.allKeys.filter((k) => !collapsedKeys.value.has(k)).map((k) => [k, true])),
  set: (keys: Record<string, boolean>) => {
    collapsedKeys.value = new Set(cache.allKeys.filter((k) => !keys[k]))
  },
})

const selectionKeys = computed({
  get: () => (cache.selectedId === null ? {} : { [String(cache.selectedId)]: true }),
  set: (keys: Record<string, boolean>) => {
    // Clicking the selected node again keeps it selected.
    const key = Object.keys(keys).find((k) => keys[k])
    if (key !== undefined) cache.select(Number(key))
  },
})

const canEdit = computed(() => cache.selectedNode !== null && !cache.selectedIsDeleted)

// ----- value dialog (used for both "Edit" and "Add child") -----------------

const dialog = ref<{ mode: 'edit' | 'add'; value: string } | null>(null)
const valueInput = ref<{ $el: HTMLInputElement } | null>(null)
const dialogError = computed(() => {
  const value = dialog.value?.value.trim() ?? ''
  if (!value) return 'Value must not be empty.'
  if (value.length > MAX_VALUE_LENGTH) return `Value must not be longer than ${MAX_VALUE_LENGTH} characters.`
  return null
})

function openEdit() {
  if (!canEdit.value) return
  dialog.value = { mode: 'edit', value: cache.selectedNode!.value }
}

function openAdd() {
  if (!canEdit.value) return
  dialog.value = { mode: 'add', value: '' }
}

async function focusInput() {
  await nextTick()
  valueInput.value?.$el.focus()
  valueInput.value?.$el.select()
}

function submitDialog() {
  if (!dialog.value || dialogError.value || cache.selectedId === null) return
  try {
    if (dialog.value.mode === 'edit') {
      cache.updateValue(cache.selectedId, dialog.value.value)
    } else {
      const parentKey = String(cache.selectedId)
      const id = cache.addChild(cache.selectedId, dialog.value.value)
      collapsedKeys.value.delete(parentKey)
      cache.select(id)
    }
    dialog.value = null
  } catch (error) {
    showError(error)
  }
}

// ----- actions --------------------------------------------------------------

function deleteSelected() {
  if (!canEdit.value) return
  try {
    cache.remove(cache.selectedId!)
  } catch (error) {
    showError(error)
  }
}

async function apply() {
  applying.value = true
  try {
    const response = await treeApi.apply(cache.buildApplyRequest())
    cache.applyResult(response)

    const parts = [
      `${response.insertedCount} added`,
      `${response.updatedCount} updated`,
      `${response.deletedCount} deleted`,
    ]
    toast.add({ severity: 'success', summary: 'Changes applied', detail: parts.join(', '), life: 3500 })
    if (response.skipped.length > 0) {
      toast.add({
        severity: 'warn',
        summary: 'Some elements were not added',
        detail: `${response.skipped.length} new element(s) were dropped because their parent was deleted.`,
        life: 6000,
      })
    }
  } catch (error) {
    showError(error)
  } finally {
    applying.value = false
  }

  try {
    await db.refresh()
  } catch (error) {
    showError(error)
  }
}

function onKeydown(event: KeyboardEvent) {
  if (dialog.value) return
  if (event.key === 'F2') openEdit()
  else if (event.key === 'Delete') deleteSelected()
  else if (event.key === 'Insert') openAdd()
}

function showError(error: unknown) {
  toast.add({ severity: 'error', summary: 'Error', detail: (error as Error).message, life: 5000 })
}
</script>

<template>
  <div class="card h-100 shadow-sm">
    <div class="card-header d-flex align-items-center gap-2 flex-wrap">
      <i class="pi pi-box text-secondary" aria-hidden="true" />
      <h2 class="h6 mb-0 me-auto">CachedTreeView</h2>
      <span
        v-if="cache.pendingChanges.total > 0"
        class="badge rounded-pill text-bg-warning"
        data-testid="pending-count"
        :title="`${cache.pendingChanges.added} added, ${cache.pendingChanges.updated} edited, ${cache.pendingChanges.deleted} deleted`"
      >
        {{ cache.pendingChanges.total }} pending
      </span>
    </div>

    <div class="px-3 py-2 border-bottom d-flex align-items-center gap-2 flex-wrap">
      <Button label="Add child" icon="pi pi-plus" size="small" severity="secondary" outlined
              :disabled="!canEdit" data-testid="add-child" @click="openAdd" />
      <Button label="Edit" icon="pi pi-pencil" size="small" severity="secondary" outlined
              :disabled="!canEdit" data-testid="edit" @click="openEdit" />
      <Button label="Delete" icon="pi pi-trash" size="small" severity="danger" outlined
              :disabled="!canEdit" data-testid="delete" @click="deleteSelected" />
      <Button label="Apply" icon="pi pi-check" size="small" class="ms-auto"
              :disabled="cache.pendingChanges.total === 0" :loading="applying"
              data-testid="apply" @click="apply" />
    </div>

    <div class="card-body p-2 tree-scroll" tabindex="-1" @keydown="onKeydown">
      <Tree
        v-model:selection-keys="selectionKeys"
        v-model:expanded-keys="expandedKeys"
        :value="cache.tree"
        selection-mode="single"
        :meta-key-selection="false"
        class="p-0 border-0"
      >
        <template #default="{ node }">
          <span
            class="node-label"
            :class="{ 'node-deleted': node.data.isDeleted }"
            :data-testid="`cache-node-${node.key}`"
            @dblclick="openEdit"
          >
            <span
              v-if="node.data.hiddenLevels > 0"
              class="hidden-levels"
              :title="`${node.data.hiddenLevels} intermediate level(s) not loaded into the cache`"
            >…</span>
            <span class="node-text">{{ node.label }}</span>
            <span v-if="!node.data.isNew" class="node-id">#{{ node.key }}</span>
            <span v-if="node.data.isDeleted" class="badge text-bg-danger ms-1">deleted</span>
            <span v-else-if="node.data.isNew" class="badge text-bg-success ms-1">new</span>
            <span v-else-if="node.data.isModified" class="badge text-bg-warning ms-1">edited</span>
          </span>
        </template>
        <template #empty>
          <span class="text-muted small">The cache is empty. Select an element in DBTreeView and load it.</span>
        </template>
      </Tree>
    </div>

    <div class="card-footer small text-muted">
      Changes stay in the cache until you click Apply. Shortcuts: F2 edit, Insert add child, Delete delete.
    </div>

    <Dialog
      :visible="dialog !== null"
      modal
      :header="dialog?.mode === 'add' ? 'Add child element' : 'Edit element'"
      :style="{ width: '26rem' }"
      :breakpoints="{ '576px': '92vw' }"
      @update:visible="(v: boolean) => { if (!v) dialog = null }"
      @show="focusInput"
    >
      <form v-if="dialog" class="d-flex flex-column gap-2" @submit.prevent="submitDialog">
        <label for="node-value" class="form-label mb-0">Value</label>
        <InputText
          id="node-value"
          ref="valueInput"
          v-model="dialog.value"
          :invalid="dialogError !== null && dialog.value !== ''"
          :maxlength="MAX_VALUE_LENGTH"
          autocomplete="off"
          data-testid="value-input"
        />
        <small v-if="dialogError && dialog.value" class="text-danger">{{ dialogError }}</small>
        <div class="d-flex justify-content-end gap-2 mt-2">
          <Button label="Cancel" severity="secondary" text @click="dialog = null" />
          <Button type="submit" :label="dialog.mode === 'add' ? 'Add' : 'Save'"
                  :disabled="dialogError !== null" data-testid="dialog-submit" />
        </div>
      </form>
    </Dialog>
  </div>
</template>
