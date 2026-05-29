import * as React from "react";
import * as SliderPrimitive from "@radix-ui/react-slider";
import { cn } from "../../lib/utils";

const Slider = React.forwardRef<
  React.ElementRef<typeof SliderPrimitive.Root>,
  React.ComponentPropsWithoutRef<typeof SliderPrimitive.Root>
>(({ className, ...props }, ref) => (
  <SliderPrimitive.Root
    ref={ref}
    className={cn(
      "relative flex w-full touch-none select-none items-center",
      className
    )}
    {...props}
  >
    <SliderPrimitive.Track className="relative h-1.5 w-full grow overflow-hidden rounded-full bg-fa-glass-strong">
      <SliderPrimitive.Range className="absolute h-full bg-fa-frost-bright" />
    </SliderPrimitive.Track>
    <SliderPrimitive.Thumb
      className={cn(
        "block h-4 w-4 rounded-full border-2 border-fa-ink bg-fa-frost-bright",
        "shadow-[0_0_0_1px_var(--fa-frost-bright),0_0_12px_rgba(212,236,255,0.55)]",
        "transition-transform hover:scale-110 active:scale-115",
        "focus-visible:outline-none focus-visible:shadow-[0_0_0_1px_var(--fa-frost-bright),0_0_0_4px_rgba(164,212,244,0.25),0_0_12px_rgba(212,236,255,0.7)]",
        "disabled:pointer-events-none disabled:opacity-50"
      )}
    />
  </SliderPrimitive.Root>
));
Slider.displayName = SliderPrimitive.Root.displayName;

export { Slider };
