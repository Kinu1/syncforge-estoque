import { useRef, useState, type FormEvent } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { ArrowRight, FlaskConical, Package, Send } from "lucide-react"
import { Link, useNavigate, useSearchParams } from "react-router"
import { EmptyState, ErrorNotice, Loading, PageHeading } from "@/components/operations"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { api, ApiError, type AllocationRequest, type AllocationStock, type Channel, type ChannelListing } from "@/lib/api"
import { allocationKey, batchKey, divergenceKey, listingKey, requestKey, type PendingRequest } from "@/lib/operations"

const stockQueries = [allocationKey, batchKey, listingKey, divergenceKey]

function TransferScenario({ onLoaded }: { onLoaded: () => Promise<void> }) {
  const [open, setOpen] = useState(false)
  const scenario = useMutation({
    mutationFn: api.loadTransferScenario,
    async onSuccess() {
      await onLoaded()
      setOpen(false)
    },
  })
  return (
    <Dialog open={open} onOpenChange={value => { if (!scenario.isPending) setOpen(value) }}>
      <DialogTrigger asChild><Button variant="outline"><FlaskConical aria-hidden="true" /> Carregar cenário de transferência</Button></DialogTrigger>
      <DialogContent showCloseButton={false}>
        <DialogHeader>
          <DialogTitle>Carregar cenário de transferência?</DialogTitle>
          <DialogDescription>As importações, os vínculos e os lotes desta sessão serão substituídos. O cenário começa com 4 unidades no canal A e 3 no B. Transfira duas de A para B e acompanhe a confirmação após a perda de resposta.</DialogDescription>
        </DialogHeader>
        {scenario.isError && <ErrorNotice error={scenario.error} />}
        <DialogFooter>
          <Button variant="outline" disabled={scenario.isPending} onClick={() => setOpen(false)}>Cancelar</Button>
          <Button disabled={scenario.isPending} onClick={() => scenario.mutate()}>{scenario.isPending ? "Carregando…" : "Carregar e substituir"}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

interface ChannelDraft {
  listingId: string
  amount: string
}

function ChannelEditor({ channel, listings, draft, onChange, locked, busy, planned }: {
  channel: Channel
  listings: ChannelListing[]
  draft: ChannelDraft
  onChange: (draft: ChannelDraft) => void
  locked: boolean
  busy: boolean
  planned: number | null
}) {
  const listing = listings.find(entry => entry.listingId === draft.listingId)
  return (
    <fieldset className="min-w-0 rounded-xl border bg-card p-5" disabled={busy}>
      <legend className="px-2 font-semibold">Canal {channel}</legend>
      <div className="space-y-2">
        <Label htmlFor={"listing-" + channel}>Anúncio do canal {channel}</Label>
        <Select value={draft.listingId} onValueChange={listingId => onChange({ ...draft, listingId })} disabled={locked || busy}>
          <SelectTrigger id={"listing-" + channel} className="w-full"><SelectValue placeholder="Selecionar anúncio" /></SelectTrigger>
          <SelectContent>{listings.map(option => <SelectItem key={option.listingId} value={option.listingId}>{option.name} · {option.listingId}</SelectItem>)}</SelectContent>
        </Select>
        {!listings.length && <p className="text-sm text-muted-foreground">Todos os anúncios deste canal já têm vínculo.</p>}
      </div>
      <dl className="my-5 grid grid-cols-2 gap-3 border-y py-4 text-sm">
        <div><dt className="text-muted-foreground">Saldo observado</dt><dd className="mt-1 text-xl font-semibold tabular-nums">{listing?.quantity ?? "—"}</dd></div>
        <div><dt className="text-muted-foreground">Último planejado</dt><dd className="mt-1 text-xl font-semibold tabular-nums">{planned ?? "—"}</dd></div>
      </dl>
      <div className="space-y-2">
        <Label htmlFor={"quantity-" + channel}>Quantidade para o canal {channel}</Label>
        <Input id={"quantity-" + channel} className="h-10 tabular-nums" type="number" min="0" step="1" inputMode="numeric" required value={draft.amount} onChange={event => onChange({ ...draft, amount: event.target.value })} aria-describedby="distribution-balance" />
      </div>
    </fieldset>
  )
}

function AllocationForm({ stock, listings, refresh }: {
  stock: AllocationStock
  listings: ChannelListing[]
  refresh: () => Promise<void>
}) {
  const client = useQueryClient()
  const navigate = useNavigate()
  const request = useRef<PendingRequest | null>(null)
  const channelOptions = (channel: Channel) => listings.filter(entry => entry.channel === channel && (!entry.sku || entry.sku === stock.sku))
  const channels = ["A", "B"] as const
  function initialDraft(channel: Channel) {
    const linked = listings.find(entry => entry.channel === channel && entry.sku === stock.sku)
    const planned = channel === "A" ? stock.plannedA : stock.plannedB
    return { listingId: linked?.listingId ?? "", amount: String(planned ?? linked?.quantity ?? 0) }
  }
  const [draft, setDraft] = useState(() => ({ A: initialDraft("A"), B: initialDraft("B") }))
  const allocate = useMutation({
    mutationFn: (body: AllocationRequest) => {
      request.current = requestKey(request.current, JSON.stringify(body))
      return api.allocate(stock.sku, body, request.current.key)
    },
    async onSuccess({ id }) {
      await Promise.all(stockQueries.map(queryKey => client.invalidateQueries({ queryKey })))
      navigate("/lotes/" + encodeURIComponent(id))
    },
  })
  const validAmounts = channels.every(channel => /^\d+$/.test(draft[channel].amount) && Number.isSafeInteger(Number(draft[channel].amount)))
  const remaining = stock.availableQuantity - Number(draft.A.amount) - Number(draft.B.amount)
  const target = (channel: Channel) => {
    const listing = channelOptions(channel).find(entry => entry.listingId === draft[channel].listingId)
    return listing && { listingId: listing.listingId, version: listing.version, quantity: Number(draft[channel].amount) }
  }
  const busy = stock.active || allocate.isPending
  const outdated = allocate.error instanceof ApiError && allocate.error.status === 409
  const unchanged = stock.plannedA !== null && stock.plannedA === Number(draft.A.amount) && stock.plannedB === Number(draft.B.amount)
    && channels.every(channel => listings.find(entry => entry.listingId === draft[channel].listingId)?.quantity === Number(draft[channel].amount))
  const valid = !busy && !outdated && !unchanged && validAmounts && remaining >= 0
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const channelA = target("A")
    const channelB = target("B")
    if (valid && channelA && channelB) allocate.mutate({ revision: stock.revision, channelA, channelB })
  }
  return (
    <form className="space-y-4" onSubmit={submit}>
      <div className="grid gap-4 md:grid-cols-2">
        {channels.map(channel => {
          const options = channelOptions(channel)
          return <ChannelEditor key={channel} channel={channel} listings={options} draft={draft[channel]} onChange={value => setDraft(previous => ({ ...previous, [channel]: value }))}
            locked={options.some(entry => entry.sku === stock.sku)} busy={busy} planned={channel === "A" ? stock.plannedA : stock.plannedB} />
        })}
      </div>
      <div className="space-y-4 rounded-xl border bg-card p-5">
        <div className="flex flex-wrap items-center justify-between gap-4">
          <div className="space-y-1">
            <h2 className="font-semibold">Resumo da distribuição</h2>
            <p id="distribution-balance" role="status" className={validAmounts && remaining >= 0 ? "text-sm text-muted-foreground" : "text-sm text-destructive"}>
              {!validAmounts ? "Informe quantidades inteiras a partir de zero." : remaining < 0
                ? "A soma excede o disponível em " + Math.abs(remaining) + " un."
                : (stock.availableQuantity - remaining) + " un. planejadas · " + remaining + " un. livres"}
            </p>
          </div>
          <Button type="submit" disabled={!valid || !target("A") || !target("B")}><Send aria-hidden="true" /> {allocate.isPending ? "Enviando…" : "Enviar distribuição"}</Button>
        </div>
        {allocate.isError && <ErrorNotice error={allocate.error} />}
        {outdated && <Button type="button" variant="outline" onClick={() => void refresh()}>Atualizar saldos e revisar</Button>}
        {stock.latestBatchId && <div className="flex flex-wrap items-center justify-between gap-2 border-t pt-3 text-sm">
          <span>{stock.active ? "Distribuição em andamento. Acompanhe a confirmação de cada canal." : "Última distribuição deste SKU"}</span>
          <Button variant="outline" size="sm" asChild><Link to={"/lotes/" + encodeURIComponent(stock.latestBatchId)}>Ver lote <ArrowRight aria-hidden="true" /></Link></Button>
        </div>}
      </div>
    </form>
  )
}

export function Allocations() {
  const client = useQueryClient()
  const [params, setParams] = useSearchParams()
  const [refreshId, setRefreshId] = useState(0)
  const result = useQuery({
    queryKey: allocationKey,
    queryFn: ({ signal }) => api.allocations(signal),
    staleTime: 0,
    refetchInterval: query => query.state.data?.stocks.some(stock => stock.active) ? 2000 : false,
  })
  const stock = result.data?.stocks.find(entry => entry.sku === params.get("sku")) ?? result.data?.stocks[0]
  async function refresh() {
    const next = await result.refetch()
    if (next.isSuccess) setRefreshId(previous => previous + 1)
  }
  return (
    <div className="space-y-6">
      <PageHeading title="Distribuição entre canais" description="Escolha quanto do estoque disponível publicar em cada anúncio. Numa transferência, a redução precisa ser confirmada antes do aumento."
        action={<TransferScenario onLoaded={async () => {
          await Promise.all(stockQueries.map(queryKey => client.invalidateQueries({ queryKey })))
          setParams({}, { replace: true })
          setRefreshId(previous => previous + 1)
        }} />} />
      {result.isPending && <Loading>Consultando estoque e canais…</Loading>}
      {result.isError && <ErrorNotice error={result.error} onRetry={() => void refresh()} />}
      {result.data && !stock && <section className="rounded-xl border bg-card"><EmptyState title="Nenhum SKU para distribuir" description="Importe um CSV do ERP ou carregue o cenário de transferência." action={<Button variant="outline" asChild><Link to="/importacoes">Ir para importações</Link></Button>} /></section>}
      {stock && result.data && <>
        <div className="flex flex-wrap items-center justify-between gap-4 rounded-xl border bg-secondary/60 px-5 py-4">
          <div className="min-w-0 space-y-2">
            <Label htmlFor="allocated-sku">SKU do ERP</Label>
            <Select value={stock.sku} onValueChange={sku => setParams({ sku }, { replace: true })}>
              <SelectTrigger id="allocated-sku" className="max-w-full bg-card font-mono"><SelectValue /></SelectTrigger>
              <SelectContent>{result.data.stocks.map(entry => <SelectItem key={entry.sku} value={entry.sku}>{entry.sku}</SelectItem>)}</SelectContent>
            </Select>
          </div>
          <div className="flex items-center gap-3"><Package className="text-primary" aria-hidden="true" /><div><p className="text-sm text-muted-foreground">Disponível no ERP</p><p className="text-xl font-semibold tabular-nums">{stock.availableQuantity} un.</p></div></div>
        </div>
        <AllocationForm key={[stock.sku, stock.revision, stock.latestBatchId, stock.active, refreshId].join("/")} stock={stock} listings={result.data.listings} refresh={refresh} />
      </>}
    </div>
  )
}
