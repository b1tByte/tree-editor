<script setup lang="ts">
import { ref } from 'vue'
import Button from 'primevue/button'
import ConfirmDialog from 'primevue/confirmdialog'
import Toast from 'primevue/toast'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import { treeApi } from './api/treeApi'
import CachedTreeView from './components/CachedTreeView.vue'
import DbTreeView from './components/DbTreeView.vue'
import { useCacheStore } from './stores/cacheStore'
import { useDbTreeStore } from './stores/dbTreeStore'

const cache = useCacheStore()
const db = useDbTreeStore()
const confirm = useConfirm()
const toast = useToast()
const resetting = ref(false)

function confirmReset() {
  confirm.require({
    header: 'Reset',
    message: 'Restore the database to the initial sample data and clear the cache? Unapplied changes will be lost.',
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: 'Reset', severity: 'danger' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: reset,
  })
}

async function reset() {
  resetting.value = true
  try {
    await treeApi.reset()
    cache.clear()
    db.select(null)
    db.expandedKeys = {}
    await db.refresh()
    toast.add({ severity: 'success', summary: 'Reset', detail: 'Initial sample data restored.', life: 3000 })
  } catch (error) {
    toast.add({ severity: 'error', summary: 'Error', detail: (error as Error).message, life: 5000 })
  } finally {
    resetting.value = false
  }
}
</script>

<template>
  <Toast position="bottom-right" />
  <ConfirmDialog />

  <nav class="navbar bg-body border-bottom shadow-sm">
    <div class="container-xxl">
      <span class="navbar-brand d-flex align-items-center gap-2 mb-0">
        <img src="/favicon.svg" alt="" width="24" height="24" />
        Tree Editor
      </span>
      <Button
        label="Reset"
        icon="pi pi-refresh"
        severity="danger"
        size="small"
        outlined
        :loading="resetting"
        data-testid="reset"
        @click="confirmReset"
      />
    </div>
  </nav>

  <main class="container-xxl py-3">
    <div class="row g-3">
      <div class="col-12 col-lg-6">
        <DbTreeView />
      </div>
      <div class="col-12 col-lg-6">
        <CachedTreeView />
      </div>
    </div>
  </main>
</template>
