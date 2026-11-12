import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, setToken, type ErrorItem, type Page, type Session } from './api'

type Tab = 'overview' | 'errors' | 'proposals' | 'assistant'

const ERROR_LABELS: Record<string, string> = {
  unmapped_status: 'Status sem de-para',
  missing_customer: 'Pedido sem cliente',
  negative_total: 'Total negativo',
  not_found_in_erp: 'Não encontrado no ERP',
  save_failed: 'Falha ao gravar',
  missing_id: 'Pedido sem id',
}

const label = (code: string | null) => (code ? ERROR_LABELS[code] ?? code : '-')
const when = (iso: string) => new Date(iso).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' })

export default function App() {
  const [session, setSession] = useState<Session | null>(() => {
    const saved = sessionStorage.getItem('session')
    const parsed = saved ? (JSON.parse(saved) as Session) : null
    if (parsed && new Date(parsed.expiresAtUtc) > new Date()) {
      setToken(parsed.token)
      return parsed
    }
    return null
  })
  const [tab, setTab] = useState<Tab>('overview')

  const signIn = (s: Session) => {
    setToken(s.token)
    sessionStorage.setItem('session', JSON.stringify(s))
    setSession(s)
  }

  const signOut = useCallback(() => {
    setToken(null)
    sessionStorage.removeItem('session')
    setSession(null)
  }, [])

  if (!session) {
    return <Login onSignIn={signIn} />
  }

  const canAct = session.role === 'Approver'

  return (
    <div className="shell">
      <header className="topbar">
        <div className="brand">
          <span className="logo">◆</span> Painel de Integrações
        </div>
        <nav className="tabs">
          {(
            [
              ['overview', 'Visão geral'],
              ['errors', 'Erros'],
              ['proposals', 'Propostas de de-para'],
              ['assistant', '✦ Assistente IA'],
            ] as const
          ).map(([id, text]) => (
            <button key={id} className={tab === id ? 'tab active' : 'tab'} onClick={() => setTab(id)}>
              {text}
            </button>
          ))}
        </nav>
        <div className="user">
          <span>
            {session.displayName} · <em>{canAct ? 'aprovador' : 'leitura'}</em>
          </span>
          <button className="ghost" onClick={signOut}>
            Sair
          </button>
        </div>
      </header>

      <main className="content">
        {tab === 'overview' && <Overview onAuthError={signOut} />}
        {tab === 'errors' && <Errors canAct={canAct} onAuthError={signOut} />}
        {tab === 'proposals' && <Proposals canAct={canAct} onAuthError={signOut} />}
        {tab === 'assistant' && <Assistant onAuthError={signOut} />}
      </main>
    </div>
  )
}

function useLoad<T>(load: () => Promise<T>, onAuthError: () => void) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(() => {
    setError(null)
    load()
      .then(setData)
      .catch((e: unknown) => {
        if (e instanceof ApiError && e.status === 401) onAuthError()
        setError(e instanceof Error ? e.message : 'Erro inesperado.')
      })
  }, [load, onAuthError])

  useEffect(reload, [reload])
  return { data, error, reload }
}

function Login({ onSignIn }: { onSignIn: (s: Session) => void }) {
  const [userName, setUserName] = useState('aprovador')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onSignIn(await api.login(userName, password))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Não foi possível entrar.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login">
      <form className="card login-card" onSubmit={submit}>
        <div className="brand big">
          <span className="logo">◆</span> Painel de Integrações
        </div>
        <p className="muted">Acompanhe erros, reprocesse pedidos e aprove de-para sugeridos pela IA.</p>
        <label>
          Usuário
          <input value={userName} onChange={(e) => setUserName(e.target.value)} autoComplete="username" />
        </label>
        <label>
          Senha
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
        </label>
        {error && <div className="alert">{error}</div>}
        <button className="primary" disabled={busy}>
          {busy ? 'Entrando…' : 'Entrar'}
        </button>
        <p className="hint">Demonstração: <code>aprovador</code> / <code>demo-aprovador</code> ou <code>leitor</code> / <code>demo-leitor</code></p>
      </form>
    </div>
  )
}

