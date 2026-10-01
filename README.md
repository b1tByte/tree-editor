# Tree Editor with a Local Cache

A web application with two tree views:

- **DBTreeView** shows the tree stored in the database. It loads the tree lazily, one level at a time, as you expand nodes.
- **CachedTreeView** shows the elements you loaded into a local cache. You can edit, add and delete elements there and then apply all changes to the database at once.

**Stack:** PostgreSQL 18 · .NET 10 (ASP.NET Core Minimal API, Npgsql) · Vue 3 (Composition API, TypeScript) · PrimeVue 4 · Bootstrap 5 · Pinia

## Quick start

Requirements: Docker with Docker Compose.

```bash
docker compose up --build
```

Then open **http://localhost:8080**.

| Service | URL | Notes |
|---|---|---|
| UI | http://localhost:8080 | nginx serves the UI and proxies `/api` to the API |
| API | http://localhost:5080 | e.g. http://localhost:5080/api/nodes/roots |
| PostgreSQL | `localhost:5433` | database `treeeditor`, user `postgres`, password `postgres` |

On first start the API creates the schema and loads the sample data. The **Reset** button restores the sample data at any time.

## Running without Docker (development)

Requirements: .NET 10 SDK, Node.js 22.12 or later, Docker for the database (or your own PostgreSQL).

```bash
# 1. Database
docker compose up -d db

# 2. API on http://localhost:5080 (connects to localhost:5433, see appsettings.json)
cd backend/TreeEditor.Api
dotnet run

# 3. UI on http://localhost:5173 (proxies /api to http://localhost:5080)
cd frontend
npm install
npm run dev
```

Unit tests for the cache logic:

```bash
cd frontend
npm test
```

## How to use

1. In **DBTreeView**, expand nodes and select one. Click **Load into cache** or double-click the node.
2. Load more elements in any order. Related elements are arranged into the correct hierarchy automatically. If an element is shown under an ancestor that is not its direct parent (the levels in between are not cached), a `…` marker appears; hover over it to see how many levels are missing.
3. In **CachedTreeView**, select an element and use **Add child**, **Edit** or **Delete**. You can also use the shortcuts Insert, F2 and Delete. Changes are marked as `new`, `edited` or `deleted` and stay in the cache.
4. Click **Apply** to write all pending changes to the database in one transaction. DBTreeView reloads afterwards.
5. Click **Reset** to restore the initial sample data and clear the cache.

**Things worth trying:**

- Load a deep node (for example *Business*), then *Catalog*, then *Computers*, and watch them connect.
- Delete *Computers* in the cache, then load *Laptops*. It appears as deleted right away, even before Apply.
- Apply the deletion. All descendants of *Computers* are deleted in the database, including the ones you never loaded (*Desktops*, *Gaming*, …).

## Sample data

There is one root, *Catalog*, with 35 elements and six levels of nesting, for example:
*Catalog → Electronics → Computers → Laptops → Business → ThinkPad X1*.
The data is defined in [`SeedData.cs`](backend/TreeEditor.Api/Data/SeedData.cs). Ids are assigned in pre-order, so the tree is the same after every reset.

## Database schema

One table, defined in [`Schema.sql`](backend/TreeEditor.Api/Data/Schema.sql):

| Column | Type | Description |
|---|---|---|
| `id` | `bigint` identity, PK | |
| `parent_id` | `bigint` FK → `tree_nodes.id`, nullable | `NULL` for root elements |
| `value` | `text`, not empty | element value |
| `path` | `text`, unique | materialized path of ancestor ids plus the node itself, e.g. `/1/2/3/` |
| `depth` | `int` | 0 for roots |
| `is_deleted` | `boolean` | soft-delete flag |
| `updated_at` | `timestamptz` | last change |

Indexes: `parent_id` (used to load children) and `path` with `text_pattern_ops` (used for prefix queries).

**Why adjacency list + materialized path.**
`parent_id` is enough to load the tree level by level. The materialized path adds two things:

1. **Every loaded element knows all its ancestors.** The API returns `ancestorIds` with each node, so the cache can place elements that were loaded separately and in any order. It does this without any extra database calls.
2. **Deleting a subtree takes one indexed query**, `UPDATE … WHERE path LIKE '/1/2/3/%'`. This covers descendants that were never loaded into the cache, and needs no recursion.

The usual downside of a materialized path is that moving nodes requires rewriting paths. That does not apply here, because parent–child relationships never change.

