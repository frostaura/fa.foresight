import { useEffect, useState } from "react";
import { Link, NavLink, Outlet } from "react-router-dom";
import { Activity, BarChart3, ChevronLeft, ChevronRight, Workflow, Radio } from "lucide-react";
import { cn } from "../lib/cn";
import foresightMark from "../assets/brand/foresight-mark.svg";

// Nav is split into two groups separated by a horizontal divider — Models lives below the divider
// because it's a configuration surface (defining how predictions get made) rather than a real-time
// trading surface like Paper Trading / Markets.
const navTop = [
  { to: "/paper-trading", label: "Paper Trading", icon: Activity },
  { to: "/markets", label: "Markets", icon: BarChart3 },
];
const navBottom = [
  { to: "/models", label: "Models", icon: Workflow },
  { to: "/channels", label: "Channels", icon: Radio },
];

const STORAGE_KEY = "fa.foresight.sidebar.collapsed";
// Viewport width below which the sidebar is forced collapsed regardless of user preference. 1024px
// matches Tailwind's `lg` breakpoint — the same threshold the Backtesting form uses to drop from
// 5 columns to 2, so collapsing the sidebar at that boundary keeps the page from getting cramped.
const NARROW_BREAKPOINT_PX = 1024;

export default function Layout() {
  // User's persisted preference. Independent of viewport — restored as-is on wide screens.
  const [userCollapsed, setUserCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(STORAGE_KEY) === "1";
    } catch {
      return false;
    }
  });
  // Viewport-driven flag. Forces collapsed when true, regardless of userCollapsed.
  const [narrow, setNarrow] = useState<boolean>(() =>
    typeof window !== "undefined" && window.matchMedia(`(max-width: ${NARROW_BREAKPOINT_PX - 1}px)`).matches
  );

  // matchMedia is preferable to a resize listener — fires only at the breakpoint crossover, not
  // on every pixel of drag. Mirrors the standard reduced-motion / dark-mode subscription shape.
  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${NARROW_BREAKPOINT_PX - 1}px)`);
    const handler = (e: MediaQueryListEvent) => setNarrow(e.matches);
    mq.addEventListener("change", handler);
    return () => mq.removeEventListener("change", handler);
  }, []);

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, userCollapsed ? "1" : "0");
    } catch {
      // localStorage unavailable — ignore.
    }
  }, [userCollapsed]);

  // Effective collapse state — either the user explicitly collapsed, or the viewport is narrow.
  // On wide screens the user's preference governs; below the breakpoint the layout forces it.
  const collapsed = userCollapsed || narrow;

  // Active state visual: no border / no glass background — replaced with a slow text shimmer
  // applied only to the label span (background-clip: text doesn't work on SVG icons). Icon
  // stays static at fa-frost-bright when active so the still icon + breathing text combo reads
  // cleanly.
  const navLinkClass = ({ isActive }: { isActive: boolean }) =>
    cn(
      "flex items-center rounded-md text-sm transition",
      collapsed ? "justify-center h-10 w-10 mx-auto" : "gap-3 px-3 py-2",
      isActive
        ? "text-fa-frost-bright"
        : "text-fa-frost/70 hover:text-fa-frost-bright hover:bg-fa-glass/40"
    );

  return (
    <div className="h-screen flex">
      <aside
        className={cn(
          "relative shrink-0 h-screen border-r border-fa-edge bg-fa-ink-2/40 backdrop-blur flex flex-col transition-[width] duration-[var(--fa-duration)]",
          collapsed ? "w-16" : "w-60"
        )}
        style={{ transitionTimingFunction: "var(--fa-ease)" }}
      >
        {/* Edge-pill toggle: a small circular chevron button that floats half on / half off the
            sidebar's right border. This is the standard modern sidebar pattern (Linear, Notion,
            Cursor, GitLab) — it's always in the same screen position, doesn't compete with the
            brand row for space, and reads as the dedicated "collapse" affordance.
            Hidden when the viewport is narrow because the toggle is locked-collapsed there. */}
        {!narrow && (
          <button
            type="button"
            onClick={() => setUserCollapsed((c) => !c)}
            aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
            title={collapsed ? "Expand sidebar" : "Collapse sidebar"}
            className={cn(
              "absolute top-7 -right-3 z-20 h-6 w-6 rounded-full",
              "flex items-center justify-center",
              "bg-fa-ink-2 border border-fa-edge text-fa-frost-dim",
              "hover:bg-fa-glass-strong hover:text-fa-frost-bright hover:border-fa-frost/40",
              "shadow-sm transition",
            )}
          >
            {collapsed
              ? <ChevronRight className="h-3.5 w-3.5" />
              : <ChevronLeft className="h-3.5 w-3.5" />}
          </button>
        )}
        <div
          className={cn(
            "py-5 border-b border-fa-edge flex items-center",
            collapsed ? "px-2 justify-center" : "px-5 gap-3"
          )}
        >
          <Link
            to="/"
            aria-label="Foresight home"
            className={cn(
              "flex items-center min-w-0 rounded-md transition hover:opacity-80",
              collapsed ? "" : "gap-3"
            )}
          >
            <img
              src={foresightMark}
              alt=""
              aria-hidden
              className="h-8 w-8 shrink-0"
            />
            {!collapsed && (
              <span className="text-fa-frost-bright text-2xl font-light tracking-tight">Foresight</span>
            )}
          </Link>
        </div>
        <nav className={cn("flex-1 space-y-0.5 overflow-y-auto", collapsed ? "p-2" : "p-3")}>

          {navTop.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={navLinkClass}
              title={collapsed ? item.label : undefined}
            >
              {({ isActive }) => (
                <>
                  <item.icon className="h-4 w-4 shrink-0" />
                  {!collapsed && <span className={isActive ? "fa-nav-shimmer" : ""}>{item.label}</span>}
                </>
              )}
            </NavLink>
          ))}
          <div className={cn("h-px bg-fa-edge", collapsed ? "my-2 mx-1" : "my-2 mx-2")} aria-hidden />
          {navBottom.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={navLinkClass}
              title={collapsed ? item.label : undefined}
            >
              {({ isActive }) => (
                <>
                  <item.icon className="h-4 w-4 shrink-0" />
                  {!collapsed && <span className={isActive ? "fa-nav-shimmer" : ""}>{item.label}</span>}
                </>
              )}
            </NavLink>
          ))}
        </nav>
        {!collapsed && (
          <div className="p-3 border-t border-fa-edge">
            <div className="pb-1 text-center text-[10px] uppercase tracking-wider text-fa-frost-dim/70">
              FrostAura Labs
            </div>
          </div>
        )}
      </aside>
      <main className="flex-1 min-w-0 h-screen overflow-y-auto">
        <Outlet />
      </main>
    </div>
  );
}
