import { expect, test } from "@playwright/test"

test.skip(process.env.SYNCFORGE_E2E_REAL !== "1", "Requer a operação local completa.")

test("transfere duas unidades após a confirmação da redução no primeiro canal", async ({ page }) => {
  await page.goto("/")
  await page.getByRole("link", { name: "Distribuição", exact: true }).click()
  await page.getByRole("button", { name: "Carregar cenário de transferência" }).click()
  await page.getByRole("dialog").getByRole("button", { name: "Carregar e substituir" }).click()

  const quantityA = page.getByRole("spinbutton", { name: "Quantidade para o canal A" })
  const quantityB = page.getByRole("spinbutton", { name: "Quantidade para o canal B" })
  await expect(quantityA).toHaveValue("4")
  await expect(quantityB).toHaveValue("3")
  await quantityB.fill("5")
  await expect(page.getByText("A soma excede o disponível em 2 un.")).toBeVisible()
  const submit = page.getByRole("button", { name: "Enviar distribuição" })
  await expect(submit).toBeDisabled()
  await quantityA.fill("2")
  await expect(page.getByText("7 un. planejadas · 0 un. livres")).toBeVisible()
  await submit.click()

  const channelB = page.getByRole("listitem").filter({ has: page.getByRole("heading", { name: "DEMO-001 · Canal B" }) })
  await expect(channelB.getByText("Saldo planejado de 5 un.")).toBeVisible()
  await expect(page.getByText("2 de 2 sincronizados")).toBeVisible({ timeout: 30_000 })
  await channelB.locator("summary").click()
  await expect(channelB.getByRole("cell", { name: "Aguardando redução" })).toBeVisible()
  await page.getByRole("link", { name: "Conferir saldos" }).click()
  const observed = page.getByRole("group", { name: "Canal B", exact: true })
  await expect(observed.locator("dd").first()).toHaveText("5")
  await expect(page.getByRole("group", { name: "Canal A", exact: true }).locator("dd").first()).toHaveText("2")
})

test("executa o cenário misto contra a API e o worker", async ({ page }) => {
  await page.goto("/")
  await page.getByRole("button", { name: "Carregar cenário misto" }).click()
  await page.getByRole("button", { name: "Carregar e substituir" }).click()
  for (const sku of ["DEMO-001", "DEMO-002", "DEMO-003"]) {
    await page.getByRole("checkbox", { name: "Selecionar " + sku }).check()
  }
  await page.getByRole("button", { name: "Sincronizar seleção" }).click()
  await expect(page.getByText("2 de 3 sincronizados")).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText("Conflito", { exact: true }).filter({ visible: true })).toBeVisible()
})

test("importa o CSV de exemplo e vincula um anúncio na API local", async ({ page }) => {
  await page.goto("/importacoes")
  await page.getByLabel("Selecionar CSV").setInputFiles("public/estoque-exemplo.csv")
  await page.getByRole("button", { name: "Validar arquivo" }).click()
  await expect(page.getByText("3 linhas válidas · 0 erros")).toBeVisible()
  await page.getByRole("button", { name: "Confirmar importação" }).click()
  await expect(page.getByRole("checkbox", { name: "Selecionar DEMO-001" })).toBeVisible()
  await page.getByRole("button", { name: "Vincular DEMO-002" }).click()
  const dialog = page.getByRole("dialog")
  await dialog.getByRole("combobox", { name: "Anúncio" }).click()
  await page.getByRole("option", { name: /Caderno pontilhado/ }).click()
  await dialog.getByRole("button", { name: "Salvar vínculo" }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByRole("checkbox", { name: "Selecionar DEMO-002" })).toBeVisible()
})
