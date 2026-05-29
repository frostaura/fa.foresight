import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Check, ChevronDown, Search, X } from "lucide-react";
import { cn } from "../lib/cn";

export interface RichMultiSelectOption {
  value: string;
  label: string;
  sublabel?: string;
  stat?: string;
  disabled?: boolean;
}

export interface RichMultiSelectProps {
  options: RichMultiSelectOption[];
  value: string[];
  onChange: (next: string[]) => void;
  placeholder?: string;
  label?: string;
  searchable?: boolean;
  minSelected?: number;
  className?: string;
}

export default function RichMultiSelect({
  options,
  value,
  onChange,
  placeholder = "Select…",
  label,
  searchable = true,
  minSelected,
  className,
}: RichMultiSelectProps) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [rect, setRect] = useState<{ left: number; top: number; width: number; bottom: number } | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  // Measure the trigger so the portalled panel can anchor to it with fixed positioning.
  // This sidesteps any ancestor `overflow: hidden`/scroll clipping the absolutely-positioned panel.
  const measure = useCallback(() => {
    const el = triggerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    setRect({ left: r.left, top: r.top, width: r.width, bottom: r.bottom });
  }, []);

  useLayoutEffect(() => {
    if (!open) return;
    measure();
    // Re-measure while open: scroll on any ancestor (capture), and viewport resize.
    window.addEventListener("scroll", measure, true);
    window.addEventListener("resize", measure);
    return () => {
      window.removeEventListener("scroll", measure, true);
      window.removeEventListener("resize", measure);
    };
  }, [open, measure]);

  // Close on outside click — the panel is portalled out of the container, so check both.
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      const target = e.target as Node;
      const inContainer = containerRef.current?.contains(target);
      const inPanel = panelRef.current?.contains(target);
      if (!inContainer && !inPanel) {
        setOpen(false);
        setQuery("");
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  // Close on Escape
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        setOpen(false);
        setQuery("");
      }
    };
    document.addEventListener("keydown", handler);
    return () => document.removeEventListener("keydown", handler);
  }, [open]);

  // Auto-focus search when opened
  useEffect(() => {
    if (open && searchable) {
      requestAnimationFrame(() => searchRef.current?.focus());
    }
  }, [open, searchable]);

  const filtered = query.trim()
    ? options.filter((o) =>
        o.label.toLowerCase().includes(query.toLowerCase()) ||
        (o.sublabel ?? "").toLowerCase().includes(query.toLowerCase())
      )
    : options;

  const toggle = useCallback(
    (optValue: string) => {
      const isSelected = value.includes(optValue);
      if (isSelected) {
        if (minSelected != null && value.length <= minSelected) return;
        onChange(value.filter((v) => v !== optValue));
      } else {
        onChange([...value, optValue]);
      }
    },
    [value, onChange, minSelected]
  );

  const handleKeyDown = (e: React.KeyboardEvent, optValue: string) => {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      toggle(optValue);
    }
  };

  // Trigger label rendering
  const selectedOptions = options.filter((o) => value.includes(o.value));
  const renderTriggerContent = () => {
    if (selectedOptions.length === 0) {
      return <span className="text-fa-frost-dim truncate">{placeholder}</span>;
    }
    if (selectedOptions.length <= 2) {
      return (
        <span className="flex items-center gap-1 min-w-0 flex-1 overflow-hidden">
          {selectedOptions.map((o) => (
            <span
              key={o.value}
              className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-fa-glass border border-fa-edge text-fa-frost-bright text-[11px] shrink-0 max-w-[120px] truncate"
            >
              {o.label}
            </span>
          ))}
        </span>
      );
    }
    return (
      <span className="flex items-center gap-1.5 min-w-0 flex-1 overflow-hidden">
        <span className="text-fa-frost-bright text-xs tabular-nums shrink-0">
          {selectedOptions.length} selected
        </span>
        <span className="inline-flex items-center px-1.5 py-0.5 rounded bg-fa-glass border border-fa-edge text-fa-frost-bright text-[11px] shrink-0 max-w-[100px] truncate">
          {selectedOptions[0].label}
        </span>
      </span>
    );
  };

  return (
    <div ref={containerRef} className={cn("relative", className)}>
      {label && (
        <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider mb-1">
          {label}
        </div>
      )}

      {/* Trigger — a div (not a button) so the inner Clear button is valid HTML
          (a <button> cannot be nested inside another <button>). */}
      <div
        ref={triggerRef}
        role="button"
        tabIndex={0}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => { setOpen((o) => !o); }}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            setOpen((o) => !o);
          }
        }}
        className={cn(
          "w-full flex items-center justify-between gap-2 px-2.5 py-1.5 rounded-md cursor-pointer",
          "border border-fa-edge bg-fa-ink-2 text-sm",
          "hover:bg-fa-glass transition-colors",
          "focus:outline-none focus-visible:ring-1 focus-visible:ring-fa-frost-bright/40",
          open && "border-fa-frost-bright/30 bg-fa-glass"
        )}
      >
        <span className="flex items-center gap-1 min-w-0 flex-1 overflow-hidden">
          {renderTriggerContent()}
        </span>
        {value.length > 0 && (
          <button
            type="button"
            aria-label="Clear selection"
            onClick={(e) => {
              e.stopPropagation();
              if (minSelected != null && minSelected > 0) {
                // keep only the first selected option to satisfy minSelected=1
                onChange(value.slice(0, minSelected));
              } else {
                onChange([]);
              }
            }}
            className="shrink-0 p-0.5 rounded text-fa-frost-dim hover:text-fa-frost-bright hover:bg-fa-glass transition-colors"
          >
            <X className="h-3 w-3" />
          </button>
        )}
        <ChevronDown
          className={cn(
            "h-3.5 w-3.5 shrink-0 text-fa-frost-dim transition-transform",
            open && "rotate-180"
          )}
        />
      </div>

      {/* Panel — portalled to <body> with fixed positioning so no ancestor
          `overflow: hidden`/scroll container can clip it. */}
      {open && rect && createPortal(
        (() => {
          const GAP = 4;
          const spaceBelow = window.innerHeight - rect.bottom - GAP;
          const spaceAbove = rect.top - GAP;
          const flipUp = spaceBelow < 240 && spaceAbove > spaceBelow;
          const maxHeight = Math.max(160, (flipUp ? spaceAbove : spaceBelow) - 8);
          const panelStyle: React.CSSProperties = {
            position: "fixed",
            left: rect.left,
            width: rect.width,
            maxHeight,
            ...(flipUp
              ? { bottom: window.innerHeight - rect.top + GAP }
              : { top: rect.bottom + GAP }),
          };
          return (
        <div
          ref={panelRef}
          role="listbox"
          aria-multiselectable="true"
          aria-label={label ?? placeholder}
          style={panelStyle}
          className={cn(
            "z-[60] min-w-[180px] flex flex-col",
            "rounded-md border border-fa-edge bg-fa-ink-2 shadow-lg"
          )}
        >
          {/* Search */}
          {searchable && (
            <div className="flex items-center gap-2 px-2.5 py-2 border-b border-fa-edge shrink-0">
              <Search className="h-3.5 w-3.5 shrink-0 text-fa-frost-dim" />
              <input
                ref={searchRef}
                type="text"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Search…"
                className="flex-1 bg-transparent text-xs text-fa-frost-bright placeholder:text-fa-frost-dim/50 focus:outline-none"
                onKeyDown={(e) => e.stopPropagation()}
              />
              {query && (
                <button
                  type="button"
                  onClick={() => setQuery("")}
                  className="text-fa-frost-dim hover:text-fa-frost-bright transition-colors"
                  aria-label="Clear search"
                >
                  <X className="h-3 w-3" />
                </button>
              )}
            </div>
          )}

          {/* Options */}
          <div className="flex-1 min-h-0 overflow-y-auto py-1" role="presentation">
            {filtered.length === 0 ? (
              <div className="px-3 py-4 text-center text-xs text-fa-frost-dim">
                No options match
              </div>
            ) : (
              filtered.map((opt) => {
                const isSelected = value.includes(opt.value);
                const isAtMin = isSelected && minSelected != null && value.length <= minSelected;
                const isDisabled = opt.disabled || isAtMin;

                return (
                  <div
                    key={opt.value}
                    role="option"
                    aria-selected={isSelected}
                    aria-disabled={isDisabled}
                    tabIndex={isDisabled ? -1 : 0}
                    onClick={() => !isDisabled && toggle(opt.value)}
                    onKeyDown={(e) => !isDisabled && handleKeyDown(e, opt.value)}
                    className={cn(
                      "flex items-center gap-2.5 px-2.5 py-2 text-xs cursor-pointer transition-colors",
                      "focus:outline-none focus-visible:bg-fa-glass",
                      isSelected
                        ? "bg-fa-glass text-fa-frost-bright"
                        : "text-fa-frost-dim hover:bg-fa-glass/60 hover:text-fa-frost-bright",
                      isDisabled && "opacity-50 cursor-not-allowed"
                    )}
                  >
                    {/* Checkbox indicator */}
                    <span
                      className={cn(
                        "flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded border transition-colors",
                        isSelected
                          ? "bg-fa-frost-bright/20 border-fa-frost-bright/50"
                          : "border-fa-edge bg-transparent"
                      )}
                    >
                      {isSelected && <Check className="h-2.5 w-2.5 text-fa-frost-bright" />}
                    </span>

                    {/* Label + sublabel */}
                    <span className="flex-1 min-w-0">
                      <span className="block truncate font-medium leading-tight">
                        {opt.label}
                      </span>
                      {opt.sublabel && (
                        <span className="block truncate text-fa-frost-dim text-[10px] leading-tight mt-0.5">
                          {opt.sublabel}
                        </span>
                      )}
                    </span>

                    {/* Stat chip */}
                    {opt.stat && (
                      <span className="shrink-0 text-[10px] tabular-nums text-fa-frost-dim bg-fa-glass border border-fa-edge rounded px-1.5 py-0.5">
                        {opt.stat}
                      </span>
                    )}
                  </div>
                );
              })
            )}
          </div>
        </div>
          );
        })(),
        document.body
      )}
    </div>
  );
}
