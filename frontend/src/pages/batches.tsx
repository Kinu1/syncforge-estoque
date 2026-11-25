import { useRef } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { ArrowLeft, ArrowRight, ListFilter, RefreshCw, RotateCcw } from "lucide-react"
import { Link, useNavigate, useParams } from "react-router"
import { EmptyState, ErrorNotice, ItemBadge, Loading, PageHeading, StatusBadge } from "@/components/operations"
import { Button } from "@/components/ui/button"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { api, type BatchItem } from "@/lib/api"
import { activeStatuses, allocationKey, batchKey, dateTime, divergenceKey, requestKey, statusLabel, type PendingRequest } from "@/lib/operations"

function batchSummary(items: BatchItem[]) {
  const active = items.some(({ status }) => activeStatuses.has(status))
  const synchronized = items.filter(({ status }) => status === "synchronized").length
  return { active, synchronized }
}

export function Batches() {
  const batches = useQuery({ queryKey: batchKey, queryFn: ({ signal }) => api.batches(signal) })

  return (
    <div className="space-y-6">
      <PageHeading
        title="Lotes de sincronização"
        description="Acompanhe as atualizações enviadas e consulte o histórico de tentativas por produto."
        action={<Button variant="outline" disabled={batches.isFetching} onClick={() => void batches.refetch()}><RefreshCw aria-hidden="true" /> Atualizar lotes</Button>}
      />
      <section aria-labelledby="batches-heading" className="rounded-xl border bg-card">
        <div className="border-b px-5 py-4"><h2 id="batches-heading" className="font-semibold">Histórico de lotes</h2></div>
        {batches.isPending && <Loading>Consultando lotes…</Loading>}
        {batches.isError && <div className="p-5"><ErrorNotice error={batches.error} onRetry={() => void batches.refetch()} /></div>}
        {batches.isSuccess && batches.data.length === 0 && (
          <EmptyState title="Nenhum lote enviado" description="Selecione os produtos divergentes para iniciar uma sincronização." action={<Button asChild variant="outline"><Link to="/divergencias">Ver divergências</Link></Button>} />
        )}
        {batches.data && batches.data.length > 0 && (
          <ul className="divide-y">
            {batches.data.map((batch) => {
              const summary = batchSummary(batch.items)
              const complete = summary.synchronized === batch.items.length
              return (
                <li className="flex flex-wrap items-center justify-between gap-3 px-5 py-4" key={batch.id}>
                  <div className="space-y-1">
                    <p className="font-mono text-xs font-semibold">Lote {batch.id.slice(0, 8)}</p>
                    <p className="text-xs text-muted-foreground">{dateTime(batch.createdAt)} · {summary.synchronized}/{batch.items.length} sincronizados</p>
                  </div>
                  <div className="flex flex-wrap items-center gap-3">
                    {summary.active
                      ? <StatusBadge tone="info">Em andamento</StatusBadge>
                      : complete
                        ? <StatusBadge tone="success">Concluído</StatusBadge>
                        : <StatusBadge tone="warning">Com pendências</StatusBadge>}
                    <Button asChild size="sm" variant="outline"><Link aria-label={"Abrir lote " + batch.id.slice(0, 8)} to={"/lotes/" + encodeURIComponent(batch.id)}>Abrir lote <ArrowRight aria-hidden="true" /></Link></Button>
                  </div>
                </li>
              )
            })}
          </ul>
        )}
      </section>
    </div>
  )
}

export function BatchDetailRoute() {
  const { batchId } = useParams()
  return batchId ? <BatchDetail key={batchId} batchId={batchId} /> : null
}

