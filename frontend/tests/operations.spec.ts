import { createHash } from "node:crypto"
import { expect, test, type Page, type Route } from "@playwright/test"
import AxeBuilder from "@axe-core/playwright"
import type { BatchItem, Divergence } from "../src/lib/api"

const capturedAt = "2026-11-24T12:00:00.000Z"
const exampleCsv = "sku,physical_quantity,reserved_quantity\nDEMO-001,12,5"
const badCsv = "sku,physical_quantity,reserved_quantity\nDEMO-001,-1,0"
const row: Divergence = {
  sku: "DEMO-001",
  physicalQuantity: 12,
  reservedQuantity: 5,
  availableQuantity: 7,
  listingId: "LIST-001",
  observedQuantity: 10,
  observedVersion: 4,
  observedAt: capturedAt,
  revision: 1,
  status: "divergent",
}

function item(sku: string, status: BatchItem["status"], message: string | null = null): BatchItem {
  return {
    sku,
    listingId: "LIST-" + sku.slice(-3),
    desiredQuantity: 7,
    status,
    message,
    attempts: message ? [{ at: capturedAt, status, message }] : [],
  }
}

async function routeApi(page: Page, handle: (route: Route, path: string) => Promise<void>) {
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname.replace("/api/v1", "")
    if (request.method() !== "GET") {
      const hash = createHash("sha256").update(request.postDataBuffer() ?? Buffer.alloc(0)).digest("hex")
      expect(request.headers()["x-amz-content-sha256"]).toBe(hash)
    }

    if (path === "/demo/session") {
      await route.fulfill({ status: 200, json: { expiresAt: "2035-11-24T13:00:00.000Z" } })
    } else {
      await handle(route, path)
    }
  })
}

test("vincula os anúncios e revisa a distribuição após uma alteração concorrente", async ({ page }) => {
  let revision = 1
  const keys: string[] = []
  await routeApi(page, async (route, path) => {
    if (path === "/allocations") {
      await route.fulfill({ json: {
        stocks: [{ sku: "KIT", availableQuantity: 7, revision, plannedA: null, plannedB: null, latestBatchId: null, active: false }],
        listings: ["A", "B"].map(channel => ({
          listingId: "KIT-" + channel, name: "Kit de canetas", sku: null, channel, quantity: 0, version: 1,
        })),
      } })
    } else if (path === "/allocations/KIT") {
      keys.push(route.request().headers()["idempotency-key"])
      expect(route.request().postDataJSON()).toEqual({ revision,
        channelA: { listingId: "KIT-A", quantity: 4, version: 1 },
        channelB: { listingId: "KIT-B", quantity: 2, version: 1 },
      })
      if (revision === 1) {
        revision = 2
        await route.fulfill({ status: 409, json: { detail: "O estoque mudou." } })
      } else {
        await route.fulfill({ status: 202, json: { id: "lote-canais" } })
      }
    } else if (path === "/sync-batches/lote-canais") {
      await route.fulfill({ json: { id: "lote-canais", createdAt: capturedAt, items: [] } })
    } else {
      await route.fulfill({ status: 404 })
    }
  })

  await page.goto("/distribuicao")
  for (const attempt of [1, 2]) {
    for (const [channel, quantity] of [["A", "4"], ["B", "2"]]) {
      await page.getByRole("combobox", { name: "Anúncio do canal " + channel }).click()
      await page.getByRole("option", { name: "Kit de canetas · KIT-" + channel }).click()
      await page.getByRole("spinbutton", { name: "Quantidade para o canal " + channel }).fill(quantity)
    }
    await expect(page.getByText("6 un. planejadas · 1 un. livres")).toBeVisible()
    if (attempt === 1) {
      expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([])
    }
    await page.getByRole("button", { name: "Enviar distribuição" }).click()
    if (attempt === 1) {
      await expect(page.getByText("O estoque mudou.")).toBeVisible()
      await expect(page.getByRole("button", { name: "Enviar distribuição" })).toBeDisabled()
      await page.getByRole("button", { name: "Atualizar saldos e revisar" }).click()
    }
  }
  await expect(page).toHaveURL(/\/lotes\/lote-canais$/)
  expect(keys).toHaveLength(2)
  expect(keys.every(Boolean)).toBe(true)
  expect(keys[0]).not.toBe(keys[1])
})

