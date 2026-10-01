import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import type { NodeDto } from '../src/api/treeApi'
import { useCacheStore, type CachedTreeItem } from '../src/stores/cacheStore'

/** Sample chain: 1 Catalog > 2 Electronics > 3 Computers > 4 Laptops > 5 Business */
function dto(id: number, ancestorIds: number[], value = `Node ${id}`, isDeleted = false): NodeDto {
  return {
    id,
    parentId: ancestorIds.at(-1) ?? null,
    value,
    isDeleted,
    ancestorIds,
    hasChildren: true,
  }
}

const catalog = dto(1, [])
const electronics = dto(2, [1])
const computers = dto(3, [1, 2])
const laptops = dto(4, [1, 2, 3])
const business = dto(5, [1, 2, 3, 4])
const books = dto(20, [1])

/** Compact representation of the tree: "id(children...)". */
function shape(items: CachedTreeItem[]): string {
  return items.map((i) => (i.children.length ? `${i.data.id}(${shape(i.children)})` : `${i.data.id}`)).join(',')
}

describe('cache store', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('places separately loaded elements into the correct hierarchy regardless of load order', () => {
    const cache = useCacheStore()
    cache.addLoaded(laptops)
    cache.addLoaded(business)
    expect(shape(cache.tree)).toBe('4(5)')

    cache.addLoaded(catalog)
    // Catalog is an ancestor of Laptops; two levels in between are not cached.
    expect(shape(cache.tree)).toBe('1(4(5))')
    expect(cache.tree[0]!.children[0]!.data.hiddenLevels).toBe(2)

    cache.addLoaded(electronics)
    cache.addLoaded(books)
    expect(shape(cache.tree)).toBe('1(2(4(5)),20)')

    cache.addLoaded(computers)
    expect(shape(cache.tree)).toBe('1(2(3(4(5))),20)')
    expect(cache.tree[0]!.children[0]!.children[0]!.children[0]!.data.hiddenLevels).toBe(0)
  })

  it('does not load the same element twice', () => {
    const cache = useCacheStore()
    expect(cache.addLoaded(laptops)).toBe('added')
    cache.updateValue(4, 'Notebooks')
    expect(cache.addLoaded(laptops)).toBe('exists')
    expect(cache.nodes.get(4)!.value).toBe('Notebooks')
  })

  it('keeps edits, additions and deletions as pending changes', () => {
    const cache = useCacheStore()
    cache.addLoaded(electronics)
    cache.addLoaded(books)
    cache.addLoaded(laptops)

    cache.updateValue(2, '  Gadgets  ')
    const poetry = cache.addChild(20, 'Poetry')
    const haiku = cache.addChild(poetry, 'Haiku')
    cache.remove(4)

    expect(cache.pendingChanges).toEqual({ added: 2, updated: 1, deleted: 1, total: 4 })
    expect(cache.buildApplyRequest()).toEqual({
      added: [
        { tempId: poetry, parentId: 20, value: 'Poetry' },
        { tempId: haiku, parentId: poetry, value: 'Haiku' },
      ],
      updated: [{ id: 2, value: 'Gadgets' }],
      deleted: [4],
      cachedIds: [2, 20, 4],
    })
  })

  it('treats the whole cached subtree of a deleted element as deleted', () => {
    const cache = useCacheStore()
    cache.addLoaded(electronics)
    cache.addLoaded(laptops)
    const newChild = cache.addChild(4, 'New laptop')
    cache.updateValue(4, 'Edited laptops')

    cache.remove(2)

    const flat = new Map<number, CachedTreeItem>()
    const walk = (items: CachedTreeItem[]) => items.forEach((i) => (flat.set(i.data.id, i), walk(i.children)))
    walk(cache.tree)
    expect([...flat.values()].every((i) => i.data.isDeleted)).toBe(true)

    // Deleted elements can no longer be edited.
    expect(() => cache.updateValue(4, 'x')).toThrow()
    expect(() => cache.addChild(newChild, 'x')).toThrow()
    expect(() => cache.remove(4)).toThrow()

    // Only the topmost deletion is sent; edits and additions inside the subtree are dropped.
    expect(cache.buildApplyRequest()).toEqual({ added: [], updated: [], deleted: [2], cachedIds: [2, 4] })
  })

  it('marks a descendant loaded after its ancestor was deleted as deleted', () => {
    const cache = useCacheStore()
    cache.addLoaded(computers)
    cache.remove(3)

    cache.addLoaded(business) // never loaded before, ancestor 3 is pending deletion
    expect(cache.tree[0]!.children[0]!.data.isDeleted).toBe(true)
    expect(() => cache.updateValue(5, 'x')).toThrow()
  })

  it('shows elements already deleted in the database as deleted and read-only', () => {
    const cache = useCacheStore()
    cache.addLoaded(dto(7, [1, 2], 'Old', true))
    expect(cache.tree[0]!.data.isDeleted).toBe(true)
    expect(() => cache.updateValue(7, 'x')).toThrow()
    expect(cache.pendingChanges.total).toBe(0)
  })

  it('rejects empty values', () => {
    const cache = useCacheStore()
    cache.addLoaded(books)
    expect(() => cache.updateValue(20, '   ')).toThrow()
    expect(() => cache.addChild(20, '')).toThrow()
  })

  it('synchronizes with the Apply result: maps temporary ids and refreshes state', () => {
    const cache = useCacheStore()
    cache.addLoaded(books)
    cache.addLoaded(electronics)
    const poetry = cache.addChild(20, 'Poetry')
    const haiku = cache.addChild(poetry, 'Haiku')
    const dropped = cache.addChild(20, 'Dropped')
    cache.remove(dropped)
    cache.remove(2)
    cache.select(haiku)

    cache.applyResult({
      idMap: [
        { tempId: poetry, id: 40 },
        { tempId: haiku, id: 41 },
      ],
      skipped: [],
      nodes: [
        dto(2, [1], 'Node 2', true),
        dto(20, [1], 'Node 20'),
        dto(40, [1, 20], 'Poetry'),
        dto(41, [1, 20, 40], 'Haiku'),
      ],
      insertedCount: 2,
      updatedCount: 0,
      deletedCount: 1,
    })

    expect(shape(cache.tree)).toBe('2,20(40(41))')
    expect(cache.selectedId).toBe(41)
    expect(cache.pendingChanges.total).toBe(0)
    expect(cache.nodes.get(2)!.deletedInDb).toBe(true)
    expect(cache.has(dropped)).toBe(false)

    // New elements get fresh temporary ids that never collide with mapped ones.
    const next = cache.addChild(41, 'Senryu')
    expect(next).toBeLessThan(0)
    expect(cache.buildApplyRequest().added).toEqual([{ tempId: next, parentId: 41, value: 'Senryu' }])
  })

  it('drops new elements the server skipped because their parent was deleted', () => {
    const cache = useCacheStore()
    cache.addLoaded(laptops)
    const child = cache.addChild(4, 'Orphan')

    cache.applyResult({
      idMap: [],
      skipped: [child],
      nodes: [dto(4, [1, 2, 3], 'Node 4', true)],
      insertedCount: 0,
      updatedCount: 0,
      deletedCount: 0,
    })

    expect(cache.has(child)).toBe(false)
    expect(cache.nodes.get(4)!.deletedInDb).toBe(true)
  })
})