function BatchDetail({ batchId }: { batchId: string }) {
  const client = useQueryClient()
  const navigate = useNavigate()
  const requests = useRef(new Map<string, PendingRequest>())
  const batch = useQuery({
    queryKey: [...batchKey, batchId],
    queryFn: ({ signal }) => api.batch(batchId, signal),
    refetchInterval: (query) => query.state.data?.items.some(({ status }) => activeStatuses.has(status)) ? 2000 : false,
  })

  const retry = useMutation({
    mutationFn: (sku: string) => {
      const operation = requestKey(requests.current.get(sku) ?? null, batchId + "/" + sku)
      requests.current.set(sku, operation)
      return api.retryItem(batchId, sku, operation.key)
    },
    async onSuccess({ id }, sku) {
      requests.current.delete(sku)
      await client.invalidateQueries({ queryKey: batchKey })
      if (id !== batchId) navigate("/lotes/" + encodeURIComponent(id))
    },
  })

  const items = batch.data?.items ?? []
  const summary = batchSummary(items)
  const distributed = items.some(item => item.channel)

  return (
    <div className="space-y-6">
      <Button variant="ghost" className="-ml-2.5" asChild><Link to="/lotes"><ArrowLeft aria-hidden="true" /> Voltar aos lotes</Link></Button>
      <PageHeading title={"Lote " + batchId.slice(0, 8)} description={batch.data ? "Enviado em " + dateTime(batch.data.createdAt) : "Resultado e tentativas de sincronização por produto."} />
      {batch.isPending && <Loading>Consultando o lote…</Loading>}
      {batch.isError && <ErrorNotice error={batch.error} onRetry={() => void batch.refetch()} />}
      {batch.data && (
        <section aria-labelledby="batch-detail-heading" className="rounded-xl border bg-card">
          <div className="flex flex-wrap items-start justify-between gap-4 border-b px-5 py-4">
            <div>
              <h2 id="batch-detail-heading" className="font-semibold">Resultado por SKU</h2>
              <p className="mt-0.5 text-sm text-muted-foreground" aria-live="polite">
                {summary.synchronized} de {items.length} sincronizados {summary.active && "· processamento em andamento"}
              </p>
            </div>
            <Button asChild variant="outline" onClick={() => void client.invalidateQueries({ queryKey: distributed ? allocationKey : divergenceKey })}>
              <Link to={distributed ? "/distribuicao" : "/divergencias"}><ListFilter aria-hidden="true" /> Conferir saldos</Link>
            </Button>
          </div>
          {retry.isError && <div className="px-5 pt-4"><ErrorNotice error={retry.error} /></div>}
          {items.length === 0 && <EmptyState title="Lote sem itens" description="As atualizações aparecerão aqui quando forem registradas." />}
          <ul className="divide-y">
            {items.map((item) => (
              <li key={item.sku + "/" + item.listingId} className="px-5 py-4">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0">
                    <h3 className="font-mono text-sm font-semibold">{item.sku}{item.channel && " · Canal " + item.channel}</h3>
                    <p className="mt-1 text-sm text-muted-foreground">Saldo planejado de {item.desiredQuantity} un.</p>
                    {item.message && <p className="mt-1 break-words text-sm">{item.message}</p>}
                  </div>
                  <div className="flex flex-wrap items-center gap-2.5">
                    <ItemBadge status={item.status} />
                    {item.channel && ["failed", "blocked", "conflict", "superseded"].includes(item.status) && (
                      <Button asChild size="sm" variant="outline"><Link to={"/distribuicao?sku=" + encodeURIComponent(item.sku)}>Revisar distribuição</Link></Button>
                    )}
                    {!item.channel && item.status === "failed" && (
                      <Button size="sm" variant="outline" onClick={() => retry.mutate(item.sku)} disabled={retry.isPending}>
                        <RotateCcw aria-hidden="true" /> {retry.isPending && retry.variables === item.sku ? "Reprocessando…" : "Reprocessar"}
                        <span className="sr-only"> {item.sku}</span>
                      </Button>
                    )}
                  </div>
                </div>
                {item.attempts.length > 0 && (
                  <details className="mt-3 rounded-md border bg-muted/30">
                    <summary className="cursor-pointer px-3 py-2 text-sm font-medium">Histórico de tentativas <span className="text-muted-foreground">({item.attempts.length})</span></summary>
                    <Table>
                      <TableHeader>
                        <TableRow><TableHead className="pl-3">Horário</TableHead><TableHead>Situação</TableHead><TableHead>Detalhe</TableHead></TableRow>
                      </TableHeader>
                      <TableBody>
                        {item.attempts.map((attempt, index) => (
                          <TableRow key={index}>
                            <TableCell className="pl-3 text-xs tabular-nums">{dateTime(attempt.at)}</TableCell>
                            <TableCell className="text-xs">{statusLabel[attempt.status as keyof typeof statusLabel] ?? attempt.status}</TableCell>
                            <TableCell className="max-w-64 min-w-24 text-xs whitespace-normal">{attempt.message || "—"}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </details>
                )}
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  )
}
