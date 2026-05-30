import { cn } from "../lib/cn";

/**
 * RichList — iOS-style inset grouped list container + row primitives.
 *
 * Usage:
 *   <RichList>
 *     {items.map((item, i) => (
 *       <RichListRow key={item.id} index={i} onClick={...}>
 *         ...
 *       </RichListRow>
 *     ))}
 *   </RichList>
 */

export function RichList({ children, className }: { children: React.ReactNode; className?: string }) {
  // A clean, flat list — no surrounding box/bezel. Rows are separated only by a faint hairline.
  return (
    <div className={cn("divide-y divide-fa-edge/30", className)}>
      {children}
    </div>
  );
}

export function RichListRow({
  children,
  index,
  onClick,
  disabled,
  className,
}: {
  children: React.ReactNode;
  index: number;
  onClick?: () => void;
  disabled?: boolean;
  className?: string;
}) {
  const isEven = index % 2 === 0;
  return (
    <div
      role={onClick && !disabled ? "button" : undefined}
      tabIndex={onClick && !disabled ? 0 : undefined}
      onClick={onClick && !disabled ? onClick : undefined}
      onKeyDown={
        onClick && !disabled
          ? (e) => {
              if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                onClick();
              }
            }
          : undefined
      }
      className={cn(
        "px-1 sm:px-2 py-4 transition-colors",
        // Whisper-subtle zebra — just enough to separate rows, never a bright band.
        isEven ? "bg-fa-frost/[0.015]" : "bg-transparent",
        onClick && !disabled && "cursor-pointer hover:bg-fa-frost/[0.04]",
        className,
      )}
    >
      {children}
    </div>
  );
}
