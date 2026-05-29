import { type ReactNode } from "react";
import { Link } from "react-router-dom";
import { ArrowLeft } from "lucide-react";

export default function PageHeader({ title, subtitle, action, leading, backTo }: { title: string; subtitle?: string; action?: ReactNode; leading?: ReactNode; backTo?: string }) {
  return (
    <div className="px-4 sm:px-8 py-4 sm:py-6 border-b border-fa-edge flex items-center justify-between gap-4 sm:gap-6">
      <div className="flex items-center gap-4 min-w-0">
        {backTo && (
          <Link
            to={backTo}
            aria-label="Back"
            className="h-10 w-10 shrink-0 rounded-full border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright flex items-center justify-center transition"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
        )}
        {leading}
        <div className="min-w-0">
          <h1 className="text-xl sm:text-2xl font-light text-fa-frost-bright tracking-tight">{title}</h1>
          {subtitle && <p className="text-fa-frost-dim text-xs sm:text-sm mt-1">{subtitle}</p>}
        </div>
      </div>
      {action}
    </div>
  );
}
