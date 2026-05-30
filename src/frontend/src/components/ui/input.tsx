import * as React from "react";
import { cn } from "../../lib/utils";

export type InputProps = React.InputHTMLAttributes<HTMLInputElement>;

const Input = React.forwardRef<HTMLInputElement, InputProps>(
  ({ className, type, ...props }, ref) => {
    return (
      <input
        type={type}
        className={cn(
          "flex w-full rounded-md border border-fa-edge bg-fa-glass px-3 py-2",
          "text-sm text-fa-frost-bright placeholder:text-fa-frost-dim/60",
          "focus-visible:outline-none focus-visible:border-fa-frost/40",
          "disabled:cursor-not-allowed disabled:opacity-50",
          "transition",
          className
        )}
        ref={ref}
        {...props}
      />
    );
  }
);
Input.displayName = "Input";

export { Input };
