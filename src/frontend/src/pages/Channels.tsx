import { useState } from "react";
import { Send, MessageSquare, CheckCircle2, XCircle, Loader2, Info } from "lucide-react";
import PageHeader from "../components/PageHeader";
import InfoTip, { TipBody } from "../components/InfoTip";
import { useListChannelsQuery, useTestChannelMutation, type Channel } from "../store/api";

// How to obtain each channel's credentials — surfaced in-app so setup is self-serve (Phase 7 intent).
const SETUP: Record<string, { steps: string[] }> = {
  telegram: {
    steps: [
      "Open Telegram and message @BotFather → /newbot → copy the API token.",
      "Set TELEGRAM_BOT_TOKEN in .env.",
      "Message your new bot once; your numeric chat id is captured for the allowlist + notifications.",
      "Restart the API to pick up the token."
    ]
  },
  discord: {
    steps: [
      "discord.com/developers → New Application → Bot → Reset Token → copy.",
      "On the Bot page, toggle ON 'MESSAGE CONTENT INTENT' (required to read commands).",
      "In Discord: Settings → Advanced → Developer Mode ON, then right-click the channel → Copy Channel ID.",
      "Set DISCORD_BOT_TOKEN + DISCORD_CHANNEL_ID in .env and restart."
    ]
  }
};

function ChannelIcon({ id }: { id: string }) {
  return id === "telegram" ? <Send className="h-5 w-5" /> : <MessageSquare className="h-5 w-5" />;
}

function ChannelCard({ channel }: { channel: Channel }) {
  const [test, { isLoading }] = useTestChannelMutation();
  const [result, setResult] = useState<{ ok: boolean; detail: string } | null>(null);

  const runTest = async () => {
    setResult(null);
    try {
      const r = await test(channel.id).unwrap();
      setResult(r);
    } catch {
      setResult({ ok: false, detail: "Request failed." });
    }
  };

  const setup = SETUP[channel.id];

  return (
    <div className="fa-card p-5 flex flex-col gap-4">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-3 text-fa-frost-bright">
          <ChannelIcon id={channel.id} />
          <span className="text-lg font-light">{channel.name}</span>
          {setup && (
            <InfoTip
              width={300}
              content={
                <TipBody title={`Configure ${channel.name}`}>
                  <ol className="list-decimal pl-4 space-y-1">
                    {setup.steps.map((s, i) => (
                      <li key={i}>{s}</li>
                    ))}
                  </ol>
                </TipBody>
              }
            >
              <button aria-label="How to configure" className="text-fa-frost-dim hover:text-fa-frost-bright transition">
                <Info className="h-4 w-4" />
              </button>
            </InfoTip>
          )}
        </div>
        <span
          className={
            "text-[11px] px-2 py-0.5 rounded-full border " +
            (channel.configured
              ? "border-emerald-500/40 text-emerald-300 bg-emerald-500/10"
              : "border-fa-edge text-fa-frost-dim bg-fa-glass")
          }
        >
          {channel.configured ? "Configured" : "Not configured"}
        </span>
      </div>

      <dl className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm">
        <dt className="text-fa-frost-dim">Notify target</dt>
        <dd className="text-fa-frost text-right">{channel.notifyTarget ?? "—"}</dd>
        <dt className="text-fa-frost-dim">Allowlisted</dt>
        <dd className="text-fa-frost text-right">{channel.allowlistCount}</dd>
        <dt className="text-fa-frost-dim">Commands · rich</dt>
        <dd className="text-fa-frost text-right">
          {channel.supportsCommands ? "yes" : "no"} · {channel.supportsRichContent ? "yes" : "no"}
        </dd>
      </dl>

      <div className="flex items-center gap-3">
        <button
          onClick={runTest}
          disabled={isLoading || !channel.configured}
          className="inline-flex items-center gap-2 rounded-lg border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong disabled:opacity-40 disabled:cursor-not-allowed px-3 py-1.5 text-sm text-fa-frost-bright transition"
        >
          {isLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
          Test connection
        </button>
        {result && (
          <span className={"inline-flex items-center gap-1.5 text-sm " + (result.ok ? "text-emerald-300" : "text-rose-300")}>
            {result.ok ? <CheckCircle2 className="h-4 w-4" /> : <XCircle className="h-4 w-4" />}
            {result.detail}
          </span>
        )}
      </div>
    </div>
  );
}

export default function Channels() {
  const { data: channels, isLoading } = useListChannelsQuery();

  return (
    <div className="flex-1 overflow-y-auto">
      <PageHeader
        title="Channels"
        subtitle="Notification + command bots. Configure credentials in .env, then test connectivity here."
      />
      <div className="px-8 py-6 grid gap-5 max-w-3xl">
        {isLoading && <div className="text-fa-frost-dim text-sm">Loading channels…</div>}
        {channels?.map((c) => (
          <ChannelCard key={c.id} channel={c} />
        ))}
        <p className="text-xs text-fa-frost-dim leading-relaxed">
          Tokens are read from environment variables and never displayed here. Hover the info icon on each channel for
          step-by-step setup. After editing <code className="text-fa-frost">.env</code>, restart the API to apply.
        </p>
      </div>
    </div>
  );
}
