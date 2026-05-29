import { useEffect, useMemo, useRef, useState } from "react";
import { Send, Sparkles } from "lucide-react";
import { Spinner } from "./ui";
import { useChatMarketMutation, useSuggestMarketQuestionsMutation } from "../store/api";

const DEFAULT_SUGGESTIONS = [
  "What signal would change your call?",
  "Walk me through your fair-value reasoning.",
  "What are the biggest risks to this position?",
  "Which sources are you weighting and why?"
];

function extractErrorMessage(e: unknown): string {
  // RTK Query mutation errors come back as { status, data } objects, not Error instances. The
  // server returns `{ error: "..." }` on InvalidOperationException; surface it verbatim so the
  // user sees real upstream failures (e.g., empty-model-response, content filter) rather than a
  // generic 'Chat failed.' message.
  if (e && typeof e === "object") {
    const anyE = e as { data?: { error?: string }; error?: string; message?: string };
    if (anyE.data?.error) return anyE.data.error;
    if (typeof anyE.error === "string") return anyE.error;
    if (anyE.message) return anyE.message;
  }
  if (e instanceof Error) return e.message;
  return "Chat failed.";
}

interface Message {
  role: "user" | "assistant";
  content: string;
}

// Tiny markdown renderer for the chat bubble — bold, lists, line breaks. Avoids pulling in a full
// parser for what is otherwise constrained LLM output.
function renderMarkdown(text: string): React.ReactNode {
  const lines = text.split("\n");
  const out: React.ReactNode[] = [];
  let listBuf: string[] = [];
  const flushList = (key: string) => {
    if (listBuf.length === 0) return;
    out.push(
      <ul key={`ul-${key}`} className="list-disc pl-5 space-y-0.5 my-1">
        {listBuf.map((l, i) => (
          <li key={i}>{inline(l)}</li>
        ))}
      </ul>
    );
    listBuf = [];
  };
  const inline = (s: string): React.ReactNode => {
    const parts = s.split(/(\*\*[^*]+\*\*)/g);
    return parts.map((p, i) =>
      p.startsWith("**") && p.endsWith("**")
        ? <strong key={i} className="text-fa-frost-bright">{p.slice(2, -2)}</strong>
        : <span key={i}>{p}</span>
    );
  };
  lines.forEach((raw, i) => {
    const line = raw.replace(/^\s+/, "");
    if (/^[-*]\s+/.test(line)) {
      listBuf.push(line.replace(/^[-*]\s+/, ""));
      return;
    }
    flushList(`b${i}`);
    if (line.length === 0) {
      out.push(<div key={i} className="h-2" />);
    } else {
      out.push(<p key={i} className="leading-snug">{inline(line)}</p>);
    }
  });
  flushList("end");
  return out;
}

