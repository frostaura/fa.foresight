import { useEffect, useRef, useState } from "react";
import { Link, NavLink, Outlet } from "react-router-dom";
import {
  Activity,
  BarChart2,
  ChevronLeft,
  ChevronRight,
  FlaskConical,
  Gauge,
  Menu,
  Workflow,
  X,
} from "lucide-react";
import { cn } from "../lib/cn";
import foresightMark from "../assets/brand/foresight-mark.svg";

// ── Navigation structure ────────────────────────────────────────────────────────────────────────
//
// Trading (group header — not a link)
//   Status      /trading/status
//   Live        /trading/live
//   Paper       /trading/paper
// ── divider ──
// Models        /models
// Backtesting   /models/backtesting

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
const MOBILE_BREAKPOINT_PX = 1024;

export default function Layout() {
  // Desktop: user-controlled collapse (icon-rail vs full sidebar)
  const [userCollapsed, setUserCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(STORAGE_KEY) === "1";
    } catch {
      return false;
    }
  });

  // Mobile: whether we're below the breakpoint
  const [isMobile, setIsMobile] = useState<boolean>(
    () =>
      typeof window !== "undefined" &&
      window.matchMedia(`(max-width: ${MOBILE_BREAKPOINT_PX - 1}px)`).matches
  );

  // Mobile: whether the off-canvas drawer is open
  const [drawerOpen, setDrawerOpen] = useState(false);

  // Track media query changes
  useEffect(() => {
    const mq = window.matchMedia(`(max-width: ${MOBILE_BREAKPOINT_PX - 1}px)`);
    const handler = (e: MediaQueryListEvent) => {
      setIsMobile(e.matches);
      if (!e.matches) setDrawerOpen(false); // close drawer when going desktop
    };
    mq.addEventListener("change", handler);
    return () => mq.removeEventListener("change", handler);
  }, []);

  // Persist desktop collapse state
  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, userCollapsed ? "1" : "0");
    } catch {
      /* ignore */
    }
  }, [userCollapsed]);

  // Close drawer on outside scroll / ESC
  const backdropRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!drawerOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setDrawerOpen(false);
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [drawerOpen]);

  // On desktop the sidebar collapses to icon-rail when userCollapsed=true.
  // On mobile the sidebar is always hidden; the drawer overlays on top instead.
  const collapsed = !isMobile && userCollapsed;

  const navLinkClass = ({ isActive }: { isActive: boolean }) =>
    cn(
      "flex items-center rounded-md text-sm transition min-h-[40px]",
      collapsed ? "justify-center h-10 w-10 mx-auto" : "gap-3 px-3 py-2",
      isActive
        ? "text-fa-frost-bright"
        : "text-fa-frost/70 hover:text-fa-frost-bright hover:bg-fa-glass/40"
    );

  // Drawer-specific nav link class (always expanded, never icon-rail)
  const drawerNavLinkClass = ({ isActive }: { isActive: boolean }) =>
    cn(
      "flex items-center gap-3 px-3 py-2 rounded-md text-sm transition min-h-[40px]",
      isActive
        ? "text-fa-frost-bright"
        : "text-fa-frost/70 hover:text-fa-frost-bright hover:bg-fa-glass/40"
    );

  const closeDrawer = () => setDrawerOpen(false);

  // ── Sidebar nav content (shared between desktop sidebar + mobile drawer) ──────────────────
  function NavContent({
    linkClass,
    isDrawer = false,
  }: {
    linkClass: (p: { isActive: boolean }) => string;
    isDrawer?: boolean;
  }) {
    return (
      <>
        {/* Brand row */}
        <div
          className={cn(
            "py-5 border-b border-fa-edge flex items-center",
            collapsed && !isDrawer ? "px-2 justify-center" : "px-5 gap-3"
          )}
        >
          <Link
            to="/"
            aria-label="Foresight home"
            className="flex items-center gap-3 min-w-0 rounded-md transition hover:opacity-80"
            onClick={isDrawer ? closeDrawer : undefined}
          >
            <img src={foresightMark} alt="" aria-hidden className="h-8 w-8 shrink-0" />
            {(!collapsed || isDrawer) && (
              <span className="text-fa-frost-bright text-2xl font-light tracking-tight">
                Foresight
              </span>
            )}
          </Link>
        </div>

        {/* Nav groups */}
        <nav
          className={cn(
            "flex-1 space-y-0.5 overflow-y-auto",
            collapsed && !isDrawer ? "p-2" : "p-3"
          )}
        >
          {navGroups.map((group) => (
            <div key={group.groupLabel}>
              {(!collapsed || isDrawer) && (
                <div className="px-3 pb-1 pt-2 text-[10px] uppercase tracking-[0.14em] text-fa-frost-dim/60 font-medium">
                  {group.groupLabel}
                </div>
              )}
              {group.items.map((item) => (
                <NavLink
                  key={item.to}
                  to={item.to}
                  className={linkClass}
                  title={collapsed && !isDrawer ? item.label : undefined}
                  onClick={isDrawer ? closeDrawer : undefined}
                >
                  {({ isActive }) => (
                    <>
                      <item.icon className="h-4 w-4 shrink-0" />
                      {(!collapsed || isDrawer) && (
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
            className={cn(
              "h-px bg-fa-edge",
              collapsed && !isDrawer ? "my-2 mx-1" : "my-2 mx-2"
            )}
            aria-hidden
          />

          {/* Models / Backtesting */}
          {navBottom.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.to === "/models"}
              className={linkClass}
              title={collapsed && !isDrawer ? item.label : undefined}
              onClick={isDrawer ? closeDrawer : undefined}
            >
              {({ isActive }) => (
                <>
                  <item.icon className="h-4 w-4 shrink-0" />
                  {(!collapsed || isDrawer) && (
                    <span className={isActive ? "fa-nav-shimmer" : ""}>
                      {item.label}
                    </span>
                  )}
                </>
              )}
            </NavLink>
          ))}
        </nav>

        {(!collapsed || isDrawer) && (
          <div className="p-3 border-t border-fa-edge">
            <div className="pb-1 text-center text-[10px] uppercase tracking-wider text-fa-frost-dim/70">
              FrostAura Technologies
            </div>
          </div>
        )}
      </>
    );
  }

  return (
    <div className="h-screen flex overflow-hidden">
      {/* ── Desktop sidebar ─────────────────────────────────────────────────────────────────── */}
      {!isMobile && (
        <aside
          className={cn(
            "relative shrink-0 h-screen border-r border-fa-edge bg-fa-ink-2/40 backdrop-blur flex flex-col transition-[width] duration-[var(--fa-duration)]"
          )}
          style={{
            width: collapsed ? "4rem" : "15rem",
            transitionTimingFunction: "var(--fa-ease)",
          }}
        >
          {/* Edge-pill collapse toggle */}
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
            {collapsed ? (
              <ChevronRight className="h-3.5 w-3.5" />
            ) : (
              <ChevronLeft className="h-3.5 w-3.5" />
            )}
          </button>

          <NavContent linkClass={navLinkClass} />
        </aside>
      )}

      {/* ── Mobile: top bar with hamburger ──────────────────────────────────────────────────── */}
      {isMobile && (
        <div className="fixed top-0 left-0 right-0 z-40 h-14 flex items-center px-4 gap-3 bg-fa-ink/95 backdrop-blur border-b border-fa-edge">
          <button
            type="button"
            onClick={() => setDrawerOpen(true)}
            aria-label="Open navigation"
            className="h-10 w-10 flex items-center justify-center rounded-md text-fa-frost-dim hover:text-fa-frost-bright hover:bg-fa-glass/60 transition"
          >
            <Menu className="h-5 w-5" />
          </button>
          <Link to="/" className="flex items-center gap-2.5 hover:opacity-80 transition">
            <img src={foresightMark} alt="" aria-hidden className="h-7 w-7" />
            <span className="text-fa-frost-bright text-xl font-light tracking-tight">
              Foresight
            </span>
          </Link>
        </div>
      )}

      {/* ── Mobile: off-canvas drawer ────────────────────────────────────────────────────────── */}
      {isMobile && drawerOpen && (
        <>
          {/* Backdrop */}
          <div
            ref={backdropRef}
            className="fixed inset-0 z-40 bg-fa-ink/70 backdrop-blur-sm"
            onClick={closeDrawer}
            aria-hidden
          />
          {/* Drawer panel */}
          <aside className="fixed left-0 top-0 bottom-0 z-50 w-72 flex flex-col bg-fa-ink-2 border-r border-fa-edge shadow-2xl">
            {/* Drawer close button */}
            <button
              type="button"
              onClick={closeDrawer}
              aria-label="Close navigation"
              className="absolute top-4 right-4 h-8 w-8 flex items-center justify-center rounded-md text-fa-frost-dim hover:text-fa-frost-bright hover:bg-fa-glass/60 transition"
            >
              <X className="h-4 w-4" />
            </button>
            <NavContent linkClass={drawerNavLinkClass} isDrawer />
          </aside>
        </>
      )}

      {/* ── Main content area ────────────────────────────────────────────────────────────────── */}
      <main
        className={cn(
          "flex-1 min-w-0 h-screen overflow-y-auto",
          isMobile && "pt-14" // offset for the fixed mobile top bar
        )}
      >
        <Outlet />
      </main>
    </div>
  );
}
