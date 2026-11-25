import type { ReactNode } from "react"
import { AlertCircle, LoaderCircle, PackageOpen } from "lucide-react"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { type ItemStatus } from "@/lib/api"
import { errorMessage, statusLabel } from "@/lib/operations"
import { cn } from "@/lib/utils"

export function PageHeading({ title, description, action }: {
  title: string
  description: string
  action?: ReactNode
}) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div className="max-w-2xl">
        <h1 className="text-[clamp(1.8rem,4vw,2.55rem)] leading-tight font-semibold tracking-[-.04em]">{title}</h1>
        <p className="mt-2 text-sm leading-relaxed text-muted-foreground sm:text-base">{description}</p>
      </div>
      {action}
    </div>
  )
}

export function EmptyState({ title, description, action }: {
  title: string
  description: string
  action?: ReactNode
}) {
  return (
    <div className="flex flex-col items-center gap-3 px-5 py-12 text-center">
      <div className="grid size-10 place-items-center rounded-lg bg-muted text-muted-foreground">
        <PackageOpen className="size-5" aria-hidden="true" />
      </div>
      <div>
        <h2 className="font-medium">{title}</h2>
        <p className="mt-1 max-w-md text-sm leading-relaxed text-muted-foreground">{description}</p>
      </div>
      {action}
    </div>
  )
}

export function ErrorNotice({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  return (
    <Alert variant="destructive">
      <AlertCircle aria-hidden="true" />
      <AlertDescription>
        <p>{errorMessage(error)}</p>
        {onRetry && <Button size="sm" variant="outline" className="mt-2" onClick={onRetry}>Tentar novamente</Button>}
      </AlertDescription>
    </Alert>
  )
}

export function Loading({ children }: { children: ReactNode }) {
  return (
    <p role="status" className="flex min-h-32 items-center justify-center gap-2 p-5 text-sm text-muted-foreground">
      <LoaderCircle className="size-4 motion-safe:animate-spin" aria-hidden="true" /> {children}
    </p>
  )
}

type Tone = "neutral" | "success" | "warning" | "danger" | "info"
const badgeTone: Record<Tone, string> = {
  neutral: "bg-stone-100 text-stone-800",
  success: "bg-emerald-100 text-emerald-900",
  warning: "bg-amber-100 text-amber-950",
  danger: "bg-red-100 text-red-900",
  info: "bg-sky-100 text-sky-950",
}

export function StatusBadge({ children, tone }: { children: ReactNode; tone: Tone }) {
  return (
    <Badge variant="secondary" className={cn("w-fit gap-1.5 rounded-full font-medium", badgeTone[tone])}>
      <span aria-hidden="true" className="size-1.5 rounded-full bg-current" />
      {children}
    </Badge>
  )
}

const itemTone: Record<ItemStatus, Tone> = {
  pending: "neutral",
  processing: "info",
  awaitingConfirmation: "info",
  waitingForReduction: "info",
  blocked: "warning",
  synchronized: "success",
  conflict: "warning",
  superseded: "neutral",
  failed: "danger",
}

export function ItemBadge({ status }: { status: ItemStatus }) {
  return <StatusBadge tone={itemTone[status]}>{statusLabel[status] ?? status}</StatusBadge>
}
