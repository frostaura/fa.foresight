import { useEffect, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

interface SideDrawerProps {
  open: boolean;
  onClose: () => void;
  widthClass?: string;
  children: ReactNode;
}

export default function SideDrawer({
  open,
  onClose,
  widthClass = "w-full md:w-[640px] lg:w-[760px]",
  children
}: SideDrawerProps) {
  // Entrance flag: starts false so the panel mounts off-screen (translate-x-full) even when `open`
  // is already true (these modals are conditionally mounted, so without this they'd appear with no
  // slide-in). Flipping it true on the next frame triggers the slide-in + backdrop fade transition.
  const [entered, setEntered] = useState(false);
  useEffect(() => {
    const id = requestAnimationFrame(() => setEntered(true));
    return () => cancelAnimationFrame(id);
  }, []);
  const visible = open && entered;

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    // Lock background scroll so wheel events on the drawer don't fall through to the page underneath.
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      window.removeEventListener("keydown", onKey);
      document.body.style.overflow = prev;
    };
  }, [open, onClose]);

  // Portal to <body> so the drawer overlays the WHOLE viewport. Without this, any ancestor with a
  // CSS transform/filter/will-change becomes the containing block for our `position: fixed`
  // elements, trapping the drawer inside that container (e.g. the page card) instead of the screen.
  return createPortal(
    <>
      <div
        aria-hidden
        onClick={onClose}
        className={`fixed inset-0 z-[100] bg-fa-ink/55 backdrop-blur-[2px] transition-opacity duration-[var(--fa-duration)] ${
          visible ? "opacity-100" : "opacity-0 pointer-events-none"
        }`}
        style={{ transitionTimingFunction: "var(--fa-ease)" }}
      />
      <div
        aria-hidden={!open}
        className={`fixed inset-y-0 right-0 z-[101] pointer-events-none ${widthClass}`}
      >
        <div
          role="dialog"
          aria-modal="false"
          className={`pointer-events-auto h-full bg-fa-ink/95 backdrop-blur-md border-l border-fa-edge shadow-2xl shadow-fa-ink/80 transition-transform duration-[var(--fa-duration)] flex flex-col ${
            visible ? "translate-x-0" : "translate-x-full"
          }`}
          style={{ transitionTimingFunction: "var(--fa-ease)" }}
        >
          {children}
        </div>
      </div>
    </>,
    document.body,
  );
}
