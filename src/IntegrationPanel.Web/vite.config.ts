import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Em desenvolvimento, /api vai para a API (.NET) na porta 5180.
// No build, a tela compilada vai para o wwwroot da API: um único processo serve tudo.
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': 'http://localhost:5180' } },
  build: { outDir: '../IntegrationPanel.Api/wwwroot', emptyOutDir: true },
})
