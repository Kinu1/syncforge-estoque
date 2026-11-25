import { ApiError, type ItemStatus } from "@/lib/api"

export const divergenceKey = ["divergences"] as const
export const listingKey = ["listings"] as const
export const batchKey = ["batches"] as const
export const allocationKey = ["allocations"] as const

export const activeStatuses = new Set<ItemStatus>(["pending", "processing", "awaitingConfirmation", "waitingForReduction"])

export const statusLabel: Record<ItemStatus, string> = {
  pending: "Na fila",
  processing: "Processando",
  awaitingConfirmation: "Aguardando confirmação",
  waitingForReduction: "Aguardando redução",
  blocked: "Aumento bloqueado",
  synchronized: "Sincronizado",
  conflict: "Conflito",
  superseded: "Versão superada",
  failed: "Falha",
}

const dateTimeFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "short",
})

export function dateTime(value: string | null) {
  if (!value) return "—"
  return dateTimeFormatter.format(new Date(value))
}

export function errorMessage(error: unknown) {
  if (error instanceof ApiError) return error.message
  if (error instanceof TypeError) return "Verifique a conexão com o SyncForge e tente novamente."
  return error instanceof Error ? error.message : "Ocorreu um erro. Tente novamente."
}

export interface PendingRequest {
  signature: string
  key: string
}

export function requestKey(current: PendingRequest | null, signature: string): PendingRequest {
  return current?.signature === signature ? current : { signature, key: crypto.randomUUID() }
}
