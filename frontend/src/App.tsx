import { useEffect, useRef } from "react"
import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query"
import { ArrowDownUp, Boxes, History, LayoutGrid, Split, Upload, Warehouse } from "lucide-react"
import { BrowserRouter, Link, NavLink, Route, Routes, useLocation } from "react-router"
import { EmptyState, ErrorNotice, Loading } from "@/components/operations"
import { Button } from "@/components/ui/button"
import { api } from "@/lib/api"
import { dateTime } from "@/lib/operations"
import { cn } from "@/lib/utils"
import { BatchDetailRoute, Batches } from "@/pages/batches"
import { Divergences } from "@/pages/divergences"
import { Imports } from "@/pages/imports"
import { Overview } from "@/pages/overview"
import { Allocations } from "@/pages/allocations"
import "./workspace.css"

const client = new QueryClient({
  defaultOptions: {
    queries: { retry: false, staleTime: 5000 },
  },
})

const nav = [
  { to: "/", label: "Visão geral", icon: LayoutGrid },
  { to: "/importacoes", label: "Importações", icon: Upload },
  { to: "/divergencias", label: "Divergências", icon: ArrowDownUp },
  { to: "/distribuicao", label: "Distribuição", icon: Split },
  { to: "/lotes", label: "Lotes", icon: History },
]

function Workspace() {
  const { pathname } = useLocation()
  const main = useRef<HTMLElement>(null)
  const previous = useRef(pathname)
  const session = useQuery({
    queryKey: ["demo-session"],
    queryFn: ({ signal }) => api.session(signal),
    staleTime: Infinity,
  })

  useEffect(() => {
    if (previous.current === pathname) return
    previous.current = pathname
    main.current?.focus()
  }, [pathname])

  return (
    <div className="min-h-dvh lg:grid lg:grid-cols-[15rem_minmax(0,1fr)]">
      <a href="#conteudo" className="sr-only focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-50 focus:rounded-md focus:bg-card focus:p-3 focus:shadow-md">Pular para o conteúdo</a>
      <aside className="border-b bg-card lg:sticky lg:top-0 lg:flex lg:h-dvh lg:flex-col lg:border-r lg:border-b-0">
        <Link to="/" className="flex w-fit items-center gap-2.5 px-4 py-4 sm:px-6 lg:my-3">
          <span className="grid size-9 place-items-center rounded-lg bg-primary text-primary-foreground"><Boxes className="size-[1.15rem]" aria-hidden="true" /></span>
          <span>
            <span className="block text-lg leading-5 font-semibold tracking-tight">SyncForge</span>
            <span className="block text-xs text-muted-foreground">Operação de estoque</span>
          </span>
        </Link>
        <nav aria-label="Navegação da operação" className="grid grid-cols-5 gap-1 px-2 pb-3 sm:px-4 lg:grid-cols-1 lg:gap-1.5 lg:px-4">
          {nav.map(({ to, label, icon: Icon }) => (
            <NavLink key={to} to={to} end={to === "/"} className={({ isActive }) => cn(
              "flex min-h-12 min-w-0 flex-col items-center justify-center gap-1 rounded-lg px-1 py-2 text-center text-[11px] font-medium leading-tight outline-offset-2 hover:bg-muted sm:flex-row sm:gap-2 sm:text-sm lg:justify-start lg:px-3",
              isActive ? "bg-[#e7efe7] text-[#164830]" : "text-muted-foreground",
            )}>
              <Icon className="size-4 shrink-0" aria-hidden="true" /> {label}
            </NavLink>
          ))}
        </nav>
        <div className="mt-auto hidden border-t px-5 py-5 lg:flex lg:items-center lg:gap-3">
          <Warehouse className="size-4 shrink-0 text-primary" aria-hidden="true" />
          <div className="text-xs leading-relaxed">
            <p className="font-semibold">Loja demonstração</p>
            <p className="text-muted-foreground">Canais simulados</p>
          </div>
        </div>
      </aside>

      <div className="min-w-0">
        <header className="hidden min-h-15 items-center justify-between gap-3 border-b px-8 text-sm text-muted-foreground sm:flex xl:px-10">
          <span>Operação <span className="px-1 text-stone-400">/</span> {nav.find(({ to }) => to !== "/" && pathname.startsWith(to))?.label ?? "Visão geral"}</span>
          {session.data && <div className="text-xs">Sessão de demonstração até <time dateTime={session.data.expiresAt}>{dateTime(session.data.expiresAt)}</time></div>}
        </header>
        <main id="conteudo" tabIndex={-1} ref={main} className="mx-auto max-w-6xl px-4 py-6 sm:px-8 sm:py-9 xl:px-10">
          {session.isPending && <Loading>Iniciando sessão de demonstração…</Loading>}
          {session.isError && (
            <div className="space-y-4">
              <h1 className="text-2xl font-semibold">Sessão indisponível</h1>
              <ErrorNotice error={session.error} onRetry={() => void session.refetch()} />
            </div>
          )}
          {session.isSuccess && (
            <Routes>
              <Route path="/" element={<Overview />} />
              <Route path="/importacoes" element={<Imports />} />
              <Route path="/divergencias" element={<Divergences />} />
              <Route path="/distribuicao" element={<Allocations />} />
              <Route path="/lotes" element={<Batches />} />
              <Route path="/lotes/:batchId" element={<BatchDetailRoute />} />
              <Route path="*" element={<EmptyState title="Página inexistente" description="Abra a visão geral para acessar as telas da operação." action={<Button asChild><Link to="/">Ir para a visão geral</Link></Button>} />} />
            </Routes>
          )}
        </main>
      </div>
    </div>
  )
}

export function App() {
  return (
    <QueryClientProvider client={client}>
      <BrowserRouter>
        <Workspace />
      </BrowserRouter>
    </QueryClientProvider>
  )
}

export default App