function Overview({ onAuthError }: { onAuthError: () => void }) {
  const { data, error } = useLoad(api.dashboard, onAuthError)
  if (error) return <div className="alert">{error}</div>
  if (!data) return <p className="muted">Carregando…</p>

  const rate = data.total ? ((data.errors + data.gaveUp) / data.total) * 100 : 0
  const max = Math.max(1, ...data.last7Days.map((d) => d.success + d.errors))

  return (
    <div className="grid">
      <Stat title="Itens processados" value={data.total} />
      <Stat title="Com sucesso" value={data.success} tone="ok" />
      <Stat title="Com erro" value={data.errors} tone="warn" note={`${rate.toFixed(1)}% do total, contando desistidos`} />
      <Stat title="Desistidos" value={data.gaveUp} tone="bad" note="passaram do limite de tentativas" />
      <Stat title="Propostas pendentes" value={data.pendingProposals} tone="accent" note="de-para sugerido pela IA" />

      <section className="card wide">
        <h3>Últimos 7 dias</h3>
        <div className="chart">
          {data.last7Days.map((d) => (
            <div key={d.day} className="bar-col" title={`${d.success} ok, ${d.errors} com erro`}>
              <div className="bar">
                <div className="bar-err" style={{ height: `${(d.errors / max) * 100}%` }} />
                <div className="bar-ok" style={{ height: `${(d.success / max) * 100}%` }} />
              </div>
              <span>{new Date(d.day + 'T12:00:00').toLocaleDateString('pt-BR', { weekday: 'short', day: '2-digit' })}</span>
            </div>
          ))}
        </div>
        <div className="legend">
          <span><i className="dot ok" /> sucesso</span>
          <span><i className="dot err" /> erro</span>
        </div>
      </section>

      <section className="card wide">
        <h3>Erros por tipo</h3>
        {data.errorsByCode.map((c) => (
          <div key={c.code} className="row-bar">
            <span>{label(c.code)}</span>
            <div className="track">
              <div style={{ width: `${(c.count / Math.max(...data.errorsByCode.map((x) => x.count))) * 100}%` }} />
            </div>
            <strong>{c.count}</strong>
          </div>
        ))}
      </section>
    </div>
  )
}

function Stat({ title, value, tone, note }: { title: string; value: number; tone?: string; note?: string }) {
  return (
    <section className={`card stat ${tone ?? ''}`}>
      <span className="muted">{title}</span>
      <strong>{value}</strong>
      {note && <small className="muted">{note}</small>}
    </section>
  )
}