export default function MarketChat({
  providerId,
  externalId
}: {
  providerId: string;
  externalId: string;
}) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [suggestions, setSuggestions] = useState<string[]>(DEFAULT_SUGGESTIONS);
  const [suggestionsLoading, setSuggestionsLoading] = useState(false);
  const [chat, { isLoading }] = useChatMarketMutation();
  const [suggest] = useSuggestMarketQuestionsMutation();
  const scrollerRef = useRef<HTMLDivElement | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);
  const autofiredKey = useRef<string | null>(null);

  const conversationKey = `${providerId}:${externalId}`;

  const refreshSuggestions = useMemo(() => async (history: Message[]) => {
    setSuggestionsLoading(true);
    try {
      const res = await suggest({
        providerId,
        externalId,
        messages: history.map((m) => ({ role: m.role, content: m.content }))
      }).unwrap();
      if (Array.isArray(res.suggestions) && res.suggestions.length > 0) {
        setSuggestions(res.suggestions);
      }
    } catch {
      // Keep prior suggestions on failure — the chip row should never blank out.
    } finally {
      setSuggestionsLoading(false);
    }
  }, [suggest, providerId, externalId]);

  const send = useMemo(() => async (history: Message[]) => {
    setError(null);
    try {
      const res = await chat({
        providerId,
        externalId,
        messages: history.map((m) => ({ role: m.role, content: m.content }))
      }).unwrap();
      const updated: Message[] = [...history, { role: "assistant", content: res.reply }];
      setMessages(updated);
      // Refresh suggestions with the latest turn so the chips stay contextual.
      void refreshSuggestions(updated);
    } catch (e) {
      setError(extractErrorMessage(e));
    }
  }, [chat, providerId, externalId, refreshSuggestions]);

  // Auto-fire the initial recommendation on first open per market. Cancelled if the user navigates
  // away (the component unmounts) — RTK Query handles abort.
  useEffect(() => {
    if (autofiredKey.current === conversationKey) return;
    autofiredKey.current = conversationKey;
    setMessages([]);
    setError(null);
    setSuggestions(DEFAULT_SUGGESTIONS);
    const opening: Message = {
      role: "user",
      content: "Give me your initial take on this market — call, confidence, fair-value estimate, and the key reasoning. Be decisive."
    };
    setMessages([opening]);
    void send([opening]);
    // Kick off an initial suggestion set tailored to this market's snapshot, in parallel with the
    // first chat. Will be overwritten the moment the first assistant reply lands.
    void refreshSuggestions([]);
  }, [conversationKey, send, refreshSuggestions]);

  const sendSuggestion = async (text: string) => {
    if (isLoading) return;
    const next: Message[] = [...messages, { role: "user", content: text }];
    setMessages(next);
    await send(next);
    inputRef.current?.focus();
  };

  // Scroll to the bottom whenever a message lands or the LLM starts thinking.
  useEffect(() => {
    const el = scrollerRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [messages, isLoading]);

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    const trimmed = input.trim();
    if (!trimmed || isLoading) return;
    const next: Message[] = [...messages, { role: "user", content: trimmed }];
    setMessages(next);
    setInput("");
    await send(next);
    inputRef.current?.focus();
  };

  return (
    <div className="fa-card flex flex-col shrink-0 overflow-hidden">
      <div className="flex items-center gap-2 px-4 py-3 border-b border-fa-edge">
        <Sparkles className="h-4 w-4 text-fa-frost-bright" />
        <h3 className="text-fa-frost-bright text-base font-medium">Foresight analyst</h3>
        <span className="text-fa-frost-dim text-xs ml-1">— ask the model anything about this market</span>
      </div>

      <div ref={scrollerRef} className="px-4 py-3 max-h-96 overflow-y-auto space-y-3">
        {messages.length === 0 && !isLoading && (
          <div className="text-fa-frost-dim text-sm">Initial recommendation loading…</div>
        )}
        {messages.map((m, i) => (
          <div
            key={i}
            className={`flex ${m.role === "user" ? "justify-end" : "justify-start"}`}
            style={{ animation: "fa-fade-up 240ms ease-out both" }}
          >
            <div
              className={`max-w-[88%] rounded-md px-3 py-2 text-sm leading-snug ${
                m.role === "user"
                  ? "bg-fa-frost/15 text-fa-frost-bright border border-fa-frost/25"
                  : "bg-fa-glass text-fa-frost border border-fa-edge"
              }`}
            >
              {m.role === "assistant" ? renderMarkdown(m.content) : <span className="whitespace-pre-wrap">{m.content}</span>}
            </div>
          </div>
        ))}
        {isLoading && (
          <div className="flex justify-start" style={{ animation: "fa-fade-up 240ms ease-out both" }}>
            <div className="rounded-md px-3 py-2 text-sm bg-fa-glass text-fa-frost-dim border border-fa-edge flex items-center gap-2">
              <Spinner /> Thinking…
            </div>
          </div>
        )}
        {error && (
          <div className="rounded-md border border-fa-danger/40 bg-fa-danger/10 text-fa-danger text-xs px-3 py-2">
            {error}
          </div>
        )}
      </div>

      <div
        className={`px-3 pt-3 pb-1 border-t border-fa-edge flex flex-wrap gap-1.5 transition-opacity duration-[var(--fa-duration)] ${suggestionsLoading ? "opacity-60" : "opacity-100"}`}
        style={{ transitionTimingFunction: "var(--fa-ease)" }}
      >
        {suggestions.map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => void sendSuggestion(s)}
            disabled={isLoading}
            className="text-[11px] rounded-full border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright px-2.5 py-1 transition disabled:opacity-40 disabled:cursor-not-allowed"
          >
            {s}
          </button>
        ))}
      </div>

      <form onSubmit={onSubmit} className="flex items-end gap-2 px-3 pb-3 pt-2">
        <textarea
          ref={inputRef}
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void onSubmit(e as unknown as React.FormEvent);
            }
          }}
          rows={1}
          placeholder="Ask anything about this market…"
          className="flex-1 resize-none rounded-md bg-fa-ink/60 border border-fa-edge px-3 py-2 text-sm text-fa-frost-bright placeholder:text-fa-frost-dim/70 focus:outline-none focus:border-fa-frost/50 transition"
          disabled={isLoading}
        />
        <button
          type="submit"
          disabled={isLoading || input.trim().length === 0}
          className="h-9 w-9 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright flex items-center justify-center transition disabled:opacity-40 disabled:cursor-not-allowed shrink-0"
          aria-label="Send"
        >
          {isLoading ? <Spinner /> : <Send className="h-4 w-4" />}
        </button>
      </form>
    </div>
  );
}
