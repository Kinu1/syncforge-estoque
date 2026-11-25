import { useRef, useState, type FormEvent } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Link2, RefreshCw, Search, Send } from "lucide-react"
import { Link, useLocation, useNavigate } from "react-router"
import { EmptyState, ErrorNotice, Loading, PageHeading, StatusBadge } from "@/components/operations"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { ApiError, api, type Divergence, type SyncSelection } from "@/lib/api"
import { allocationKey, batchKey, dateTime, divergenceKey, listingKey, requestKey, type PendingRequest } from "@/lib/operations"

const statusNotes = {
  aligned: { label: "Alinhado", tone: "success" },
  divergent: { label: "Divergente", tone: "warning" },
  unlinked: { label: "Sem vínculo", tone: "neutral" },
} as const

function LinkListingDialog({ sku, onClose, onSuccess }: {
  sku: string
  onClose: () => void
  onSuccess: () => void
}) {
  const [listingId, setListingId] = useState("")
  const saved = useRef(false)
  const listings = useQuery({ queryKey: listingKey, queryFn: ({ signal }) => api.listings(signal) })
  const save = useMutation({
    mutationFn: () => api.linkListing(sku, listingId),
    onSuccess() {
      saved.current = true
      onSuccess()
    },
  })
  const available = listings.data?.filter((entry) => !entry.sku || entry.sku === sku) ?? []

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (listingId) save.mutate()
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !save.isPending) onClose() }}>
      <DialogContent onCloseAutoFocus={(event) => {
        event.preventDefault()
        const triggerId = saved.current ? "divergence-filter" : "link-" + sku
        document.getElementById(triggerId)?.focus()
      }}>
        <DialogHeader>
          <DialogTitle>Vincular anúncio</DialogTitle>
          <DialogDescription>Escolha o anúncio do marketplace correspondente ao SKU {sku}.</DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} className="space-y-4">
          {listings.isPending && <Loading>Consultando anúncios…</Loading>}
          {listings.isError && <ErrorNotice error={listings.error} onRetry={() => void listings.refetch()} />}
          {listings.isSuccess && available.length === 0 && (
            <p className="text-sm text-muted-foreground">Nenhum anúncio livre encontrado.</p>
          )}
          {available.length > 0 && (
            <div className="space-y-2">
              <Label htmlFor="listing-id">Anúncio</Label>
              <Select value={listingId} onValueChange={setListingId} disabled={save.isPending}>
                <SelectTrigger id="listing-id" className="w-full"><SelectValue placeholder="Selecionar anúncio" /></SelectTrigger>
                <SelectContent>
                  {available.map((entry) => (
                    <SelectItem key={entry.listingId} value={entry.listingId}>
                      {entry.name} · {entry.quantity} un.
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          )}
          {save.isError && <ErrorNotice error={save.error} />}
          <DialogFooter>
            <Button type="button" variant="outline" disabled={save.isPending} onClick={onClose}>Cancelar</Button>
            <Button type="submit" disabled={!listingId || save.isPending}>{save.isPending ? "Vinculando…" : "Salvar vínculo"}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

export function Divergences() {
  const navigate = useNavigate()
  const location = useLocation()
  const client = useQueryClient()
  const [filter, setFilter] = useState("all")
  const [search, setSearch] = useState("")
  const [selection, setSelection] = useState(() => new Map<string, SyncSelection>())
  const [linkSku, setLinkSku] = useState<string | null>(null)
  const request = useRef<PendingRequest | null>(null)
  const rows = useQuery({ queryKey: divergenceKey, queryFn: ({ signal }) => api.divergences(signal) })
  const enqueue = useMutation({
    mutationFn: (items: SyncSelection[]) => {
      const signature = JSON.stringify(items)
      request.current = requestKey(request.current, signature)
      return api.enqueue(items, request.current.key)
    },
    async onSuccess({ id }) {
      request.current = null
      await client.invalidateQueries({ queryKey: batchKey })
      navigate("/lotes/" + encodeURIComponent(id))
    },
    onError(error) {
      if (error instanceof ApiError && error.status === 409) void rows.refetch()
    },
  })

  const visible = (rows.data ?? []).filter((row) =>
    (filter === "all" || row.status === filter) && row.sku.toLowerCase().includes(search.trim().toLowerCase()),
  )
  const selectedItems = [...selection.values()].sort((left, right) => left.sku.localeCompare(right.sku))
  const availableBySku = new Map(rows.data?.map((row) => [row.sku, row]))
  const staleSelection = selectedItems.some((item) => {
    const row = availableBySku.get(item.sku)
    return row?.status !== "divergent" || row.revision !== item.revision || row.observedVersion !== item.observedVersion
  })

  function toggle(row: Divergence, checked: boolean) {
    setSelection((old) => {
      const next = new Map(old)
      if (checked && row.observedVersion !== null) {
        next.set(row.sku, { sku: row.sku, revision: row.revision, observedVersion: row.observedVersion })
      } else {
        next.delete(row.sku)
      }
      return next
    })
  }

  return (
    <div className="space-y-6">
      <PageHeading title="Divergências" description="Compare o estoque do ERP com os anúncios em canal único." action={<Button asChild variant="outline"><Link to="/distribuicao">Ver distribuição entre canais</Link></Button>} />
      {location.state?.notice && (
        <p role="status" className="rounded-lg border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-950">{location.state.notice}</p>
      )}

      <section aria-labelledby="divergence-heading" className="rounded-xl border bg-card">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b px-5 py-4">
          <div>
            <h2 id="divergence-heading" className="font-semibold">Produtos comparados</h2>
            <p className="mt-0.5 text-sm text-muted-foreground">Disponível = físico − reservado.</p>
          </div>
          <Button variant="outline" onClick={() => void rows.refetch()} disabled={rows.isFetching || enqueue.isPending}>
            <RefreshCw aria-hidden="true" /> Atualizar saldos
          </Button>
        </div>

        <div className="flex flex-wrap items-center gap-3 border-b px-5 py-3">
          <div className="relative min-w-0 grow basis-40 sm:grow-0 sm:basis-56">
            <Search aria-hidden="true" className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
            <Input aria-label="Buscar por SKU" placeholder="Buscar por SKU" className="pl-8" value={search} onChange={(event) => setSearch(event.target.value)} />
          </div>
          <Select value={filter} onValueChange={setFilter}>
            <SelectTrigger id="divergence-filter" aria-label="Filtrar por situação" className="max-w-full"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem value="all">Todas as situações</SelectItem>
              <SelectItem value="divergent">Divergentes</SelectItem>
              <SelectItem value="aligned">Alinhados</SelectItem>
              <SelectItem value="unlinked">Sem vínculo</SelectItem>
            </SelectContent>
          </Select>
        </div>

        {rows.isPending && <Loading>Consultando divergências…</Loading>}
        {rows.isError && <div className="p-5"><ErrorNotice error={rows.error} onRetry={() => void rows.refetch()} /></div>}
        {rows.isSuccess && visible.length === 0 && (
          rows.data.length === 0
            ? <EmptyState title="Nenhum produto em canal único" description="Os SKUs repartidos entre canais aparecem em Distribuição." action={<Button asChild variant="outline"><Link to="/importacoes">Ir para importações</Link></Button>} />
            : <EmptyState title="Nenhum resultado" description="Ajuste a busca por SKU ou o filtro de situação." />
        )}
        {visible.length > 0 && (
          <Table className="max-md:block">
            <TableHeader className="max-md:sr-only">
              <TableRow>
                <TableHead className="w-10 pl-5"><span className="sr-only">Selecionar</span></TableHead>
                <TableHead>SKU</TableHead>
                <TableHead className="hidden lg:table-cell">Físico</TableHead>
                <TableHead className="hidden lg:table-cell">Reservado</TableHead>
                <TableHead>Disponível</TableHead>
                <TableHead>Marketplace</TableHead>
                <TableHead>Situação</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody className="max-md:block">
              {visible.map((row) => (
                <TableRow key={row.sku} className="max-md:grid max-md:grid-cols-[1.5rem_minmax(0,1fr)_minmax(6rem,auto)] max-md:items-center max-md:gap-2 max-md:px-4 max-md:py-3">
                  <TableCell className="pl-5 max-md:col-start-1 max-md:row-start-1 max-md:p-0">
                    {((row.status === "divergent" && row.observedVersion !== null) || selection.has(row.sku)) && (
                      <Checkbox aria-label={"Selecionar " + row.sku} checked={selection.has(row.sku)} disabled={enqueue.isPending} onCheckedChange={(checked) => toggle(row, checked === true)} />
                    )}
                  </TableCell>
                  <TableCell className="max-md:col-start-2 max-md:row-start-1 max-md:p-0 max-md:whitespace-normal max-md:[overflow-wrap:anywhere]">
                    <div className="font-mono text-xs font-semibold">{row.sku}</div>
                    <div className="text-xs text-muted-foreground lg:hidden">{row.physicalQuantity} físico · {row.reservedQuantity} reservado</div>
                  </TableCell>
                  <TableCell className="hidden tabular-nums lg:table-cell">{row.physicalQuantity}</TableCell>
                  <TableCell className="hidden tabular-nums lg:table-cell">{row.reservedQuantity}</TableCell>
                  <TableCell className="tabular-nums max-md:col-start-2 max-md:row-start-2 max-md:p-0">
                    <span className="block text-xs text-muted-foreground md:hidden">ERP disponível</span>
                    <span className="font-semibold">{row.availableQuantity}</span>
                  </TableCell>
                  <TableCell className="max-md:col-start-3 max-md:row-start-2 max-md:p-0 max-md:whitespace-normal">
                    <span className="block text-xs text-muted-foreground md:hidden">Marketplace</span>
                    {row.observedQuantity ?? "—"}
                    {row.observedAt && <div className="text-xs text-muted-foreground">{dateTime(row.observedAt)}</div>}
                  </TableCell>
                  <TableCell className="pr-5 max-md:col-start-3 max-md:row-start-1 max-md:p-0">
                    {row.status === "unlinked" ? (
                      <Button id={"link-" + row.sku} size="sm" variant="outline" onClick={() => setLinkSku(row.sku)}>
                        <Link2 aria-hidden="true" /> Vincular
                        <span className="sr-only"> {row.sku}</span>
                      </Button>
                    ) : <StatusBadge tone={statusNotes[row.status].tone}>{statusNotes[row.status].label}</StatusBadge>}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {enqueue.isError && <div className="px-5 py-3"><ErrorNotice error={enqueue.error} /></div>}
        {staleSelection && (
          <p role="status" className="px-5 py-3 text-sm text-amber-950">Os saldos selecionados mudaram. Limpe a seleção e revise os valores.</p>
        )}
        <div className="flex flex-wrap items-center justify-between gap-3 border-t bg-muted/30 px-5 py-4">
          <div className="flex flex-wrap items-center gap-2">
            <span role="status" className="text-sm text-muted-foreground">{selectedItems.length} selecionados</span>
            {selectedItems.length > 0 && <Button size="sm" variant="ghost" disabled={enqueue.isPending} onClick={() => setSelection(new Map())}>Limpar seleção</Button>}
          </div>
          <Button disabled={!selectedItems.length || staleSelection || enqueue.isPending || rows.isFetching} onClick={() => enqueue.mutate(selectedItems)}>
            <Send aria-hidden="true" /> {enqueue.isPending ? "Enviando…" : "Sincronizar seleção"}
          </Button>
        </div>
      </section>

      {linkSku && (
        <LinkListingDialog sku={linkSku} onClose={() => setLinkSku(null)} onSuccess={() => {
          setLinkSku(null)
          void client.invalidateQueries({ queryKey: divergenceKey })
          void client.invalidateQueries({ queryKey: listingKey })
          void client.invalidateQueries({ queryKey: allocationKey })
        }} />
      )}
    </div>
  )
}
