import * as React from "react";
import { Slot } from "@radix-ui/react-slot";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "../../lib/utils";

const buttonVariants = cva(
  "inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium transition disabled:pointer-events-none disabled:opacity-50",
  {
    variants: {
      variant: {
        default:
          "border border-fa-edge bg-fa-glass-strong hover:bg-fa-glass hover:border-fa-frost/40 text-fa-frost-bright",
        primary:
          "bg-fa-frost/15 border border-fa-frost/40 text-fa-frost-bright hover:bg-fa-frost/25",
        destructive:
          "border border-fa-danger/40 bg-fa-danger/10 text-fa-danger hover:bg-fa-danger/20",
        outline:
          "border border-fa-edge bg-transparent text-fa-frost hover:bg-fa-glass hover:text-fa-frost-bright",
        ghost:
          "text-fa-frost/80 hover:text-fa-frost-bright hover:bg-fa-glass",
        link: "text-fa-frost-bright underline-offset-4 hover:underline",
      },
      size: {
        default: "px-4 py-2",
        sm: "px-3 py-1.5 text-xs",
        lg: "px-6 py-2.5",
        icon: "h-9 w-9",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  }
);

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  asChild?: boolean;
}

const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, asChild = false, ...props }, ref) => {
    const Comp = asChild ? Slot : "button";
    return (
      <Comp
        className={cn(buttonVariants({ variant, size, className }))}
        ref={ref}
        {...props}
      />
    );
  }
);
Button.displayName = "Button";

export { Button, buttonVariants };