function Errors({ canAct, onAuthError }: { canAct: boolean; onAuthError: () => void }) {
  const [code, setCode] = useState('')
  const [page, setPage] = useState(1)
  const [notice, setNotice] = useState<string | null>(null)
  const load = useCallback(() => api.errors(code, page), [code, page])
  const { data, error, reload } = useLoad<Page<ErrorItem>>(load, onAuthError)

  const reprocess = async (item: ErrorItem) => {
    try {
      setNotice((await api.reprocess(item.id)).message)
      reload()
    } catch (e) {
      setNotice(e instanceof Error ? e.message : 'Não foi possível reprocessar.')
    }
  }

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  return (
    <section className="card">
      <div className="toolbar">
        <h3>Itens com erro</h3>
        <select value={code} onChange={(e) => { setCode(e.target.value); setPage(1) }}>
          <option value="">Todos os tipos</option>
          {Object.entries(ERROR_LABELS).map(([value, text]) => (
            <option key={value} value={value}>{text}</option>
          ))}
        </select>
      </div>
      {notice && <div className="notice">{notice}</div>}
      {error && <div className="alert">{error}</div>}
      <table>
        <thead>
          <tr>
            <th>Pedido</th><th>Tipo</th><th>Detalhe</th><th>Situação</th><th>Tentativas</th><th>Atualizado</th>{canAct && <th />}
          </tr>
        </thead>
        <tbody>
          {data?.items.map((item) => (
            <tr key={item.id}>
              <td><code>{item.externalId}</code></td>
              <td>{label(item.errorCode)}</td>
              <td className="muted">{item.errorDetail ?? item.message}</td>
              <td><span className={`pill ${item.status === 'GaveUp' ? 'bad' : 'warn'}`}>{item.status === 'GaveUp' ? 'Desistido' : 'Na fila'}</span></td>
              <td>{item.attempts}</td>
              <td className="muted">{when(item.updatedAtUtc)}</td>
              {canAct && (
                <td><button className="small" onClick={() => reprocess(item)}>Reprocessar</button></td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
      <div className="pager">
        <button className="ghost" disabled={page <= 1} onClick={() => setPage(page - 1)}>‹ Anterior</button>
        <span className="muted">Página {page} de {pages} · {data?.total ?? 0} itens</span>
        <button className="ghost" disabled={page >= pages} onClick={() => setPage(page + 1)}>Próxima ›</button>
      </div>
    </section>
  )
}

function Proposals({ canAct, onAuthError }: { canAct: boolean; onAuthError: () => void }) {
  const { data, error, reload } = useLoad(api.proposals, onAuthError)
  const statuses = useLoad(api.storeStatuses, onAuthError).data ?? []
  const [choice, setChoice] = useState<Record<number, string>>({})
  const [notice, setNotice] = useState<string | null>(null)

  const act = async (fn: () => Promise<{ message: string; reopenedOrders?: number }>) => {
    try {
      const r = await fn()
      setNotice(r.reopenedOrders ? `${r.message} ${r.reopenedOrders} pedido(s) voltaram para a fila.` : r.message)
      reload()
    } catch (e) {
      setNotice(e instanceof Error ? e.message : 'Não foi possível concluir.')
    }
  }

  return (
    <div className="stack">
      <p className="muted">
        Quando o ERP envia um status novo, a IA sugere o de-para. <strong>Nada muda até uma pessoa aprovar.</strong>
      </p>
      {notice && <div className="notice">{notice}</div>}
      {error && <div className="alert">{error}</div>}
      {data?.map((p) => (
        <section key={p.id} className={`card proposal ${p.state.toLowerCase()}`}>
          <div className="proposal-head">
            <div>
              <code>{p.erpStatus}</code> <span className="arrow">→</span> <strong>{p.suggestedStoreStatus}</strong>
            </div>
            <span className={`pill ${p.state === 'Pending' ? 'accent' : p.state === 'Approved' ? 'ok' : 'bad'}`}>
              {p.state === 'Pending' ? 'Aguardando' : p.state === 'Approved' ? 'Aprovada' : 'Recusada'}
            </span>
          </div>
          <p>{p.reason}</p>
          <small className="muted">
            {p.affectedOrders} pedido(s) parado(s) · sugerido por {p.source}
            {p.decidedBy && ` · decidido por ${p.decidedBy}`}
            {p.decisionNote && ` · "${p.decisionNote}"`}
          </small>
          {canAct && p.state === 'Pending' && (
            <div className="actions">
              <select value={choice[p.id] ?? p.suggestedStoreStatus} onChange={(e) => setChoice({ ...choice, [p.id]: e.target.value })}>
                {statuses.map((s) => <option key={s}>{s}</option>)}
              </select>
              <button className="primary small" onClick={() => act(() => api.approve(p.id, choice[p.id] ?? p.suggestedStoreStatus, 'Aprovado pelo painel'))}>
                Aprovar
              </button>
              <button
                className="ghost small"
                onClick={() => {
                  const note = window.prompt('Motivo da recusa:')
                  if (note) act(() => api.reject(p.id, note))
                }}
              >
                Recusar
              </button>
            </div>
          )}
        </section>
      ))}
    </div>
  )
}

interface Turn {
  who: 'user' | 'ai'
  text: string
  tools?: string[]
}

const TOOL_LABELS: Record<string, string> = {
  get_error_summary: 'resumo de erros',
  get_top_unmapped_statuses: 'status sem de-para',
  get_recent_errors: 'erros recentes',
}

function Assistant({ onAuthError }: { onAuthError: () => void }) {
  const [turns, setTurns] = useState<Turn[]>([
    { who: 'ai', text: 'Olá! Pergunte sobre a saúde das integrações. Eu consulto os dados e respondo; aprovar e reprocessar continuam com você.' },
  ])
  const [question, setQuestion] = useState('')
  const [busy, setBusy] = useState(false)
  const suggestions = ['Como foram as integrações esta semana?', 'Quais status estão sem de-para?', 'Quais foram os últimos erros?']

  const ask = async (q: string) => {
    if (!q.trim() || busy) return
    setTurns((t) => [...t, { who: 'user', text: q }])
    setQuestion('')
    setBusy(true)
    try {
      const r = await api.ask(q)
      setTurns((t) => [...t, { who: 'ai', text: r.answer, tools: r.toolsUsed }])
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) onAuthError()
      setTurns((t) => [...t, { who: 'ai', text: e instanceof Error ? e.message : 'Não consegui responder.' }])
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card chat">
      <div className="messages">
        {turns.map((t, i) => (
          <div key={i} className={`msg ${t.who}`}>
            <div className="bubble">{t.text}</div>
            {t.tools && t.tools.length > 0 && (
              <small className="muted">consultou: {t.tools.map((x) => TOOL_LABELS[x] ?? x).join(', ')}</small>
            )}
          </div>
        ))}
        {busy && <div className="msg ai"><div className="bubble typing">consultando os dados…</div></div>}
      </div>
      <div className="suggestions">
        {suggestions.map((s) => (
          <button key={s} className="chip" onClick={() => ask(s)} disabled={busy}>{s}</button>
        ))}
      </div>
      <form className="composer" onSubmit={(e) => { e.preventDefault(); ask(question) }}>
        <input value={question} maxLength={500} onChange={(e) => setQuestion(e.target.value)} placeholder="Pergunte em português…" />
        <button className="primary" disabled={busy || !question.trim()}>Perguntar</button>
      </form>
    </section>
  )
}
