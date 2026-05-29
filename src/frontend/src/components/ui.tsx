import { type ReactNode, type HTMLAttributes, type ButtonHTMLAttributes, type InputHTMLAttributes, forwardRef, useState } from "react";
import { Check } from "lucide-react";
import { cn } from "../lib/cn";

export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("fa-card p-5", className)} {...props} />;
}

export function CardHeader({ title, subtitle, action }: { title: ReactNode; subtitle?: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-4 mb-4">
      <div>
        <h3 className="text-fa-frost-bright text-base font-medium">{title}</h3>
        {subtitle && <p className="text-fa-frost-dim text-sm mt-0.5">{subtitle}</p>}
      </div>
      {action}
    </div>
  );
}

export function Stat({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="fa-card p-4">
      <div className="fa-stat-label">{label}</div>
      <div className="fa-stat-value mt-1">{value}</div>
      {hint && <div className="text-xs text-fa-frost-dim mt-1">{hint}</div>}
    </div>
  );
}

export const Button = forwardRef<HTMLButtonElement, ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "default" | "primary" | "ghost" }>(
  ({ className, variant = "default", ...props }, ref) => {
    const cls = variant === "primary" ? "fa-button-primary" : variant === "ghost" ? "fa-button-ghost" : "fa-button";
    return <button ref={ref} className={cn(cls, className)} {...props} />;
  }
);
Button.displayName = "Button";

export function Input({ className, ...props }: React.InputHTMLAttributes<HTMLInputElement>) {
  return <input className={cn("fa-input w-full", className)} {...props} />;
}

export function Select({ className, children, ...props }: React.SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select className={cn("fa-input w-full", className)} {...props}>
      {children}
    </select>
  );
}

export function Badge({ children, tone = "default" }: { children: ReactNode; tone?: "default" | "success" | "warn" | "danger" }) {
  const map = {
    default: "border-fa-edge text-fa-frost",
    success: "border-fa-success/40 text-fa-success",
    warn: "border-fa-warning/40 text-fa-warning",
    danger: "border-fa-danger/40 text-fa-danger"
  } as const;
  return (
    <span className={cn("inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs", map[tone])}>
      {children}
    </span>
  );
}

export function Empty({ title, hint, action }: { title: string; hint?: string; action?: ReactNode }) {
  return (
    <div className="fa-card flex flex-col items-center text-center py-12 px-6">
      <div className="text-fa-frost-bright text-base font-medium">{title}</div>
      {hint && <p className="text-fa-frost-dim text-sm mt-2 max-w-sm">{hint}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
}

export function Spinner() {
  return (
    <span className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-fa-frost/40 border-t-fa-frost-bright" />
  );
}

type CheckboxProps = Omit<InputHTMLAttributes<HTMLInputElement>, "type"> & { label?: ReactNode };

export const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(
  ({ className, label, checked, disabled, ...props }, ref) => {
    return (
      <label
        className={cn(
          "inline-flex items-center gap-2 text-xs text-fa-frost-dim transition select-none",
          disabled ? "opacity-50 cursor-not-allowed" : "hover:text-fa-frost-bright cursor-pointer",
          className
        )}
      >
        <span
          className={cn(
            "relative inline-flex h-4 w-4 shrink-0 items-center justify-center rounded border transition",
            checked
              ? "bg-fa-frost border-fa-frost-bright"
              : "bg-fa-glass border-fa-edge hover:border-fa-frost/40"
          )}
        >
          <input
            ref={ref}
            type="checkbox"
            checked={checked}
            disabled={disabled}
            className="absolute inset-0 m-0 cursor-inherit opacity-0"
            {...props}
          />
          {checked && <Check className="h-3 w-3 text-fa-ink pointer-events-none" strokeWidth={3} />}
        </span>
        {label && <span className="leading-none">{label}</span>}
      </label>
    );
  }
);
Checkbox.displayName = "Checkbox";

export function LivePulse({ label = "Live", title }: { label?: string; title?: string }) {
  return (
    <span
      className="inline-flex items-center gap-1 fa-overline text-fa-success"
      title={title ?? "Live — auto-refreshing"}
    >
      <span className="relative inline-flex h-2 w-2">
        <span className="absolute inset-0 rounded-full bg-fa-success opacity-75 animate-ping" />
        <span className="relative h-2 w-2 rounded-full bg-fa-success" />
      </span>
      {label}
    </span>
  );
}

/**
 * Lightweight hover/focus tooltip for inline help. Wraps a trigger element; on hover or focus
 * shows a floating bubble above or below. Pointer-events on the bubble are disabled so it never
 * captures clicks aimed at sibling controls. Use sparingly — for short explanations that don't
 * warrant a popover or modal.
 */
export function Tooltip({
  content,
  side = "top",
  className,
  children
}: {
  content: ReactNode;
  side?: "top" | "bottom";
  className?: string;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  return (
    <span
      className={cn("relative inline-flex items-center", className)}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
      onFocus={() => setOpen(true)}
      onBlur={() => setOpen(false)}
    >
      {children}
      {open && (
        <span
          role="tooltip"
          className={cn(
            "absolute left-1/2 -translate-x-1/2 w-64 rounded-md border border-fa-edge",
            "bg-fa-ink/95 backdrop-blur px-2.5 py-1.5 fa-caption text-fa-frost",
            "normal-case tracking-normal shadow-2xl z-50 pointer-events-none leading-snug",
            side === "top" ? "bottom-full mb-2" : "top-full mt-2"
          )}
        >
          {content}
        </span>
      )}
    </span>
  );
}

export function ConnectingPulse({ label = "Connecting" }: { label?: string }) {
  return (
    <span className="inline-flex items-center gap-1 fa-overline text-fa-frost-dim">
      <span className="h-2 w-2 rounded-full border border-fa-frost-dim border-t-transparent animate-spin" />
      {label}
    </span>
  );
}
