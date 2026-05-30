import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { AlertTriangle, Check, Loader2, X } from "lucide-react";
import { cn } from "../lib/cn";

export interface ConfirmOptions {
  title: string;
  description?: React.ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Tints the icon + confirm button rose. Use for irreversible actions (delete, wipe). */
  destructive?: boolean;
  /** Optional async work to run when the user confirms. If it throws, the message is shown
   *  inline and the dialog stays open so the user can cancel or retry. */
  onConfirm?: () => void | Promise<void>;
}

type Resolver = (ok: boolean) => void;
type Internal = ConfirmOptions & { resolve: Resolver };

const ConfirmContext = createContext<((opts: ConfirmOptions) => Promise<boolean>) | null>(null);

/**
 * Drop-in replacement for the native `window.confirm`. Returns a promise that resolves true if
 * the user pressed the confirm button, false on cancel / dismiss / escape. The dialog matches
 * the FrostAura visual language (glass + backdrop blur + frost colours) and supports keyboard
 * (Escape to cancel, Enter to confirm) + click-outside-to-dismiss.
 *
 * Usage:
 *   const confirm = useConfirm();
 *   if (!await confirm({ title: "Delete?", description: "Cannot be undone.", destructive: true })) return;
 */
export function useConfirm() {
  const ctx = useContext(ConfirmContext);
  if (!ctx) throw new Error("useConfirm must be used inside <ConfirmProvider>");
  return ctx;
}

export function ConfirmProvider({ children }: { children: React.ReactNode }) {
  const [pending, setPending] = useState<Internal | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const confirm = useCallback((opts: ConfirmOptions) => {
    return new Promise<boolean>((resolve) => {
      setError(null);
      setBusy(false);
      setPending({ ...opts, resolve });
    });
  }, []);

  const close = useCallback((ok: boolean) => {
    if (!pending) return;
    pending.resolve(ok);
    setPending(null);
    setBusy(false);
    setError(null);
  }, [pending]);

  const onConfirm = async () => {
    if (!pending) return;
    if (pending.onConfirm) {
      try {
        setBusy(true);
        setError(null);
        await pending.onConfirm();
        close(true);
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        setError(msg || "Action failed");
        setBusy(false);
      }
    } else {
      close(true);
    }
  };

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      {pending && (
        <ConfirmDialog
          opts={pending}
          busy={busy}
          error={error}
          onCancel={() => close(false)}
          onConfirm={onConfirm}
        />
      )}
    </ConfirmContext.Provider>
  );
}

function ConfirmDialog({
  opts, busy, error, onCancel, onConfirm,
}: {
  opts: ConfirmOptions;
  busy: boolean;
  error: string | null;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const confirmRef = useRef<HTMLButtonElement>(null);

  // Focus the confirm button on open. For destructive actions a focused Cancel would be
  // safer, but consistency with most "are you sure" dialogs (and the user's expectation of
  // pressing Enter to confirm) wins out — they have to deliberately mouse-or-click.
  useEffect(() => {
    confirmRef.current?.focus();
  }, []);

  // Esc cancels, Enter confirms — match the native confirm() reflexes.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (busy) return;
      if (e.key === "Escape") { e.preventDefault(); onCancel(); }
      else if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); onConfirm(); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [busy, onCancel, onConfirm]);

  return createPortal(
    <div
      className="fixed inset-0 z-[60] bg-fa-ink/70 backdrop-blur-sm flex items-center justify-center p-6 fa-confirm-enter"
      onClick={busy ? undefined : onCancel}
      role="dialog"
      aria-modal="true"
      aria-labelledby="fa-confirm-title"
    >
      <div
        className={cn(
          "fa-card w-full max-w-md p-6 space-y-5 relative",
          "shadow-[0_20px_60px_-15px_rgba(0,0,0,0.6)]",
          "border border-fa-edge",
          opts.destructive && "ring-1 ring-rose-400/20"
        )}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-start gap-3">
          <div
            className={cn(
              "shrink-0 flex items-center justify-center w-10 h-10 rounded-full",
              opts.destructive
                ? "bg-rose-400/10 text-rose-300 ring-1 ring-rose-400/30"
                : "bg-fa-frost-bright/10 text-fa-frost-bright ring-1 ring-fa-frost-bright/30"
            )}
          >
            {opts.destructive
              ? <AlertTriangle className="h-5 w-5" />
              : <Check className="h-5 w-5" />}
          </div>
          <div className="min-w-0 flex-1">
            <h2 id="fa-confirm-title" className="text-fa-frost-bright text-base font-light tracking-tight">
              {opts.title}
            </h2>
            {opts.description && (
              <p className="text-fa-frost-dim text-sm mt-1.5 leading-relaxed">
                {opts.description}
              </p>
            )}
          </div>
          <button
            onClick={onCancel}
            disabled={busy}
            aria-label="Dismiss"
            className="shrink-0 -mt-1 -mr-1 text-fa-frost-dim hover:text-fa-frost-bright transition disabled:opacity-40"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
        {error && (
          <div className="text-rose-300 text-xs bg-rose-400/5 border border-rose-400/20 rounded-md px-3 py-2">
            {error}
          </div>
        )}
        <div className="flex items-center justify-end gap-2 pt-1">
          <button
            onClick={onCancel}
            disabled={busy}
            className="px-3 py-1.5 rounded-md text-fa-frost-dim hover:text-fa-frost-bright text-sm transition disabled:opacity-40"
          >
            {opts.cancelLabel ?? "Cancel"}
          </button>
          <button
            ref={confirmRef}
            onClick={onConfirm}
            disabled={busy}
            className={cn(
              "inline-flex items-center gap-2 px-4 py-1.5 rounded-md text-sm border transition disabled:opacity-50 disabled:cursor-not-allowed",
              opts.destructive
                ? "bg-rose-400/15 hover:bg-rose-400/25 text-rose-200 border-rose-400/30"
                : "bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright border-fa-frost-bright/30"
            )}
          >
            {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            {opts.confirmLabel ?? (opts.destructive ? "Delete" : "Confirm")}
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}
