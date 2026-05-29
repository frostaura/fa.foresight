import { useEffect, useState } from "react";
import { Link, NavLink, Outlet } from "react-router-dom";
import {
  Activity,
  BarChart2,
  ChevronLeft,
  ChevronRight,
  FlaskConical,
  Gauge,
  Workflow,
} from "lucide-react";
import { cn } from "../lib/cn";
import foresightMark from "../assets/brand/foresight-mark.svg";

// ── Navigation structure ────────────────────────────────────────────────────────────────────
//
// Trading (group header — not a link)
//   Status      /trading/status
//   Live        /trading/live
//   Paper       /trading/paper
// ── divider ──
// Models        /models

type NavGroup = {
  groupLabel: string;
  items: NavItem[];
};
type NavItem = {
  to: string;
  label: string;
  icon: React.ElementType;
};

const navGroups: NavGroup[] = [
  {
    groupLabel: "Trading",
    items: [
      { to: "/trading/status", label: "Status", icon: Gauge },
      { to: "/trading/live", label: "Live", icon: Activity },
      { to: "/trading/paper", label: "Paper", icon: BarChart2 },
    ],
  },
];

const navBottom: NavItem[] = [
  { to: "/models", label: "Models", icon: Workflow },
  { to: "/models/backtesting", label: "Backtesting", icon: FlaskConical },
];

const STORAGE_KEY = "fa.foresight.sidebar.collapsed";
const NARROW_BREAKPOINT_PX = 1024;

export default function Layout() {
  const [userCollapsed, setUserCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(STORAGE_KEY) === "1";
    } catch {
      return false;
    }
  });
  const [narrow, setNarrow] = useState<boolean>(() =>
    typeof window !== "undefined" &&
    window.matchMedia(`(max-width: ${NARROW_BREAKPOINT_PX - 1}px)`).matches
  );

  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${NARROW_BREAKPOINT_PX - 1}px)`);
    const handler = (e: MediaQueryListEvent) => setNarrow(e.matches);
    mq.addEventListener("change", handler);
    return () => mq.removeEventListener("change", handler);
  }, []);

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, userCollapsed ? "1" : "0");
    } catch { /* ignore */ }
  }, [userCollapsed]);

  const collapsed = userCollapsed || narrow;

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
        {/* Edge-pill collapse toggle */}
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
              "shadow-sm transition"
            )}
          >
            {collapsed
              ? <ChevronRight className="h-3.5 w-3.5" />
              : <ChevronLeft className="h-3.5 w-3.5" />}
          </button>
        )}

        {/* Brand row */}
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
            <img src={foresightMark} alt="" aria-hidden className="h-8 w-8 shrink-0" />
            {!collapsed && (
              <span className="text-fa-frost-bright text-2xl font-light tracking-tight">
                Foresight
              </span>
            )}
          </Link>
        </div>

        {/* Nav */}
        <nav
          className={cn(
            "flex-1 space-y-0.5 overflow-y-auto",
            collapsed ? "p-2" : "p-3"
          )}
        >
          {/* Trading group */}
          {navGroups.map((group) => (
            <div key={group.groupLabel}>
              {!collapsed && (
                <div className="px-3 pb-1 pt-2 text-[10px] uppercase tracking-[0.14em] text-fa-frost-dim/60 font-medium">
                  {group.groupLabel}
                </div>
              )}
              {group.items.map((item) => (
                <NavLink
                  key={item.to}
                  to={item.to}
                  className={navLinkClass}
                  title={collapsed ? item.label : undefined}
                >
                  {({ isActive }) => (
                    <>
                      <item.icon className="h-4 w-4 shrink-0" />
                      {!collapsed && (
                        <span className={isActive ? "fa-nav-shimmer" : ""}>
                          {item.label}
                        </span>
                      )}
                    </>
                  )}
                </NavLink>
              ))}
            </div>
          ))}

          {/* Divider */}
          <div
            className={cn("h-px bg-fa-edge", collapsed ? "my-2 mx-1" : "my-2 mx-2")}
            aria-hidden
          />

          {/* Models / Backtesting */}
          {navBottom.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.to === "/models"}
              className={navLinkClass}
              title={collapsed ? item.label : undefined}
            >
              {({ isActive }) => (
                <>
                  <item.icon className="h-4 w-4 shrink-0" />
                  {!collapsed && (
                    <span className={isActive ? "fa-nav-shimmer" : ""}>
                      {item.label}
                    </span>
                  )}
                </>
              )}
            </NavLink>
          ))}
        </nav>

        {!collapsed && (
          <div className="p-3 border-t border-fa-edge">
            <div className="pb-1 text-center text-[10px] uppercase tracking-wider text-fa-frost-dim/70">
              FrostAura Technologies
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