test("valida e confirma o arquivo de estoque, com correção de linhas inválidas", async ({ page }) => {
  let confirmed = false
  const previewRow = {
    line: 2,
    sku: row.sku,
    physicalQuantity: 12,
    reservedQuantity: 5,
    availableQuantity: 7,
  }
  await routeApi(page, async (route, path) => {
    if (path === "/imports") {
      const { csv, capturedAt } = route.request().postDataJSON()
      expect(Number.isNaN(Date.parse(capturedAt))).toBe(false)
      const valid = csv === exampleCsv
      await route.fulfill({
        status: 201,
        json: {
          id: "imp-1",
          rows: valid ? [previewRow] : [],
          errors: valid ? [] : [{ line: 2, message: "O estoque físico deve ser não negativo." }],
        },
      })
    } else if (path === "/imports/imp-1/confirm") {
      confirmed = true
      await route.fulfill({ status: 204 })
    } else if (path === "/divergences") {
      await route.fulfill({ json: confirmed ? [row] : [] })
    } else {
      await route.fulfill({ status: 404 })
    }
  })

  await page.goto("/importacoes")
  const upload = page.getByLabel("Selecionar CSV")
  await upload.setInputFiles({ name: "invalido.csv", mimeType: "text/csv", buffer: Buffer.from(badCsv) })
  await page.getByRole("button", { name: "Validar arquivo" }).click()
  await expect(page.getByText("Linha 2 — O estoque físico deve ser não negativo.")).toBeVisible()
  await expect(page.getByRole("button", { name: "Confirmar importação" })).toBeDisabled()

  await upload.setInputFiles({ name: "estoque.csv", mimeType: "text/csv", buffer: Buffer.from(exampleCsv) })
  await page.getByRole("button", { name: "Validar arquivo" }).click()
  await expect(page.getByRole("heading", { name: "Prévia da importação" })).toBeVisible()
  await expect(page.getByRole("cell", { name: "DEMO-001", exact: true })).toBeVisible()
  await page.getByRole("button", { name: "Confirmar importação" }).click()
  await expect(page.getByRole("heading", { name: "Divergências", exact: true })).toBeVisible()
  await expect(page.getByText("Estoque importado. Confira os saldos comparados.")).toBeVisible()
  await expect(page.getByRole("checkbox", { name: "Selecionar DEMO-001" })).toBeVisible()
  expect(confirmed).toBe(true)
})

