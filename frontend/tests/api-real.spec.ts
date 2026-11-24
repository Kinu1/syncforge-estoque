import { expect, test } from "@playwright/test"

test.skip(process.env.SYNCFORGE_E2E_REAL !== "1", "Requer a operação local completa.")

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
