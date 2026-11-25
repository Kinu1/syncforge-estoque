export interface ImportPreview {
  id: string
  rows: Array<{
    line: number
    sku: string
    physicalQuantity: number
    reservedQuantity: number
    availableQuantity: number
  }>
  errors: Array<{ line: number; message: string }>
}

export interface Divergence {
  sku: string
  physicalQuantity: number
  reservedQuantity: number
  availableQuantity: number
  listingId: string | null
  observedQuantity: number | null
  observedVersion: number | null
  observedAt: string | null
  revision: number
  status: "aligned" | "divergent" | "unlinked"
}

export interface ListingLink {
  listingId: string
  name: string
  sku: string | null
  quantity: number
}

export type Channel = "A" | "B"

export interface ChannelListing extends ListingLink {
  channel: Channel
  version: number
}

export interface AllocationStock {
  sku: string
  availableQuantity: number
  revision: number
  plannedA: number | null
  plannedB: number | null
  latestBatchId: string | null
  active: boolean
}

export interface AllocationTarget {
  listingId: string
  version: number
  quantity: number
}

export interface AllocationRequest {
  revision: number
  channelA: AllocationTarget
  channelB: AllocationTarget
}

export type ItemStatus =
  | "pending"
  | "processing"
  | "awaitingConfirmation"
  | "waitingForReduction"
  | "blocked"
  | "synchronized"
  | "conflict"
  | "superseded"
  | "failed"

export interface BatchItem {
  channel?: Channel | null
  sku: string
  listingId: string
  desiredQuantity: number
  status: ItemStatus
  message: string | null
  attempts: Array<{ at: string; status: string; message: string | null }>
}

export interface SyncBatch {
  id: string
  createdAt: string
  items: BatchItem[]
}

export interface SyncSelection {
  sku: string
  revision: number
  observedVersion: number
}

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

interface RequestOptions {
  method?: "GET" | "POST" | "PUT"
  body?: unknown
  signal?: AbortSignal
  idempotencyKey?: string
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = "GET", signal, idempotencyKey } = options
  const headers = new Headers({ Accept: "application/json" })
  const body = options.body === undefined ? undefined : JSON.stringify(options.body)

  if (method !== "GET") {
    const hash = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(body ?? ""))
    const hex = Array.from(new Uint8Array(hash), (byte) => byte.toString(16).padStart(2, "0"))
    headers.set("x-amz-content-sha256", hex.join(""))
  }
  if (body !== undefined) headers.set("Content-Type", "application/json")
  if (idempotencyKey) headers.set("Idempotency-Key", idempotencyKey)

  const response = await fetch("/api/v1" + path, {
    method,
    headers,
    body,
    signal,
    credentials: "same-origin",
  })

  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    const message = typeof problem?.detail === "string"
      ? problem.detail
      : "A operação falhou. Tente novamente."
    throw new ApiError(response.status, message)
  }

  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

const enc = encodeURIComponent

export const api = {
  session: (signal: AbortSignal) =>
    request<{ expiresAt: string }>("/demo/session", { method: "POST", signal }),
  loadScenario: () =>
    request<void>("/demo/scenario", { method: "POST", body: { scenario: "mixed" } }),
  loadTransferScenario: () =>
    request<void>("/demo/scenario", { method: "POST", body: { scenario: "transfer" } }),
  allocations: (signal: AbortSignal) =>
    request<{ stocks: AllocationStock[]; listings: ChannelListing[] }>("/allocations", { signal }),
  allocate: (sku: string, body: AllocationRequest, idempotencyKey: string) =>
    request<{ id: string }>("/allocations/" + enc(sku), { method: "POST", body, idempotencyKey }),
  previewImport: (csv: string, capturedAt: string) =>
    request<ImportPreview>("/imports", { method: "POST", body: { csv, capturedAt } }),
  confirmImport: (id: string) =>
    request<void>("/imports/" + enc(id) + "/confirm", { method: "POST" }),
  divergences: (signal: AbortSignal) => request<Divergence[]>("/divergences", { signal }),
  listings: (signal: AbortSignal) => request<ListingLink[]>("/listing-links", { signal }),
  linkListing: (sku: string, listingId: string) =>
    request<void>("/listing-links/" + enc(sku), { method: "PUT", body: { listingId } }),
  batches: (signal: AbortSignal) => request<SyncBatch[]>("/sync-batches", { signal }),
  batch: (id: string, signal: AbortSignal) => request<SyncBatch>("/sync-batches/" + enc(id), { signal }),
  enqueue: (items: SyncSelection[], idempotencyKey: string) =>
    request<{ id: string }>("/sync-batches", { method: "POST", body: { items }, idempotencyKey }),
  retryItem: (id: string, sku: string, idempotencyKey: string) =>
    request<{ id: string }>("/sync-batches/" + enc(id) + "/items/" + enc(sku) + "/retry", {
      method: "POST",
      idempotencyKey,
    }),
}
