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
  return (
    <div
      className={cn(
        "rounded-xl border border-fa-edge overflow-hidden divide-y divide-fa-edge/60",
        className,
      )}
    >
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
        "px-4 sm:px-5 py-4 transition",
        isEven ? "bg-fa-glass/15" : "bg-transparent",
        onClick && !disabled && "cursor-pointer hover:bg-fa-glass/40",
        className,
      )}
    >
      {children}
    </div>
  );
}