test("carrega o cenário, envia seleção e detalha um lote parcial", async ({ page }, testInfo) => {
  let seeded = false
  let detailReads = 0
  const rows = [row, { ...row, sku: "DEMO-002" }, { ...row, sku: "DEMO-003" }]
  await routeApi(page, async (route, path) => {
    if (path === "/demo/scenario") {
      expect(route.request().postDataJSON()).toEqual({ scenario: "mixed" })
      seeded = true
      await route.fulfill({ status: 204 })
    } else if (path === "/divergences") {
      await route.fulfill({ json: seeded ? rows : [] })
    } else if (path === "/sync-batches") {
      expect(route.request().headers()["idempotency-key"]).toBeTruthy()
      expect(route.request().postDataJSON()).toEqual({
        items: rows.map(({ sku, revision, observedVersion }) => ({ sku, revision, observedVersion })),
      })
      await route.fulfill({ status: 202, json: { id: "batch-1" } })
    } else if (path === "/sync-batches/batch-1") {
      detailReads += 1
      const items = detailReads === 1
        ? rows.map(({ sku }) => item(sku, "pending"))
        : [
            item("DEMO-001", "synchronized"),
            item("DEMO-002", "synchronized", "Aplicação confirmada após consulta."),
            item("DEMO-003", "conflict", "O saldo do anúncio foi alterado."),
          ]
      await route.fulfill({ json: { id: "batch-1", createdAt: capturedAt, items } })
    } else {
      await route.fulfill({ status: 404 })
    }
  })

  await page.goto("/")
  await expect(page.getByText("Nenhum produto em canal único")).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath("visao-geral.png"), fullPage: true })
  await page.getByRole("button", { name: "Carregar cenário misto" }).click()
  const dialog = page.getByRole("dialog")
  await expect(dialog).toBeVisible()
  await page.keyboard.press("Escape")
  await expect(dialog).toBeHidden()
  await expect(page.getByRole("button", { name: "Carregar cenário misto" })).toBeFocused()
  await page.getByRole("button", { name: "Carregar cenário misto" }).click()
  await dialog.getByRole("button", { name: "Carregar e substituir" }).click()

  for (const { sku } of rows) await page.getByRole("checkbox", { name: "Selecionar " + sku }).check()
  await expect(page.getByText("3 selecionados")).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath("divergencias.png"), fullPage: true })
  const dimensions = await page.evaluate(() => ({
    width: document.documentElement.clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
  }))
  expect(dimensions.scrollWidth).toBe(dimensions.width)
  const scan = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze()
  expect(scan.violations).toEqual([])

  await page.getByRole("button", { name: "Sincronizar seleção" }).click()
  await expect(page.getByText("2 de 3 sincronizados")).toBeVisible()
  await expect(page.getByText("Conflito", { exact: true }).filter({ visible: true })).toBeVisible()
  const reconciled = page.getByRole("listitem").filter({ has: page.getByRole("heading", { name: "DEMO-002" }) })
  await reconciled.getByText("Histórico de tentativas").click()
  await expect(reconciled.getByRole("cell", { name: "Aplicação confirmada após consulta." })).toBeVisible()
  await expect(page.getByRole("button", { name: /Reprocessar/ })).toHaveCount(0)
})

test("mantém a chave ao reenviar uma tentativa sem resposta", async ({ page }) => {
  const keys: string[] = []
  await routeApi(page, async (route, path) => {
    if (path === "/sync-batches/batch-1") {
      const status = keys.length > 1 ? "synchronized" : "failed"
      await route.fulfill({ json: { id: "batch-1", createdAt: capturedAt, items: [item("DEMO-001", status)] } })
    } else if (path === "/sync-batches/batch-1/items/DEMO-001/retry") {
      keys.push(route.request().headers()["idempotency-key"])
      if (keys.length === 1) await route.abort("failed")
      else await route.fulfill({ status: 202, json: { id: "batch-1" } })
    } else {
      await route.fulfill({ status: 404 })
    }
  })

  await page.goto("/lotes/batch-1")
  const retry = page.getByRole("button", { name: "Reprocessar DEMO-001" })
  await retry.click()
  await expect(page.getByRole("alert")).toContainText("Verifique a conexão")
  await retry.click()
  await expect(page.getByText("1 de 1 sincronizados")).toBeVisible()
  expect(keys[0]).toBeTruthy()
  expect(keys[1]).toBe(keys[0])
})

test("apresenta recuperação diante de problema ao iniciar a sessão", async ({ page }) => {
  let attempts = 0
  await page.route("**/api/v1/demo/session", async (route) => {
    attempts += 1
    if (attempts === 1) await route.fulfill({ status: 503, json: { detail: "Sessão temporariamente indisponível." } })
    else await route.fulfill({ json: { expiresAt: "2035-11-24T13:00:00.000Z" } })
  })
  await page.route("**/api/v1/divergences", (route) => route.fulfill({ json: [] }))
  await page.goto("/")
  await expect(page.getByText("Sessão temporariamente indisponível.")).toBeVisible()
  await page.getByRole("button", { name: "Tentar novamente" }).click()
  await expect(page.getByRole("heading", { name: "Visão geral" })).toBeVisible()
})
