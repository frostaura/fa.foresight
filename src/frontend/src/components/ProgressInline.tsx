import type { ReactNode } from "react";
import { cn } from "../lib/cn";

type Tone = "frost" | "emerald" | "amber";

const TONE_FILL: Record<Tone, string> = {
  frost: "bg-fa-frost-bright",
  emerald: "bg-emerald-300",
  amber: "bg-amber-300",
};

export interface ProgressInlineProps {
  /**
   * Completion fraction, 0–100. Pass `null` for an indeterminate bar — used when work is genuinely
   * in flight but no percentage is known (the point is that it never *looks* frozen).
   */
  pct: number | null;
  /** Short status text shown left of the bar, e.g. "Building features 5m". */
  label?: ReactNode;
  /** Right-aligned detail, e.g. "62%" or "fold 3/5". For determinate bars defaults to the rounded %. */
  detail?: ReactNode;
  tone?: Tone;
  size?: "sm" | "md";
  className?: string;
}

/**
 * The canonical "something is happening" bar used across training, chaos, backtests and any
 * blocking op. Determinate when `pct` is a number (fills + animates width); indeterminate when
 * `pct` is null (a frost segment sweeps the track). Keeps the brand's frosty/glass posture — thin,
 * rounded, calm. Pair it with phase text so the user always reads *what* is happening, not just that
 * the app is busy. The indeterminate sweep keyframe lives in index.css (`fa-progress-slide`).
 */
export function ProgressInline({
  pct,
  label,
  detail,
  tone = "frost",
  size = "sm",
  className,
}: ProgressInlineProps) {
  const indeterminate = pct == null;
  const clamped = indeterminate ? 0 : Math.min(100, Math.max(0, pct));
  const shownDetail = detail ?? (indeterminate ? null : `${clamped.toFixed(0)}%`);
  const track = size === "md" ? "h-2" : "h-1.5";

  return (
    <div className={cn("w-full", className)}>
      {(label != null || shownDetail != null) && (
        <div className="flex items-center justify-between gap-2 mb-1 fa-caption text-fa-frost-dim">
          {label != null ? <span className="truncate">{label}</span> : <span />}
          {shownDetail != null && <span className="tabular-nums shrink-0">{shownDetail}</span>}
        </div>
      )}
      <div
        role="progressbar"
        aria-valuenow={indeterminate ? undefined : Math.round(clamped)}
        aria-valuemin={0}
        aria-valuemax={100}
        className={cn("relative w-full overflow-hidden rounded-full bg-fa-edge/60", track)}
      >
        {indeterminate ? (
          <span
            aria-hidden
            className={cn("fa-progress-slide absolute inset-y-0 rounded-full opacity-80", TONE_FILL[tone])}
          />
        ) : (
          <span
            aria-hidden
            className={cn("absolute inset-y-0 left-0 rounded-full transition-[width] duration-300", TONE_FILL[tone])}
            style={{ width: `${clamped}%` }}
          />
        )}
      </div>
    </div>
  );
}
