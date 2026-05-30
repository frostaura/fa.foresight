import * as React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "../../lib/utils";

const badgeVariants = cva(
  "inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium transition-colors",
  {
    variants: {
      variant: {
        default: "border-fa-edge text-fa-frost bg-transparent",
        success: "border-fa-success/40 text-fa-success bg-fa-success/10",
        warning: "border-fa-warning/40 text-fa-warning bg-fa-warning/10",
        danger: "border-fa-danger/40 text-fa-danger bg-fa-danger/10",
        primary: "border-fa-frost/40 text-fa-frost-bright bg-fa-frost/10",
        outline: "border-fa-edge text-fa-frost-dim bg-transparent",
      },
    },
    defaultVariants: {
      variant: "default",
    },
  }
);

export interface BadgeProps
  extends React.HTMLAttributes<HTMLDivElement>,
    VariantProps<typeof badgeVariants> {}

function Badge({ className, variant, ...props }: BadgeProps) {
  return (
    <div className={cn(badgeVariants({ variant }), className)} {...props} />
  );
}

export { Badge, badgeVariants };