Deletion is a soft delete: elements are only marked with `is_deleted`. Both views show deleted elements struck through, and they can no longer be edited.

## API

| Method & path | Description |
|---|---|
| `GET /api/nodes/roots` | Root elements |
| `GET /api/nodes/{id}/children` | Direct children of an element (one level) |
| `GET /api/nodes/{id}` | A single element with its `ancestorIds`; used to load it into the cache |
| `POST /api/nodes/apply` | Applies all pending cache changes in one transaction |
| `POST /api/reset` | Restores the initial sample data |

Every node is returned as `{ id, parentId, value, isDeleted, ancestorIds, hasChildren }`.

`apply` request and response:

```jsonc
// request
{
  "added":     [{ "tempId": -1, "parentId": 20, "value": "Poetry" },   // parent: database id
                { "tempId": -2, "parentId": -1, "value": "Haiku" }],   // parent: another new node
  "updated":   [{ "id": 21, "value": "Fiction (edited)" }],
  "deleted":   [3],                 // whole subtrees are deleted
  "cachedIds": [3, 20, 21]          // the response returns the current state of these nodes
}
// response
{
  "idMap":   [{ "tempId": -1, "id": 36 }, { "tempId": -2, "id": 37 }],
  "skipped": [],                    // new nodes whose parent no longer exists or is deleted
  "nodes":   [ /* fresh state of cached and inserted nodes */ ],
  "insertedCount": 2, "updatedCount": 1, "deletedCount": 11
}
```

## Implementation notes

**DBTreeView** ([`dbTreeStore.ts`](frontend/src/stores/dbTreeStore.ts)) never loads the whole tree. It requests roots first, then one level of children whenever a node is expanded. After Apply or Reset it reloads only the levels that are currently expanded.

**Cache** ([`cacheStore.ts`](frontend/src/stores/cacheStore.ts)) is a flat map of elements. The hierarchy shown in CachedTreeView is computed from it:

- Each element is placed under its **nearest cached ancestor**, found through `ancestorIds`. This makes the result independent of load order.
- New elements get **negative temporary ids**. They can be nested, for example a new child under a new child. The server returns the real ids, and the cache remaps all references.
- An element counts as **deleted** if it, or any of its ancestors, is deleted, either in the database or as a pending change. This also applies to elements loaded *after* their ancestor was deleted in the cache. Deleted elements are read-only.
- The cache reaches the database **only** when it loads an element (`GET /api/nodes/{id}`) and when it applies changes (`POST /api/nodes/apply`). Apply also returns the fresh state of all cached elements, so no separate refresh call is needed.
- Loading an element that is already cached does nothing, so pending edits are never overwritten.
- When building the Apply request, the cache leaves out:
  - edits to deleted elements;
  - new elements inside deleted subtrees;
  - deletions already covered by a deleted ancestor.

**Apply** ([`ApplyService.cs`](backend/TreeEditor.Api/Nodes/ApplyService.cs)) runs in a single transaction:

1. Insert new elements, parents before children (topological order).
2. Update values.
3. Soft-delete subtrees by path prefix.

If changes conflict (for example, the same data changed from another browser tab), **deletion wins**. New elements under a deleted parent are skipped and reported back. Value updates to deleted elements are ignored. The transaction takes a `SHARE ROW EXCLUSIVE` table lock, so concurrent Apply calls run one after another; reads are not blocked.

**Data access** uses plain SQL with Npgsql instead of an ORM. The interesting parts of this task, such as the prefix-based subtree delete and the batch updates with `unnest`, are SQL. Writing them directly keeps them explicit and easy to review. The schema script runs on API startup, so no separate migration step is needed.

**Validation:** values are trimmed, must not be empty, and are at most 200 characters long. This is checked in the UI, in the API, and by a database constraint (non-empty).

## Possible improvements

- Paging for nodes with a very large number of children.
- Optimistic concurrency, such as a row version per node, to detect edits based on stale values.
- API integration tests against a real PostgreSQL instance (for example with Testcontainers).

## Project structure

```
backend/
  TreeEditor.Api/
    Data/        schema script, sample data, initializer (create + reset)
    Nodes/       contracts, read queries, apply service, endpoints
  Dockerfile
frontend/
  src/
    api/         typed API client
    stores/      Pinia stores: dbTreeStore (DBTreeView), cacheStore (cache logic)
    components/  DbTreeView.vue, CachedTreeView.vue
  tests/         unit tests for the cache logic (Vitest)
  Dockerfile, nginx.conf
docker-compose.yml
```
