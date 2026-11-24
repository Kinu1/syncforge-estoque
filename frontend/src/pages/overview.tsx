import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { ArrowDownUp, ArrowRight, CircleCheck, FlaskConical, Import, Link2Off } from "lucide-react"
import { Link, useNavigate } from "react-router"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { EmptyState, ErrorNotice, Loading, PageHeading } from "@/components/operations"
import { api } from "@/lib/api"
import { batchKey, divergenceKey, listingKey } from "@/lib/operations"

export function Overview() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [scenarioOpen, setScenarioOpen] = useState(false)
  const stock = useQuery({ queryKey: divergenceKey, queryFn: ({ signal }) => api.divergences(signal) })
  const mixed = useMutation({
    mutationFn: api.loadScenario,
    async onSuccess() {
      await Promise.all([divergenceKey, listingKey, batchKey].map((queryKey) =>
        queryClient.invalidateQueries({ queryKey }),
      ))
      navigate("/divergencias")
    },
  })

  const counts = { aligned: 0, divergent: 0, unlinked: 0 }
  for (const row of stock.data ?? []) counts[row.status] += 1

  const metrics = [
    { label: "Saldos alinhados", value: counts.aligned, icon: CircleCheck, color: "bg-emerald-100 text-emerald-900" },
    { label: "Divergências", value: counts.divergent, icon: ArrowDownUp, color: "bg-amber-100 text-amber-950" },
    { label: "Sem vínculo", value: counts.unlinked, icon: Link2Off, color: "bg-stone-200 text-stone-800" },
  ]

  return (
    <div className="space-y-7 sm:space-y-9">
      <PageHeading
        title="Visão geral"
        description="Compare o estoque disponível no ERP com o saldo publicado no marketplace."
        action={<Button asChild size="lg"><Link to="/importacoes"><Import aria-hidden="true" /> Importar estoque</Link></Button>}
      />

      <section aria-label="Resumo do estoque" className="grid gap-3 sm:grid-cols-3">
        {metrics.map(({ label, value, icon: Icon, color }) => (
          <div className="rounded-xl border bg-card px-4 py-4 sm:px-5" key={label}>
            <div className="flex items-center gap-3">
              <span className={"grid size-9 shrink-0 place-items-center rounded-md " + color}><Icon className="size-[1.1rem]" aria-hidden="true" /></span>
              <span className="text-sm font-medium text-muted-foreground">{label}</span>
            </div>
            <p className="mt-3 text-3xl font-semibold tracking-tight tabular-nums">{stock.isSuccess ? value : "—"}</p>
          </div>
        ))}
      </section>

      <section aria-labelledby="stock-heading" className="rounded-xl border bg-card">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b px-5 py-4">
          <div>
            <h2 id="stock-heading" className="font-semibold tracking-tight">Estoque comparado</h2>
            <p className="mt-0.5 text-sm text-muted-foreground">Situação dos produtos do último estoque importado.</p>
          </div>
          <Button asChild variant="outline"><Link to="/divergencias">Ver divergências <ArrowRight aria-hidden="true" /></Link></Button>
        </div>
        {stock.isPending && <Loading>Consultando saldos…</Loading>}
        {stock.isError && <div className="p-5"><ErrorNotice error={stock.error} onRetry={() => void stock.refetch()} /></div>}
        {stock.isSuccess && stock.data.length === 0 && <EmptyState title="Nenhum estoque importado" description="Importe um CSV do ERP ou carregue o cenário de demonstração." />}
        {stock.isSuccess && stock.data.length > 0 && (
          <div className="px-5 py-6">
            <p className="mb-3 text-sm text-muted-foreground"><strong className="font-semibold text-foreground">{stock.data.length}</strong> produtos comparados</p>
            <div className="flex h-2 gap-0.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
              {counts.aligned > 0 && <div className="bg-emerald-600" style={{ flex: counts.aligned }} />}
              {counts.divergent > 0 && <div className="bg-amber-500" style={{ flex: counts.divergent }} />}
              {counts.unlinked > 0 && <div className="bg-stone-400" style={{ flex: counts.unlinked }} />}
            </div>
          </div>
        )}
      </section>

      <section aria-labelledby="scenario-heading" className="rounded-xl border border-[#cdded0] bg-[#e9f2e8] p-5 sm:p-6">
        <div className="flex flex-wrap items-center gap-4">
          <span className="grid size-10 place-items-center rounded-md border border-[#bcd4c4] bg-white/75 text-emerald-900"><FlaskConical className="size-5" aria-hidden="true" /></span>
          <div className="grow basis-56">
            <h2 id="scenario-heading" className="font-semibold text-[#183829]">Cenário de demonstração</h2>
            <p className="mt-1 text-sm leading-relaxed text-[#385b47]">Pratique com três produtos fictícios e acompanhe respostas diferentes do marketplace simulado.</p>
          </div>
          <Dialog open={scenarioOpen} onOpenChange={(open) => { if (!mixed.isPending) setScenarioOpen(open) }}>
            <DialogTrigger asChild>
              <Button variant="outline" className="bg-white text-[#23362c]">
                Carregar cenário misto <ArrowRight aria-hidden="true" />
              </Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Carregar cenário misto?</DialogTitle>
                <DialogDescription>O cenário substitui as importações, vínculos e lotes desta sessão por dados fictícios.</DialogDescription>
              </DialogHeader>
              {mixed.isError && <ErrorNotice error={mixed.error} />}
              <DialogFooter>
                <Button variant="outline" onClick={() => setScenarioOpen(false)} disabled={mixed.isPending}>Cancelar</Button>
                <Button disabled={mixed.isPending} onClick={() => mixed.mutate()}>{mixed.isPending ? "Carregando…" : "Carregar e substituir"}</Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </section>
    </div>
  )
}
