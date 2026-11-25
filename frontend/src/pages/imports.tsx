import { useState, type FormEvent } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { ArrowRight, Download, FileSpreadsheet } from "lucide-react"
import { useNavigate } from "react-router"
import { ErrorNotice, PageHeading } from "@/components/operations"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { api, type ImportPreview } from "@/lib/api"
import { allocationKey, divergenceKey } from "@/lib/operations"

function localCaptureTime() {
  const now = new Date()
  return new Date(now.getTime() - now.getTimezoneOffset() * 60000).toISOString().slice(0, 16)
}

export function Imports() {
  const navigate = useNavigate()
  const client = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [capturedAt, setCapturedAt] = useState(localCaptureTime)
  const [preview, setPreview] = useState<ImportPreview | null>(null)

  const validate = useMutation({
    async mutationFn({ selectedFile, time }: { selectedFile: File; time: string }) {
      if (selectedFile.size > 64 * 1024) throw new Error("O CSV precisa ter até 64 KB.")
      const timestamp = new Date(time)
      if (Number.isNaN(timestamp.getTime())) throw new Error("Informe uma data e hora válidas.")
      return api.previewImport(await selectedFile.text(), timestamp.toISOString())
    },
    onSuccess: setPreview,
  })
  const confirm = useMutation({
    mutationFn: api.confirmImport,
    async onSuccess() {
      await Promise.all([divergenceKey, allocationKey].map(queryKey => client.invalidateQueries({ queryKey })))
      navigate("/divergencias", { state: { notice: "Estoque importado. Confira os saldos comparados." } })
    },
  })

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (file) {
      setPreview(null)
      confirm.reset()
      validate.mutate({ selectedFile: file, time: capturedAt })
    }
  }

  const busy = validate.isPending || confirm.isPending
  return (
    <div className="space-y-6">
      <PageHeading title="Importações" description="Carregue o estoque físico e reservado do ERP e confira o saldo disponível antes de confirmar." />
      <form onSubmit={submit} className="rounded-xl border bg-card">
        <div className="flex flex-wrap items-start justify-between gap-3 border-b px-5 py-4 sm:px-6">
          <div className="flex items-start gap-3">
            <span className="grid size-9 shrink-0 place-items-center rounded-md bg-secondary text-primary"><FileSpreadsheet className="size-5" aria-hidden="true" /></span>
            <div>
              <h2 className="font-semibold">Arquivo de estoque</h2>
              <p className="mt-0.5 text-sm text-muted-foreground">CSV em UTF-8, até 25 produtos e 64 KB.</p>
            </div>
          </div>
          <Button asChild variant="outline"><a href="/estoque-exemplo.csv" download><Download aria-hidden="true" /> Baixar modelo</a></Button>
        </div>
        <fieldset disabled={busy} className="grid gap-4 px-5 py-5 sm:grid-cols-[minmax(0,1fr)_minmax(0,16rem)] sm:px-6">
          <div className="min-w-0 space-y-2">
            <Label htmlFor="stock-file">Selecionar CSV</Label>
            <Input id="stock-file" type="file" accept=".csv,text/csv" required className="h-10 cursor-pointer" onChange={(event) => {
              setFile(event.target.files?.[0] ?? null)
              setPreview(null)
              validate.reset()
              confirm.reset()
            }} aria-describedby="csv-headers" />
            <p id="csv-headers" className="break-words text-xs leading-relaxed text-muted-foreground">
              Colunas <code>sku</code>, <code>physical_quantity</code> e <code>reserved_quantity</code>.
            </p>
          </div>
          <div className="min-w-0 space-y-2">
            <Label htmlFor="capture-time">Data e hora da captura</Label>
            <Input id="capture-time" type="datetime-local" required className="h-10" value={capturedAt} onChange={(event) => {
              setCapturedAt(event.target.value)
              setPreview(null)
              validate.reset()
            }} />
          </div>
        </fieldset>
        {validate.isError && <div className="px-5 pb-4 sm:px-6"><ErrorNotice error={validate.error} /></div>}
        <div className="flex justify-end border-t bg-muted/30 px-5 py-4 sm:px-6">
          <Button type="submit" disabled={!file || busy}>
            {validate.isPending ? "Validando CSV…" : "Validar arquivo"} <ArrowRight aria-hidden="true" />
          </Button>
        </div>
      </form>

      {preview && (
        <section aria-labelledby="preview-heading" className="rounded-xl border bg-card">
          <div className="border-b px-5 py-4 sm:px-6" role="status">
            <h2 id="preview-heading" className="font-semibold">Prévia da importação</h2>
            <p className="mt-0.5 text-sm text-muted-foreground">{preview.rows.length} linhas válidas · {preview.errors.length} erros</p>
          </div>
          {preview.errors.length > 0 && (
            <div className="px-5 py-4 sm:px-6">
              <Alert variant="destructive">
                <AlertTitle>Corrija o CSV antes de confirmar</AlertTitle>
                <AlertDescription>
                  <ul className="list-disc space-y-1 pl-4">
                    {preview.errors.map(({ line, message }, index) => (
                      <li key={index}>Linha {line} — {message}</li>
                    ))}
                  </ul>
                </AlertDescription>
              </Alert>
            </div>
          )}
          {preview.rows.length > 0 && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="pl-5 sm:pl-6">SKU</TableHead>
                  <TableHead className="whitespace-normal"><span className="hidden sm:inline">Estoque </span>físico</TableHead>
                  <TableHead className="whitespace-normal">Reservado</TableHead>
                  <TableHead className="whitespace-normal">Disponível</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {preview.rows.map((row) => (
                  <TableRow key={row.line}>
                    <TableCell className="pl-5 font-mono text-xs font-medium sm:pl-6">{row.sku}</TableCell>
                    <TableCell className="tabular-nums">{row.physicalQuantity}</TableCell>
                    <TableCell className="tabular-nums">{row.reservedQuantity}</TableCell>
                    <TableCell className="font-semibold tabular-nums">{row.availableQuantity}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
          {confirm.isError && <div className="px-5 py-4 sm:px-6"><ErrorNotice error={confirm.error} /></div>}
          <div className="flex flex-wrap items-center justify-between gap-3 border-t bg-muted/30 px-5 py-4 sm:px-6">
            <p className="text-sm text-muted-foreground">Produtos ausentes do CSV mantêm o saldo anterior.</p>
            <Button disabled={busy || !preview.rows.length || Boolean(preview.errors.length)} onClick={() => confirm.mutate(preview.id)}>
              {confirm.isPending ? "Confirmando…" : "Confirmar importação"}
            </Button>
          </div>
        </section>
      )}
    </div>
  )
}
